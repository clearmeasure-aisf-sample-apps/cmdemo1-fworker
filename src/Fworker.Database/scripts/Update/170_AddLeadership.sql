-- The leadership row (W9.10b; the number is the reserved range 170-179 of W9.10b): the one row (Id 1) beside the SQL application lock "fworker:leader" that fences the gateway's single actor. The lock itself is
-- not in a table (sp_getapplock, owner Session, on a dedicated connection: it ends with the connection); this row says whose term it is. Epoch grows by one each time a process takes the lock, Holder is the
-- process of the term, and TakeoverBy / TakeoverAt are the request of a newer process that wants the lock (the holder stops its node, saves the plant and releases; a request that is not refreshed within a
-- few seconds is stale and ignored). The gateway writes the row (insert on the first term, update after); the script only builds the table. Expand-only: a new table.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[Leadership]'

	IF OBJECT_ID(N'factory.Leadership', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[Leadership] (
			[Id] int NOT NULL,
			[Epoch] bigint NOT NULL,
			[Holder] nvarchar(100) NOT NULL,
			[AcquiredAt] datetimeoffset NOT NULL,
			[TakeoverBy] nvarchar(100) NULL,
			[TakeoverAt] datetimeoffset NULL,
			CONSTRAINT [PK_Leadership] PRIMARY KEY ([Id])
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
