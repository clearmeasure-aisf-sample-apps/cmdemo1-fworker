-- The delivery train's state table, as the EF migration DeliveryTrain built it.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[DeliveryTrains]'

	IF OBJECT_ID(N'factory.DeliveryTrains', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[DeliveryTrains] (
			[Key] nvarchar(300) NOT NULL,
			[Revision] int NOT NULL,
			[StateJson] nvarchar(max) NOT NULL,
			[UpdatedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_DeliveryTrains] PRIMARY KEY ([Key])
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
