SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_ID() <= 4
BEGIN
    THROW 51000, N'Виберіть користувацьку базу даних замість системної бази.', 1;
END;

IF @@TRANCOUNT <> 0
BEGIN
    THROW 51001, N'Запустіть скрипт без іншої відкритої транзакції.', 1;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.objects
    WHERE schema_id = SCHEMA_ID(N'dbo')
      AND name IN
      (
          N'Users', N'UserProfiles', N'Olympiads',
          N'Problems', N'OlympiadProblems', N'ProblemExamples'
      )
)
BEGIN
    THROW 51002, N'У схемі dbo вже є об''єкти CodeRank. Для існуючої схеми потрібна окрема міграція.', 1;
END;

BEGIN TRY
    BEGIN TRANSACTION;


    
    CREATE TABLE [dbo].[Users]
    (
        [Id] BIGINT IDENTITY(1, 1) NOT NULL,
        [Email] NVARCHAR(256) NOT NULL,
        [NormalizedEmail] NVARCHAR(256) COLLATE Latin1_General_100_CI_AS NOT NULL,
        [PasswordHash] NVARCHAR(512) NOT NULL,
        [Role] NVARCHAR(16) NOT NULL
            CONSTRAINT [DF_Users_Role] DEFAULT (N'User'),
        [IsActive] BIT NOT NULL
            CONSTRAINT [DF_Users_IsActive] DEFAULT (1),
        [CreatedAtUtc] DATETIME2 NOT NULL
            CONSTRAINT [DF_Users_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [CK_Users_Role] CHECK ([Role] IN (N'User', N'Admin'))
    );

    CREATE UNIQUE NONCLUSTERED INDEX [UX_Users_NormalizedEmail]
        ON [dbo].[Users] ([NormalizedEmail]);

    
    CREATE TABLE [dbo].[UserProfiles]
    (
        [UserId] BIGINT NOT NULL,
        [FirstName] NVARCHAR(100) NOT NULL,
        [LastName] NVARCHAR(100) NOT NULL,
        CONSTRAINT [PK_UserProfiles] PRIMARY KEY CLUSTERED ([UserId]),
        CONSTRAINT [FK_UserProfiles_Users]
            FOREIGN KEY ([UserId])
            REFERENCES [dbo].[Users] ([Id])
            ON DELETE CASCADE
            ON UPDATE NO ACTION
    );


    CREATE TABLE [dbo].[Olympiads]
    (
        [Id] BIGINT IDENTITY(1, 1) NOT NULL,
        [Title] NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [StartsAtUtc] DATETIME2 NOT NULL,
        [EndsAtUtc] DATETIME2 NOT NULL,
        [IsPublished] BIT NOT NULL
            CONSTRAINT [DF_Olympiads_IsPublished] DEFAULT (0),
        [CreatedByUserId] BIGINT NOT NULL,
        [CreatedAtUtc] DATETIME2 NOT NULL
            CONSTRAINT [DF_Olympiads_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_Olympiads] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [CK_Olympiads_DateRange] CHECK ([EndsAtUtc] > [StartsAtUtc]),
        CONSTRAINT [FK_Olympiads_Users]
            FOREIGN KEY ([CreatedByUserId])
            REFERENCES [dbo].[Users] ([Id])
            ON DELETE NO ACTION
            ON UPDATE NO ACTION
    );

    CREATE NONCLUSTERED INDEX [IX_Olympiads_PublishedStart]
        ON [dbo].[Olympiads] ([IsPublished], [StartsAtUtc]);

    
    CREATE TABLE [dbo].[Problems]
    (
        [Id] BIGINT IDENTITY(1, 1) NOT NULL,
        [Title] NVARCHAR(200) NOT NULL,
        [Statement] NVARCHAR(MAX) NOT NULL,
        [InputFormat] NVARCHAR(MAX) NULL,
        [OutputFormat] NVARCHAR(MAX) NULL,
        [Difficulty] NVARCHAR(16) NULL,
        [CreatedByUserId] BIGINT NOT NULL,
        [CreatedAtUtc] DATETIME2 NOT NULL
            CONSTRAINT [DF_Problems_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_Problems] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [CK_Problems_Difficulty] CHECK ([Difficulty] IS NULL OR [Difficulty] IN (N'Easy', N'Medium', N'Hard')),
        CONSTRAINT [FK_Problems_Users]
            FOREIGN KEY ([CreatedByUserId])
            REFERENCES [dbo].[Users] ([Id])
            ON DELETE NO ACTION
            ON UPDATE NO ACTION
    );


    CREATE TABLE [dbo].[OlympiadProblems]
    (
        [OlympiadId] BIGINT NOT NULL,
        [ProblemId] BIGINT NOT NULL,
        [Position] INT NOT NULL,
        [MaxPoints] INT NOT NULL
            CONSTRAINT [DF_OlympiadProblems_MaxPoints] DEFAULT (100),
        [IsVisible] BIT NOT NULL
            CONSTRAINT [DF_OlympiadProblems_IsVisible] DEFAULT (1),
        CONSTRAINT [PK_OlympiadProblems] PRIMARY KEY CLUSTERED ([OlympiadId], [ProblemId]),
        CONSTRAINT [CK_OlympiadProblems_Position] CHECK ([Position] > 0),
        CONSTRAINT [CK_OlympiadProblems_MaxPoints] CHECK ([MaxPoints] > 0),
        CONSTRAINT [FK_OlympiadProblems_Olympiads]
            FOREIGN KEY ([OlympiadId])
            REFERENCES [dbo].[Olympiads] ([Id])
            ON DELETE CASCADE
            ON UPDATE NO ACTION,
        CONSTRAINT [FK_OlympiadProblems_Problems]
            FOREIGN KEY ([ProblemId])
            REFERENCES [dbo].[Problems] ([Id])
            ON DELETE NO ACTION
            ON UPDATE NO ACTION
    );

    CREATE UNIQUE NONCLUSTERED INDEX [UX_OlympiadProblems_Position]
        ON [dbo].[OlympiadProblems] ([OlympiadId], [Position]);

  
    CREATE TABLE [dbo].[ProblemExamples]
    (
        [Id] BIGINT IDENTITY(1, 1) NOT NULL,
        [ProblemId] BIGINT NOT NULL,
        [Position] INT NOT NULL,
        [InputData] NVARCHAR(MAX) NOT NULL,
        [ExpectedOutput] NVARCHAR(MAX) NOT NULL,
        [Explanation] NVARCHAR(MAX) NULL,
        CONSTRAINT [PK_ProblemExamples] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [CK_ProblemExamples_Position] CHECK ([Position] > 0),
        CONSTRAINT [FK_ProblemExamples_Problems]
            FOREIGN KEY ([ProblemId])
            REFERENCES [dbo].[Problems] ([Id])
            ON DELETE CASCADE
            ON UPDATE NO ACTION
    );

    CREATE UNIQUE NONCLUSTERED INDEX [UX_ProblemExamples_Position]
        ON [dbo].[ProblemExamples] ([ProblemId], [Position]);

    COMMIT TRANSACTION;
    PRINT N'Схему CodeRank успішно створено: 6 таблиць, 6 PK, 6 FK, 6 CHECK, 3 унікальні індекси.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;


SELECT
    DB_NAME() AS DatabaseName,
    SCHEMA_NAME(t.schema_id) AS SchemaName,
    t.name AS TableName,
    COUNT(c.column_id) AS ColumnCount
FROM sys.tables AS t
INNER JOIN sys.columns AS c ON c.object_id = t.object_id
WHERE t.schema_id = SCHEMA_ID(N'dbo')
  AND t.name IN (N'Users', N'UserProfiles', N'Olympiads', N'Problems', N'OlympiadProblems', N'ProblemExamples')
GROUP BY t.schema_id, t.name
ORDER BY t.name;


SELECT
    fk.name AS ForeignKeyName,
    OBJECT_NAME(fk.parent_object_id) AS ChildTable,
    OBJECT_NAME(fk.referenced_object_id) AS ParentTable,
    fk.delete_referential_action_desc AS DeleteAction
FROM sys.foreign_keys AS fk
WHERE fk.parent_object_id IN
(
    OBJECT_ID(N'dbo.UserProfiles'), OBJECT_ID(N'dbo.Olympiads'),
    OBJECT_ID(N'dbo.Problems'), OBJECT_ID(N'dbo.OlympiadProblems'),
    OBJECT_ID(N'dbo.ProblemExamples')
)
ORDER BY ChildTable, ForeignKeyName;

SELECT
    OBJECT_NAME(cc.parent_object_id) AS TableName,
    cc.name AS CheckConstraintName,
    cc.definition AS CheckDefinition
FROM sys.check_constraints AS cc
WHERE cc.parent_object_id IN
(
    OBJECT_ID(N'dbo.Users'), OBJECT_ID(N'dbo.Olympiads'),
    OBJECT_ID(N'dbo.Problems'), OBJECT_ID(N'dbo.OlympiadProblems'),
    OBJECT_ID(N'dbo.ProblemExamples')
)
ORDER BY TableName, CheckConstraintName;

SELECT
    OBJECT_NAME(i.object_id) AS TableName,
    i.name AS UniqueIndexName,
    c.name AS ColumnName,
    ic.key_ordinal AS ColumnPosition
FROM sys.indexes AS i
INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.is_unique = 1 AND i.is_primary_key = 0 AND ic.key_ordinal > 0
  AND i.object_id IN (OBJECT_ID(N'dbo.Users'), OBJECT_ID(N'dbo.OlympiadProblems'), OBJECT_ID(N'dbo.ProblemExamples'))
ORDER BY TableName, UniqueIndexName, ColumnPosition;
