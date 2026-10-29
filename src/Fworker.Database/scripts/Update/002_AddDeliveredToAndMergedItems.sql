-- The delivery tables, as the EF migration DeliveryTables built them.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[DeliveredTo] and [factory].[MergedItems]'

	IF OBJECT_ID(N'factory.DeliveredTo', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[DeliveredTo] (
			[ItemKey] nvarchar(300) NOT NULL,
			[Environment] nvarchar(32) NOT NULL,
			[Release] nvarchar(200) NOT NULL,
			[DeliveredAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_DeliveredTo] PRIMARY KEY ([ItemKey], [Environment])
		)
	END

	IF OBJECT_ID(N'factory.MergedItems', N'U') IS NULL
	BEGIN
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
		)
	END

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MergedItems_Commit' AND object_id = OBJECT_ID(N'factory.MergedItems'))
		CREATE INDEX [IX_MergedItems_Commit] ON [factory].[MergedItems] ([Commit])

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
