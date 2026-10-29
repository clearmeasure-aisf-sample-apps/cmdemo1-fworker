-- The ingress dedup table (src/Fworker.Integrations/Correlation/IngressDedup.cs), which used to be created on first use.
BEGIN TRY
	BEGIN TRANSACTION
	PRINT N'Creating [factory].[IngressDedup]'

	IF SCHEMA_ID(N'factory') IS NULL EXEC(N'CREATE SCHEMA [factory]')

	IF OBJECT_ID(N'factory.IngressDedup', N'U') IS NULL
	BEGIN
		CREATE TABLE factory.IngressDedup (
			Layer nvarchar(16) NOT NULL, [Key] nvarchar(400) NOT NULL, MessageId uniqueidentifier NOT NULL, ClaimedAt datetime2 NOT NULL,
			CONSTRAINT PK_IngressDedup PRIMARY KEY (Layer, [Key]))
	END

	PRINT 'The database update succeeded'
	COMMIT TRANSACTION
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
	THROW;
END CATCH
GO
