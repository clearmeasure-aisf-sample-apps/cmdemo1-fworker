-- The replay flag, as the EF migration DeliveryShadowReplay added it. The default constraint is left unnamed, as EF left it.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Adding Replay to [factory].[DeliveryShadowComparisons]'

	IF COL_LENGTH(N'factory.DeliveryShadowComparisons', N'Replay') IS NULL
		ALTER TABLE [factory].[DeliveryShadowComparisons] ADD [Replay] bit NOT NULL DEFAULT CAST(0 AS bit)

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
