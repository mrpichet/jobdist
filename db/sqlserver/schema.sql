-- =============================================================================================
-- JobDist schema for SQL Server (idempotent; executed at start-up when Database:AutoMigrate=true).
-- Batches are separated by GO lines.
-- =============================================================================================
IF OBJECT_ID(N'dbo.Workers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Workers
    (
        WorkerId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Workers PRIMARY KEY,
        Name                NVARCHAR(200)    NOT NULL,
        Status              INT              NOT NULL,
        Capabilities        NVARCHAR(MAX)    NOT NULL,   -- JSON array of {JobType, MaxConcurrency, SpeedFactor}
        Spec                NVARCHAR(2000)   NOT NULL,   -- JSON {CpuCores, MemoryMb, GpuCount, BenchmarkScore, Os}
        MaxTotalConcurrency INT              NOT NULL,
        TokenHash           VARBINARY(32)    NOT NULL,   -- SHA-256 of the worker token (token never stored)
        EcdhPublicKey       NVARCHAR(512)    NULL,
        MlKemPublicKey      VARCHAR(2048)    NULL,
        Version             NVARCHAR(50)     NULL,
        Tags                NVARCHAR(2000)   NOT NULL,
        RegisteredUtc       DATETIMEOFFSET(3) NOT NULL,
        LastHeartbeatUtc    DATETIMEOFFSET(3) NOT NULL,
        CpuUsagePercent     FLOAT            NOT NULL CONSTRAINT DF_Workers_Cpu DEFAULT 0,
        MemoryUsagePercent  FLOAT            NOT NULL CONSTRAINT DF_Workers_Mem DEFAULT 0,
        OfflineCount        INT              NOT NULL CONSTRAINT DF_Workers_Offline DEFAULT 0,
        RegisteredBy        NVARCHAR(200)    NULL
    );
    CREATE UNIQUE INDEX UX_Workers_Name ON dbo.Workers(Name);
    CREATE INDEX IX_Workers_Status_Heartbeat ON dbo.Workers(Status, LastHeartbeatUtc);
END
GO
IF OBJECT_ID(N'dbo.Jobs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Jobs
    (
        JobId              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Jobs PRIMARY KEY NONCLUSTERED,
        JobType            NVARCHAR(100)    NOT NULL,
        Payload            NVARCHAR(MAX)    NULL,
        ProtectedPayload   NVARCHAR(MAX)    NULL,       -- AES-256-GCM (sensitive jobs)
        PayloadSha256      CHAR(64)         NOT NULL,
        PayloadSize        INT              NOT NULL,
        Status             INT              NOT NULL,
        Priority           INT              NOT NULL,
        TargetWorkerId     UNIQUEIDENTIFIER NULL,
        StrictTarget       BIT              NOT NULL,
        AssignedWorkerId   UNIQUEIDENTIFIER NULL,
        CurrentAttemptId   UNIQUEIDENTIFIER NULL,
        Attempt            INT              NOT NULL,
        MaxAttempts        INT              NOT NULL,
        TimeoutSeconds     INT              NOT NULL,
        CreatedUtc         DATETIMEOFFSET(3) NOT NULL,
        RunAtUtc           DATETIMEOFFSET(3) NULL,
        NextAttemptUtc     DATETIMEOFFSET(3) NOT NULL,
        AssignedUtc        DATETIMEOFFSET(3) NULL,
        DeliveredUtc       DATETIMEOFFSET(3) NULL,
        LeaseExpiresUtc    DATETIMEOFFSET(3) NULL,
        StartedUtc         DATETIMEOFFSET(3) NULL,
        CompletedUtc       DATETIMEOFFSET(3) NULL,
        LastDurationMs     FLOAT            NULL,
        Result             NVARCHAR(MAX)    NULL,
        LastError          NVARCHAR(4000)   NULL,
        CancelReason       NVARCHAR(500)    NULL,
        LastFailedWorkerId UNIQUEIDENTIFIER NULL,
        IdempotencyKey     NVARCHAR(100)    NULL,
        SubmittedBy        NVARCHAR(200)    NULL,
        Sensitive          BIT              NOT NULL,
        Tags               NVARCHAR(2000)   NOT NULL,
        Version            BIGINT           NOT NULL
    );
    -- Clustered on creation time: inserts append at the end (no page splits with sequential v7 GUIDs either).
    CREATE CLUSTERED INDEX CIX_Jobs_Created ON dbo.Jobs(CreatedUtc);
    -- Ready queue (filtered: only pending rows are indexed, stays small even with millions of finished jobs).
    CREATE INDEX IX_Jobs_Ready ON dbo.Jobs(NextAttemptUtc, Priority DESC) INCLUDE (Version) WHERE Status = 0;
    -- Jobs held by workers (assignment delivery, fail-over, load).
    CREATE INDEX IX_Jobs_Held ON dbo.Jobs(AssignedWorkerId, Status) INCLUDE (JobType, LeaseExpiresUtc, DeliveredUtc) WHERE AssignedWorkerId IS NOT NULL;
    CREATE INDEX IX_Jobs_Lease ON dbo.Jobs(LeaseExpiresUtc) WHERE LeaseExpiresUtc IS NOT NULL;
    CREATE UNIQUE INDEX UX_Jobs_Idempotency ON dbo.Jobs(SubmittedBy, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;
    CREATE INDEX IX_Jobs_Status_Type ON dbo.Jobs(Status, JobType);
    CREATE INDEX IX_Jobs_Completed ON dbo.Jobs(CompletedUtc) WHERE CompletedUtc IS NOT NULL;
END
GO
IF OBJECT_ID(N'dbo.JobAttempts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JobAttempts
    (
        AttemptId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_JobAttempts PRIMARY KEY NONCLUSTERED,
        JobId              UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_JobAttempts_Jobs REFERENCES dbo.Jobs(JobId) ON DELETE CASCADE,
        Attempt            INT              NOT NULL,
        WorkerId           UNIQUEIDENTIFIER NOT NULL,
        JobType            NVARCHAR(100)    NOT NULL,
        Priority           INT              NOT NULL,
        PayloadSize        INT              NOT NULL,
        AssignedUtc        DATETIMEOFFSET(3) NOT NULL,
        StartedUtc         DATETIMEOFFSET(3) NULL,
        FinishedUtc        DATETIMEOFFSET(3) NULL,
        DurationMs         FLOAT            NULL,
        QueueWaitMs        FLOAT            NOT NULL,
        Outcome            INT              NOT NULL,
        Error              NVARCHAR(4000)   NULL,
        SchedulerScore     FLOAT            NOT NULL,
        WorkerLoadAtAssign FLOAT            NOT NULL
    );
    CREATE CLUSTERED INDEX CIX_JobAttempts_Assigned ON dbo.JobAttempts(AssignedUtc);
    CREATE INDEX IX_JobAttempts_Job ON dbo.JobAttempts(JobId);
    CREATE INDEX IX_JobAttempts_Finished ON dbo.JobAttempts(FinishedUtc) INCLUDE (WorkerId, JobType, Outcome, DurationMs) WHERE FinishedUtc IS NOT NULL;
END
GO
IF OBJECT_ID(N'dbo.WorkerCommands', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WorkerCommands
    (
        CommandId    UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_WorkerCommands PRIMARY KEY,
        WorkerId     UNIQUEIDENTIFIER NOT NULL,
        Kind         INT              NOT NULL,
        JobId        UNIQUEIDENTIFIER NULL,
        Reason       NVARCHAR(500)    NULL,
        IssuedUtc    DATETIMEOFFSET(3) NOT NULL,
        DeliveredUtc DATETIMEOFFSET(3) NULL,
        AckedUtc     DATETIMEOFFSET(3) NULL
    );
    CREATE INDEX IX_WorkerCommands_Pending ON dbo.WorkerCommands(WorkerId, DeliveredUtc) WHERE AckedUtc IS NULL;
    CREATE INDEX IX_WorkerCommands_Job ON dbo.WorkerCommands(JobId) WHERE JobId IS NOT NULL;
END
GO
IF OBJECT_ID(N'dbo.LeaderLeases', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LeaderLeases
    (
        Name       NVARCHAR(100)     NOT NULL CONSTRAINT PK_LeaderLeases PRIMARY KEY,
        HolderId   NVARCHAR(200)     NOT NULL,
        ExpiresUtc DATETIMEOFFSET(3) NOT NULL
    );
END
GO
-- ---------------------------------------------------------------------------------------------
-- Web console additions (2026-11): fencing term, alert recipients, alert outbox, queue thresholds, idempotency records.
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.LeaderLeases', N'Term') IS NULL
    ALTER TABLE dbo.LeaderLeases ADD Term BIGINT NOT NULL CONSTRAINT DF_LeaderLeases_Term DEFAULT 0;
GO
IF OBJECT_ID(N'dbo.AlertRecipients', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AlertRecipients
    (
        RecipientId      UNIQUEIDENTIFIER  NOT NULL CONSTRAINT PK_AlertRecipients PRIMARY KEY,
        Name             NVARCHAR(200)     NOT NULL,
        Channel          NVARCHAR(50)      NOT NULL,
        AddressProtected NVARCHAR(MAX)     NOT NULL,
        AddressMasked    NVARCHAR(600)     NOT NULL,
        EventTypes       NVARCHAR(500)     NOT NULL,
        MinSeverity      INT               NOT NULL,
        Enabled          BIT               NOT NULL,
        CreatedUtc       DATETIMEOFFSET(3) NOT NULL,
        UpdatedUtc       DATETIMEOFFSET(3) NOT NULL,
        UpdatedBy        NVARCHAR(200)     NULL,
        Version          BIGINT            NOT NULL
    );
END
GO
IF OBJECT_ID(N'dbo.AlertOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AlertOutbox
    (
        OutboxId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertOutbox PRIMARY KEY,
        EventKey       NVARCHAR(300)     NOT NULL,
        RecipientId    UNIQUEIDENTIFIER  NOT NULL,
        RecipientName  NVARCHAR(200)     NULL,
        Channel        NVARCHAR(50)      NOT NULL,
        AddressMasked  NVARCHAR(600)     NOT NULL,
        EventType      INT               NOT NULL,
        Severity       INT               NOT NULL,
        Subject        NVARCHAR(300)     NOT NULL,
        Title          NVARCHAR(300)     NOT NULL,
        Body           NVARCHAR(MAX)     NOT NULL,
        OccurredUtc    DATETIMEOFFSET(3) NOT NULL,
        CreatedUtc     DATETIMEOFFSET(3) NOT NULL,
        Status         INT               NOT NULL,
        Attempts       INT               NOT NULL CONSTRAINT DF_AlertOutbox_Attempts DEFAULT 0,
        NextAttemptUtc DATETIMEOFFSET(3) NOT NULL,
        LockedBy       NVARCHAR(200)     NULL,
        LockedUntilUtc DATETIMEOFFSET(3) NULL,
        SentUtc        DATETIMEOFFSET(3) NULL,
        CorrelationId  NVARCHAR(100)     NULL,
        LastError      NVARCHAR(1000)    NULL,
        InstanceId     NVARCHAR(200)     NULL
    );
    CREATE UNIQUE INDEX UX_AlertOutbox_Event ON dbo.AlertOutbox(EventKey, RecipientId);
    CREATE INDEX IX_AlertOutbox_Due ON dbo.AlertOutbox(NextAttemptUtc, OutboxId) WHERE Status = 0;
    CREATE INDEX IX_AlertOutbox_Created ON dbo.AlertOutbox(CreatedUtc);
END
GO
IF OBJECT_ID(N'dbo.QueueThresholds', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QueueThresholds
    (
        ThresholdId UNIQUEIDENTIFIER  NOT NULL CONSTRAINT PK_QueueThresholds PRIMARY KEY,
        JobType     NVARCHAR(100)     NOT NULL,
        MaxPending  INT               NOT NULL,
        WarnPercent INT               NOT NULL,
        Enabled     BIT               NOT NULL,
        UpdatedBy   NVARCHAR(200)     NULL,
        UpdatedUtc  DATETIMEOFFSET(3) NOT NULL,
        Version     BIGINT            NOT NULL
    );
    CREATE UNIQUE INDEX UX_QueueThresholds_Type ON dbo.QueueThresholds(JobType);
END
GO
IF OBJECT_ID(N'dbo.IdempotencyRecords', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IdempotencyRecords
    (
        Principal      NVARCHAR(200)     NOT NULL,
        Route          NVARCHAR(300)     NOT NULL,
        IdempotencyKey NVARCHAR(100)     NOT NULL,
        RequestHash    CHAR(64)          NOT NULL,
        StatusCode     INT               NOT NULL,
        ResponseBody   NVARCHAR(MAX)     NULL,
        CreatedUtc     DATETIMEOFFSET(3) NOT NULL,
        ExpiresUtc     DATETIMEOFFSET(3) NOT NULL,
        CONSTRAINT PK_IdempotencyRecords PRIMARY KEY (Principal, Route, IdempotencyKey)
    );
    CREATE INDEX IX_IdempotencyRecords_Expires ON dbo.IdempotencyRecords(ExpiresUtc);
END
GO
