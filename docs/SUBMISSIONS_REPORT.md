# SCRUM-9 — надсилання розв’язків

У гілці `feature/submission-management` від `develop` після PR №15 додано надсилання розв’язків, їх зберігання та приватну історію. PentiumServer приймає код через ASP.NET Core API та зберігає його в MSSQL. [SCRUM-9](https://coderank.atlassian.net/browse/SCRUM-9)

## Контракт API

Для трьох маршрутів потрібен Bearer JWT. API бере ID автора з `sub` перевіреного токена та звіряє активність користувача в БД.

| Метод і шлях | Запит | Успішна відповідь |
| --- | --- | --- |
| `POST /api/v1/olympiads/{olympiadId}/problems/{problemId}/submissions` | `{ "language": "python3", "sourceCode": "print(42)" }` | `201 Created`, `Location: /api/v1/submissions/{id}` та `{ id, status, submittedAtUtc }` |
| `GET /api/v1/submissions` | Необов’язкові `olympiadId`, `problemId`, `page`, `pageSize`, `sort` | `200`, `{ items, page, pageSize, hasMore }`. Елемент містить `id`, `olympiadId`, `problemId`, `language`, `status` та `submittedAtUtc`. |
| `GET /api/v1/submissions/{id}` | ID спроби | `200`, ті самі поля елемента та `sourceCode`. |

Значення мов `cpp17` і `python3` відповідають C++17 та Python 3. Вихідний код має бути непорожнім і займати до 65 536 байтів у UTF-8 включно. Загальний ліміт тіла POST становить 512 КіБ із запасом для JSON-екранування. Під час запису API перевіряє опублікованість олімпіади й видимість зв’язку в одній транзакції з додаванням спроби. Початок включено, завершення виключено за правилом `StartsAtUtc ≤ now < EndsAtUtc`. Серверний `now` зберігається як `submittedAtUtc`.

Історія повертає лише спроби автора. Обидва фільтри можна поєднувати. Типові параметри `page=1`, `pageSize=20`, `sort=newest`. Межі сторінки 1–1000, розміру 1–50. `sort=oldest` змінює напрямок сортування. Час і ID сортуються в одному напрямку, щоб розв’язки з однаковим часом мали визначений порядок. `hasMore` позначає наступну доступну сторінку в цих межах. Історія без збігів повертає `items: []`, а код доступний через окреме читання власної спроби. Відповіді позначені `Cache-Control: no-store`.

| HTTP | Умова |
| --- | --- |
| `400` | Некоректний JSON, зайві поля, непідтримувана мова, порожній або завеликий код, неправильні параметри історії |
| `401` | Відсутній або недійсний JWT |
| `403` | Користувача з підписаного JWT немає в БД або він неактивний |
| `404` | Недоступна олімпіада чи задача, відсутня або чужа спроба |
| `409` | `olympiad_not_started`, `olympiad_ended` або `submission_write_conflict` після конфлікту запису |
| `413` | Тіло POST перевищило 512 КіБ |

## Схема й черга

Міграція `20261107144141_AddSubmissions` додає `dbo.Submissions` і три індекси. `Id` є первинним ключем `bigint IDENTITY`. `UserId`, `OlympiadId` та `ProblemId` є окремими зовнішніми ключами до `Users`, `Olympiads` і `Problems` з `NO ACTION`. Зв’язок у `OlympiadProblems` потрібен під час надсилання. Після від’єднання спроби лишаються в історії, а нове надсилання отримує `404`. Problems CRUD повертає `409` з кодом `problem_has_submissions`, якщо спроби блокують видалення задачі.

`Language` має тип `varchar(16)`, `SourceCode` — `nvarchar(max)`, `Status` — `varchar(32)`, `SubmittedAtUtc` — `datetime2`. Поля обов’язкові. SQL перевіряє дозволені коди мови й статусу та фізичний розмір UTF-16 від 2 до 131 072 байтів. Точний ліміт UTF-8 і перевірку на непорожній текст застосовує API. Індекс `(UserId, SubmittedAtUtc DESC, Id DESC)` підтримує читання історії, `(ProblemId)` — перевірку залежностей при видаленні, `(Status, Id)` — майбутнє читання черги.

У цьому етапі кожна спроба отримує `Queued` і відображається як «Очікує перевірки». Під час SCRUM-10 окремий Judge Worker зможе переводити її з `Queued` у `Running`, потім у фінальний статус. Передбачені коди `Accepted`, `WrongAnswer` (Wrong Answer), `CompilationError` (Compilation Error), `RuntimeError` (Runtime Error), `TimeLimitExceeded` (Time Limit Exceeded), `MemoryLimitExceeded` (Memory Limit Exceeded), `SystemError` (System Error). Компіляція, виконання та формування вердикту належать Worker. Поточний API надає створення й читання спроб.

## Мінімальні SQL GRANT

У вже налаштованій БД CodeRank додатково потрібні `SELECT, INSERT` на нову таблицю. Нижче повний набір прав саме для маршруту надсилання, історії та деталей. DBA звіряє, якому database user відповідає login `CodeRankAPI`, і видає відсутні права в `CodeRankSeedTest`. Права для інших модулів перевіряються за їхніми звітами. [Синтаксис GRANT](https://learn.microsoft.com/en-us/sql/t-sql/statements/grant-object-permissions-transact-sql?view=sql-server-ver15)

```sql
IF DB_NAME() <> N'CodeRankSeedTest'
    THROW 50001, N'Select CodeRankSeedTest before applying QA grants.', 1;

GRANT CONNECT TO [CodeRankAPI];
GRANT SELECT (Id, IsActive) ON OBJECT::dbo.Users TO [CodeRankAPI];
GRANT SELECT (Id, IsPublished, StartsAtUtc, EndsAtUtc)
    ON OBJECT::dbo.Olympiads TO [CodeRankAPI];
GRANT SELECT (OlympiadId, ProblemId, IsVisible)
    ON OBJECT::dbo.OlympiadProblems TO [CodeRankAPI];
GRANT SELECT, INSERT ON OBJECT::dbo.Submissions TO [CodeRankAPI];
```

Тими самими правами читання `Submissions` користується перевірка видалення в Problems CRUD. Для роботи API достатньо цього доступу до нової таблиці. DBA запускає DDL зі свого облікового запису.

## Міграція CodeRankSeedTest

1. Після погодження DBA зроби резервну копію тестової БД. У новій сесії SSMS явно вибери `CodeRankSeedTest` і звір результат `SELECT DB_NAME(), SERVERPROPERTY('ProductVersion');`.
2. Звір шість таблиць SQL-first, первинні ключі `Id bigint` у `Users`, `Olympiads` та `Problems`, тип `datetime2` у колонок часу олімпіади та рядок `20261031164420_InitialBaseline` у `dbo.__EFMigrationsHistory`. Для першого застосування таблиця `Submissions` та рядок нової міграції мають бути відсутні. На свіжій ізольованій БД спочатку застосуй [початкову схему](../database/CodeRank_ER_MVP_CreateSchema.sql), потім baseline, згенерований для цієї сесії командою `dotnet ef migrations script 0 InitialBaseline --project backend/CodeRank.Api`. Історичний `InitialBaseline.sql` сам обирає `CodeRankSeedTest`.
3. Відкрий [AddSubmissions.sql](../database/migrations/AddSubmissions.sql), у копії для виконання підстав `N'CodeRankSeedTest'` у `@ExpectedDatabase`. Виконай весь скрипт у цій сесії. Він звіряє ціль і baseline, створює таблицю, індекси й рядок історії в одній транзакції. Повторний запуск за наявності таблиці та запису міграції пропускає створення. При розбіжності між історією та схемою DBA перевіряє причину.
4. Звір обидва ID в історії, вісім стовпців, три індекси та `NO_ACTION` для всіх трьох FK. Застосуй відсутні `GRANT` з попереднього розділу. Увійди як `CodeRankAPI` і підтвердь доступ до нової таблиці. Після цього запускай нову версію API. [Розгортання міграцій EF Core](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)

За потреби повернення до попередньої версії API залиш таблицю для збереження історії. EF `Down` відмовляється видаляти таблицю, поки є спроби.

## Ручний QA

1. Спрямуй локальний Backend на `CodeRankSeedTest` через локальні секрети. Запусти `dotnet run --project backend/CodeRank.Api --launch-profile https` і `npm run dev` у `frontend` після `npm ci`. Підготуй два активні тестові акаунти учасників, чинного адміністратора та опубліковану QA-олімпіаду, яка триває, з видимою задачею.
2. Відкрий умову без входу й перевір підказку увійти. Після входу надішли C++17 із редактора й Python 3 з UTF-8 файлу, що містить кирилицю. Перевір `201`, `Location`, статус «Очікує перевірки», дати UTC й записи історії. Файл читається в браузері. Текстове поле нормалізує переноси до LF, API зберігає отриманий JSON-рядок без змін.
3. Відкрий `Location` із токеном автора, потім з токеном другого учасника та без токена. Очікуй `200` з кодом, `404` та `401`. Звір фільтри й сортування, зокрема `?olympiadId=<id>&problemId=<id>&pageSize=1`. Перевір відхилення порожнього коду, невідомої мови та коду понад 64 КіБ.
4. Перевір надсилання до початку, після завершення, після приховування задачі та після зняття олімпіади з публікації. Очікуй `409` для часу та `404` для видимості. Після від’єднання задачі звір, що попередню спробу видно автору через API історії, а DELETE задачі від її автора дає `409 problem_has_submissions`.
5. На вузькому екрані перевір редактор, завантаження файлу, історію та швидкі повторні натискання. Імітуй мережеву помилку в DevTools. Історія має підтримувати повтор, редактор — зберігати введений текст. Якщо відповідь на POST втрачена, онови історію перед повторним надсиланням. Один явний повтор після втрати відповіді може створити ще одну спробу.

## Перевірки

| Набір | Результат |
| --- | --- |
| Backend Unit/Integration, SQLite in-memory | 275/275, з них 39 сценаріїв SCRUM-9. Два сценарії перевіряють 413 на локальному Kestrel із тим самим ізольованим SQLite. |
| Database Seeder, SQLite in-memory | 26/26 |
| Frontend Unit | 168/168 |
| Playwright із підміною API, десктоп і мобільний viewport | 208/208 |
| Backend Release та Frontend Production Build | Успішні |
| EF Core і SQL | Snapshot збігається з моделлю. Forward SQL збігається з операціями EF Core. ScriptDom розібрав forward SQL і згенерований rollback без синтаксичних помилок. |
| Ручний QA на Microsoft SQL Server | NOT RUN. Локальна перевірка доступності LocalDB повернула помилку доступу до конфігурації в реєстрі. `CodeRankSeedTest` і `CodeRankDB` очікують окремого рішення щодо застосування. |
