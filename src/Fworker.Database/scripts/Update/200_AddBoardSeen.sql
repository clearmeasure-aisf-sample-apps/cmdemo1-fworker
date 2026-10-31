-- What the board reconciler last saw of each card (W9.10g; the number is the reserved range 200-209 of W9.10g): one row per card, the issue's repository ("owner/repo", lower case) and number, the Status column it
-- stood in and whether the issue was open. The reconciler publishes a card whose column or state differs from its row (or that has none), so a restart no longer forgets what it had seen: a card that closed while
-- no gateway was up is still news (its merge is derived from it), and a closed card with no row is history. The gateway writes the changes of a pass in one transaction and removes the rows of cards that left the
-- board. Expand-only: a new table.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[BoardSeen]'

	IF OBJECT_ID(N'factory.BoardSeen', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[BoardSeen] (
			[Repository] nvarchar(200) NOT NULL,
			[Number] int NOT NULL,
			[StatusColumn] nvarchar(200) NULL,
			[IsOpen] bit NOT NULL,
			[SeenAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_BoardSeen] PRIMARY KEY ([Repository], [Number])
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
