-- The engine's markers (W9.9b; the number is the reserved range 290-299 of W9.9b): when the v2 engine last acted and when a process that was not the engine ran with the database configured. The adoption at every start
-- (Fworker.Workflows.Adoption) reads them to tell whether v1 may have acted on the items since v2 last did (the kill switch FACTORY_V2_OFF was on, or the gateway was away longer than the break gap), which decides whether an item
-- the state comment changed is the next generation of its instance (W9.10n). Row 'acted': written by the acting process (a heartbeat every minute, At the time, Holder the process, Detail the epoch of its lease). Row 'v1-ran':
-- written at the start of a process on which the kill switch turns an environment of v2Environments back to v1. Both are only evidence for the adoption: the durable truth stays the board and the state comments. Expand-only: a new table.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[EngineMarkers]'

	IF OBJECT_ID(N'factory.EngineMarkers', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[EngineMarkers] (
			[Name] nvarchar(40) NOT NULL,
			[At] datetimeoffset NOT NULL,
			[Holder] nvarchar(100) NOT NULL,
			[Detail] nvarchar(200) NOT NULL,
			CONSTRAINT [PK_EngineMarkers] PRIMARY KEY ([Name])
		)
	END

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
