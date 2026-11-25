using System.ComponentModel.DataAnnotations;
using JobDist.Contracts;
using JobDist.Web.Localization;

namespace JobDist.Web.Components;

/// <summary>X2 form model.</summary>
public sealed class RecipientModel : IValidatableObject
{
    /// <summary>true for a new recipient (address required).</summary>
    public bool IsNew { get; init; }

    [Required(ErrorMessageResourceType = typeof(Messages), ErrorMessageResourceName = nameof(Messages.Required))]
    [StringLength(200)]
    public string? Name { get; set; }

    [Required(ErrorMessageResourceType = typeof(Messages), ErrorMessageResourceName = nameof(Messages.Required))]
    [StringLength(50)]
    public string? Channel { get; set; }

    [StringLength(512)]
    public string? Address { get; set; }

    public IEnumerable<AlertEventType> EventTypes { get; set; } = [];

    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Warning;

    public bool Enabled { get; set; } = true;

    public long? Version { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IsNew && string.IsNullOrWhiteSpace(Address))
        {
            yield return new ValidationResult(AppText.Get("Recipient.AddressRequired"), [nameof(Address)]);
        }
        if (!EventTypes.Any())
        {
            yield return new ValidationResult(AppText.Get("Recipient.EventsRequired"), [nameof(EventTypes)]);
        }
    }

    public static RecipientModel From(AlertRecipientResponse r) => new()
    {
        Name = r.Name, Channel = r.Channel, EventTypes = r.EventTypes.ToList(), MinSeverity = r.MinSeverity, Enabled = r.Enabled, Version = r.Version
    };

    public AlertRecipientRequest ToRequest() =>
        new(Name!.Trim(), Channel!.Trim(), string.IsNullOrWhiteSpace(Address) ? null : Address.Trim(), EventTypes.Distinct().ToList(), MinSeverity, Enabled, Version);
}

/// <summary>X6 form model.</summary>
public sealed class ThresholdModel
{
    [Required(ErrorMessageResourceType = typeof(Messages), ErrorMessageResourceName = nameof(Messages.Required))]
    [StringLength(100)]
    [RegularExpression(@"^(\*|[A-Za-z0-9][A-Za-z0-9._\-]*)$", ErrorMessageResourceType = typeof(Messages), ErrorMessageResourceName = nameof(Messages.JobTypeFormat))]
    public string? JobType { get; set; }

    [Range(1, 10_000_000)]
    public int MaxPending { get; set; } = 1_000;

    [Range(1, 99)]
    public int WarnPercent { get; set; } = 80;

    public bool Enabled { get; set; } = true;

    public long? Version { get; set; }

    public static ThresholdModel From(QueueThresholdResponse t) => new() { JobType = t.JobType, MaxPending = t.MaxPending, WarnPercent = t.WarnPercent, Enabled = t.Enabled, Version = t.Version };

    public QueueThresholdRequest ToRequest() => new(JobType!.Trim(), MaxPending, WarnPercent, Enabled, Version);
}
