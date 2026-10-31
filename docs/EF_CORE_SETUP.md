# EF Core для наявної бази CodeRank

Backend залишається у `backend/CodeRank.Api/`, namespace — `CodeRank.Api`.
Моделі у `Models/`, контекст у `Data/CodeRankDbContext.cs` відповідають шести таблицям `dbo` зі скрипта `database/CodeRank_ER_MVP_CreateSchema.sql`.

## Залежності

- Target Framework: `net10.0`.
- `Microsoft.EntityFrameworkCore.SqlServer` і `Microsoft.EntityFrameworkCore.Design`: `10.0.12`.
- Локальний інструмент `dotnet-ef`: `10.0.12`, описаний у `.config/dotnet-tools.json`.
- `System.Security.Cryptography.Pkcs`: `9.0.21`, пряме посилання на виправлену версію залежності SQL-клієнта. Без нього відновлювалася вразлива `9.0.11`; аудит NuGet видавав NU1902/NU1903.

З кореня репозиторію:

```powershell
dotnet restore backend/CodeRank.Api/CodeRank.Api.csproj
dotnet tool restore
dotnet build backend/CodeRank.Api/CodeRank.Api.csproj --no-restore
```

## Локальна конфігурація

У проєкті визначено `UserSecretsId`: `CodeRank.Api-LocalDevelopment`.
У Visual Studio: правою кнопкою на **CodeRank.Api → Manage User Secrets**.
Вставте в локальний файл секретів:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=<SQL_SERVER>;Database=<DATABASE_NAME>;User ID=<SQL_LOGIN>;Password=<LOCAL_PASSWORD>;Encrypt=True;TrustServerCertificate=False;"
  }
}
```

Файл User Secrets розташовано поза репозиторієм. Не переносіть реальний пароль у `appsettings.json`, `appsettings.Development.json`, приклади конфігурації або команди, що потраплять у Git. Для локального SQL Server із самопідписаним сертифікатом можна явно вказати `TrustServerCertificate=True`; це налаштування не перевіряє довіру до сертифіката сервера.

Для інших середовищ конфігурацію можна передати змінною процесу `ConnectionStrings__DefaultConnection`. Вона має вищий пріоритет за User Secrets. `.env.example` містить лише назву змінної: ASP.NET Core самостійно не завантажує `.env`. `appsettings.example.json` — довідковий шаблон, а не файл, який додаток автоматично читає. Приклади не містять реальних облікових даних.

User Secrets використовуються в середовищі Development. Якщо запуск не використовує `launchSettings.json`, задайте середовище явно:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project backend/CodeRank.Api --no-launch-profile
```

При відсутньому `DefaultConnection` додаток завершується з повідомленням про потрібний ключ конфігурації.

