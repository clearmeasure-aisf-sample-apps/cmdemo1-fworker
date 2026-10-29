-- Generated from WolverineFx 6.56.0 / Weasel 9.47.0, schema wolverine, by Gateway.Tests WolverineStorageScriptTests (FWORKER_WRITE_WOLVERINE_SQL=1); do not edit.
SET QUOTED_IDENTIFIER ON;


IF NOT EXISTS ( SELECT  *
                FROM    sys.schemas
                WHERE   name = N'wolverine' )
    EXEC('CREATE SCHEMA [wolverine]');


IF OBJECT_ID('wolverine.wolverine_outgoing_envelopes') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_outgoing_envelopes (
    id              uniqueidentifier    NOT NULL,
    owner_id        int                 NOT NULL,
    destination     varchar(250)        NOT NULL,
    deliver_by      datetimeoffset      NULL,
    body            varbinary(max)      NOT NULL,
    attempts        int                 NULL DEFAULT 0,
    message_type    varchar(250)        NOT NULL,
CONSTRAINT pkey_wolverine_outgoing_envelopes_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_outgoing_envelopes_owner' AND object_id = OBJECT_ID(N'wolverine.wolverine_outgoing_envelopes'))
    CREATE INDEX idx_wolverine_outgoing_envelopes_owner ON wolverine.wolverine_outgoing_envelopes (owner_id) WHERE ([owner_id]<>0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_outgoing_envelopes_recover' AND object_id = OBJECT_ID(N'wolverine.wolverine_outgoing_envelopes'))
    CREATE INDEX idx_wolverine_outgoing_envelopes_recover ON wolverine.wolverine_outgoing_envelopes (destination) WHERE ([owner_id]=0);
IF OBJECT_ID('wolverine.wolverine_incoming_envelopes') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_incoming_envelopes (
    id                uniqueidentifier    NOT NULL,
    status            varchar(25)         NOT NULL,
    owner_id          int                 NOT NULL,
    execution_time    datetimeoffset      NULL DEFAULT NULL,
    attempts          int                 NULL DEFAULT 0,
    body              varbinary(max)      NOT NULL,
    message_type      varchar(250)        NOT NULL,
    received_at       varchar(250)        NOT NULL,
    keep_until        datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_incoming_envelopes_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_owner' AND object_id = OBJECT_ID(N'wolverine.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_owner ON wolverine.wolverine_incoming_envelopes (owner_id) WHERE ([owner_id]<>0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_recover' AND object_id = OBJECT_ID(N'wolverine.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_recover ON wolverine.wolverine_incoming_envelopes (received_at) WHERE ([status]='Incoming' AND [owner_id]=0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_keep_until' AND object_id = OBJECT_ID(N'wolverine.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_keep_until ON wolverine.wolverine_incoming_envelopes (keep_until) WHERE ([status]='Handled');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_incoming_envelopes_scheduled' AND object_id = OBJECT_ID(N'wolverine.wolverine_incoming_envelopes'))
    CREATE INDEX idx_wolverine_incoming_envelopes_scheduled ON wolverine.wolverine_incoming_envelopes (execution_time) WHERE ([status]='Scheduled');
IF OBJECT_ID('wolverine.wolverine_dead_letters') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_dead_letters (
    id                   uniqueidentifier    NOT NULL,
    execution_time       datetimeoffset      NULL DEFAULT NULL,
    body                 varbinary(max)      NOT NULL,
    message_type         varchar(250)        NOT NULL,
    received_at          varchar(250)        NOT NULL,
    source               varchar(250)        NULL,
    exception_type       varchar(max)        NULL,
    exception_message    varchar(max)        NULL,
    sent_at              datetimeoffset      NULL,
    replayable           bit                 NULL,
    failure_group        varchar(100)        NULL,
    failure_title        nvarchar(500)       NULL,
    failed_at            datetimeoffset      NULL,
    first_failed_at      datetimeoffset      NULL,
    replay_count         int                 NULL,
    last_replayed_at     datetimeoffset      NULL,
    archived_at          datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_dead_letters_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_dead_letters_replayable' AND object_id = OBJECT_ID(N'wolverine.wolverine_dead_letters'))
    CREATE INDEX idx_wolverine_dead_letters_replayable ON wolverine.wolverine_dead_letters (replayable) WHERE ([replayable]=1);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_dead_letters_failure_group' AND object_id = OBJECT_ID(N'wolverine.wolverine_dead_letters'))
    CREATE INDEX idx_wolverine_dead_letters_failure_group ON wolverine.wolverine_dead_letters (failure_group);
IF OBJECT_ID('wolverine.wolverine_dead_letter_history') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_dead_letter_history (
    id                  uniqueidentifier    NOT NULL,
    replay_count        int                 NULL,
    last_replayed_at    datetimeoffset      NULL,
    first_failed_at     datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_dead_letter_history_id PRIMARY KEY (id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_wolverine_dead_letter_history_last_replayed_at' AND object_id = OBJECT_ID(N'wolverine.wolverine_dead_letter_history'))
    CREATE INDEX idx_wolverine_dead_letter_history_last_replayed_at ON wolverine.wolverine_dead_letter_history (last_replayed_at);
IF TYPE_ID(N'wolverine.EnvelopeIdList') IS NULL
CREATE TYPE wolverine.EnvelopeIdList AS TABLE (id UNIQUEIDENTIFIER NOT NULL)
GO
CREATE OR ALTER PROCEDURE wolverine.uspDeleteIncomingEnvelopes
    @IDLIST wolverine.EnvelopeIdList READONLY
AS

DELETE
FROM wolverine.wolverine_incoming_envelopes
WHERE id IN (SELECT ID FROM @IDLIST);

GO
GO
CREATE OR ALTER PROCEDURE wolverine.uspDeleteOutgoingEnvelopes
    @IDLIST wolverine.EnvelopeIdList READONLY
AS

DELETE
FROM wolverine.wolverine_outgoing_envelopes
WHERE id IN (SELECT ID FROM @IDLIST);

GO
GO
CREATE OR ALTER PROCEDURE wolverine.uspDiscardAndReassignOutgoing
    @DISCARDS wolverine.EnvelopeIdList READONLY,
    @REASSIGNED wolverine.EnvelopeIdList READONLY,
    @OWNERID INT

AS

DELETE
FROM wolverine.wolverine_outgoing_envelopes
WHERE id IN (SELECT ID FROM @DISCARDS);

UPDATE wolverine.wolverine_outgoing_envelopes
SET owner_id = @OWNERID
WHERE ID IN (SELECT ID FROM @REASSIGNED);

GO
GO
CREATE OR ALTER PROCEDURE wolverine.uspMarkIncomingOwnership
    @IDLIST wolverine.EnvelopeIdList READONLY,
    @owner INT
AS

-- GH-4216: without the status predicate this also rewrites rows that are already Incoming or Handled.
-- The load side of the poll selects only Scheduled rows, so constraining the update to match costs nothing
-- and keeps the promotion from touching a row it never selected.
UPDATE wolverine.wolverine_incoming_envelopes
SET owner_id = @owner, status = 'Incoming'
WHERE status = 'Scheduled' AND id IN (SELECT ID FROM @IDLIST);

GO
GO
CREATE OR ALTER PROCEDURE wolverine.uspMarkOutgoingOwnership
    @IDLIST wolverine.EnvelopeIdList READONLY,
    @owner INT
AS

UPDATE wolverine.wolverine_outgoing_envelopes
SET owner_id = @owner
WHERE id IN (SELECT ID FROM @IDLIST);

GO
IF OBJECT_ID('wolverine.wolverine_nodes') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_nodes (
    id              uniqueidentifier    NOT NULL,
    node_number     int                 NOT NULL IDENTITY,
    description     varchar(max)        NOT NULL,
    uri             varchar(500)        NOT NULL,
    started         datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
    health_check    datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
    version         varchar(500)        NULL,
    capabilities    nvarchar(max)       NULL,
CONSTRAINT pkey_wolverine_nodes_id PRIMARY KEY (id)
);
END
IF OBJECT_ID('wolverine.wolverine_node_assignments') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_node_assignments (
    id         varchar(500)        NOT NULL,
    node_id    uniqueidentifier    NULL,
    started    datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
CONSTRAINT pkey_wolverine_node_assignments_id PRIMARY KEY (id)
);
END

IF OBJECT_ID(N'wolverine.fkey_wolverine_node_assignments_node_id', N'F') IS NULL
ALTER TABLE wolverine.wolverine_node_assignments
ADD CONSTRAINT fkey_wolverine_node_assignments_node_id FOREIGN KEY(node_id)
 REFERENCES wolverine.wolverine_nodes(id)ON DELETE CASCADE
;

IF OBJECT_ID('wolverine.wolverine_control_queue') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_control_queue (
    id              uniqueidentifier    NOT NULL,
    message_type    varchar(500)        NOT NULL,
    node_id         uniqueidentifier    NOT NULL,
    body            varbinary(max)      NOT NULL,
    posted          datetimeoffset      NOT NULL DEFAULT GETUTCDATE(),
    expires         datetimeoffset      NULL,
CONSTRAINT pkey_wolverine_control_queue_id PRIMARY KEY (id)
);
END
IF OBJECT_ID('wolverine.wolverine_agent_restrictions') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_agent_restrictions (
    id      uniqueidentifier    NOT NULL,
    uri     varchar(500)        NOT NULL,
    type    varchar(500)        NOT NULL,
    node    int                 NOT NULL DEFAULT 0,
CONSTRAINT pkey_wolverine_agent_restrictions_id PRIMARY KEY (id)
);
END
IF OBJECT_ID('wolverine.wolverine_node_records') IS NULL
BEGIN
CREATE TABLE wolverine.wolverine_node_records (
    id             int               NOT NULL IDENTITY,
    node_number    int               NOT NULL,
    event_name     varchar(500)      NOT NULL,
    timestamp      datetimeoffset    NOT NULL DEFAULT GETUTCDATE(),
    description    varchar(1000)     NULL,
CONSTRAINT pkey_wolverine_node_records_id PRIMARY KEY (id)
);
END
