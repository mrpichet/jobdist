-- =============================================================================================
-- JobDist schema for PostgreSQL 14+ (idempotent; executed at start-up when Database:AutoMigrate=true).
-- Identifiers are unquoted (folded to lower case); Dapper maps them case-insensitively to the DTO properties.
-- =============================================================================================
CREATE TABLE IF NOT EXISTS workers
(
    workerid            UUID          NOT NULL PRIMARY KEY,
    name                VARCHAR(200)  NOT NULL,
    status              INT           NOT NULL,
    capabilities        TEXT          NOT NULL,   -- JSON array
    spec                TEXT          NOT NULL,   -- JSON object
    maxtotalconcurrency INT           NOT NULL,
    tokenhash           BYTEA         NOT NULL,   -- SHA-256 of the worker token
    ecdhpublickey       VARCHAR(512)  NULL,
    mlkempublickey      VARCHAR(2048) NULL,
    version             VARCHAR(50)   NULL,
    tags                TEXT          NOT NULL,
    registeredutc       TIMESTAMPTZ   NOT NULL,
    lastheartbeatutc    TIMESTAMPTZ   NOT NULL,
    cpuusagepercent     DOUBLE PRECISION NOT NULL DEFAULT 0,
    memoryusagepercent  DOUBLE PRECISION NOT NULL DEFAULT 0,
    offlinecount        INT           NOT NULL DEFAULT 0,
    registeredby        VARCHAR(200)  NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_workers_name ON workers(name);
CREATE INDEX IF NOT EXISTS ix_workers_status_heartbeat ON workers(status, lastheartbeatutc);

CREATE TABLE IF NOT EXISTS jobs
(
    jobid              UUID          NOT NULL PRIMARY KEY,
    jobtype            VARCHAR(100)  NOT NULL,
    payload            TEXT          NULL,
    protectedpayload   TEXT          NULL,
    payloadsha256      CHAR(64)      NOT NULL,
    payloadsize        INT           NOT NULL,
    status             INT           NOT NULL,
    priority           INT           NOT NULL,
    targetworkerid     UUID          NULL,
    stricttarget       BOOLEAN       NOT NULL,
    assignedworkerid   UUID          NULL,
    currentattemptid   UUID          NULL,
    attempt            INT           NOT NULL,
    maxattempts        INT           NOT NULL,
    timeoutseconds     INT           NOT NULL,
    createdutc         TIMESTAMPTZ   NOT NULL,
    runatutc           TIMESTAMPTZ   NULL,
    nextattemptutc     TIMESTAMPTZ   NOT NULL,
    assignedutc        TIMESTAMPTZ   NULL,
    deliveredutc       TIMESTAMPTZ   NULL,
    leaseexpiresutc    TIMESTAMPTZ   NULL,
    startedutc         TIMESTAMPTZ   NULL,
    completedutc       TIMESTAMPTZ   NULL,
    lastdurationms     DOUBLE PRECISION NULL,
    result             TEXT          NULL,
    lasterror          VARCHAR(4000) NULL,
    cancelreason       VARCHAR(500)  NULL,
    lastfailedworkerid UUID          NULL,
    idempotencykey     VARCHAR(100)  NULL,
    submittedby        VARCHAR(200)  NULL,
    sensitive          BOOLEAN       NOT NULL,
    tags               TEXT          NOT NULL,
    version            BIGINT        NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jobs_ready ON jobs(nextattemptutc, priority DESC) WHERE status = 0;
CREATE INDEX IF NOT EXISTS ix_jobs_held ON jobs(assignedworkerid, status) WHERE assignedworkerid IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_jobs_lease ON jobs(leaseexpiresutc) WHERE leaseexpiresutc IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_idempotency ON jobs(submittedby, idempotencykey) WHERE idempotencykey IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_jobs_status_type ON jobs(status, jobtype);
CREATE INDEX IF NOT EXISTS ix_jobs_created ON jobs(createdutc);
CREATE INDEX IF NOT EXISTS ix_jobs_completed ON jobs(completedutc) WHERE completedutc IS NOT NULL;

CREATE TABLE IF NOT EXISTS jobattempts
(
    attemptid          UUID          NOT NULL PRIMARY KEY,
    jobid              UUID          NOT NULL REFERENCES jobs(jobid) ON DELETE CASCADE,
    attempt            INT           NOT NULL,
    workerid           UUID          NOT NULL,
    jobtype            VARCHAR(100)  NOT NULL,
    priority           INT           NOT NULL,
    payloadsize        INT           NOT NULL,
    assignedutc        TIMESTAMPTZ   NOT NULL,
    startedutc         TIMESTAMPTZ   NULL,
    finishedutc        TIMESTAMPTZ   NULL,
    durationms         DOUBLE PRECISION NULL,
    queuewaitms        DOUBLE PRECISION NOT NULL,
    outcome            INT           NOT NULL,
    error              VARCHAR(4000) NULL,
    schedulerscore     DOUBLE PRECISION NOT NULL,
    workerloadatassign DOUBLE PRECISION NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jobattempts_job ON jobattempts(jobid);
CREATE INDEX IF NOT EXISTS ix_jobattempts_finished ON jobattempts(finishedutc) WHERE finishedutc IS NOT NULL;

CREATE TABLE IF NOT EXISTS workercommands
(
    commandid    UUID         NOT NULL PRIMARY KEY,
    workerid     UUID         NOT NULL,
    kind         INT          NOT NULL,
    jobid        UUID         NULL,
    reason       VARCHAR(500) NULL,
    issuedutc    TIMESTAMPTZ  NOT NULL,
    deliveredutc TIMESTAMPTZ  NULL,
    ackedutc     TIMESTAMPTZ  NULL
);
CREATE INDEX IF NOT EXISTS ix_workercommands_pending ON workercommands(workerid, deliveredutc) WHERE ackedutc IS NULL;
CREATE INDEX IF NOT EXISTS ix_workercommands_job ON workercommands(jobid) WHERE jobid IS NOT NULL;

CREATE TABLE IF NOT EXISTS leaderleases
(
    name       VARCHAR(100) NOT NULL PRIMARY KEY,
    holderid   VARCHAR(200) NOT NULL,
    expiresutc TIMESTAMPTZ  NOT NULL
);
