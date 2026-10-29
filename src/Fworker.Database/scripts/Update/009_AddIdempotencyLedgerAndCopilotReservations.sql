-- The tables of the command stores (src/Fworker.Integrations/Commands/SqlCommandStores.cs), which used to create them on first use.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[IdempotencyLedger] and [factory].[CopilotReservations]'

	IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory]')

	IF OBJECT_ID(N'factory.IdempotencyLedger', N'U') IS NULL
	BEGIN
		CREATE TABLE factory.IdempotencyLedger (
			[Key] nvarchar(400) NOT NULL CONSTRAINT PK_IdempotencyLedger PRIMARY KEY,
			Ok bit NOT NULL, Failure int NOT NULL, Error nvarchar(max) NOT NULL, Execution nvarchar(max) NOT NULL, Url nvarchar(max) NOT NULL,
			RecordedAt datetime2 NOT NULL)
	END

	IF OBJECT_ID(N'factory.CopilotReservations', N'U') IS NULL
	BEGIN
		CREATE TABLE factory.CopilotReservations (
			Item int NOT NULL, Phase nvarchar(100) NOT NULL, Worker nvarchar(200) NOT NULL, State int NOT NULL, At datetime2 NOT NULL,
			TaskId nvarchar(200) NOT NULL, Url nvarchar(max) NOT NULL,
			CONSTRAINT PK_CopilotReservations PRIMARY KEY (Item, Phase))
	END

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
