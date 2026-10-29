-- The merge train's state table, as the EF migration MergeTrain built it.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[MergeTrains]'

	IF OBJECT_ID(N'factory.MergeTrains', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[MergeTrains] (
			[Key] nvarchar(200) NOT NULL,
			[Revision] int NOT NULL,
			[StateJson] nvarchar(max) NOT NULL,
			[UpdatedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_MergeTrains] PRIMARY KEY ([Key])
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
