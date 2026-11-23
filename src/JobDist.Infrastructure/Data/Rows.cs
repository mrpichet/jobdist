namespace JobDist.Infrastructure.Data;

/// <summary>Generic key/count row for GROUP BY queries (key read as text so int and string keys share one shape).</summary>
internal sealed class CountRow
{
    public string GroupKey { get; set; } = string.Empty;
    public long Count { get; set; }
}
