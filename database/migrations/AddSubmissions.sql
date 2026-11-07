-- EF Core 10.0.12, InitialBaseline -> 20261107144141_AddSubmissions.
-- In SSMS select the intended database and fill in its name here before running.
DECLARE @ExpectedDatabase sysname = N'';

IF @ExpectedDatabase = N'' OR DB_NAME() <> @ExpectedDatabase
    THROW 50001, N'Select the intended database and set @ExpectedDatabase.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50002, N'The existing CodeRank migration history is required.', 1;
IF @@TRANCOUNT <> 0
    THROW 50008, N'Run AddSubmissions without an open transaction.', 1;

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [dbo].[__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261031164420_InitialBaseline'
    )
        THROW 50003, N'Apply the SQL-first baseline before AddSubmissions.', 1;

    IF EXISTS (
        SELECT 1 FROM [dbo].[__EFMigrationsHistory] WITH (UPDLOCK, HOLDLOCK)
        WHERE [MigrationId] = N'20261107144141_AddSubmissions'
    )
    BEGIN
        IF OBJECT_ID(N'dbo.Submissions', N'U') IS NULL
            THROW 50004, N'Migration history and schema disagree. Check with the DBA.', 1;
    END
    ELSE
    BEGIN
        IF OBJECT_ID(N'dbo.Submissions') IS NOT NULL
            THROW 50005, N'Submissions already exists without its migration record.', 1;
        IF OBJECT_ID(N'dbo.Users', N'U') IS NULL OR OBJECT_ID(N'dbo.Olympiads', N'U') IS NULL
            OR OBJECT_ID(N'dbo.Problems', N'U') IS NULL
            THROW 50006, N'The SQL-first Users, Olympiads and Problems tables are required.', 1;

        CREATE TABLE [dbo].[Submissions] (
            [Id] bigint NOT NULL IDENTITY,
            [UserId] bigint NOT NULL,
            [OlympiadId] bigint NOT NULL,
            [ProblemId] bigint NOT NULL,
            [Language] varchar(16) NOT NULL,
            [SourceCode] nvarchar(max) NOT NULL,
            [Status] varchar(32) NOT NULL CONSTRAINT [DF_Submissions_Status] DEFAULT 'Queued',
            [SubmittedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_Submissions_SubmittedAtUtc] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_Submissions] PRIMARY KEY ([Id]),
            CONSTRAINT [CK_Submissions_Language] CHECK ([Language] IN ('cpp17', 'python3')),
            CONSTRAINT [CK_Submissions_SourceCode] CHECK (DATALENGTH([SourceCode]) BETWEEN 2 AND 131072),
            CONSTRAINT [CK_Submissions_Status] CHECK ([Status] IN ('Queued', 'Running', 'Accepted', 'WrongAnswer', 'CompilationError', 'RuntimeError', 'TimeLimitExceeded', 'MemoryLimitExceeded', 'SystemError')),
            CONSTRAINT [FK_Submissions_Olympiads] FOREIGN KEY ([OlympiadId]) REFERENCES [dbo].[Olympiads] ([Id]),
            CONSTRAINT [FK_Submissions_Problems] FOREIGN KEY ([ProblemId]) REFERENCES [dbo].[Problems] ([Id]),
            CONSTRAINT [FK_Submissions_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([Id])
        );

        CREATE INDEX [IX_Submissions_ProblemId] ON [dbo].[Submissions] ([ProblemId]);

        CREATE INDEX [IX_Submissions_Status_Id] ON [dbo].[Submissions] ([Status], [Id]);

        CREATE INDEX [IX_Submissions_User_Time_Id] ON [dbo].[Submissions] ([UserId], [SubmittedAtUtc] DESC, [Id] DESC);

        INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261107144141_AddSubmissions', N'10.0.12');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
