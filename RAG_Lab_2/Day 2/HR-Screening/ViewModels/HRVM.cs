using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HR_Screening
{
    /// <summary>
    /// Represents the form data submitted by the HR user.
    /// </summary>
    public class ScreeningRequestViewModel
    {
        [Required(ErrorMessage = "Candidate name is required.")]
        [Display(Name = "Candidate Full Name")]
        public string CandidateName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Job title is required.")]
        [Display(Name = "Target Job Title")]
        public string JobTitle { get; set; } = string.Empty;

        [Required(ErrorMessage = "Job description is required.")]
        [Display(Name = "Job Description / Requirements")]
        public string JobDescription { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please upload a candidate CV file.")]
        [Display(Name = "Candidate CV File (.txt, .pdf)")]
        public IFormFile CvFile { get; set; }
    }

    /// <summary>
    /// Represents the structured JSON output returned by the n8n AI Agent.
    /// </summary>
    public class ScreeningResult
    {
        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("recommendation")]
        public string Recommendation { get; set; } = string.Empty;

        [JsonPropertyName("matchingSkills")]
        public List<string> MatchingSkills { get; set; } = new();

        [JsonPropertyName("missingSkills")]
        public List<string> MissingSkills { get; set; } = new();

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Wraps the result with the original inputs for rendering on the Dashboard.
    /// </summary>
    public class ScreeningResponseViewModel
    {
        public string CandidateName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public ScreeningResult Result { get; set; } = new();
    }
}