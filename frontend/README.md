# CodeRank — Frontend Authentication MVP

Vanilla JavaScript, HTML і CSS, без UI-фреймворків. Сторінки `/login`, `/register`, `/dashboard`; інтерфейс українською, адаптивний для ПК і смартфонів.

## Локальний запуск

Потрібні Node.js 22.12+ з npm та .NET 10 для Backend. Команди нижче виконуються з кореня репозиторію у двох терміналах.

Backend:

```powershell
dotnet run --project backend/CodeRank.Api --launch-profile https
```

Наявні Connection String і `Jwt:SigningKey` налаштовуються лише в User Secrets або оточенні Backend, як описано в [AUTH_API.md](../docs/AUTH_API.md). Для ручних сценаріїв використовується окрема CodeRankAuthTest; ці команди не створюють і не наповнюють БД. Перед реєстрацією перевірте, що Backend справді підключений до цієї тестової БД. SQL-схему, міграції та User Secrets Frontend не змінює.

Frontend:

```powershell
cd frontend
npm install
npm run dev
```

Відкрити **http://localhost:5173/login**. Vite використовує рівно порт 5173 з `strictPort`; якщо він зайнятий, запуск завершується помилкою. SPA fallback забезпечує пряме відкриття та перезавантаження всіх трьох маршрутів. Вказуйте саме `localhost`, оскільки `http://127.0.0.1:5173` є іншим Origin.

API Base URL за замовчуванням — `https://localhost:7160`. Для локальної зміни створіть `frontend/.env.local`:

```dotenv
VITE_API_BASE_URL=https://localhost:7160
```

Після зміни перезапустіть Vite; для production build змінна враховується під час збірки. Усі `VITE_*` значення публічні й потрапляють у браузер. Не додавайте сюди JWT Signing Key, SQL Connection String, паролі або токени.

Браузер повинен довіряти локальному ASP.NET HTTPS-сертифікату. Якщо потрібно, налаштуйте його локально командою `dotnet dev-certs https --trust`, потім перевірте доступ до `https://localhost:7160` у браузері. Під час реалізації Frontend системну довіру автоматично не змінювали.

## CORS

Backend у **Development** дозволяє лише Origin `http://localhost:5173`, методи GET/POST та заголовки Content-Type/Authorization. Політика виконується між routing та authentication/authorization; cookie credentials не використовуються. У Production цей CORS не реєструється й не вмикається. Після оновлення коду перезапустіть Backend. Інший Origin потребує окремої явної політики, а не `AllowAnyOrigin`.

## Сценарії та клієнтська сесія

- Реєстрація надсилає лише email, password, firstName, lastName. Успіх 201 переводить на Login; токен не видається і сесія автоматично не створюється.
- Успішний Login зберігає accessToken окремо під ключем `coderank.accessToken` у `sessionStorage`. Dashboard викликає `/api/v1/users/me` з Bearer-заголовком і показує дані лише після успішної перевірки. JWT не передається через URL, не декодується як доказ автентичності та не записується до журналу.
- Несекретні чернетки зберігаються під час input/change: `coderank.draft.login` містить лише email, `coderank.draft.register` — firstName, lastName, email. Після reload і повернення до форми значення відновлюються. Пароль виключено явним whitelist навіть при увімкненому показі пароля; повний FormData/DTO до Storage не записується. `localStorage` не використовується.
- Успішна реєстрація очищає Register-чернетку; успішний Login та Logout очищають обидві чернетки. Після помилки API несекретні поля залишаються для повторення. За заблокованого/переповненого сховища збереження чернетки може бути недоступне; форма продовжує працювати. Дані чернеток доступні JavaScript у тій самій вкладці так само, як інші дані sessionStorage.
- За відсутнього токена або HTTP 401 профіль не показується, токен очищається, відкривається Login. Інші помилки дозволяють повторити запит без показу неперевірених даних.
- Logout очищає клієнтську сесію цієї вкладки. Це **не серверне відкликання JWT**: вже виданий токен залишається чинним на сервері до expiry.
- `sessionStorage` доступний JavaScript, тому не захищає від викрадення токена через XSS. Дані користувача виводяться через `textContent`; сторонніх скриптів і UI-бібліотек немає. Зберігання триває в межах сесії вкладки; браузер може відновлювати сесію або спочатку копіювати її до вкладки з opener. Синхронний вихід з усіх вкладок/пристроїв не реалізовано.
- Refresh tokens, серверне відкликання сесій та інші функції поза Authentication MVP не додавалися.

