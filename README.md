# CodeRank

CodeRank v1.0.0 — вебплатформа для внутрішніх олімпіад із програмування. Студенти й учасники можуть переглядати змагання та умови задач, а організатори — готувати олімпіади, додавати задачі й керувати публікацією. Проєкт розроблено в межах дисципліни «Проєктний практикум».

[Відкрити CodeRank](https://pentiumserver.tail72e3ef.ts.net) · [Релізна гілка](https://github.com/n25101882-svg/CodeRank/tree/release/v1.0.0)

[Можливості MVP](#можливості-mvp) · [Локальний запуск](#локальний-запуск) · [Тести](#автоматизовані-тести) · [Deployment Instructions](#deployment-instructions) · [Посібник користувача](#посібник-користувача) · [Документація API](#документація-api)

## Можливості MVP

- Реєстрація, вхід, профіль та вихід. API хешує паролі, видає JWT після входу й обмежує частоту запитів до Login.
- Публічний каталог опублікованих олімпіад із видимими задачами, умовами та прикладами.
- Створення, редагування й видалення власних задач. Система відхиляє видалення задачі, яку прив’язано до олімпіади.
- Керування олімпіадами для активного адміністратора, включно із чернетками, порядком і видимістю задач та публікацією.
- Надсилання розв’язків для видимих задач активної олімпіади й перегляд власної історії.

Docker Worker оцінює Python 3 і C++17 в ізольованому QA. На публічному стенді оцінювання вимкнено до усунення ризику очищення контейнерів і завершення перевірки безпеки. Надіслані розв’язки можуть залишатися в черзі. Таблиця лідерів запланована на Post-MVP. [Issue №24](https://github.com/n25101882-svg/CodeRank/issues/24) · [SCRUM-11](https://coderank.atlassian.net/browse/SCRUM-11)

## Технології та архітектура

| Компонент | Технології та призначення |
| --- | --- |
| Клієнт | HTML, CSS і Vanilla JavaScript. Vite запускає сервер розробки та збирає статичні файли. Клієнт звертається до REST API за шляхом `/api/v1`. |
| API | ASP.NET Core Web API, .NET 10. Сервер перевіряє вхідні дані, JWT та права доступу до операцій. |
| Дані | Microsoft SQL Server і Entity Framework Core. Початкову схему створюють SQL-скрипти. EF Core веде історію міграцій. |
| Розгортання | PentiumServer з Ubuntu Server 26.04.1 LTS. systemd запускає API, Nginx віддає клієнт і проксіює запити, Tailscale Funnel надає публічний HTTPS. |
| Judge | Окремий .NET Worker використовує API черги й запускає тести в Docker. Документований запуск призначено для ізольованого QA. |

## Локальний запуск

### 1. Системні вимоги

| Потрібно | Для чого |
| --- | --- |
| Git і доступ до приватного репозиторію | Клонування та оновлення коду. Для команд нижче використовуйте PowerShell 7. |
| .NET 10 SDK | Відновлення пакетів, збірка та запуск API. |
| Node.js 22.12 або новіший і npm | Встановлення залежностей та запуск Vite. Вимогу зафіксовано у [package.json](frontend/package.json). |
| Доступний Microsoft SQL Server, SSMS або sqlcmd | Окрема локальна БД та запуск SQL-скриптів. |
| Сучасний браузер із довірою до локального ASP.NET HTTPS-сертифіката | Запуск і перевірка інтерфейсу. |

Для локального Frontend і API достатньо SQL Server. Docker потрібен лише для [окремого QA Judge](docs/JUDGE_DOCKER_PROVIDER.md#реальний-qa).

### 2. Клонування та залежності

Увійдіть у GitHub обліковим записом із доступом до CodeRank. Команди нижче наведено для PowerShell. Виконуйте їх із кореня репозиторію, якщо не зазначено інший каталог.

```powershell
git clone --branch release/v1.0.0 https://github.com/n25101882-svg/CodeRank.git
cd CodeRank
dotnet restore CodeRank.Api.slnx
dotnet tool restore
npm.cmd --prefix frontend ci
```

На Linux і macOS використовуйте `npm` замість `npm.cmd`. Маніфест [.config/dotnet-tools.json](.config/dotnet-tools.json) фіксує версію `dotnet-ef`.

### 3. Схема SQL Server

Створіть окрему порожню користувацьку БД для розробки. У прикладах `<SQL_SERVER>` і `<DATABASE_NAME>` позначають її сервер та назву. У SSMS виберіть саме цю БД і виконайте `SELECT DB_NAME();` для перевірки контексту. Початкові скрипти запускайте обліковим записом із правами DDL. Для API налаштуйте окремий обліковий запис із правами на потрібні операції.

| Порядок | Скрипт | Результат |
| --- | --- | --- |
| 1 | [CodeRank_ER_MVP_CreateSchema.sql](database/CodeRank_ER_MVP_CreateSchema.sql) | Створює шість початкових таблиць. Виконуйте тільки в порожній БД. |
| 2 | [InitialBaseline.sql](database/migrations/InitialBaseline.sql) | Перевіряє початкову схему і записує базову міграцію в `dbo.__EFMigrationsHistory`. |
| 3 | [AddSubmissions.sql](database/migrations/AddSubmissions.sql) | Створює таблицю розв’язків. |
| 4 | [AddJudgeLeases.sql](database/migrations/AddJudgeLeases.sql) | Додає поля та обмеження черги Judge. |
| 5 | [AddJudgeHiddenTests.sql](database/migrations/AddJudgeHiddenTests.sql) | Додає приватні тести й знімки тестів для оцінювання. |

У **локальній копії** кожного зі скриптів 2–5 заповніть `@ExpectedDatabase` точною назвою своєї БД, наприклад `N'<DATABASE_NAME>'` після заміни заповнювача. Виконуйте по одному скрипту і зупиніться, якщо попередній завершився помилкою. Для `sqlcmd` явно передайте БД через `-d`, кодову сторінку UTF-8 через `-f 65001` і зупинку при SQL-помилці через `-b`. [Довідка sqlcmd](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility?view=sql-server-ver17)

Приклад першого кроку для `sqlcmd` (ODBC). Укажіть локальний сервер, цільову БД та обліковий запис з правами DDL. Пароль введіть у запиті `sqlcmd`.

```powershell
sqlcmd -S '<SQL_SERVER>' -d '<DATABASE_NAME>' -U '<SQL_LOGIN>' -N -f 65001 -b -i 'database/CodeRank_ER_MVP_CreateSchema.sql'
```

```sql
SELECT DB_NAME() AS DatabaseName;
SELECT MigrationId, ProductVersion
FROM dbo.__EFMigrationsHistory
ORDER BY MigrationId;
```

Очікуються чотири записи міграцій від `InitialBaseline` до `AddJudgeHiddenTests`. Під час запуску API перевіряє доступність БД, міграції застосовуються окремо. Для вже ініціалізованої БД звірте поточну схему й історію та застосовуйте лише відсутні міграції. [EF Core та наявна БД](docs/EF_CORE_SETUP.md) · [Права для профілю й реєстрації](docs/AUTH_API.md#права-mssql-та-межі-mvp) · [Права для задач](docs/PROBLEMS_API.md#sql-server--дозволи-для-майбутньої-ручної-інтеграції)

### 4. Секрети Development

API має `UserSecretsId` у [CodeRank.Api.csproj](backend/CodeRank.Api/CodeRank.Api.csproj). [Backend .env.example](backend/CodeRank.Api/.env.example) перелічує параметри. ASP.NET Core не завантажує `.env` автоматично. Для Development використовуйте User Secrets або змінні середовища API `ConnectionStrings__DefaultConnection` та `Jwt__SigningKey`.

Шаблон рядка для SQL-автентифікації з довіреним сертифікатом SQL Server:

```text
Server=<SQL_SERVER>;Database=<DATABASE_NAME>;User ID=<SQL_LOGIN>;Password=<LOCAL_PASSWORD>;Encrypt=True;TrustServerCertificate=False
```

Введіть готовий локальний рядок у прихованому запиті PowerShell. Команда створює окремий випадковий ключ і передає обидва значення до Secret Manager через stdin.

```powershell
$api = 'backend/CodeRank.Api/CodeRank.Api.csproj'
$sqlInput = Read-Host 'Рядок підключення до локальної БД' -AsSecureString
$sqlConnection = [System.Net.NetworkCredential]::new('', $sqlInput).Password
$localSecrets = @{
    'ConnectionStrings:DefaultConnection' = $sqlConnection
    'Jwt:SigningKey' = [Convert]::ToBase64String(
        [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}
$localSecrets | ConvertTo-Json -Compress | dotnet user-secrets set --project $api
Remove-Variable sqlInput, sqlConnection, localSecrets
```

Приклад розраховано на PowerShell 7. User Secrets зберігає дані в профілі розробника поза Git. Для повторного запуску залиште налаштований ключ. У [Frontend .env.example](frontend/.env.example) наведено лише публічний `VITE_API_BASE_URL`. Значення з префіксом `VITE_` потрапляють у браузер. Паролі, рядки підключення та ключі належать конфігурації API. [Secret Manager](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0) · [Змінні середовища Vite](https://vite.dev/guide/env-and-mode)

### 5. Backend і Frontend

У першому терміналі з кореня перевірте підключення до локальної БД. У разі успіху запустіть профіль HTTPS. Якщо браузер ще не довіряє локальному сертифікату, встановіть довіру до нього командою `dotnet dev-certs https --trust` у своєму профілі.

```powershell
$api = 'backend/CodeRank.Api/CodeRank.Api.csproj'
dotnet run --project $api --launch-profile https -- --Database:CheckConnection=true
# Після Database connection successful та коду 0:
dotnet run --project $api --launch-profile https
```

API доступний за `https://localhost:7160`. Запит `GET /api/v1/olympiads` повертає JSON. У порожній БД список очікувано порожній. За потреби додайте демонстраційні дані у виділену Development-БД за [інструкцією Database Seeding](docs/DATABASE_SEEDING.md). Seeder запускається окремою командою.

У другому терміналі з кореня запустіть Vite:

```powershell
npm.cmd --prefix frontend run dev
```

Відкрийте [локальний каталог](http://localhost:5173/olympiads) або [сторінку реєстрації](http://localhost:5173/register). Використовуйте саме `localhost:5173` для Development CORS. Якщо API недоступний, звірте пройдений DB check, довіру до HTTPS і порт 7160. Якщо потрібен інший API URL, задайте публічне значення у `frontend/.env.local` і перезапустіть Vite. [Документація Frontend](frontend/README.md)

## Автоматизовані тести

Запускайте збірку й тестові проєкти з кореня. API та Seeder мають ізольовані інтеграційні тести на SQLite.

```powershell
dotnet build CodeRank.Api.slnx -c Release
dotnet test tests/CodeRank.Api.Auth.Tests/CodeRank.Api.Auth.Tests.csproj -c Release
dotnet test tests/CodeRank.Api.Seeding.Tests/CodeRank.Api.Seeding.Tests.csproj -c Release
dotnet test tests/CodeRank.Judge.Worker.Tests/CodeRank.Judge.Worker.Tests.csproj -c Release
dotnet test tests/CodeRank.Judge.Sandbox.Tests/CodeRank.Judge.Sandbox.Tests.csproj -c Release
```

Playwright перехоплює API-запити. На Windows за замовчуванням потрібен Chrome. Для систем, де Playwright використовує вбудований Chromium, встановіть його командою `npx playwright install chromium` із `frontend/`. Якщо порт 5173 зайнято, перед тестом задайте в оточенні вільний `CODERANK_TEST_PORT`.

```powershell
npm.cmd --prefix frontend run build
npm.cmd --prefix frontend run test:unit
npm.cmd --prefix frontend run test:browser
```

Реальні Docker-тести вмикаються окремо лише в [ізольованому QA](docs/JUDGE_DOCKER_PROVIDER.md#реальний-qa).

## Deployment Instructions

Публічна версія працює на PentiumServer з Ubuntu Server 26.04.1 LTS. Tailscale Funnel приймає HTTPS, Nginx віддає `frontend/dist` і передає `/api/` службі `coderank-api`. SQL Server доступний API приватно. [Публічний стенд](https://pentiumserver.tail72e3ef.ts.net)

1. **Зберіть реліз.** Зафіксуйте коміт і підготуйте два пакети. Для одного origin залиште `VITE_API_BASE_URL` незаданим у середовищі збірки та у файлах Vite.

   ```powershell
   dotnet publish backend/CodeRank.Api/CodeRank.Api.csproj -c Release -p:UseAppHost=false -o release-out/api
   npm.cmd --prefix frontend ci
   npm.cmd --prefix frontend run build
   ```

2. **Підготуйте БД і службу.** Звірте резервну копію, ціль SQL Server та історію міграцій. Запускайте API під непривілейованим користувачем з ASP.NET Core Runtime 10. Передайте рядок підключення та JWT-ключ через захищений `EnvironmentFile` systemd. Для публічної конфігурації встановіть `Judge__Enabled=false` і `Judge__Completion__Enabled=false`.
3. **Налаштуйте проксі.** Залиште API на loopback. Перевірте, що через довірений проксі API отримує HTTPS-схему публічної адреси, а Nginx повертає `index.html` для прямих маршрутів SPA. Зовнішні маршрути Judge відхиляйте. Перевірте конфігурацію командою `nginx -t`.
4. **Виконайте приймання.** Перевірте HTTPS, каталог, реєстрацію та вхід, створення задачі й захист профілю після Logout. Після публікації перевірте прямі маршрути після F5.

Команди для служби, приклад Nginx, SQL-послідовність і відкат наведено в [плані розгортання](docs/PRACTICAL_11.md#план-розгортання). Результати стенда зафіксовано в [Smoke Test Suite](docs/PRACTICAL_11.md#smoke-test-suite).

## Посібник користувача

| Дія | Кроки |
| --- | --- |
| Реєстрація | На `/register` укажіть ім’я, прізвище, email і пароль. Пароль має містити 12–128 символів. Уникайте відомих паролів, послідовностей і простих повторів. Після реєстрації відкриється форма входу. |
| Вхід і профіль | Увійдіть на `/login` з email і паролем. У «Мій профіль» (`/dashboard`) перегляньте дані акаунта та роль. |
| Олімпіади | Відкрийте `/olympiads`, виберіть опубліковану олімпіаду та видиму задачу. За наявності відобразяться формати вводу й виводу та приклади. |
| Власні задачі | Відкрийте «Мої задачі» (`/problems`). Заповніть назву й умову. Формати вводу й виводу та складність можна додати за потреби. Натисніть «Створити задачу». Для наявної задачі виберіть «Редагувати» або «Видалити» і підтвердьте дію. |
| Адміністрування | Активний користувач із роллю Admin відкриває «Керування олімпіадами» у профілі. Там можна створити чернетку, прив’язати доступні задачі, задати бали, порядок і видимість, опублікувати або зняти олімпіаду з публікації. |
| Вихід | Натисніть «Вийти». Сесія поточної вкладки очищується, а захищені сторінки переводять на вхід. |

## Документація API

| Модуль | Контракт |
| --- | --- |
| Реєстрація, JWT, профіль | [Authentication API](docs/AUTH_API.md) |
| Каталог олімпіад та умови задач | [Olympiads Read API](docs/OLYMPIADS_READ_API.md) · [Перегляд задач](docs/OLYMPIAD_PROBLEM_VIEW.md) |
| Власні задачі | [Problems CRUD API](docs/PROBLEMS_API.md) |
| Адміністрування | [Admin Olympiads](docs/ADMIN_OLYMPIADS_REPORT.md) |
| Надсилання розв’язків | [Submissions API](docs/SUBMISSIONS_REPORT.md) |
| Робоча черга та Docker Worker | [Judge API](docs/JUDGE_PROTOCOL_REPORT.md) · [Docker Judge QA](docs/JUDGE_DOCKER_PROVIDER.md) |

У Development OpenAPI JSON доступний після запуску API за `https://localhost:7160/openapi/v1.json`.

## Команда

CodeRank — командний навчальний проєкт. Внесок у код і документацію можна переглянути в [історії внесків GitHub](https://github.com/n25101882-svg/CodeRank/graphs/contributors). Склад команди з ПІБ і ролями потребує підтвердження.
