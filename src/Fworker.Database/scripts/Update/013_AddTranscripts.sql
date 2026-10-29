-- The session transcripts of runtime workers (W9.8), which the plant snapshot's ref store held until now: one row per session name, the item qualified
-- ("owner/repo#N"). Expand-only: a new table.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[Transcripts]'

	IF OBJECT_ID(N'factory.Transcripts', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[Transcripts] (
			[Name] nvarchar(100) NOT NULL,
			[ItemKey] nvarchar(300) NULL,
			[Runtime] nvarchar(32) NOT NULL,
			[Phase] nvarchar(32) NOT NULL,
			[StartedAt] datetimeoffset NOT NULL,
			[LastLineAt] datetimeoffset NOT NULL,
			[EndedAt] datetimeoffset NULL,
			[Revision] int NOT NULL,
			[Bytes] bigint NOT NULL,
			[LinesTotal] bigint NOT NULL,
			[LinesDropped] bigint NOT NULL,
			[LinesJson] nvarchar(max) NOT NULL,
			[UpdatedAt] datetimeoffset NOT NULL,
			CONSTRAINT [PK_Transcripts] PRIMARY KEY ([Name])
		)
	END

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transcripts_ItemKey_StartedAt' AND object_id = OBJECT_ID(N'factory.Transcripts'))
		CREATE INDEX [IX_Transcripts_ItemKey_StartedAt] ON [factory].[Transcripts] ([ItemKey], [StartedAt])

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transcripts_StartedAt' AND object_id = OBJECT_ID(N'factory.Transcripts'))
		CREATE INDEX [IX_Transcripts_StartedAt] ON [factory].[Transcripts] ([StartedAt])

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