## Перевірка з’єднання без зміни БД

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project backend/CodeRank.Api --no-launch-profile -- --Database:CheckConnection=true
```

Цей режим викликає `Database.CanConnectAsync()`, повідомляє результат і завершує процес із кодом 0 або 1. HTTP-сервер у ньому не запускається. Перевірка не запускає міграції, `EnsureCreated`, seed або будь-які операції зміни схеми/даних. Рядок підключення й пароль не виводяться у власних повідомленнях перевірки.

Для читання схеми під час scaffolding логіну достатньо `CONNECT` і `VIEW DEFINITION` саме в потрібній БД. Права `db_owner`, `db_ddladmin` і `db_datawriter` для цього етапу не потрібні. Подальші запити бізнес-даних потребуватимуть окремо визначених прав SELECT/INSERT/UPDATE/DELETE; поточний код їх не використовує.

## Відповідність схеми

- 6 сутностей і 37 скалярних полів; таблиці задані явно у схемі `dbo`.
- `UserProfiles.UserId` — PK/FK з `ValueGeneratedNever`, не IDENTITY.
- `OlympiadProblems` має складений PK `(OlympiadId, ProblemId)`.
- Збережено NOT NULL/NULL, SQL-типи й довжини, чотири IDENTITY, шість FK і їхні правила видалення.
- Явно задано три унікальні та один звичайний індекс, шість CHECK і вісім іменованих DEFAULT.
- Для `NormalizedEmail` збережено `Latin1_General_100_CI_AS`.
- Вимкнено конвенцію автоматичного створення індексів FK: контекст описує лише індекси, які є у SQL-схемі.
- Ініціалізовано CLR-значення `Role`, `IsActive`, `MaxPoints`, `IsVisible`; EF також зберігає SQL DEFAULT. Явне `false` для полів із DEFAULT 1 не підміняється значенням true.
- `Users.UserProfile` допускає відсутній профіль; кожен профіль обов’язково посилається на користувача. Створення профілю для кожного користувача залишається майбутньою бізнес-логікою.

JWT, обробники авторизації, CRUD-контролери, Frontend і seed цим налаштуванням не додаються. Початковий WeatherForecast-шаблон збережено.

## Повторне Reverse Engineering для порівняння

Активні файли не потрібно перезаписувати. За потреби генеруйте копію в нову тимчасову папку й порівнюйте її з поточним контекстом. Приклад із PowerShell, з кореня репозиторію:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$compare = Join-Path $env:TEMP ('CodeRankScaffold-' + [guid]::NewGuid().ToString('N'))
dotnet ef dbcontext scaffold 'Name=ConnectionStrings:DefaultConnection' Microsoft.EntityFrameworkCore.SqlServer `
  --project backend/CodeRank.Api `
  --context CodeRankDbContext `
  --context-dir "$compare/Data" --output-dir "$compare/Models" `
  --namespace CodeRank.Api.Models --context-namespace CodeRank.Api.Data `
  --no-onconfiguring `
  --table dbo.Users --table dbo.UserProfiles --table dbo.Olympiads `
  --table dbo.Problems --table dbo.OlympiadProblems --table dbo.ProblemExamples
```

`--no-onconfiguring` запобігає запису connection string у згенерований код. Перевіряйте CHECK, імена DEFAULT та конвенційні індекси окремо: результат scaffolding не замінює повну звірку з SQL і каталогом БД.

## InitialBaseline для існуючої CodeRankDB

**Підготовлено, але не застосовано:** `20261031164420_InitialBaseline`. Реальна схема звірена з контекстом: 6 таблиць і 37 полів. У БД таблиці історії міграцій поки немає, тому `migrations list` показує `InitialBaseline (Pending)`. Це очікуваний стан до окремого погодження застосування.

Файли:

- `backend/CodeRank.Api/Migrations/20261031164420_InitialBaseline.cs` — порожні `Up()` і `Down()`.
- `backend/CodeRank.Api/Migrations/20261031164420_InitialBaseline.Designer.cs` — повні метадані цільової моделі.
- `backend/CodeRank.Api/Migrations/CodeRankDbContextModelSnapshot.cs` — повний snapshot, 6 сутностей і 37 полів.
- `database/migrations/InitialBaseline.sql` — перевірений ідемпотентний SQL.

У `Program.cs` історію явно закріплено за `dbo.__EFMigrationsHistory` через `MigrationsHistoryTable("__EFMigrationsHistory", "dbo")`, щоб вона не залежала від default schema користувача розгортання. Ця конфігурація сама по собі не створює таблицю і не застосовує міграцію.

Операції міграції:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
}

protected override void Down(MigrationBuilder migrationBuilder)
{
}
```

Нуль операцій у `Up/Down` не означає відсутності SQL від EF: провайдер окремо керує службовою таблицею історії.

### Перевірка та повторне формування SQL

З кореня репозиторію, із локальним User Secrets або змінною середовища:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet build backend/CodeRank.Api/CodeRank.Api.csproj
dotnet ef migrations has-pending-model-changes --project backend/CodeRank.Api --context CodeRankDbContext
dotnet ef migrations list --project backend/CodeRank.Api --context CodeRankDbContext
dotnet ef migrations script 0 InitialBaseline --idempotent --project backend/CodeRank.Api --context CodeRankDbContext --output database/migrations/InitialBaseline.sql
```

`has-pending-model-changes` уже проходить: неврахованих змін моделі немає. Це відрізняється від стану міграції `Pending`: модель зафіксована у snapshot, але baseline ще не записаний у БД.

### Який SQL буде виконано

```sql
IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [dbo].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164420_InitialBaseline'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031164420_InitialBaseline', N'10.0.12');
END;

