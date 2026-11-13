# CodeRank — Frontend

Vanilla JavaScript, HTML і CSS, без UI-фреймворків. Публічні сторінки `/login`, `/register`, `/olympiads` та захищені `/dashboard`, `/problems`; інтерфейс українською, адаптивний для ПК і смартфонів. [Problems Management: можливості, контракт і ручна інтеграція](../docs/PROBLEMS_FRONTEND.md).

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

Відкрити **http://localhost:5173/login** або публічну **http://localhost:5173/olympiads**; після входу — «Мої задачі» або **http://localhost:5173/problems**. Vite використовує рівно порт 5173 з `strictPort`; якщо він зайнятий, запуск завершується помилкою. SPA fallback забезпечує пряме відкриття та перезавантаження всіх п’яти маршрутів. Вказуйте саме `localhost`, оскільки `http://127.0.0.1:5173` є іншим Origin.

У режимі розробки API Base URL за замовчуванням — `https://localhost:7160`. Для локальної зміни задайте `VITE_API_BASE_URL` у `frontend/.env.local`.

```dotenv
VITE_API_BASE_URL=https://localhost:7160
```

Після зміни перезапустіть Vite; для production build змінна враховується під час збірки. Усі `VITE_*` значення публічні й потрапляють у браузер. Не додавайте сюди JWT Signing Key, SQL Connection String, паролі або токени.

Браузер повинен довіряти локальному ASP.NET HTTPS-сертифікату. Якщо потрібно, налаштуйте його локально командою `dotnet dev-certs https --trust`, потім перевірте доступ до `https://localhost:7160` у браузері. Під час реалізації Frontend системну довіру автоматично не змінювали.

## CORS

Backend у **Development** дозволяє лише Origin `http://localhost:5173`, методи GET/POST/PUT/DELETE та заголовки Content-Type/Authorization. Політика виконується між routing та authentication/authorization; cookie credentials не використовуються. У Production цей CORS не реєструється й не вмикається. Після оновлення коду перезапустіть Backend. Інший Origin потребує окремої явної політики, а не `AllowAnyOrigin`.

## Перевірки після об’єднання модулів — PR #12

Спільна навігація містить публічні «Олімпіади» та посилання «Мій профіль» / «Мої задачі» за наявності клієнтської сесії. `/dashboard` і `/problems` залишаються захищеними, `/olympiads` не вимагає JWT. Єдиний API-клієнт зберігає публічний GET без Authorization, усі п’ять захищених Problems-операцій та обробку PUT/DELETE 204 без читання JSON. Зміна маршруту скасовує запити обох модулів.

Після вирішення конфліктів із `origin/develop` фактично виконано:

| Перевірка | Результат |
|---|---|
| `npm run test:unit` | 125/125: 61 попередній + 26 Olympiads + 37 Problems + 1 перевірка спільного клієнта |
| `npm run test:browser` | 154/154: Auth 64 + Olympiads 28 + Problems 54 + спільна навігація 8 |
| `npm run build` | Production-збірка успішна |
| Backend build | 0 помилок, 0 попереджень |
| Backend HTTP: Auth / CORS / validation / Olympiads / Problems | 179/179 |
| Database Seeder | 26/26 |

Нові `navigation.spec.js` перевіряють переходи між модулями, Back/Forward, збереження сесії, захист Problems після публічної сторінки та скасування запиту в обох напрямках. Попередні тести збережено. Playwright виконано на окремому порту 5183 для desktop/mobile; додатково перевірено ширини 320/600/768 px і переглянуто скриншоти обох модулів. API у браузері замінено mocks, Backend використовує SQLite in-memory; реальні MSSQL-БД та User Secrets під час перевірки merge не змінювалися. Ручну наскрізну перевірку об’єднаного Frontend із MSSQL у цьому етапі не виконували.

## Олімпіади — практична №8

Публічна сторінка `/olympiads` доступна через спільну навігацію з Login, Register і Dashboard, за прямим URL та після F5. Дані надходять через `createApiClient().olympiads()` з `GET /api/v1/olympiads`, без Authorization, cookies чи вимоги входу. Помилка цього запиту, включно з 401, не очищає наявний токен і не перенаправляє користувача на Login.

Приклад контракту, без вбудовування цих значень у реальний інтерфейс:

```json
[
  {
    "id": 1,
    "title": "CodeRank Test Olympiad",
    "description": "Test published olympiad",
    "startsAtUtc": "2026-11-10T10:00:00Z",
    "endsAtUtc": "2026-11-10T12:00:00Z"
  }
]
```

Backend уже відбирає опубліковані записи й сортує їх за StartsAtUtc, потім Id. Frontend зберігає цей порядок, не визначає IsPublished і не додає фіктивних записів. DTO перевіряється до показу: потрібні коректні поля, UTC-мітки із `Z` та допустимий діапазон дат. `description: null` або порожній опис не створює тексту «null». Назва й опис виводяться через `textContent`; дати — через `Intl.DateTimeFormat` з `timeZone: 'UTC'` та явною позначкою UTC, незалежно від часового поясу пристрою.

Є Loading, картки, Empty («Поки немає доступних олімпіад») та Error з кнопкою «Спробувати ще раз». Обробляються HTTP-/мережеві помилки, таймаут 15 секунд і некоректні відповіді. Повторення виконує новий GET; зміна маршруту скасовує запит через наявний AbortController. Розмітка підтримує desktop/mobile, а навігація переноситься на окремий рядок на вузьких екранах.

### Залежність від Backend та ручна перевірка

