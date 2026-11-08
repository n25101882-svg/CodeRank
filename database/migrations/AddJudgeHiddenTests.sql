-- EF Core 10.0.12, AddJudgeLeases -> 20261108084926_AddJudgeHiddenTests.
-- Select the approved database in the connection and set its name below.
DECLARE @ExpectedDatabase sysname = N'';

IF @ExpectedDatabase = N'' OR DB_NAME() <> @ExpectedDatabase
    THROW 50021, N'Select the intended database and set @ExpectedDatabase.', 1;
IF @@TRANCOUNT <> 0
    THROW 50022, N'Run AddJudgeHiddenTests without an open transaction.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    OR OBJECT_ID(N'dbo.Problems', N'U') IS NULL
    OR OBJECT_ID(N'dbo.Submissions', N'U') IS NULL
    OR COL_LENGTH(N'dbo.Submissions', N'JudgeLeaseToken') IS NULL
    THROW 50023, N'The CodeRank schema and judge lease columns are required.', 1;

SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [dbo].[__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261107162834_AddJudgeLeases'
    )
        THROW 50024, N'Apply AddJudgeLeases before AddJudgeHiddenTests.', 1;

    DECLARE @AlreadyApplied bit = 0;
    IF EXISTS (
        SELECT 1 FROM [dbo].[__EFMigrationsHistory] WITH (UPDLOCK, HOLDLOCK)
        WHERE [MigrationId] = N'20261108084926_AddJudgeHiddenTests'
    )
        SET @AlreadyApplied = 1;

    DECLARE @CasesId int = OBJECT_ID(N'dbo.JudgeTestCases', N'U');
    DECLARE @SnapshotsId int = OBJECT_ID(N'dbo.JudgeTestSnapshots', N'U');

    IF @AlreadyApplied = 1
    BEGIN
        IF @CasesId IS NULL OR @SnapshotsId IS NULL
            OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = @CasesId
                AND name IN (N'Id', N'ProblemId', N'Position', N'Input', N'ExpectedOutput', N'IsActive')) <> 6
            OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = @SnapshotsId
                AND name IN (N'SubmissionId', N'TestCasesJson')) <> 2
            OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @CasesId
                AND name = N'UX_JudgeTestCases_Problem_Position' AND is_unique = 1 AND is_disabled = 0)
            OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = @CasesId
                AND name = N'FK_JudgeTestCases_Problems'
                AND referenced_object_id = OBJECT_ID(N'dbo.Problems', N'U') AND is_disabled = 0)
            OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = @SnapshotsId
                AND name = N'FK_JudgeTestSnapshots_Submissions'
                AND referenced_object_id = OBJECT_ID(N'dbo.Submissions', N'U') AND is_disabled = 0)
            THROW 50025, N'Migration history and schema disagree. Check with the DBA.', 1;
    END
    ELSE
    BEGIN
        IF @CasesId IS NOT NULL OR @SnapshotsId IS NOT NULL
            THROW 50026, N'Judge test tables already exist without their migration record.', 1;

        CREATE TABLE [dbo].[JudgeTestCases] (
            [Id] bigint NOT NULL IDENTITY,
            [ProblemId] bigint NOT NULL,
            [Position] int NOT NULL,
            [Input] nvarchar(max) NOT NULL,
            [ExpectedOutput] nvarchar(max) NOT NULL,
            [IsActive] bit NOT NULL,
            CONSTRAINT [PK_JudgeTestCases] PRIMARY KEY ([Id]),
            CONSTRAINT [CK_JudgeTestCases_InputSize] CHECK (DATALENGTH([Input]) <= 16384),
            CONSTRAINT [CK_JudgeTestCases_OutputSize] CHECK (DATALENGTH([ExpectedOutput]) <= 16384),
            CONSTRAINT [CK_JudgeTestCases_Position] CHECK ([Position] BETWEEN 1 AND 32),
            CONSTRAINT [FK_JudgeTestCases_Problems] FOREIGN KEY ([ProblemId]) REFERENCES [dbo].[Problems] ([Id]) ON DELETE CASCADE
        );

        CREATE TABLE [dbo].[JudgeTestSnapshots] (
            [SubmissionId] bigint NOT NULL,
            [TestCasesJson] nvarchar(max) NOT NULL,
            CONSTRAINT [PK_JudgeTestSnapshots] PRIMARY KEY ([SubmissionId]),
            CONSTRAINT [CK_JudgeTestSnapshots_JsonSize] CHECK (DATALENGTH([TestCasesJson]) BETWEEN 4 AND 802816),
            CONSTRAINT [FK_JudgeTestSnapshots_Submissions] FOREIGN KEY ([SubmissionId]) REFERENCES [dbo].[Submissions] ([Id])
        );

        -- Compile the index statement after the new table exists.
        EXEC sys.sp_executesql N'CREATE UNIQUE INDEX [UX_JudgeTestCases_Problem_Position] ON [dbo].[JudgeTestCases] ([ProblemId], [Position]);';

        INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261108084926_AddJudgeHiddenTests', N'10.0.12');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
