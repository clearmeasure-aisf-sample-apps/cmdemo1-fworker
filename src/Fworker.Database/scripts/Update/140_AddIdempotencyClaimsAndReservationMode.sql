-- W9.10c (#551): the pending claim of the idempotency ledger, and the mode of a Copilot reservation.
-- factory.IdempotencyClaims holds the claim of a key whose side effect is being made (src/Fworker.Integrations/Commands/SqlCommandStores.cs): the key, the node and the lease epoch of the handling
-- that took it, and when it runs out. A claim that ran out belongs to a handling that died or was cut off; the next handling takes it over and looks for the effect before it calls again.
-- factory.CopilotReservations.Mode says what an unknown call was ('launch' a new task, 'resume' a follow-up comment, '' not known), so the reconciler knows what to look for.
-- Reserved range 140 to 149 (the plan, W9.10c). Both changes are expand-only and guarded.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[IdempotencyClaims] and adding [factory].[CopilotReservations].[Mode]'

	IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory]')

	IF OBJECT_ID(N'factory.IdempotencyClaims', N'U') IS NULL
	BEGIN
		CREATE TABLE factory.IdempotencyClaims (
			[Key] nvarchar(400) NOT NULL CONSTRAINT PK_IdempotencyClaims PRIMARY KEY,
			OwnerNode nvarchar(200) NOT NULL, OwnerEpoch bigint NOT NULL,
			ClaimedAt datetime2 NOT NULL, ExpiresAt datetime2 NOT NULL)
	END

	IF COL_LENGTH(N'factory.CopilotReservations', N'Mode') IS NULL
	BEGIN
		ALTER TABLE factory.CopilotReservations ADD Mode nvarchar(20) NOT NULL CONSTRAINT DF_CopilotReservations_Mode DEFAULT N''
	END

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