[Backend PR №10](https://github.com/n25101882-svg/CodeRank/pull/10) та [Frontend PR №11](https://github.com/n25101882-svg/CodeRank/pull/11) злиті в develop; їхню функціональність включено до цієї гілки через merge `origin/develop`. Для ручної перевірки запустіть Backend із цієї гілки на `https://localhost:7160` (або за VITE_API_BASE_URL). CORS має дозволяти `http://localhost:5173`, а SQL-користувач API — читати `dbo.Olympiads`.

1. Запустіть Frontend командами з розділу «Локальний запуск» та відкрийте `http://localhost:5173/olympiads` без входу.
2. Перевірте GET у Network, картки з наявних даних або Empty для `[]`, UTC-час і пряме відкриття після F5.
3. Для перевірки Error тимчасово заблокуйте запит у DevTools або зупиніть лише власний тестовий API, потім відновіть доступ і натисніть «Спробувати ще раз».
4. Перевірте навігацію Login/Register/Dashboard та вузький viewport. Створювати записи в MSSQL для цієї перевірки не потрібно.

Наскрізну інтеграцію цієї сторінки з MSSQL під час реалізації не виконували; наведені нижче тести використовують тільки mocks.

### Перевірки попереднього етапу Olympiads List UI

Із `frontend`:

```powershell
$env:CODERANK_TEST_PORT = '5183'
npm run test
npm run test
npm run build
```

Виконано два послідовні повні прогони: кожен має **87/87 unit** і **92/92 Playwright** (desktop 1440×1000 та mobile 390×844). Це попередні 61 unit / 64 browser плюс 26 нових unit / 28 browser-перевірок. Додатково перевірено ширини 320, 600 і 768 px. Є сценарії прямого URL/F5, публічного GET без JWT, Loading, карток та nullable Description, Empty, помилок/таймауту й повторення, неправильного DTO, безпечного тексту, UTC у браузері з часовим поясом America/Los_Angeles, скасування запиту та збереження клієнтської сесії. Регресійні Login/Register/Dashboard-тести входять до кожного повного прогону. Production-збірка `npm run build` успішна.

Спільний `tests/browser/api-mocks.js` перехоплює запити всіх браузерних наборів: дозволяє мережеве завантаження лише ресурсів власного Vite, усі API-відповіді підміняє й блокує інші Origin. Mocks підтримують GET/POST/PUT/DELETE та відповіді 204 без JSON. Service Workers у тестах вимкнені. До справжнього API та MSSQL ці тести не звертаються. `CODERANK_TEST_PORT` змінює лише ізольований тестовий сервер; Development-порт 5173 і Backend CORS залишаються без змін.

## Сценарії та клієнтська сесія

- Реєстрація надсилає лише email, password, firstName, lastName. Успіх 201 переводить на Login; токен не видається і сесія автоматично не створюється.
- Успішний Login зберігає accessToken окремо під ключем `coderank.accessToken` у `sessionStorage`. Dashboard викликає `/api/v1/users/me` з Bearer-заголовком і показує дані лише після успішної перевірки. JWT не передається через URL, не декодується як доказ автентичності та не записується до журналу.
- Несекретні чернетки зберігаються під час input/change: `coderank.draft.login` містить лише email, `coderank.draft.register` — firstName, lastName, email. Після reload і повернення до форми значення відновлюються. Пароль виключено явним whitelist навіть при увімкненому показі пароля; повний FormData/DTO до Storage не записується. `localStorage` не використовується.
- Успішна реєстрація очищає Register-чернетку; успішний Login та Logout очищають обидві чернетки. Після помилки API несекретні поля залишаються для повторення. За заблокованого/переповненого сховища збереження чернетки може бути недоступне; форма продовжує працювати. Дані чернеток доступні JavaScript у тій самій вкладці так само, як інші дані sessionStorage.
- За відсутнього токена або HTTP 401 профіль не показується, токен очищається, відкривається Login. Інші помилки дозволяють повторити запит без показу неперевірених даних.
- Logout очищає клієнтську сесію цієї вкладки. Це **не серверне відкликання JWT**: вже виданий токен залишається чинним на сервері до expiry.
- `sessionStorage` доступний JavaScript, тому не захищає від викрадення токена через XSS. Дані користувача виводяться через `textContent`; сторонніх скриптів і UI-бібліотек немає. Зберігання триває в межах сесії вкладки; браузер може відновлювати сесію або спочатку копіювати її до вкладки з opener. Синхронний вихід з усіх вкладок/пристроїв не реалізовано.
- Refresh tokens, серверне відкликання сесій та інші функції поза Authentication MVP не додавалися.

Реєстрація вимагає Unicode-літеру в імені та прізвищі й пароль завдовжки 12–128 одиниць UTF-16. Frontend і Backend відхиляють поширені паролі, прості повтори та числові послідовності, зокрема суцільну лічбу через межу розрядності. Непослідовні цифрові паролі дозволені. Дані спільного blocklist зберігаються у `shared/validation/registration-policy.json`. API отримує оригінальний пароль і хешує його через PasswordHasher.

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

Підтверджені результати етапу Authentication MVP та виправлення CR-UX-001 (попередній етап):

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

[Звіт із тестування Authentication](../docs/TEST_REPORT_AUTH.md) містить історію попередніх перевірок. Актуальні результати виправлень наведено в [PR №13](https://github.com/n25101882-svg/CodeRank/pull/13).

`npm run preview` на порту 5173 дозволяє локально оглянути `dist` після збірки. Production-збірка без `VITE_API_BASE_URL` надсилає запити на origin поточної сторінки. У режимі preview API потребує проксі на цьому origin або явно заданого API URL під час збірки та відповідного CORS. Для релізу використовується один HTTPS origin із проксі `/api/` і SPA fallback на `index.html`. [План розгортання](../docs/PRACTICAL_11.md#план-розгортання)

Довідка: [Vite server options](https://vite.dev/config/server-options), [ASP.NET Core CORS](https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0).
