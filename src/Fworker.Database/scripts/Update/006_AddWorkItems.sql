-- The work item saga's table, as the EF migration WorkItemSaga built it.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[WorkItems]'

	IF OBJECT_ID(N'factory.WorkItems', N'U') IS NULL
	BEGIN
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
		)
	END

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WorkItems_Repository_Status' AND object_id = OBJECT_ID(N'factory.WorkItems'))
		CREATE INDEX [IX_WorkItems_Repository_Status] ON [factory].[WorkItems] ([Repository], [Status])

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
