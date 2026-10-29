-- The gateway's tables, schema [factory], as the EF migration InitialCreate built them (guarded: a no-op where they exist).
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating schema [factory] and its first tables'

	IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory]')

	IF OBJECT_ID(N'factory.CatalogCopies', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[CatalogCopies] (
			[Name] nvarchar(100) NOT NULL,
			[Json] nvarchar(max) NOT NULL,
			[ContentHash] nvarchar(64) NOT NULL,
			[SourceSha] nvarchar(64) NULL,
			[FetchedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_CatalogCopies] PRIMARY KEY ([Name])
		)
	END

	IF OBJECT_ID(N'factory.Correlations', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[Correlations] (
			[Kind] nvarchar(32) NOT NULL,
			[Key] nvarchar(400) NOT NULL,
			[ItemKey] nvarchar(300) NOT NULL,
			[Generation] int NOT NULL,
			[Source] nvarchar(64) NOT NULL,
			[CreatedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_Correlations] PRIMARY KEY ([Kind], [Key])
		)
	END

	IF OBJECT_ID(N'factory.ItemHistory', N'U') IS NULL
	BEGIN
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
		)
	END

	IF OBJECT_ID(N'factory.OpenInstances', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[OpenInstances] (
			[ItemKey] nvarchar(300) NOT NULL,
			[Generation] int NOT NULL,
			[OpenedAt] datetimeoffset NOT NULL,
			[ClosedAt] datetimeoffset NULL,
			CONSTRAINT [PK_OpenInstances] PRIMARY KEY ([ItemKey])
		)
	END

	IF OBJECT_ID(N'factory.ProjectionVersions', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[ProjectionVersions] (
			[ItemKey] nvarchar(300) NOT NULL,
			[Target] nvarchar(64) NOT NULL,
			[AppliedVersion] bigint NOT NULL,
			[AppliedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_ProjectionVersions] PRIMARY KEY ([ItemKey], [Target])
		)
	END

	IF OBJECT_ID(N'factory.SessionMailbox', N'U') IS NULL
	BEGIN
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
		)
	END

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Correlations_ItemKey_Generation' AND object_id = OBJECT_ID(N'factory.Correlations'))
		CREATE INDEX [IX_Correlations_ItemKey_Generation] ON [factory].[Correlations] ([ItemKey], [Generation])

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemHistory_At' AND object_id = OBJECT_ID(N'factory.ItemHistory'))
		CREATE INDEX [IX_ItemHistory_At] ON [factory].[ItemHistory] ([At])

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemHistory_ItemKey_Generation_Id' AND object_id = OBJECT_ID(N'factory.ItemHistory'))
		CREATE INDEX [IX_ItemHistory_ItemKey_Generation_Id] ON [factory].[ItemHistory] ([ItemKey], [Generation], [Id])

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemHistory_MessageId_Kind' AND object_id = OBJECT_ID(N'factory.ItemHistory'))
		CREATE UNIQUE INDEX [IX_ItemHistory_MessageId_Kind] ON [factory].[ItemHistory] ([MessageId], [Kind]) WHERE [MessageId] IS NOT NULL

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SessionMailbox_ExpiresAt' AND object_id = OBJECT_ID(N'factory.SessionMailbox'))
		CREATE INDEX [IX_SessionMailbox_ExpiresAt] ON [factory].[SessionMailbox] ([ExpiresAt])

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SessionMailbox_SessionId_Id' AND object_id = OBJECT_ID(N'factory.SessionMailbox'))
		CREATE INDEX [IX_SessionMailbox_SessionId_Id] ON [factory].[SessionMailbox] ([SessionId], [Id])

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
