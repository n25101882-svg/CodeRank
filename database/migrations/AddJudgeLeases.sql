-- EF Core 10.0.12, AddSubmissions -> 20261107162834_AddJudgeLeases.
-- Select the approved database in the connection and set its name below.
DECLARE @ExpectedDatabase sysname = N'';

IF @ExpectedDatabase = N'' OR DB_NAME() <> @ExpectedDatabase
    THROW 50011, N'Select the intended database and set @ExpectedDatabase.', 1;
IF @@TRANCOUNT <> 0
    THROW 50012, N'Run AddJudgeLeases without an open transaction.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL OR OBJECT_ID(N'dbo.Submissions', N'U') IS NULL
    THROW 50013, N'The CodeRank migration history and Submissions table are required.', 1;

SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [dbo].[__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261107144141_AddSubmissions'
    )
        THROW 50014, N'Apply AddSubmissions before AddJudgeLeases.', 1;

    DECLARE @AlreadyApplied bit = 0;
    IF EXISTS (
        SELECT 1 FROM [dbo].[__EFMigrationsHistory] WITH (UPDLOCK, HOLDLOCK)
        WHERE [MigrationId] = N'20261107162834_AddJudgeLeases'
    )
        SET @AlreadyApplied = 1;

    DECLARE @SubmissionsId int = OBJECT_ID(N'dbo.Submissions', N'U');
    DECLARE @LeaseColumnCount int = (
        SELECT COUNT(*) FROM sys.columns
        WHERE object_id = @SubmissionsId
            AND name IN (N'JudgeAttemptCount', N'JudgeFinishedAtUtc', N'JudgeLeaseExpiresAtUtc',
                N'JudgeLeaseToken', N'JudgeStartedAtUtc', N'JudgeWorkerId')
    );

    IF @AlreadyApplied = 1
    BEGIN
        IF @LeaseColumnCount <> 6 OR (
            SELECT COUNT(*) FROM sys.indexes WHERE object_id = @SubmissionsId
                AND name IN (N'IX_Submissions_RunningLeaseExpiry', N'UX_Submissions_RunningJudgeWorker')
        ) <> 2 OR (
            SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = @SubmissionsId
                AND name IN (N'CK_Submissions_JudgeAttemptCount', N'CK_Submissions_JudgeLease')
        ) <> 2
            THROW 50015, N'Migration history and schema disagree. Check with the DBA.', 1;
    END
    ELSE
    BEGIN
        IF @LeaseColumnCount <> 0
            THROW 50016, N'Judge columns already exist without their migration record.', 1;

        -- Dependent DDL is compiled after the new columns exist in this transaction.
        ALTER TABLE [dbo].[Submissions] ADD [JudgeAttemptCount] int NOT NULL CONSTRAINT [DF_Submissions_JudgeAttemptCount] DEFAULT 0;

        ALTER TABLE [dbo].[Submissions] ADD [JudgeFinishedAtUtc] datetime2 NULL;

        ALTER TABLE [dbo].[Submissions] ADD [JudgeLeaseExpiresAtUtc] datetime2 NULL;

        ALTER TABLE [dbo].[Submissions] ADD [JudgeLeaseToken] uniqueidentifier NULL;

        ALTER TABLE [dbo].[Submissions] ADD [JudgeStartedAtUtc] datetime2 NULL;

        ALTER TABLE [dbo].[Submissions] ADD [JudgeWorkerId] varchar(64) NULL;

        EXEC sys.sp_executesql N'CREATE INDEX [IX_Submissions_RunningLeaseExpiry] ON [dbo].[Submissions] ([JudgeLeaseExpiresAtUtc]) WHERE [Status] = ''Running'';';

        EXEC sys.sp_executesql N'CREATE UNIQUE INDEX [UX_Submissions_RunningJudgeWorker] ON [dbo].[Submissions] ([JudgeWorkerId]) WHERE [Status] = ''Running'' AND [JudgeWorkerId] IS NOT NULL;';

        EXEC sys.sp_executesql N'ALTER TABLE [dbo].[Submissions] ADD CONSTRAINT [CK_Submissions_JudgeAttemptCount] CHECK ([JudgeAttemptCount] >= 0);';

        EXEC sys.sp_executesql N'ALTER TABLE [dbo].[Submissions] ADD CONSTRAINT [CK_Submissions_JudgeLease] CHECK (([JudgeWorkerId] IS NULL AND [JudgeLeaseToken] IS NULL AND [JudgeLeaseExpiresAtUtc] IS NULL)
        OR ([JudgeWorkerId] IS NOT NULL AND [JudgeLeaseToken] IS NOT NULL AND [JudgeAttemptCount] > 0
            AND [JudgeStartedAtUtc] IS NOT NULL
            AND (([Status] = ''Running'' AND [JudgeLeaseExpiresAtUtc] IS NOT NULL AND [JudgeFinishedAtUtc] IS NULL)
              OR ([Status] NOT IN (''Queued'', ''Running'') AND [JudgeLeaseExpiresAtUtc] IS NULL AND [JudgeFinishedAtUtc] IS NOT NULL))));';

        INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261107162834_AddJudgeLeases', N'10.0.12');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
