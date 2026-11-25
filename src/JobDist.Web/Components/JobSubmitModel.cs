using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using JobDist.Contracts;
using JobDist.Web.Localization;

namespace JobDist.Web.Components;

/// <summary>Form model of S03 (same rules as <see cref="SubmitJobRequest"/>; the server checks again).</summary>
public sealed class JobSubmitModel : IValidatableObject
{
    [Required(ErrorMessageResourceType = typeof(Messages), ErrorMessageResourceName = nameof(Messages.Required))]
    [StringLength(100)]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", ErrorMessageResourceType = typeof(Messages), ErrorMessageResourceName = nameof(Messages.JobTypeFormat))]
    public string? JobType { get; set; }

    [StringLength(1_048_576)]
    public string? Payload { get; set; }

    [Range(0, 100)]
    public int Priority { get; set; } = 50;

    public Guid? TargetWorkerId { get; set; }

    public bool StrictTarget { get; set; }

    [Range(1, 20)]
    public int? MaxAttempts { get; set; }

    [Range(1, 86_400)]
    public int? TimeoutSeconds { get; set; }

    public DateTime? RunAtLocal { get; set; }

    [StringLength(100)]
    public string? IdempotencyKey { get; set; }

    public bool Sensitive { get; set; }

    [StringLength(1_100)]
    public string? Tags { get; set; }

    /// <summary>Tags split by comma.</summary>
    public IReadOnlyList<string> TagList => (Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(Payload))
        {
            string? error = null;
            try
            {
                using var _ = JsonDocument.Parse(Payload);
            }
            catch (JsonException)
            {
                error = AppText.Get("Job.Submit.PayloadInvalid");
            }
            if (error is not null)
            {
                yield return new ValidationResult(error, [nameof(Payload)]);
            }
        }
        if (TagList.Count > 10 || TagList.Any(t => t.Length > 50))
        {
            yield return new ValidationResult(AppText.Get("Job.Submit.TagsInvalid"), [nameof(Tags)]);
        }
        if (StrictTarget && TargetWorkerId is null)
        {
            yield return new ValidationResult(AppText.Get("Job.Submit.StrictNeedsWorker"), [nameof(TargetWorkerId)]);
        }
    }

    /// <summary>Builds the API request.</summary>
    public SubmitJobRequest ToRequest() => new(
        JobType!.Trim(),
        string.IsNullOrWhiteSpace(Payload) ? null : Payload,
        Priority,
        TargetWorkerId,
        StrictTarget && TargetWorkerId is not null,
        MaxAttempts,
        TimeoutSeconds,
        RunAtLocal is { } r ? new DateTimeOffset(DateTime.SpecifyKind(r, DateTimeKind.Local)).ToUniversalTime() : null,
        string.IsNullOrWhiteSpace(IdempotencyKey) ? null : IdempotencyKey.Trim(),
        Sensitive,
        TagList.Count == 0 ? null : TagList);
}

/// <summary>Validation messages for DataAnnotations (resource-backed so they follow the UI language).</summary>
public static class Messages
{
    public static string Required => AppText.Get("Validation.Required");
    public static string JobTypeFormat => AppText.Get("Validation.JobTypeFormat");
}
