using System.Collections.Concurrent;

namespace JobDist.Core.Services;

/// <summary>
/// Counts rejected worker reports (stale attempt, wrong worker, bad token) per worker in a sliding window. A burst indicates a
/// broken worker or an attack and is reported by anomaly detection. Lock-free, in-memory, per instance.
/// </summary>
public sealed class WorkerReportTracker(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _events = new();

    /// <summary>Records a rejected report.</summary>
    /// <param name="subject">Worker id or client name.</param>
    public void Rejected(string subject)
    {
        var q = _events.GetOrAdd(subject, _ => new ConcurrentQueue<DateTimeOffset>());
        q.Enqueue(time.GetUtcNow());
        while (q.Count > 10_000 && q.TryDequeue(out _))
        {
        }
    }

    /// <summary>Counts per subject since a time (old entries are pruned).</summary>
    public IReadOnlyDictionary<string, int> CountsSince(DateTimeOffset since)
    {
        var result = new Dictionary<string, int>();
        foreach (var (subject, q) in _events)
        {
            while (q.TryPeek(out var first) && first < since)
            {
                q.TryDequeue(out _);
            }
            if (!q.IsEmpty)
            {
                result[subject] = q.Count;
            }
        }
        return result;
    }
}
