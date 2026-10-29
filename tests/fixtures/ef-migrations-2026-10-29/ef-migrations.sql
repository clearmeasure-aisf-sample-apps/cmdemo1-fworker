-- What the 8 EF Core migrations of Fworker.Persistence (InitialCreate to MergeTrain, EF 10.0.14) executed on the tdd database before DbUp took over the schema (2026-10-29),
-- as `dotnet ef migrations script <previous> <this>` printed them. LegacyDatabaseUpgradeTests builds a database the old way from it. Do not edit.

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory];');

CREATE TABLE [factory].[CatalogCopies] (
    [Name] nvarchar(100) NOT NULL,
    [Json] nvarchar(max) NOT NULL,
    [ContentHash] nvarchar(64) NOT NULL,
    [SourceSha] nvarchar(64) NULL,
    [FetchedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_CatalogCopies] PRIMARY KEY ([Name])
);

CREATE TABLE [factory].[Correlations] (
    [Kind] nvarchar(32) NOT NULL,
    [Key] nvarchar(400) NOT NULL,
    [ItemKey] nvarchar(300) NOT NULL,
    [Generation] int NOT NULL,
    [Source] nvarchar(64) NOT NULL,
    [CreatedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_Correlations] PRIMARY KEY ([Kind], [Key])
);

CREATE TABLE [factory].[ItemHistory] (
    [Id] bigint NOT NULL IDENTITY,
    [ItemKey] nvarchar(300) NOT NULL,
    [Generation] int NOT NULL,
    [At] datetimeoffset NOT NULL,
    [Kind] nvarchar(16) NOT NULL,
    [Type] nvarchar(200) NOT NULL,
    [Detail] nvarchar(max) NULL,
    [SessionId] nvarchar(200) NULL,
    [MessageId] uniqueidentifier NULL,
    CONSTRAINT [PK_ItemHistory] PRIMARY KEY ([Id])
);

CREATE TABLE [factory].[OpenInstances] (
    [ItemKey] nvarchar(300) NOT NULL,
    [Generation] int NOT NULL,
    [OpenedAt] datetimeoffset NOT NULL,
    [ClosedAt] datetimeoffset NULL,
    CONSTRAINT [PK_OpenInstances] PRIMARY KEY ([ItemKey])
);

CREATE TABLE [factory].[ProjectionVersions] (
    [ItemKey] nvarchar(300) NOT NULL,
    [Target] nvarchar(64) NOT NULL,
    [AppliedVersion] bigint NOT NULL,
    [AppliedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_ProjectionVersions] PRIMARY KEY ([ItemKey], [Target])
);

CREATE TABLE [factory].[SessionMailbox] (
    [Id] bigint NOT NULL IDENTITY,
    [SessionId] nvarchar(200) NOT NULL,
    [ItemKey] nvarchar(300) NULL,
    [Kind] nvarchar(64) NOT NULL,
    [Payload] nvarchar(max) NULL,
    [CreatedAt] datetimeoffset NOT NULL,
    [ExpiresAt] datetimeoffset NOT NULL,
    [DeliveredAt] datetimeoffset NULL,
    CONSTRAINT [PK_SessionMailbox] PRIMARY KEY ([Id])
);

CREATE INDEX [IX_Correlations_ItemKey_Generation] ON [factory].[Correlations] ([ItemKey], [Generation]);

CREATE INDEX [IX_ItemHistory_At] ON [factory].[ItemHistory] ([At]);

CREATE INDEX [IX_ItemHistory_ItemKey_Generation_Id] ON [factory].[ItemHistory] ([ItemKey], [Generation], [Id]);

CREATE UNIQUE INDEX [IX_ItemHistory_MessageId_Kind] ON [factory].[ItemHistory] ([MessageId], [Kind]) WHERE [MessageId] IS NOT NULL;

CREATE INDEX [IX_SessionMailbox_ExpiresAt] ON [factory].[SessionMailbox] ([ExpiresAt]);

