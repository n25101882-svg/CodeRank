# Authentication API — CodeRank

## Стан реалізації та перевірок

Три ендпоінти Authentication API перенесено до основного репозиторію в гілці `feature/auth-api`. Схема SQL-first, шість моделей, CodeRankDbContext та міграції збережені. Підписаний Access Token видається лише після успішного Login; реєстрація автоматично не авторизує користувача.

Залежності відновлено з NuGet. Збірку та обидва набори тестів виконано в основному репозиторії; результати наведено нижче. Окремо підтверджено ручну інтеграцію Authentication API з Microsoft SQL Server у тестовій БД CodeRankAuthTest та цільові сценарії JWT Security Audit через локальний HTTPS.

| Перевірка | Фактичний результат |
|---|---|
| Відновлення NuGet-пакетів API та тестів | Успішно; JwtBearer та Mvc.Testing 10.0.12 доступні |
| `dotnet build` API | Успішно, 0 помилок і 0 попереджень |
| `dotnet test` Authentication/JWT | 39/39 успішні, 0 пропущених; HTTP через WebApplicationFactory/TestServer, SQLite in-memory |
| `dotnet test` Seeder разом із новим API | 26/26 успішні, 0 пропущених; SQLite in-memory |
| Разом автоматизованих тестів | 65/65 успішних; ручні MSSQL/JWT-перевірки до цієї кількості не входять |
| Окремі перевірки CLI та старту процесу | 7/7 успішні; опис нижче |
| Ручна MSSQL-інтеграція та JWT Security Audit | Підтверджено окремо на локальному HTTPS; результати наведено у відповідному розділі |
| Запис у CodeRankDB / CodeRankSeedTest | Не виконувався |

Обидва набори фактично виконані командою `dotnet test`, із компіляцією проєктів і збереженням TRX. Перевірку синтаксису замість збірки не використовували. Для кожної фабрики HTTP-тестів створюється окрема SQLite in-memory БД; підключень до MSSQL у цих тестах немає.

Під час цього оновлення документації код і тести не змінювалися та відповідають останній перевіреній версії. Збірку й автоматизовані тести повторно не запускали; наведені 65/65 є збереженими результатами попередніх запусків.

Окремо перевірено сім запусків зібраного API як процесу: відсутній JWT-ключ; некоректний ключ; коректний JWT при недоступній БД; діагностика БД без JWT; Seeder `--dry-run` без JWT; Seeder `--apply` без JWT; заборона Seeder у Production без JWT. Використано виключно тимчасове оточення дочірніх процесів і зарезервований локальний порт без SQL Server. Кожен процес завершився з очікуваним кодом 1 та відповідним безпечним повідомленням; HTTP-сервер не запускався. Діагностика й обидва режими Seeder дійшли до перевірки БД без помилки JWT-конфігурації. Контрольні паролі, ключ і Connection String у виводі були відсутні. Жодного запису чи підключення до CodeRankDB / CodeRankSeedTest не виконано; успішний CLI apply на MSSQL під час цієї перевірки не запускався.

## Ендпоінти

| Метод і шлях | Доступ | Успіх | Типові відмови |
|---|---|---|---|
| POST `/api/v1/auth/register` | `[AllowAnonymous]` | 201 + UserResponse | 400: дані/зайві поля; 409: email уже існує |
| POST `/api/v1/auth/login` | `[AllowAnonymous]` | 200 + Access Token та expiry | 400: формат запиту; 401: неправильні дані або неактивний акаунт |
| GET `/api/v1/users/me` | `[Authorize]` | 200 + актуальні дані користувача | 401: відсутній/невалідний JWT, видалений чи неактивний користувач |

Непередбачені помилки повертають 500 з безпечним ProblemDetails і traceId, без SQL, повідомлення провайдера, паролів або хешів. Форма ValidationProblemDetails може містити службові поля ASP.NET Core.

## Реєстрація

JSON-запит нижче використовує змінну Postman для пароля. Дані — лише приклад формату; вони не підтверджують створення користувача в будь-якій БД.

```json
{
  "email": "new-user@coderank.test",
  "password": "{{registrationPassword}}",
  "firstName": "Test",
  "lastName": "Student"
}
```

Приклад структури відповіді 201:

```json
{
  "id": 1,
  "email": "new-user@coderank.test",
  "firstName": "Test",
  "lastName": "Student",
  "role": "User"
}
```

