-- The incident saga's table (W8.0 for W8.1; the number is the reserved range 100-109 of W8.1): one row per application and environment, the key "deployable/environment" in lower case, holding
-- the current or last incident of that pair (the open-instance index of wolverine-factory.md 3.3 is this row: a status other than quiet is an open incident). The state is a JSON document owned
-- by Fworker.Workflows.Incident; the columns beside it are the ones read without opening it. The incident timeline is not a table of its own: it is written to factory.ItemHistory. Expand-only: a new table.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[Incidents]'

	IF OBJECT_ID(N'factory.Incidents', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[Incidents] (
			[Key] nvarchar(200) NOT NULL,
			[Generation] int NOT NULL,
			[Revision] int NOT NULL,
			[Status] nvarchar(16) NOT NULL,
			[HotfixItem] nvarchar(300) NULL,
			[StateJson] nvarchar(max) NOT NULL,
			[UpdatedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_Incidents] PRIMARY KEY ([Key])
		)
	END

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Incidents_HotfixItem' AND object_id = OBJECT_ID(N'factory.Incidents'))
		CREATE INDEX [IX_Incidents_HotfixItem] ON [factory].[Incidents] ([HotfixItem])

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Incidents_Status' AND object_id = OBJECT_ID(N'factory.Incidents'))
		CREATE INDEX [IX_Incidents_Status] ON [factory].[Incidents] ([Status])

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
