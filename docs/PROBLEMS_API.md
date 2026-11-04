# Problems CRUD API — практична №9, етап 1

Реалізовано Backend-керування власними задачами через наявні `Problem` і `CodeRankDbContext`. Frontend, фізичну SQL-схему та EF-міграції не змінено. Усі п’ять ендпоінтів захищено `[Authorize]`; крім перевірки JWT, кожна дія звіряє `sub` з наявним активним користувачем у `Users`.

## Маршрути та відповіді

| Метод і шлях | Успішна відповідь | Основні відмови |
|---|---|---|
| `GET /api/v1/problems` | 200, масив власних задач за Id; `[]`, якщо власних задач немає | 401 |
| `GET /api/v1/problems/{id}` | 200, власна задача | 401, 404 |
| `POST /api/v1/problems` | 201, DTO та Location створеної задачі | 400, 401 |
| `PUT /api/v1/problems/{id}` | 204, без тіла відповіді | 400, 401, 404 |
| `DELETE /api/v1/problems/{id}` | 204, без тіла відповіді | 401, 404, 409 |

Відсутній або недійсний JWT, невалідний `sub`, неактивний чи видалений користувач отримують 401. Для чужого і відсутнього Id повертається 404. Роль Admin не надає обходу фільтра власника: перевірка завжди використовує `CreatedByUserId`.

Request DTO приймає тільки `title`, `statement`, `inputFormat`, `outputFormat`, `difficulty`. Невідомі поля, зокрема `createdByUserId`, `id`, `createdAtUtc` і навігаційні об’єкти, відхиляються з 400. Новий `CreatedByUserId` визначається виключно з перевіреного JWT після звірки з БД; PUT його не змінює.

Валідація: Title непорожній після обрізання крайових пробілів, до 200 UTF-16 одиниць; Statement не може бути null, порожнім або пробільним. Умова та формати введення/виведення зберігаються без обрізання пробілів. Difficulty — тільки `null`, `Easy`, `Medium`, `Hard`, із точним регістром. `inputFormat`, `outputFormat` і `difficulty` необов’язкові. PUT замінює редаговані поля; пропущені необов’язкові поля стають null.

Response DTO містить лише `id`, `title`, `statement`, `inputFormat`, `outputFormat`, `difficulty`, `createdAtUtc`. Пов’язані сутності, дані користувача та хеш пароля не серіалізуються. Дата створення походить із наявного default БД та повертається як UTC. Читання використовує `AsNoTracking`; усі запити й збереження асинхронні та підтримують скасування.

## Видалення та зв’язки

Спочатку перевіряється власник задачі, потім наявність будь-якого `OlympiadProblems` для її Id. Наявний зв’язок дає 409 незалежно від публікації олімпіади; задача і зв’язки залишаються в БД. API не видаляє ці зв’язки та не обходить FK.

Якщо зв’язок з’явився після попередньої перевірки, обмеження БД блокує DELETE. SQL Server detector розпізнає код 547; контролер повторно перевіряє зв’язок і лише за його наявності повертає безпечний 409. Інші збої не маскуються як конфлікт зв’язків, а обробляються чинним безпечним 500. Видалення задачі іншим запитом перед збереженням PUT/DELETE обробляється як 404.

```json
{
  "status": 409,
  "title": "The problem is linked to an olympiad and cannot be deleted."
}
```

У поточній SQL-схемі `ProblemExamples → Problems` має `ON DELETE CASCADE`. Тому успішне видалення задачі без `OlympiadProblems` також видаляє її приклади силами БД. Це існуюче правило з `database/CodeRank_ER_MVP_CreateSchema.sql`, його не змінювали; окремих DELETE для прикладів API не надсилає. Поведінка каскаду й збереження заблокованих записів перевірена в ізольованій SQLite.

## Приклади HTTP-запитів

Нижче наведено шаблони для Postman / HTTP-клієнта, а не виконані записи в MSSQL. `baseUrl` — наприклад, `https://localhost:7160`; `accessToken` отримується через наявний Login. `problemId` слід взяти з відповіді власного POST. Значення токена й облікові дані в документацію не вставляються.

```http
GET {{baseUrl}}/api/v1/problems
Authorization: Bearer {{accessToken}}
Accept: application/json

### Власна задача
GET {{baseUrl}}/api/v1/problems/{{problemId}}
Authorization: Bearer {{accessToken}}
Accept: application/json

### Створення
POST {{baseUrl}}/api/v1/problems
Authorization: Bearer {{accessToken}}
Content-Type: application/json

{
  "title": "Сума двох чисел",
  "statement": "Прочитайте два цілі числа та виведіть їхню суму.",
  "inputFormat": "Два цілі числа a та b.",
  "outputFormat": "Одне ціле число — сума a + b.",
  "difficulty": "Easy"
}

### Редагування
PUT {{baseUrl}}/api/v1/problems/{{problemId}}
Authorization: Bearer {{accessToken}}
Content-Type: application/json

{
  "title": "Оновлена задача",
  "statement": "Оновлена умова задачі.",
  "inputFormat": null,
  "outputFormat": null,
  "difficulty": "Medium"
}

### Видалення
DELETE {{baseUrl}}/api/v1/problems/{{problemId}}
Authorization: Bearer {{accessToken}}
```

