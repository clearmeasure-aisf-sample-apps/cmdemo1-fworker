-- The capacity scheduler's ledger, as the EF migration CapacityLedger built it.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[CapacityLedgers]'

	IF OBJECT_ID(N'factory.CapacityLedgers', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[CapacityLedgers] (
			[Key] nvarchar(100) NOT NULL,
			[Revision] int NOT NULL,
			[StateJson] nvarchar(max) NOT NULL,
			[UpdatedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_CapacityLedgers] PRIMARY KEY ([Key])
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
