using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;

namespace JobDist.Core.Services;

/// <summary>Cluster overview for dashboards, the UI and AI tools.</summary>
public sealed class ClusterService(IJobDAO jobs, IWorkerDAO workers, LeaderElectionService leader, PerformanceLearningService model, TimeProvider time)
{
    /// <summary>
    /// Builds a summary: workers by status, capacity, jobs by status, pending per type and the current leader.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ClusterSummaryDTO> GetSummaryAsync(CancellationToken ct)
    {
        // Step 1: three cheap aggregate queries.
        var list = await workers.ListAsync(false, ct).ConfigureAwait(false);
        var byStatus = await jobs.CountByStatusAsync(ct).ConfigureAwait(false);
        var pending = await jobs.PendingByTypeAsync(ct).ConfigureAwait(false);

        // Step 2: derive figures.
        var online = list.Where(w => w.Status == WorkerStatus.Online).ToList();
        var active = byStatus.GetValueOrDefault(JobStatus.Assigned) + byStatus.GetValueOrDefault(JobStatus.Running) + byStatus.GetValueOrDefault(JobStatus.CancelRequested);
        return new ClusterSummaryDTO(
            online.Count,
            list.Count(w => w.Status == WorkerStatus.Draining),
            list.Count(w => w.Status == WorkerStatus.Offline),
            online.Sum(w => w.MaxTotalConcurrency),
            active,
            Enum.GetValues<JobStatus>().ToDictionary(s => s.ToString(), s => byStatus.GetValueOrDefault(s)),
            pending,
            leader.CurrentLeader,
            leader.IsLeader,
            time.GetUtcNow());
    }

    /// <summary>Learned performance statistics (ML model snapshot).</summary>
    public IReadOnlyList<PerformanceStatDTO> GetPerformance(string? jobType) =>
        model.Snapshot()
            .Where(s => jobType is null || string.Equals(s.JobType, jobType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.JobType).ThenBy(s => s.MeanDurationMs)
            .ToList();
}
