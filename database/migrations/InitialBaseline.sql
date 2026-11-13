
-- Select the approved database in the connection and set its name below.
DECLARE @ExpectedDatabase sysname = N'';

IF @ExpectedDatabase = N'' OR DB_NAME() <> @ExpectedDatabase OR DB_ID() <= 4
    THROW 50000, N'Select the intended user database and set @ExpectedDatabase.', 1;
IF @@TRANCOUNT <> 0
    THROW 50001, N'Run the baseline without an open transaction.', 1;

SET IMPLICIT_TRANSACTIONS OFF;
IF EXISTS (
    SELECT 1
    FROM (VALUES (N'Users'), (N'UserProfiles'), (N'Olympiads'), (N'Problems'),
                 (N'OlympiadProblems'), (N'ProblemExamples')) AS required_tables(TableName)
    WHERE OBJECT_ID(N'dbo.' + required_tables.TableName, N'U') IS NULL
)
    THROW 50002, N'The six SQL-first CodeRank tables are required before the baseline.', 1;

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[__EFMigrationsHistory] (
            [MigrationId] nvarchar(150) NOT NULL,
            [ProductVersion] nvarchar(32) NOT NULL,
            CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM [dbo].[__EFMigrationsHistory] WITH (UPDLOCK, HOLDLOCK)
        WHERE [MigrationId] = N'20261031164420_InitialBaseline'
    )
    BEGIN
        INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261031164420_InitialBaseline', N'10.0.12');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

