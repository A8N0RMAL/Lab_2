using HR_Screening;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace HR_Screening.Controllers
{
    public class ScreeningController : Controller
    {
        
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        public ScreeningController(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        // GET: /
        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        // POST: /Screening/Process
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Process(ScreeningRequestViewModel request)
        {
            if (!ModelState.IsValid)
            {
                return View("Index", request);
            }

            string cvText = string.Empty;

            // Extract text based on file type
            if (request.CvFile != null && request.CvFile.Length > 0)
            {
                var fileExtension = Path.GetExtension(request.CvFile.FileName).ToLowerInvariant();

                if (fileExtension == ".txt")
                {
                    using (var reader = new StreamReader(request.CvFile.OpenReadStream()))
                    {
                        cvText = await reader.ReadToEndAsync();
                    }
                }
                else if (fileExtension == ".pdf")
                {
                    try
                    {
                        cvText = ExtractTextFromPdf(request.CvFile);
                    }
                    catch (Exception ex)
                    {
                        ModelState.AddModelError("", $"Failed to extract text from PDF: {ex.Message}");
                        return View("Index", request);
                    }
                }
                else
                {
                    ModelState.AddModelError("CvFile", "Unsupported file format. Please upload a .txt or .pdf file.");
                    return View("Index", request);
                }
            }

            if (string.IsNullOrWhiteSpace(cvText))
            {
                ModelState.AddModelError("CvFile", "The uploaded file contains no readable text.");
                return View("Index", request);
            }

            // Retrieve the Webhook URL from appsettings.json or default to local n8n
            string n8nWebhookUrl = _configuration["N8nSettings:WebhookUrl"]
                                   ?? "http://localhost:5678/webhook/hr-screening";

            try
            {
                // Serialize request data into the JSON schema expected by n8n
                var payload = new
                {
                    candidateName = request.CandidateName,
                    jobTitle = request.JobTitle,
                    jobDescription = request.JobDescription,
                    cvText = cvText
                };

                var jsonContent = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                // Call the n8n API. Allow up to 90 seconds for AI processing
                _httpClient.Timeout = TimeSpan.FromSeconds(90);
                var response = await _httpClient.PostAsync(n8nWebhookUrl, jsonContent);

                if (!response.IsSuccessStatusCode)
                {
                    ModelState.AddModelError("", $"n8n workflow returned an error status code: {response.StatusCode}");
                    return View("Index", request);
                }

                var responseString = await response.Content.ReadAsStringAsync();

                // Print the raw response to your .NET console for easy visual debugging
                Console.WriteLine("\n=================== RAW N8N RESPONSE ===================");
                Console.WriteLine(responseString);
                Console.WriteLine("========================================================\n");

                string rawTextToDeserialize = string.Empty;

                try
                {
                    // Attempt to parse the response string as a structured JSON document
                    using (JsonDocument doc = JsonDocument.Parse(responseString))
                    {
                        var root = doc.RootElement;
                        JsonElement targetElement = root;

                        // n8n wraps the output inside an "output" property
                        if (root.TryGetProperty("output", out JsonElement outputElement))
                        {
                            targetElement = outputElement;
                        }

                        // Dynamically extract based on value kind
                        if (targetElement.ValueKind == JsonValueKind.String)
                        {
                            rawTextToDeserialize = targetElement.GetString() ?? string.Empty;
                        }
                        else if (targetElement.ValueKind == JsonValueKind.Object)
                        {
                            rawTextToDeserialize = targetElement.GetRawText();
                        }
                    }
                }
                catch (JsonException)
                {
                    // Fallback: If n8n returned plain text starting with '#', treat the entire response as the raw text
                    rawTextToDeserialize = responseString;
                }

                // Locate and isolate any structured JSON block hidden inside conversational markdown
                string isolatedJson = ExtractJsonFromString(rawTextToDeserialize);

                ScreeningResult? screeningResult = null;

                if (!string.IsNullOrWhiteSpace(isolatedJson))
                {
                    try
                    {
                        // Strip potential markdown wrappers (e.g., ```json ... ```)
                        isolatedJson = CleanMarkdownJson(isolatedJson);

                        var serializerOptions = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };

                        screeningResult = JsonSerializer.Deserialize<ScreeningResult>(isolatedJson, serializerOptions);
                    }
                    catch (JsonException)
                    {
                        // JSON parsing failed despite extracting bounds
                        screeningResult = null;
                    }
                }

                // ===================================================================
                // CRITICAL RESILIENCY FALLBACK
                // If the AI skipped JSON output and returned pure Markdown / Text,
                // do not crash! Display the report inside the Summary block instead.
                // ===================================================================
                if (screeningResult == null)
                {
                    screeningResult = new ScreeningResult
                    {
                        Score = 70, // Default neutral placeholder score
                        Recommendation = "Manual Review Required",
                        Summary = rawTextToDeserialize, // Show the raw markdown report here
                        MatchingSkills = new List<string> { "Review text details below" },
                        MissingSkills = new List<string> { "Review text details below" }
                    };
                }

                var resultViewModel = new ScreeningResponseViewModel
                {
                    CandidateName = request.CandidateName,
                    JobTitle = request.JobTitle,
                    Result = screeningResult
                };

                return View("Result", resultViewModel);
            }
            catch (TaskCanceledException)
            {
                ModelState.AddModelError("", "The request to the AI screening service timed out. Please try again.");
                return View("Index", request);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Failed to connect to the n8n workflow agent: {ex.Message}");
                return View("Index", request);
            }
        }

        /// <summary>
        /// Locates and extracts the first valid JSON object block { ... } found inside raw text.
        /// Returns string.Empty if no brackets are detected.
        /// </summary>
        private string ExtractJsonFromString(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            int firstBrace = input.IndexOf('{');
            int lastBrace = input.LastIndexOf('}');

            if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
            {
                return input.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            return string.Empty; // Return empty to signal no JSON structure exists
        }

        /// <summary>
        /// Cleans any enclosing markdown code blocks that LLMs often generate.
        /// </summary>
        private string CleanMarkdownJson(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            input = input.Trim();

            // Strip prefix backticks
            if (input.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                input = input.Substring(7);
            }
            else if (input.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                input = input.Substring(3);
            }

            // Strip suffix backticks
            if (input.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                input = input.Substring(0, input.Length - 3);
            }

            return input.Trim();
        }

        /// <summary>
        /// Extracts all readable text from an uploaded PDF file stream using PdfPig.
        /// </summary>
        private string ExtractTextFromPdf(IFormFile pdfFile)
        {
            var sb = new StringBuilder();
            using (var stream = pdfFile.OpenReadStream())
            {
                using (var pdfDocument = PdfDocument.Open(stream))
                {
                    foreach (var page in pdfDocument.GetPages())
                    {
                        sb.AppendLine(page.Text);
                    }
                }
            }
            return sb.ToString();
        }
    }
}