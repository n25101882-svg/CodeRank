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

## Як надалі започаткувати EF Core Migrations для існуючої БД

**Зараз міграцій немає і вони не застосовувалися.** `Database.Migrate()` та `EnsureCreated()` не викликаються автоматично. Наявний SQL-файл і створені таблиці не змінено.

Для цієї БД можна окремо погодити **порожню початкову міграцію (baseline)**:

1. Зробити резервну копію БД та звірити модель із реальною схемою. Переконатися, що ця БД ще не має історії міграцій іншого контексту. За розбіжностей спочатку їх розібрати, а не приховувати baseline.
2. Створити міграцію в окремій гілці:

   ```powershell
   dotnet ef migrations add InitialBaseline --project backend/CodeRank.Api --context CodeRankDbContext
   ```

   Команда лише генерує файли. Згенеровані `CreateTable` спочатку описуватимуть уже наявні таблиці.

3. Перевірити згенеровану схему, потім очистити **лише тіла `Up()` і `Down()`** цієї початкової міграції. Зберегти файл міграції, `.Designer.cs` і повний `CodeRankDbContextModelSnapshot.cs`. Snapshot потрібен для обчислення наступних змін.
4. Згенерувати SQL для перевірки:

   ```powershell
   dotnet ef migrations script 0 InitialBaseline --project backend/CodeRank.Api --context CodeRankDbContext --output InitialBaseline.review.sql
   ```

   У ньому повинні залишитися лише операції історії міграцій (створення `__EFMigrationsHistory`, якщо її ще немає, і запис baseline), без створення/видалення шести таблиць CodeRank або зміни їхніх даних.
5. Тільки після перевірки та окремого дозволу на службову таблицю застосувати baseline:

   ```powershell
   dotnet ef database update InitialBaseline --project backend/CodeRank.Api --context CodeRankDbContext
   ```

   Ця майбутня дія змінить БД лише для обліку міграцій. Вона потребує окремого облікового запису для міграцій із правами на створення/запис службової таблиці; поточних CONNECT і VIEW DEFINITION логіну codex_dev для цього недостатньо. У поточному завданні вона не виконувалася.
6. Надалі змінювати моделі, створювати окремі змістовні міграції та перевіряти їхній SQL перед застосуванням.

**Важливо для нової порожньої БД:** порожній baseline не створить початкові шість таблиць. Спочатку потрібно застосувати погоджений SQL-файл `database/CodeRank_ER_MVP_CreateSchema.sql`, потім baseline. Якщо потрібен єдиний повний ланцюжок розгортання виключно з EF Migrations, слід окремо спроєктувати повну початкову міграцію та безпечне позначення її вже виконаною на існуючій БД. Не запускайте її `CreateTable` на поточній CodeRankDB.

## Джерела

- [EF Core SQL Server 10.0.12 — сумісність із .NET 10](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.12)
- [Reverse Engineering](https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding/)
- [Керування міграціями та ModelSnapshot](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing)
- [Іменовані DEFAULT у EF Core 10](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties)
- [Виправлена версія System.Security.Cryptography.Pkcs](https://github.com/advisories/GHSA-846f-f764-76mx)
