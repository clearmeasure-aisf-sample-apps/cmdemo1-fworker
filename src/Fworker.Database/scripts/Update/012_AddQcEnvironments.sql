-- The QC gate's state table (W9.1): one row per repository and pull request, the key "owner/repo!N" in lower case. Expand-only: a new table.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[QcEnvironments]'

	IF OBJECT_ID(N'factory.QcEnvironments', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[QcEnvironments] (
			[PullRequestKey] nvarchar(300) NOT NULL,
			[Repository] nvarchar(200) NOT NULL,
			[ItemKey] nvarchar(300) NULL,
			[Revision] int NOT NULL,
			[Status] nvarchar(16) NOT NULL,
			[StateJson] nvarchar(max) NOT NULL,
			[UpdatedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_QcEnvironments] PRIMARY KEY ([PullRequestKey])
		)
	END

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QcEnvironments_Repository_Status' AND object_id = OBJECT_ID(N'factory.QcEnvironments'))
		CREATE INDEX [IX_QcEnvironments_Repository_Status] ON [factory].[QcEnvironments] ([Repository], [Status])

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
