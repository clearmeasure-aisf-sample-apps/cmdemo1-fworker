-- The shadow run's comparisons, as the EF migration DeliveryShadow built them.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[DeliveryShadowComparisons]'

	IF OBJECT_ID(N'factory.DeliveryShadowComparisons', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[DeliveryShadowComparisons] (
			[Id] bigint NOT NULL IDENTITY,
			[At] datetimeoffset NOT NULL,
			[Deployable] nvarchar(100) NOT NULL,
			[Environment] nvarchar(32) NOT NULL,
			[Release] nvarchar(200) NOT NULL,
			[Outcome] nvarchar(16) NOT NULL,
			[Reason] nvarchar(2000) NOT NULL,
			[V1Action] nvarchar(32) NOT NULL,
			[V2Action] nvarchar(32) NOT NULL,
			[V1Json] nvarchar(max) NULL,
			[V2Json] nvarchar(max) NULL,
			[V1At] datetimeoffset NULL,
			[V2At] datetimeoffset NULL,
			CONSTRAINT [PK_DeliveryShadowComparisons] PRIMARY KEY ([Id])
		)
	END

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DeliveryShadowComparisons_At' AND object_id = OBJECT_ID(N'factory.DeliveryShadowComparisons'))
		CREATE INDEX [IX_DeliveryShadowComparisons_At] ON [factory].[DeliveryShadowComparisons] ([At])

	IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DeliveryShadowComparisons_Outcome_At' AND object_id = OBJECT_ID(N'factory.DeliveryShadowComparisons'))
		CREATE INDEX [IX_DeliveryShadowComparisons_Outcome_At] ON [factory].[DeliveryShadowComparisons] ([Outcome], [At])

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
