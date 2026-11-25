using System.ComponentModel.DataAnnotations;

namespace JobDist.Web.Components;

/// <summary>S05 form model.</summary>
public sealed class PriorityModel
{
    [Range(0, 100)]
    public int Priority { get; set; }
}

/// <summary>S06 form model.</summary>
public sealed class CancelModel
{
    [StringLength(500)]
    public string? Reason { get; set; }
}

/// <summary>S09 / S10 confirmation form model (no fields; the page shows what will happen).</summary>
public sealed class ConfirmModel
{
}