Реєстрація вимагає хоча б одну Unicode-літеру в імені та прізвищі й відхиляє поширені/шаблонні паролі. Довжини залишено 100 для імен і 12–128 для пароля (UTF-16). Правило не забороняє всі цифрові паролі та не вимагає uppercase/спецсимволів. Порівняльна копія для перевірки слабкості може нормалізуватися, проте пароль у POST не змінюється. Дані політики спільні з Backend у `shared/validation/registration-policy.json`; Frontend використовує нативний Web Crypto, Backend — SHA-256 для lookup та незмінений PasswordHasher для зберігання. Login старих акаунтів не посилювався.

## Збірка та перевірки

Із каталогу `frontend`:

```powershell
npm run build
npm run test:unit
npm run test:browser
# Або обидва набори тестів:
npm test
```

Браузерні тести використовують Playwright: на Windows — встановлений Google Chrome, на інших платформах — Chromium після `npx playwright install chromium`. Інший встановлений браузер можна вибрати через `$env:PLAYWRIGHT_CHANNEL = 'msedge'`; для bundled Chromium — `chromium`. Порт 5173 має бути вільний; тестовий setup запускає Vite у власному процесі runner та зупиняє його через `server.close()`. Перевіряються desktop і mobile viewport, прямі URL/reload, форми, стани очікування, 401, Logout, недоступне сховище, скасування запитів та безпечне відображення рядків. Усі API-запити цих тестів перехоплюються: до MSSQL вони не звертаються.

Якщо на 5173 уже запущено сайт для ручних тестів, залиште його працювати та виберіть порт лише для тестового runner:

```powershell
$env:CODERANK_TEST_PORT = '5183'
npm run test:browser
```

Це не змінює стандартний порт `npm run dev` або справжню CORS-політику Backend. В ізольованих браузерних тестах відповіді API залишаються mocks.

Backend, із кореня репозиторію:

```powershell
dotnet test tests/CodeRank.Api.Auth.Tests/CodeRank.Api.Auth.Tests.csproj
dotnet test tests/CodeRank.Api.Seeding.Tests/CodeRank.Api.Seeding.Tests.csproj
```

До попередніх 39 Auth-тестів додано 9 CORS-перевірок: конкретний Development Origin, preflight і відсутність CORS-дозволу в Production/Testing. Backend-тести працюють з ізольованими SQLite in-memory базами. Їх результати та браузерні mocks не заміняють ручний сценарій реєстрації/Login з CodeRankAuthTest. Frontend-тести не змінюють цю БД.

Фактично виконано для цієї реалізації:

| Перевірка | Результат |
|---|---|
| `npm install` | Успішно; npm audit: 0 vulnerabilities |
| `npm run build` | Успішно |
| `npm run test:unit` | 61/61 після виправлення чернеток: попередні 53 + 8 нових |
| `npm run test:browser` | 64/64: попередні 48 + 8 нових сценаріїв × 2 viewport; Chrome, порт 5183 |
| Auth + CORS + validation Backend | Попередні 80/80; на UX-етапі не перезапускали, Backend-код не змінено |
| Database Seeder | Попередні 26/26; на UX-етапі не перезапускали |

Звичайна Backend-збірка спочатку була заблокована запущеним API (`MSB3027`, зайнятий apphost.exe). Після цього обидва `dotnet test` успішно виконано з окремим `--artifacts-path` без зупинення API. Для звичайної локальної збірки спочатку зупиніть попередній екземпляр API, потім запустіть його з оновленим CORS. У sandbox для браузерів використовувався окремий тимчасовий каталог; системні налаштування й особисті профілі браузерів не змінювалися.

Готовність: Frontend підготовлено до ручного тесту з CodeRankAuthTest після перезапуску Backend і перевірки довіри HTTPS-сертифікату. Наскрізну реєстрацію/Login через браузер із реальною MSSQL-БД у цьому етапі не виконували, щоб не змінювати тестову БД.

[Повний звіт із тестування Authentication](../docs/TEST_REPORT_AUTH.md) містить ручну історію, поточні 125 успішних Frontend-тестів, попередні результати Backend та докази. CR-UX-001 має Verified після повторного PASS F5, перевірок очищення й відсутності пароля у Storage. Rate Limiting залишається Open. Ручний наскрізний XSS-тест відкладено (NOT RUN); для підміни JWT sub 2 → 1 зафіксовано лише перенаправлення на Login, без непідтвердженого HTTP-коду.

`npm run preview` на порту 5173 дозволяє локально оглянути `dist` після збірки. Для розгортання потрібен static hosting зі SPA fallback на `index.html` і окремо погоджена production-політика доступу до API; Development CORS у Production не відкривається.

Довідка: [Vite server options](https://vite.dev/config/server-options), [ASP.NET Core CORS](https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0).