Числові Id й дати у власних відповідях визначаються БД. Механізм Login, JWT, startup-перевірка БД та наявні CLI-команди збережені. Локальний запуск API описано в [AUTH_API.md](AUTH_API.md).

## SQL Server — дозволи для майбутньої ручної інтеграції

Потрібен SQL Login із користувачем у погодженій тестовій БД та правом підключення (`CONNECT`). Дозволи нижче випливають із запитів цієї реалізації:

| Об’єкт | Дозволи | Призначення |
|---|---|---|
| `dbo.Users` | SELECT | Перевірка існування й активності користувача |
| `dbo.Problems` | SELECT, INSERT, UPDATE, DELETE | Читання власних задач і CRUD |
| `dbo.OlympiadProblems` | SELECT | Перевірка блокувальних зв’язків перед/після DELETE |

Для чинного Login / Users/me додатково зберігається SELECT на `dbo.UserProfiles`. Якщо окремо дозволена реєстрація через API, їй також потрібні наявні INSERT на `Users` та `UserProfiles`; це не нові вимоги Problems CRUD.

Приклад для адміністратора, **не виконувався**. Спочатку потрібно вибрати погоджену тестову БД і замінити `api_db_user` на її фактичного користувача, зіставленого з Login:

```sql
GRANT CONNECT TO [api_db_user];
GRANT SELECT ON OBJECT::dbo.Users TO [api_db_user];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Problems TO [api_db_user];
GRANT SELECT ON OBJECT::dbo.OlympiadProblems TO [api_db_user];
```

Синтаксис об’єктних дозволів: [Microsoft Learn — GRANT object permissions](https://learn.microsoft.com/en-us/sql/t-sql/statements/grant-object-permissions-transact-sql). Для CRUD не потрібні CREATE/ALTER/DROP, запуск міграцій, `db_owner` чи `sysadmin`. Прямі права запису в `OlympiadProblems` цій функції не потрібні. Збережено існуючі FK та каскад для прикладів; їх перевірку й виконання забезпечує БД за наявною схемою. Реальну достатність дозволів під конкретним SQL Login ще не перевіряли.

## Збірка та тести

З кореня репозиторію:

```powershell
dotnet build backend/CodeRank.Api/CodeRank.Api.csproj
dotnet test tests/CodeRank.Api.Auth.Tests/CodeRank.Api.Auth.Tests.csproj --filter FullyQualifiedName~ProblemsApiTests
dotnet test tests/CodeRank.Api.Auth.Tests/CodeRank.Api.Auth.Tests.csproj
dotnet test tests/CodeRank.Api.Seeding.Tests/CodeRank.Api.Seeding.Tests.csproj
```

Фактично виконано з .NET SDK 10.0.401 / runtime 10.0.12 та окремим `--artifacts-path` поза репозиторієм:

| Перевірка | Результат |
|---|---|
| Backend build | Успішно, 0 помилок і 0 попереджень |
| Нові Problems-тести | 78/78 |
| Повний HTTP-набір Auth / CORS / validation / Problems | 158/158: попередні 80 + 78 нових |
| Регресія Database Seeder | 26/26 |
| Разом у двох повних наборах | 184/184, невдалих і пропущених немає |

Окремий запуск 78 Problems-тестів повторно не додається до підсумкових 184. Використано наявний `AuthApiFactory`, TestServer і нову SQLite in-memory БД для кожного тесту; DbContext, конфігурацію підключення й джерело JWT-ключа замінено тестовими. `EnsureCreated` виконується лише для цієї ізольованої SQLite, як і в попередньому тестовому підході. Ні тестовий API, ні Seeder-тести до MSSQL не підключалися.

Покрито повний CRUD і DTO, серверне призначення власника, усі п’ять маршрутів без/із некоректним JWT, прострочений JWT і некоректний sub, неактивних/видалених користувачів, приховування чужих Id, валідацію POST/PUT, заборону серверних полів, nullable Difficulty та межу Title, конфлікт зв’язків і race після pre-check, чинний каскад прикладів та безпечні інші збої.

Після Backend-етапу користувач підтвердив успішну ручну перевірку CRUD на MSSQL у CodeRankAuthTest. Автоматизований race-тест вище перевіряє FK SQLite; окремого паралельного MSSQL-прогону чи відтворення SQL Server 547 цими автотестами не приписано.

На наступному [Frontend-етапі](PROBLEMS_FRONTEND.md) додано `/problems`, розширено лише Development CORS до GET/POST/PUT/DELETE для незмінного Origin та повторно виконано Backend-тести: 166/166 HTTP і 26/26 Seeder. CRUD-контролер, моделі та схема не змінені. Ручну наскрізну інтеграцію нового Frontend із MSSQL ще потрібно виконати. Пагінацію й управління зв’язками не додавали.
