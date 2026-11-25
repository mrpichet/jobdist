using JobDist.Core.DAO;
using JobDist.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>
/// HA leader election over a database lease. Every server instance serves the REST / MCP API (active-active), but cluster-wide
/// background duties (scheduling, fail-over detection, anomaly scans, model refresh) run on exactly one leader.
/// The leader renews its lease every TTL/3; if it crashes, another instance takes over within one TTL.
/// Correctness does not depend on the lease alone: all state changes are conditional updates (status + version checks),
/// so even two overlapping leaders cannot double-assign a job.
/// </summary>
public sealed class LeaderElectionService(ILeaderLeaseDAO leases, AlertDispatchService alerts, IOptions<JobDistOptions> options, TimeProvider time, ILogger<LeaderElectionService> logger)
{
    /// <summary>Lease name used for the scheduler leader.</summary>
    public const string LeaseName = "jobdist-leader";

    private volatile bool _isLeader;
    private volatile string? _currentLeader;
    private long _term;

    /// <summary><c>true</c> while this instance holds a valid lease.</summary>
    public bool IsLeader => _isLeader;

    /// <summary>Instance id of the current leader as last observed.</summary>
    public string? CurrentLeader => _currentLeader;

    /// <summary>Lease term as last observed (grows by one whenever the leader changes; fencing token).</summary>
    public long Term => Interlocked.Read(ref _term);

    /// <summary>This instance's id.</summary>
    public string InstanceId => options.Value.InstanceId;

    /// <summary>
    /// Acquires or renews leadership once.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when this instance is the leader after the call.</returns>
    public async Task<bool> TryAcquireOrRenewAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(options.Value.LeaderLeaseSeconds);
        try
        {
            // Step 1: conditional upsert of the lease row.
            var acquired = await leases.TryAcquireAsync(LeaseName, InstanceId, now, now + ttl, ct).ConfigureAwait(false);

            // Step 2: log transitions only.
            var becameLeader = acquired && !_isLeader;
            if (acquired != _isLeader)
            {
                logger.LogInformation(acquired ? "Instance {Instance} became leader." : "Instance {Instance} lost leadership.", InstanceId);
            }
            _isLeader = acquired;

            // Step 3: remember who leads (for dashboards) and the term.
            if (!acquired || becameLeader)
            {
                var lease = await leases.GetAsync(LeaseName, ct).ConfigureAwait(false);
                _currentLeader = acquired ? InstanceId : lease?.HolderId;
                Interlocked.Exchange(ref _term, lease?.Term ?? 0);
            }
            else
            {
                _currentLeader = InstanceId;
            }

            // Step 4: alert on a leader change; term 1 is the very first leader (no alert). Event key = term → sent once.
            if (becameLeader && Term > 1)
            {
                await alerts.TryPublishAsync(new Domain.AlertEventDTO($"LeaderChanged:{Term}", Contracts.AlertEventType.LeaderChanged, Contracts.AlertSeverity.Warning,
                    "cluster:leader", $"[JobDist][Warning] Primary server changed to {InstanceId}",
                    $"Server instance {InstanceId} became the primary (leader) at {now:yyyy-MM-dd HH:mm:ss} UTC, term {Term}. " +
                    "The previous primary stopped renewing its lease (crash, network loss or shutdown).", now), ct).ConfigureAwait(false);
            }
            return acquired;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Step 5: if the database is unreachable we must assume we are no longer leader (fencing).
            logger.LogWarning(ex, "Leader lease renewal failed; stepping down.");
            _isLeader = false;
            return false;
        }
    }

    /// <summary>Releases the lease on graceful shutdown so another instance takes over immediately.</summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        if (_isLeader)
        {
            _isLeader = false;
            try
            {
                await leases.ReleaseAsync(LeaseName, InstanceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Leader lease release failed; it will expire on its own.");
            }
        }
    }
}