CREATE INDEX [IX_SessionMailbox_SessionId_Id] ON [factory].[SessionMailbox] ([SessionId], [Id]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261027210741_InitialCreate', N'10.0.14');

COMMIT;
GO

BEGIN TRANSACTION;
CREATE TABLE [factory].[DeliveredTo] (
    [ItemKey] nvarchar(300) NOT NULL,
    [Environment] nvarchar(32) NOT NULL,
    [Release] nvarchar(200) NOT NULL,
    [DeliveredAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_DeliveredTo] PRIMARY KEY ([ItemKey], [Environment])
);

CREATE TABLE [factory].[MergedItems] (
    [ItemKey] nvarchar(300) NOT NULL,
    [Commit] nvarchar(40) NOT NULL,
    [PullRequest] int NOT NULL,
    [GatesJson] nvarchar(max) NOT NULL,
    [DependenciesJson] nvarchar(max) NOT NULL,
    [Source] nvarchar(16) NOT NULL,
    [MergedAt] datetimeoffset NOT NULL,
    [MessageId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_MergedItems] PRIMARY KEY ([ItemKey], [Commit])
);

CREATE INDEX [IX_MergedItems_Commit] ON [factory].[MergedItems] ([Commit]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261028084639_DeliveryTables', N'10.0.14');

COMMIT;
GO

BEGIN TRANSACTION;
CREATE TABLE [factory].[DeliveryTrains] (
    [Key] nvarchar(300) NOT NULL,
    [Revision] int NOT NULL,
    [StateJson] nvarchar(max) NOT NULL,
    [UpdatedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_DeliveryTrains] PRIMARY KEY ([Key])
);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261028092853_DeliveryTrain', N'10.0.14');

COMMIT;
GO

BEGIN TRANSACTION;
CREATE TABLE [factory].[DeliveryShadowComparisons] (
    [Id] bigint NOT NULL IDENTITY,
    [At] datetimeoffset NOT NULL,
    [Deployable] nvarchar(100) NOT NULL,
    [Environment] nvarchar(32) NOT NULL,
    [Release] nvarchar(200) NOT NULL,
    [Outcome] nvarchar(16) NOT NULL,
    [Reason] nvarchar(2000) NOT NULL,
    [V1Action] nvarchar(32) NOT NULL,
    [V2Action] nvarchar(32) NOT NULL,
    [V1Json] nvarchar(max) NULL,
    [V2Json] nvarchar(max) NULL,
    [V1At] datetimeoffset NULL,
    [V2At] datetimeoffset NULL,
    CONSTRAINT [PK_DeliveryShadowComparisons] PRIMARY KEY ([Id])
);

CREATE INDEX [IX_DeliveryShadowComparisons_At] ON [factory].[DeliveryShadowComparisons] ([At]);

CREATE INDEX [IX_DeliveryShadowComparisons_Outcome_At] ON [factory].[DeliveryShadowComparisons] ([Outcome], [At]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261028102121_DeliveryShadow', N'10.0.14');

COMMIT;
GO

BEGIN TRANSACTION;
ALTER TABLE [factory].[DeliveryShadowComparisons] ADD [Replay] bit NOT NULL DEFAULT CAST(0 AS bit);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261028140000_DeliveryShadowReplay', N'10.0.14');

COMMIT;
GO

BEGIN TRANSACTION;
CREATE TABLE [factory].[WorkItems] (
    [ItemKey] nvarchar(300) NOT NULL,
    [Repository] nvarchar(200) NOT NULL,
    [Generation] int NOT NULL,
    [Revision] int NOT NULL,
    [Status] nvarchar(16) NOT NULL,
    [Plan] nvarchar(32) NOT NULL,
    [Step] nvarchar(32) NOT NULL,
    [StateJson] nvarchar(max) NOT NULL,
    [UpdatedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_WorkItems] PRIMARY KEY ([ItemKey])
);

CREATE INDEX [IX_WorkItems_Repository_Status] ON [factory].[WorkItems] ([Repository], [Status]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261028232838_WorkItemSaga', N'10.0.14');

COMMIT;
GO

BEGIN TRANSACTION;
CREATE TABLE [factory].[CapacityLedgers] (
    [Key] nvarchar(100) NOT NULL,
    [Revision] int NOT NULL,
    [StateJson] nvarchar(max) NOT NULL,
    [UpdatedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_CapacityLedgers] PRIMARY KEY ([Key])
);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261029040624_CapacityLedger', N'10.0.14');

COMMIT;
GO

BEGIN TRANSACTION;
CREATE TABLE [factory].[MergeTrains] (
    [Key] nvarchar(200) NOT NULL,
    [Revision] int NOT NULL,
    [StateJson] nvarchar(max) NOT NULL,
    [UpdatedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_MergeTrains] PRIMARY KEY ([Key])
);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261029040842_MergeTrain', N'10.0.14');

COMMIT;
GO
