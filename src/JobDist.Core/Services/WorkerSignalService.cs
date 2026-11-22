using System.Collections.Concurrent;

namespace JobDist.Core.Services;

/// <summary>
/// In-process wake-up signals for worker long-polls: when the scheduler (or a cancel) produces work for a worker, its
/// waiting poll request returns immediately instead of waiting for the next periodic check. Lock-free.
/// Across server instances the poll loop also re-checks the database periodically, so signals are an optimisation only.
/// </summary>
public sealed class WorkerSignalService
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _signals = new();

    /// <summary>Wakes the poll request of a worker (if one is waiting on this instance).</summary>
    /// <param name="workerId">Worker.</param>
    public void Notify(Guid workerId)
    {
        if (_signals.TryRemove(workerId, out var tcs))
        {
            tcs.TrySetResult();
        }
    }

    /// <summary>
    /// Waits until <see cref="Notify"/> is called for the worker or the timeout elapses.
    /// </summary>
    /// <param name="workerId">Worker.</param>
    /// <param name="timeout">Max wait.</param>
    /// <param name="ct">Cancellation (request aborted).</param>
    /// <returns><c>true</c> when signalled, <c>false</c> on timeout.</returns>
    public async Task<bool> WaitAsync(Guid workerId, TimeSpan timeout, CancellationToken ct)
    {
        // Step 1: share one completion source per worker (a worker has at most a few concurrent polls).
        var tcs = _signals.GetOrAdd(workerId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        // Step 2: wait for signal, timeout or cancellation.
        try
        {
            await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
