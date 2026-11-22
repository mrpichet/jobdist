namespace JobDist.Contracts;

/// <summary>Lifecycle state of a job.</summary>
public enum JobStatus
{
    /// <summary>Waiting in the queue to be scheduled (also used for a retry that waits for its back-off).</summary>
    Pending = 0,
    /// <summary>Scheduler picked a worker; the worker has not started it yet.</summary>
    Assigned = 1,
    /// <summary>Worker reported that execution started.</summary>
    Running = 2,
    /// <summary>Finished successfully.</summary>
    Succeeded = 3,
    /// <summary>Finished with an error and no retries left (or a non-retryable error).</summary>
    Failed = 4,
    /// <summary>Cancellation requested; waiting for the worker to acknowledge.</summary>
    CancelRequested = 5,
    /// <summary>Cancelled.</summary>
    Cancelled = 6
}

/// <summary>State of a registered worker.</summary>
public enum WorkerStatus
{
    /// <summary>Heartbeats arrive in time; can receive jobs.</summary>
    Online = 0,
    /// <summary>Finishing current jobs, receives no new jobs (graceful shutdown / maintenance).</summary>
    Draining = 1,
    /// <summary>Heartbeat timed out (network loss / crash). Its jobs were re-distributed.</summary>
    Offline = 2,
    /// <summary>Worker unregistered itself or was removed by an administrator.</summary>
    Unregistered = 3
}

/// <summary>Outcome reported by a worker for one execution attempt.</summary>
public enum AttemptOutcome
{
    /// <summary>Still running.</summary>
    InProgress = 0,
    /// <summary>Handler completed successfully.</summary>
    Succeeded = 1,
    /// <summary>Handler failed; the job may be retried if <c>Retryable</c> was set.</summary>
    Failed = 2,
    /// <summary>Handler exceeded its timeout.</summary>
    TimedOut = 3,
    /// <summary>Handler was cancelled on request.</summary>
    Cancelled = 4,
    /// <summary>Worker was lost (heartbeat timeout / lease expired) - set by the server.</summary>
    WorkerLost = 5,
    /// <summary>Worker unregistered while holding the job - set by the server.</summary>
    Released = 6
}

/// <summary>Command that the server pushes to a worker through heartbeat / poll responses.</summary>
public enum WorkerCommandKind
{
    /// <summary>Cancel a running job.</summary>
    CancelJob = 1,
    /// <summary>Stop taking new jobs and finish current ones.</summary>
    Drain = 2
}