- Email обов’язковий, до 256 символів, проходить EmailAddress validation; зовнішні пробіли обрізаються, NormalizedEmail = `Trim().ToUpperInvariant()`, як у Seeder.
- Пароль: 12–128 символів; він не обрізається. Хешування виконує ASP.NET Core `PasswordHasher<User>`, сумісний із Seeder.
- Ім’я і прізвище обов’язкові, до 100 символів; значення з одних пробілів відхиляються, зовнішні пробіли перед збереженням обрізаються.
- Поля `role`, `isActive`, `id`, `passwordHash` та інші невідомі поля відхиляються як 400. Новий користувач завжди має роль User і активний стан; масового прив’язування моделі БД немає.
- Users та UserProfiles додаються графом сутностей одним `SaveChangesAsync()`. Для реляційного провайдера це одна атомарна операція: збій вставлення профілю не залишає окремого користувача. Це спирається на [стандартні транзакції EF Core](https://learn.microsoft.com/en-us/ef/core/saving/transactions).
- До вставлення виконується перевірка email. Конфлікт SQL Server 2601/2627 також обробляється; 409 повертається після підтвердження, що потрібний email справді існує. Інші помилки не видаються за дублювання email.
- PasswordHash та EF navigation properties у відповідь не серіалізуються. Location відповіді — `/api/v1/users/me`, для доступу до якого потрібно виконати Login.

## Login та JWT

```json
{
  "email": "new-user@coderank.test",
  "password": "{{registrationPassword}}"
}
```

Структура відповіді 200; значення нижче є позначками формату, а не реальним токеном чи зафіксованою датою:

```json
{
  "accessToken": "<SIGNED_JWT_FROM_LOGIN>",
  "expiresAtUtc": "<UTC_TIMESTAMP_IN_ISO_8601>",
  "tokenType": "Bearer"
}
```

Login нормалізує email, читає користувача з БД та перевіряє PasswordHash стандартним PasswordHasher. Неіснуючий користувач, неправильний пароль, пошкоджений хеш і неактивний акаунт отримують однакове повідомлення 401: `Invalid credentials.` Для неіснуючого користувача теж виконується перевірка випадково створеного dummy-хешу. Автоматичного перезаписування чи оновлення хешу під час Login немає.

Токен підписується HS256 стандартною бібліотекою, містить `sub` (Id), `role`, `iat`, `jti`, `iss`, `aud`, `nbf` та `exp`. Час expiresAtUtc відповідає exp токена з точністю до секунди. Підпис, дозволений алгоритм HS256, Issuer, Audience і термін дії перевіряє стандартний [JwtBearer](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication). ClockSkew встановлено в 0; годинники серверів мають бути синхронізовані. Деталі помилки в WWW-Authenticate не розкриваються.

## JWT-конфігурація та локальний запуск

Публічні параметри в `appsettings.json`: `Jwt:Issuer = CodeRank`, `Jwt:Audience = CodeRank.Client`, `Jwt:AccessTokenMinutes = 15`. Термін дії дозволено налаштувати від 1 до 60 хвилин.

`Jwt:SigningKey` має містити Base64 від щонайменше 32 криптографічно випадкових байтів (не звичайний пароль). Джерела ключа — **лише User Secrets у Development або змінна середовища `Jwt__SigningKey`**; ключ із appsettings або CLI не використовується. Environment має вищий пріоритет за User Secrets. `.env.example` є лише довідковим шаблоном; ASP.NET Core не читає .env автоматично.

З кореня репозиторію, без розкриття ключа в тексті команд або Git:

```powershell
$jwtSigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet user-secrets set 'Jwt:SigningKey' $jwtSigningKey --project backend/CodeRank.Api
Remove-Variable jwtSigningKey
```

Альтернатива для поточного процесу PowerShell:

```powershell
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Генерація нового ключа робить раніше видані токени недійсними. Не змінюйте ключ щоразу під час запуску, якщо потрібна чинність попередніх токенів. Не додавайте заповнені змінні, JWT, паролі чи User Secrets до репозиторію.

Для ручних операцій запису використовуйте **окрему тестову MSSQL-БД** із поточною SQL-схемою та baseline і налаштовуйте підключення через User Secrets. Для підтвердженої ручної інтеграції вже використано CodeRankAuthTest; API не створює цю БД автоматично. Без окремого погодження не спрямовуйте реєстрацію на CodeRankDB чи CodeRankSeedTest.

З кореня репозиторію:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet restore backend/CodeRank.Api/CodeRank.Api.csproj
dotnet build backend/CodeRank.Api/CodeRank.Api.csproj --no-restore
dotnet run --project backend/CodeRank.Api --launch-profile https
```

Без необхідної JWT-конфігурації нормальний HTTP-запуск завершується з кодом 1 та безпечним повідомленням **до** перевірки БД/HTTP. За коректної JWT-конфігурації збережено обов’язковий DatabaseConnectionCheck перед HTTP. `UseAuthentication()` розташовано перед `UseAuthorization()`.

Seeder і діагностика БД повертають результат раніше за завантаження JWT-настройок, тому ключ для них не потрібен. Наприклад, лише діагностика:

```powershell
dotnet run --project backend/CodeRank.Api --no-launch-profile -- --Database:CheckConnection=true
```

Seeder --seed / --dry-run / --apply зберігає окремий порядок запуску. Цей етап не додає автоматичних міграцій або наповнення під час старту API.

## Перевірка через Postman

1. Створіть локальні змінні `baseUrl` (для наявного https-профілю: `https://localhost:7160`), `registrationPassword` (власний пароль 12–128 символів) і `accessToken`. Переконайтеся, що Connection String API спрямований на окрему дозволену тестову БД. Використовуйте довірений локальний HTTPS-сертифікат.
2. Виконайте **POST `{{baseUrl}}/api/v1/auth/register`**, Body → raw → JSON: приклад реєстрації вище. Очікується 201; повторний запит з тим самим email — 409. Поле `role: Admin` або неправильні вхідні дані — 400.
3. Виконайте **POST `{{baseUrl}}/api/v1/auth/login`**, Body → raw → JSON: приклад Login вище. Очікується 200. Збережіть accessToken лише в локальній змінній; неправильний пароль повинен дати 401.
4. Виконайте **GET `{{baseUrl}}/api/v1/users/me`**, Authorization → Bearer Token → `{{accessToken}}`. Очікуються Id, Email, FirstName, LastName та актуальна Role з БД без PasswordHash.
5. Повторіть GET без Authorization, зі зміненим токеном або після його закінчення — очікується 401. Не вставляйте справжні токени в Git, приклади документації або відкриті сервіси декодування.

Локальний Postman post-response script для Login (не зберігати заповнені змінні в Git):

```javascript
if (pm.response.code === 200) {
  pm.environment.set('accessToken', pm.response.json().accessToken);
}
```

## Автоматизовані перевірки

```powershell
dotnet test tests/CodeRank.Api.Auth.Tests/CodeRank.Api.Auth.Tests.csproj
dotnet test tests/CodeRank.Api.Seeding.Tests/CodeRank.Api.Seeding.Tests.csproj
```

39 успішних Authentication-тестів покривають: реєстрацію та профіль; нормалізацію; попередньо наявний email; порушення реального SQLite UNIQUE під час імітації конкурентного вставлення; відкат при збої профілю; валідацію; відмову самопризначення Admin; Login; помилковий пароль/невідомого/неактивного користувача; валідний, відсутній, підроблений, прострочений JWT; неправильні Issuer/Audience/ключ; unsigned token; невалідний sub; видалення/деактивацію; актуальну роль; відсутній профіль; некоректну JWT-конфігурацію. Окремий сценарій відхиляє правильно підписаний тим самим тестовим ключем HS384-токен: сервер дозволяє лише HS256. Тестовий ключ генерується випадково й має 64 байти, достатні для обох алгоритмів; помилка створення HS384-токена не підміняє перевірку його відхилення сервером.

SQLite-тестовий DbContext адаптує лише SQL-діалект. `EnsureCreated()` використовується виключно для приватних in-memory тестових БД. Продукційний DbContext не змінено. SQL Server-специфічні помилки 2601/2627 та concurrency на MSSQL ще потребують перевірки на окремій дозволеній БД; результати SQLite не видаються за MSSQL-інтеграцію.

## Ручне інтеграційне тестування MSSQL та JWT Security Audit

### Середовище та інтеграція

Ручну інтеграцію виконано з Microsoft SQL Server в окремій тестовій БД **CodeRankAuthTest**. API працює в локальному Development-середовищі через HTTPS. Використано SQL Server Login **CodeRankAPI** з обмеженими правами SELECT та INSERT на Users і UserProfiles. Наведені нижче результати реєстрації, Login і звірки даних у SSMS підтверджені користувачем; вони відокремлені від автоматизованих SQLite-тестів.

| Перевірка | Підтверджений результат |
|---|---|
| POST `/api/v1/auth/register` | HTTP 201 Created |
| Перевірка записів через SSMS | Дані користувача в Users і відповідного профілю в UserProfiles підтверджено SQL-запитом |
| POST `/api/v1/auth/login` | Успішний Login із видачею JWT |
| GET `/api/v1/users/me` із чинним JWT | HTTP 200 OK; повернуто правильні дані користувача |

### Ручні JWT-перевірки

Запити виконувалися до `https://localhost:7160/api/v1/users/me`. Відсутній JWT і модифікований підпис перевірені вручну за підтвердженням користувача. Audience та Expiration додатково перевірено одноразовими локальними клієнтами зі стандартною JWT-бібліотекою. Ключ отримувався з Development User Secrets; ключі й токени не виводилися та не записувалися до репозиторію.

| Сценарій | Фактична відповідь |
|---|---|
| JWT відсутній | HTTP 401 Unauthorized |
| Підпис JWT модифікований | HTTP 401 Unauthorized |
| JWT має неправильний Audience | HTTP 401 Unauthorized |
| JWT прострочений | HTTP 401 Unauthorized |

**Audience Validation.** Сформовано криптографічно правильно підписаний HS256 JWT поточним Development Signing Key: `iss = CodeRank`, `aud = CodeRank.OtherService`, `sub = 1`, `exp` на 5 хвилин у майбутньому. Коректність підпису локально підтверджено стандартною JWT-бібліотекою. Запит із цим токеном повернув HTTP 401. Контрольний токен з такими самими параметрами, але правильним `aud = CodeRank.Client`, отримав HTTP 200. Отже, причину відмови ізольовано до неправильного Audience.

**Token Expiration.** Сформовано криптографічно правильно підписаний HS256 JWT: `iss = CodeRank`, `aud = CodeRank.Client`, `sub = 1`; `exp` на 5 хвилин у минулому, `iat` на 15 хвилин у минулому, `nbf` на 10 хвилин у минулому. Таким чином, і `iat`, і `nbf` передували `exp`. Підпис, Issuer та Audience простроченого токена окремо перевірено стандартною бібліотекою з вимкненою перевіркою строку дії лише в локальному клієнті; конфігурацію API не змінювали. API повернув HTTP 401. Контрольний токен із тими самими параметрами та `exp` на 5 хвилин у майбутньому отримав HTTP 200. Причину відмови ізольовано до прострочення токена.

### Межі перевірки

Результати підтверджують перелічені ручні сценарії MSSQL-інтеграції та JWT-валідації. Це не повноцінний penetration test і не перевірка всіх можливих JWT-атак. Ручна інтеграція не підтверджує обробку одночасних реєстрацій або специфічних конфліктів SQL Server 2601/2627; ці сценарії залишаються окремими перевірками. Ручні результати не зараховуються до 65 автоматизованих тестів SQLite.

Поточне редагування документації не змінює SQL-схему, міграції, User Secrets або дані БД та не потребує повторного виконання операцій реєстрації.

## Права MSSQL та межі MVP

- Runtime: CONNECT; SELECT на Users і UserProfiles; INSERT на Users та UserProfiles для реєстрації. UPDATE/DELETE, DDL або db_owner цьому етапу не потрібні.
- `/users/me` ідентифікує Id лише за перевіреним `sub`, а дані та активність читає з БД. За відсутнього legacy-профілю FirstName/LastName повертаються як null. Нові реєстрації завжди мають профіль.
- Немає refresh tokens, logout/revocation store, email verification, password reset, lockout або rate limiting. Перед зовнішнім розгортанням потрібен окремий захист Login від перебору й рішення щодо життєвого циклу сесій.
- Claims ролі в уже виданому JWT діють до expiry. Поточний `/users/me` читає актуальну роль з БД, але майбутні Admin-ендпоінти потребуватимуть власного рішення щодо негайного відкликання ролей/токенів.
- Без налаштованого HTTPS та власного надійного JWT-ключа цю навчальну реалізацію не слід вважати завершеним зовнішнім сервісом автентифікації.