COMMIT;
GO
```

Послідовність: перевірка наявності `dbo.__EFMigrationsHistory`; її створення з двома полями та PK за відсутності; відкриття транзакції; вставлення MigrationId/ProductVersion, якщо такого запису немає; COMMIT. Створення службової таблиці стоїть перед явною транзакцією вставлення. Бізнес-таблиці не згадуються в цьому SQL; ALTER/DROP/UPDATE/DELETE відсутні. Перевірка idempotency дозволяє повторний запуск без повторного запису того самого ID. Скрипт слід виконувати одним оператором розгортання за раз.

### Застосування — тільки після окремого погодження

1. Перед зміною БД зробити резервну копію та повторно звірити актуальну схему/історію, якщо вони могли змінитися після цієї перевірки. Не видаляти та не редагувати Designer/Snapshot.
2. Перевірити сервер і вибрати саме `CodeRankDB` у SSMS. Виконувати вже переглянутий файл `database/migrations/InitialBaseline.sql` під окремим обліковим записом розгортання або DBA.
3. Альтернатива цьому SQL після погодження:

   ```powershell
   dotnet ef database update InitialBaseline --project backend/CodeRank.Api --context CodeRankDbContext
   ```

   Вибрати один спосіб застосування. CLI додатково використовує механізм блокування міграцій EF; повний набір його службових запитів не є текстом збереженого SQL-скрипта.
4. Після застосування перевірити `migrations list` і запис:

   ```sql
   SELECT [MigrationId], [ProductVersion]
   FROM [dbo].[__EFMigrationsHistory]
   WHERE [MigrationId] = N'20261031164420_InitialBaseline';
   ```

   Очікується один запис із версією `10.0.12`; шість бізнес-таблиць і дані залишаються наявними.

**У поточному завданні SQL не виконувався і `database update` не запускався.** `Database.Migrate()` та `EnsureCreated()` у код не додавалися.

### Дозволи для застосування

Поточні `CONNECT` і `VIEW DEFINITION` логіну `codex_dev` достатні для читання схеми, але недостатні для створення/заповнення історії.

Для повного скрипта під окремим обліковим записом потрібні:

- `CONNECT` до `CodeRankDB`.
- Якщо історії ще немає: `CREATE TABLE` на рівні БД і `ALTER` на схемі `dbo` для створення саме службової таблиці. `ALTER` схеми є ширшим дозволом, тому його не слід постійно надавати звичайному логіну додатка.
- `SELECT` та `INSERT` на `dbo.__EFMigrationsHistory` для перевірки MigrationId і додавання запису. Права на запис у бізнес-таблиці, UPDATE і DELETE не потрібні для цього baseline SQL.

Варіант із вужчим доступом: після погодження DBA створює службову таблицю точно за перевіреним CREATE TABLE, а обліковому запису розгортання видаються лише `CONNECT` та `SELECT, INSERT` на вже створену `dbo.__EFMigrationsHistory`. Потім він виконує весь ідемпотентний скрипт: гілка CREATE пропускається. Не потрібно видавати `db_owner`/`db_ddladmin` або INSERT на всю схему. Для CLI, якщо адміністратор обмежив стандартне виконання `sp_getapplock`/`sp_releaseapplock`, потрібно також дозволити ці службові виклики; це не потрібно самому SQL-файлу.

### Нові БД та наступні зміни

Порожній baseline не створює початкові шість таблиць у новій порожній БД. Для неї спочатку застосовується погоджений `database/CodeRank_ER_MVP_CreateSchema.sql`, потім цей baseline. Для розгортання винятково через EF потрібна окремо спроєктована повна початкова міграція та контрольоване позначення її вже виконаною на існуючій БД.

Після застосування baseline наступні зміни моделей оформлюються окремими міграціями, із перевіркою їхнього SQL перед запуском. Не очищати `Up/Down` майбутніх змістовних міграцій. `Down()` цього baseline навмисно не видаляє існуючі таблиці.

## Джерела

- [EF Core SQL Server 10.0.12 — сумісність із .NET 10](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.12)
- [Reverse Engineering](https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding/)
- [Керування міграціями та ModelSnapshot](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing)
- [Іменовані DEFAULT у EF Core 10](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties)
- [Виправлена версія System.Security.Cryptography.Pkcs](https://github.com/advisories/GHSA-846f-f764-76mx)
