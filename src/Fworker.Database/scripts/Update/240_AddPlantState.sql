-- The plant's state (W9.10k; the number is the reserved range 240-249 of W9.10k): what the plant would lose in a restart, so that it comes back from the database and not from the snapshot ref of the work-orders repository.
-- PlantAssignments: the offers and accepted assignments of items (the Id is the one the workers' item tokens name), one row per assignment. PlantHolders: the owner session of an item (Kind 'owner') and the bot holder of
-- an item (Kind 'bot'). PlantDrains: the names the operator drained (Kind 'operator') and the names the plant manager drained (Kind 'manager'). PlantState: one row ('plant') with the rest of the hand-over as a JSON
-- document (the manager's mode, the day of its last summary, the digest's carry, the leases the last sweep saw, the Copilot and Cursor owners) and the epoch of the term that wrote it: a write of an older epoch is refused.
-- The gateway replaces the rows in one transaction, and corrects what it reads at start from WorkItems and the claim refs; the durable truth stays the board, the state comments and the claim refs. Expand-only: new tables.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[PlantAssignments], [factory].[PlantHolders], [factory].[PlantDrains], [factory].[PlantState]'

	IF OBJECT_ID(N'factory.PlantAssignments', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[PlantAssignments] (
			[Id] nvarchar(60) NOT NULL,
			[Item] int NOT NULL,
			[Kind] nvarchar(20) NOT NULL,
			[Role] nvarchar(60) NOT NULL,
			[BoardColumn] nvarchar(100) NOT NULL,
			[Mode] nvarchar(60) NOT NULL,
			[Reason] nvarchar(60) NOT NULL,
			[Worker] nvarchar(100) NOT NULL,
			[PullRequest] int NOT NULL,
			[Pin] nvarchar(100) NOT NULL,
			[Accepted] bit NOT NULL,
			[AcceptedAt] datetimeoffset NULL,
			[CreatedAt] datetimeoffset NOT NULL,
			[SlotId] nvarchar(100) NOT NULL,
			[SlotRuntime] nvarchar(100) NOT NULL,
			[ArmId] nvarchar(100) NOT NULL,
			CONSTRAINT [PK_PlantAssignments] PRIMARY KEY ([Id])
		)
	END

	IF OBJECT_ID(N'factory.PlantHolders', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[PlantHolders] (
			[Item] int NOT NULL,
			[Kind] nvarchar(10) NOT NULL,
			[Holder] nvarchar(100) NOT NULL,
			CONSTRAINT [PK_PlantHolders] PRIMARY KEY ([Item], [Kind])
		)
	END

	IF OBJECT_ID(N'factory.PlantDrains', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[PlantDrains] (
			[Name] nvarchar(100) NOT NULL,
			[Kind] nvarchar(10) NOT NULL,
			CONSTRAINT [PK_PlantDrains] PRIMARY KEY ([Name], [Kind])
		)
	END

	IF OBJECT_ID(N'factory.PlantState', N'U') IS NULL
	BEGIN
		CREATE TABLE [factory].[PlantState] (
			[Name] nvarchar(40) NOT NULL,
			[Epoch] bigint NOT NULL,
			[Holder] nvarchar(100) NOT NULL,
			[SavedAt] datetimeoffset NOT NULL,
			[Document] nvarchar(max) NOT NULL,
			CONSTRAINT [PK_PlantState] PRIMARY KEY ([Name])
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
