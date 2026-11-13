# Практична робота №11 · CodeRank v1.0.0

**Тема роботи.** Закриття останніх завдань з беклогу проєкту. Стабілізація роботи додатку. Перевірка працездатності розгорнутого додатку.

**Мета роботи.** Зафіксувати обсяг MVP, стабілізувати релізну гілку та підготувати перевірку критичних сценаріїв на розгорнутому стенді.

Стан на 13.11.2026. Вихідний коміт гілки `release/v1.0.0` збігався з `develop` на `fcd89b9` після squash merge PR №23. Релізна копія відповідає цьому дереву. Зміни та прогони виконано в окремій робочій копії, оскільки основний checkout був на `feature/judge-worker-integration` із локальною зміною `.gitignore`. [Релізна гілка](https://github.com/n25101882-svg/CodeRank/tree/release/v1.0.0) · [Вихідний коміт](https://github.com/n25101882-svg/CodeRank/commit/fcd89b9ceb7ab8649c2f79083ade6668f11d757b) · [PR №23](https://github.com/n25101882-svg/CodeRank/pull/23)

**Готовність.** Код пройшов передрелізні перевірки й готовий до погодженого розгортання з вимкненим Judge. Приймання v1.0.0 залежить від перевірки цільового MSSQL, HTTPS і ST1–ST4 на розгорнутій версії.

## Feature Freeze

| Модуль | Межі v1.0.0 |
| --- | --- |
| Реєстрація, вхід, профіль і вихід | Серверна валідація реєстрації, хешування паролів, обмеження частоти Login, JWT і захист профілю. Вихід очищає клієнтську сесію. |
| Олімпіади й задачі | Каталог опублікованих олімпіад, список видимих задач, умови й публічні приклади. SCRUM-8 у Jira має статус Done. |
| Редактор власних задач | Створення, перегляд, оновлення і видалення доступних для редагування задач. Доступ перевіряється на сервері. |
| Адміністрування олімпіад | Активний адміністратор керує чернетками, публікацією та прив’язками задач. SCRUM-12 має статус Done. |
| Надсилання розв’язків | Збереження розв’язку для видимої задачі активної олімпіади, отримання `Queued` і власна історія. SCRUM-9 має статус Code Review / QA. За вимкненого Judge черга не виконує код. |
| Автоматична оцінка Python 3.12 та C++17 | Docker Worker інтегровано й перевірено на ізольованому QA. У публічній конфігурації оцінювання вимкнено. SCRUM-10 має статус In Progress. |

Статуси Jira звірено 13.11.2026. Скріншот дошки слід зробити після того, як команда відобразить погоджені статуси та позначки Post-MVP. [SCRUM-8](https://coderank.atlassian.net/browse/SCRUM-8) · [SCRUM-9](https://coderank.atlassian.net/browse/SCRUM-9) · [SCRUM-10](https://coderank.atlassian.net/browse/SCRUM-10) · [SCRUM-12](https://coderank.atlassian.net/browse/SCRUM-12)

### Post-MVP

- **SCRUM-11.** Таблиця лідерів. Статус Jira на дату звіту — To Do. Поза обсягом v1.0.0. [SCRUM-11](https://coderank.atlassian.net/browse/SCRUM-11)
- **Публічне оцінювання.** Виправити Issue №24, повторити Docker QA та E2E і пройти окремий security review. Для автоматичного оцінювання у v1.0.0 зафіксовано лише ізольований QA. [Issue №24](https://github.com/n25101882-svg/CodeRank/issues/24)
- **NsJail Worker.** PR №22 відкритий і перебуває поза обсягом релізу. [PR №22](https://github.com/n25101882-svg/CodeRank/pull/22)

## Стабілізація та Code Cleanup

| Ділянка | Результат |
| --- | --- |
| Frontend | Виправлено production fallback API. Без `VITE_API_BASE_URL` збірка використовує origin сторінки. Розробка зберігає локальний API, а явно заданий URL має пріоритет. Наявний клієнт обробляє таймаути, перервані запити, коди HTTP і некоректні відповіді. Форми та сторінки показують повідомлення з можливістю повтору там, де запит можна повторити. |
| Backend | Локальний `CodeRank.Api.http` тепер перевіряє чинний `GET /api/v1/olympiads` через HTTPS замість шаблонного WeatherForecast. Глобальний обробник помилок повертає Problem Details і trace ID для внутрішньої помилки. Для розгортання оновлено SQL baseline та інструкції EF Core. |
| SQL baseline | Прибрано автоматичний перехід у `CodeRankSeedTest`. Скрипт вимагає явно задану цільову БД і наявність шести таблиць SQL-first. Єдина транзакція охоплює службову таблицю історії та запис baseline. |
| Журнали | У робочих джерелах перевірено налагоджувальні виклики, тимчасові коментарі та мертвий код за пошуковими ознаками. Шаблонний запит знайдено й виправлено. Діагностика Worker і перевірка підключення до БД потрібні для запуску та QA. Запис сирого stderr обмежений opt-in тестом із синтетичною програмою в тестовій збірці. |
| Секрети та середовища | Application settings містять несекретні параметри. JWT і ключі Judge читаються з приватної конфігурації, а рядок MSSQL задається окремо. `.gitignore` покриває `.env`, `.env.*`, `appsettings.Local.json`, `node_modules`, `dist` та результати Playwright. У відстежуваному дереві пошук і ручний перегляд виявили лише заповнювачі, тестові дані та повідомлення валідації. Production працює на одному origin. Development CORS застосовується в середовищі Development. |

## Перевірки релізної копії

Прогони 13.11.2026 виконано на Windows із SDK .NET 10.0.401 і Node.js 24.22.1. Backend-тести використовували ізольовану SQLite і два сценарії Kestrel на loopback. Playwright перехоплював API-запити.

| Перевірка | Виконана команда | Статус і фактичний результат |
| --- | --- | --- |
| Restore .NET, перша спроба | `dotnet restore CodeRank.Api.slnx --source "$env:NUGET_PACKAGES" -p:NuGetAudit=false -m:1 --disable-build-servers` | **FAIL**. Sandbox відхилив доступ до користувацького NuGet.Config. Спроби з `--configfile` до виділення локального AppData дали ту саму помилку. |
| Restore .NET після налаштування середовища | `dotnet restore CodeRank.Api.slnx --configfile $configPath --source "$env:NUGET_PACKAGES" -p:NuGetAudit=false -m:1 --disable-build-servers` | **PASS**. Використано локальний кеш пакетів, окремі `APPDATA` і `LOCALAPPDATA` та `NuGet.Offline.Config` із порожнім переліком джерел. Такий самий restore виконано для чотирьох тестових проєктів. |
| Release build API, Worker, offline Sandbox | `dotnet build CodeRank.Api.slnx -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false` | **PASS**. 0 попереджень, 0 помилок. |
| Публікація пакета API | `dotnet publish backend/CodeRank.Api/CodeRank.Api.csproj -c Release --no-restore -p:UseAppHost=false -p:UseSharedCompilation=false -o ../publish-api --disable-build-servers` | **PASS**. Зібрано framework-dependent пакет. Запуск пакета на Linux — **NOT RUN**. |
| API unit та integration | `dotnet tests/CodeRank.Api.Auth.Tests/bin/Release/net10.0/CodeRank.Api.Auth.Tests.dll -nocolor -xml ../CodeRank.Api.Auth.Tests.verified.xml` | **PASS**. 351/351, включно з двома Kestrel-тестами. |
| Seeder | `dotnet tests/CodeRank.Api.Seeding.Tests/bin/Release/net10.0/CodeRank.Api.Seeding.Tests.dll -nocolor -xml ../CodeRank.Api.Seeding.Tests.xml` | **PASS**. 26/26. |
| Judge Worker | `dotnet tests/CodeRank.Judge.Worker.Tests/bin/Release/net10.0/CodeRank.Judge.Worker.Tests.dll -nocolor -xml ../CodeRank.Judge.Worker.Tests.xml` | **PASS**. 114 пройшли. 10 Docker Desktop opt-in сценаріїв у цьому прогоні пропущено. |
| Offline Sandbox unit | `dotnet tests/CodeRank.Judge.Sandbox.Tests/bin/Release/net10.0/CodeRank.Judge.Sandbox.Tests.dll -nocolor -xml ../CodeRank.Judge.Sandbox.Tests.xml` | **PASS**. 151/151. |
| Frontend restore і production build | `npm.cmd ci --offline --no-audit --no-fund`, `npm.cmd run build` з `frontend/` | **PASS**. Vite 8.3.5 створив `dist`. У зібраному JS без `VITE_API_BASE_URL` перевірено `window.location.origin` і відсутність `localhost:7160`. |
| Frontend unit | `npm.cmd run test:unit` з `frontend/` | **PASS**. 169/169. |
| Frontend Playwright | `npm.cmd run test:browser` з `frontend/`, `CODERANK_TEST_PORT=55173`, `PLAYWRIGHT_CHANNEL=chrome` | **PASS**. 210/210, desktop і mobile. |
| Production `dist`, прямі URL та F5 | `node ../../production-preview-check.mjs` з `frontend/` в ізольованій копії | **PASS**. Chrome, Vite preview на `127.0.0.1:55174`, дев’ять маршрутів після F5, гостьові редиректи й 18 API-запитів на origin сторінки. У браузері не було помилок. API повертав синтетичні дані. |
| EF Core model snapshot | `dotnet ef migrations has-pending-model-changes --project backend/CodeRank.Api/CodeRank.Api.csproj --context CodeRankDbContext --configuration Release --no-build` | **PASS**. Змін моделі після останньої міграції немає. Перевірка виконана з фіктивним рядком підключення. |
| Статична перевірка SQL | `pwsh -NoProfile -File ../check-release-sql.ps1` | **PASS**. ScriptDom розібрав усі п’ять файлів. Перевірено guard цілі й відсутність `USE`. Застосування SQL на цільовій MSSQL — **NOT RUN**. |
| Секрети та Git | `python ../scan-release-secrets.py --index`, `git diff --cached --check`, `git status --short --branch` | **PASS**. Відстежувані файли й індекс перевірено за сигнатурами JWT, приватних ключів, токенів та рядків підключення. Збіги переглянуто. Релізна копія чиста після фіксації змін. |
| Deployment і ST1–ST4 | Команди й кроки нижче | **NOT RUN**. Очікують погодженого стенда. |

Тестові збірки виконувалися для кожного `$project` командою `dotnet build "tests/$project/$project.csproj" -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false`. Перший прогін API командою `dotnet tests/CodeRank.Api.Auth.Tests/bin/Release/net10.0/CodeRank.Api.Auth.Tests.dll -nocolor -xml ../CodeRank.Api.Auth.Tests.xml` мав **FAIL**. Із 351 тесту 165 зупинилися на доступі до Windows Event Log, два Kestrel-запити завершилися передчасно. Для успішного повтору `Logging__EventLog__LogLevel__Default=None` задано тільки в тестовому процесі.

### Ручний QA на Windows · 13.11.2026

Результати надані командою для попереднього ручного прогону Docker Judge. Точний журнал команд цього прогону до звіту не додано. Команди повторення є в інструкції Worker. [Docker QA](JUDGE_DOCKER_PROVIDER.md#реальний-qa)

| Сценарій | Результат |
| --- | --- |
| Opt-in Docker Desktop QA | **PASS**, 10/10. |
| E2E на `CodeRankJudgeQA` | **PASS**. Python Accepted, C++17 Accepted, WrongAnswer і TimeLimitExceeded. |
| Docker Engine unavailable | **PASS**. Зафіксовано SystemError і коректну зупинку Worker. |
| Після відновлення Engine | **PASS**. Наступна заявка отримала Accepted. |
| Cleanup | **PASS**. Нуль залишкових контейнерів, recovery marker відсутній. |

## Release readiness checklist

- [x] Обсяг v1.0.0 зафіксовано, Post-MVP перелічено, базу релізної гілки звірено з `develop`.
- [x] Перевірено Release build, пакет API, Frontend `dist`, тести API, Worker і браузера.
- [x] Перевірено конфігурацію, правила ігнорування та ймовірні секрети у відстежуваних файлах.
- [x] Підготовлено схему розгортання з вимкненим Judge, план відкату і ST1–ST4.
- [ ] Підтвердити Linux-хост, домен, TLS, приватний доступ MSSQL і відповідального за резервну копію.
- [ ] На погодженому стенді перевірити історію міграцій, запуск API та всі ST1–ST4.
- [ ] Додати URL і скриншоти, отримати фінальне погодження та створити тег для прийнятого коміту.

### Блокери

| Обсяг | Умова приймання |
| --- | --- |
| Розгортання | Окремий дозвіл, погоджений хост, HTTPS-домен і підтверджене приватне з’єднання з MSSQL. |
| Приймання v1.0.0 | Звірена схема MSSQL, успішний запуск Linux API і ST1–ST4 з фактичними результатами й доказами. |
| Публічний Judge | Відкритий Issue №24. Поки cleanup не підтверджено в усіх потрібних сценаріях і не завершено security review, діють вимкнені прапорці Judge та обмеження доступу до його API. [Issue №24](https://github.com/n25101882-svg/CodeRank/issues/24) |

## План розгортання

### Підготовка хоста й SQL Server

Після дозволу перевірити `pentiumserver` як кандидат на стенд. Звірити ОС, архітектуру, вільні порти, доступність Nginx і `Microsoft.AspNetCore.App 10.0.x`, права для окремого системного користувача, домен і сертифікат. Команди для інвентаризації — `uname -m`, `cat /etc/os-release`, `dotnet --list-runtimes`, `nginx -v` та `ss -lnt`. Підтвердити доступність адреси стенда для викладача. Якщо методичка вимагає саме хмарний хостинг, погодити використання `pentiumserver` із викладачем. [ASP.NET Core і Nginx](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0)

1. Узгодити SQL Server та окрему БД стенда. Дозволити SQL-порт лише з API-хоста по приватній мережі. Використати довірений SQL-сертифікат з `Encrypt=True` і `TrustServerCertificate=False`. Для API виділити обліковий запис з об’єктними правами на потрібні таблиці, DBA виконує DDL окремо. [Права Problems](PROBLEMS_API.md) · [Права Admin](ADMIN_OLYMPIADS_REPORT.md) · [Права Submissions](SUBMISSIONS_REPORT.md)
2. Зняти резервну копію й перевірити можливість відновлення. У SQL-сесії DBA звірити `SELECT DB_NAME();` та історію міграцій. Для порожньої БД застосувати `database/CodeRank_ER_MVP_CreateSchema.sql`, потім `InitialBaseline.sql`, `AddSubmissions.sql`, `AddJudgeLeases.sql`, `AddJudgeHiddenTests.sql`. Для наявної БД застосувати лише відсутні узгоджені кроки після звірки схеми. У копіях міграцій заповнити `@ExpectedDatabase`. Для `sqlcmd` передати погоджену БД через `-d`, файл через `-i`, ввімкнути `-b` і зупинити послідовність при ненульовому exit code. [Початкова схема](../database/CodeRank_ER_MVP_CreateSchema.sql) · [Baseline](../database/migrations/InitialBaseline.sql)
3. Звірити `dbo.__EFMigrationsHistory` за чотирма ID `20261031164420_InitialBaseline`, `20261107144141_AddSubmissions`, `20261107162834_AddJudgeLeases`, `20261108084926_AddJudgeHiddenTests` і наявність відповідних таблиць. Для smoke підготувати опубліковану олімпіаду з видимою задачею та дозволити створення тестового акаунта через Register. Адміністративний доступ надати окремому погодженому акаунту. Seeder у Production заблокований.

### Пакети й служба

На погодженій збірочній машині з чистої релізної копії підготувати `release-out/api` та `frontend/dist`. Записати SHA коміту для перевірки й відкату. `VITE_API_BASE_URL` має бути відсутнім у середовищі і Vite env-файлах при збиранні для одного origin. [Vite та змінні збірки](https://vite.dev/guide/env-and-mode)

```bash
git status --short --branch
git rev-parse HEAD
dotnet publish backend/CodeRank.Api/CodeRank.Api.csproj -c Release -p:UseAppHost=false -o release-out/api
(cd frontend && npm ci && npm run build)
```

Зберегти обидва пакети в окремому каталозі версії й підготувати посилання `/srv/coderank/current` на цю версію. Підготувати непривілейованого користувача `coderank` із правами читання пакета. Звірити шлях `dotnet` і доступність `127.0.0.1:5000`. Шаблон `coderank-api.service` використовує окреме ім’я служби.

```ini
[Unit]
Description=CodeRank ASP.NET Core API
After=network-online.target
Wants=network-online.target

[Service]
User=coderank
WorkingDirectory=/srv/coderank/current/api
EnvironmentFile=/etc/coderank/api.env
ExecStart=/usr/bin/dotnet /srv/coderank/current/api/CodeRank.Api.dll
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

`/etc/coderank/api.env` — файл поза Git із правами `0600` для root. systemd передає його змінні процесу. Нижче шаблон із демонстраційним доменом. Реальні значення `ConnectionStrings__DefaultConnection` і `Jwt__SigningKey` заповнює відповідальний за розгортання.

```dotenv
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5000
AllowedHosts=coderank.example.com
ReverseProxy__KnownProxies__0=127.0.0.1
ConnectionStrings__DefaultConnection="<приватний рядок MSSQL>"
Jwt__SigningKey="<Base64 щонайменше 32 випадкових байтів>"
Judge__Enabled=false
Judge__Completion__Enabled=false
```

Перед стартом перевірити MSSQL з тією самою конфігурацією під користувачем служби. Очікуються `Database connection successful` і exit code `0`. Параметр діагностики діє лише для цього запуску. Для звичайної служби `Database__CheckConnection` залишити відсутнім або встановити `false`. [systemd-run](https://www.freedesktop.org/software/systemd/man/latest/systemd-run.html)

```bash
sudo systemd-run --wait --pipe --collect \
  --property=User=coderank \
  --property=WorkingDirectory=/srv/coderank/current/api \
  --property=EnvironmentFile=/etc/coderank/api.env \
  /usr/bin/dotnet /srv/coderank/current/api/CodeRank.Api.dll --Database:CheckConnection=true
```

Стенд v1.0.0 запускає Frontend і API. Для окремого Windows Worker, якщо він встановлений, `JudgeWorker__EvaluateSubmissions=false`. Тестові облікові дані та Judge-ключі належать ізольованому QA-середовищу.

### Nginx і приймання

Використати один HTTPS origin. Додати правила нижче до погодженого TLS virtual host із дійсним сертифікатом та HTTP→HTTPS перенаправленням. `/srv/coderank/current/frontend` містить вміст `dist`. Nginx передає фактичну адресу клієнта й HTTPS-схему з однієї довіреної loopback-адреси. [Nginx try_files](https://nginx.org/en/docs/http/ngx_http_core_module.html#try_files) · [Forwarded headers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)

```nginx
root /srv/coderank/current/frontend;

location ~* ^/api/v1/judge(?:/|$) {
    return 404;
}

location /api/ {
    proxy_pass http://127.0.0.1:5000;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
}

location /assets/ {
    try_files $uri =404;
}

location / {
    try_files $uri $uri/ /index.html;
}
```

1. Після перевірки конфігурації `sudo nginx -t` застосувати погоджений unit через `sudo systemctl daemon-reload`, `sudo systemctl restart coderank-api` та `sudo systemctl reload nginx`. У `systemctl status coderank-api --no-pager` і `journalctl -u coderank-api -n 40 --no-pager` підтвердити нормальний запуск. Через `ss -lnt` звірити loopback-порт API та HTTPS-порт Nginx.
2. З машини QA перевірити `curl -fsS https://coderank.example.com/api/v1/olympiads`, прямий `/olympiads/<id>/problems/<id>` і HTTPS без обходу перевірки сертифіката. Переконатися, що `/api/v1/users/me` без JWT відповідає `401`, а `POST /api/v1/judge/jobs/claim` на зовнішньому origin відхиляється Nginx з `404`.
3. Виконати ST1–ST4 на цьому ж HTTPS origin. Якщо будь-який крок дає FAIL, записати крок і код або trace ID. Для відкату повернути попередню версію API і статичних файлів разом, перезапустити API й повторити smoke. Якщо зміни БД несумісні з попередньою версією, DBA виконує погоджене відновлення з резервної копії.

## Smoke Test Suite

**URL розгорнутого застосунку** — [додати HTTPS-посилання після розгортання]. **Коміт стенда й дата** — [заповнити після прогону].

| ID | Дія на стенді | Очікуваний результат | Статус |
| --- | --- | --- | --- |
| ST1 | Відкрити `/` у чистому вікні, перевірити Console та Network. Відкрити `/olympiads`, перейти до задачі й натиснути F5. | Відкривається Login, каталог і пряма сторінка задачі працюють. HTML, JS, CSS і API надходять через HTTPS. У консолі немає JS, CORS або mixed-content помилок. | **NOT RUN** |
| ST2 | Зареєструвати тестовий акаунт і увійти. Відкрити `/dashboard`. | Register повертає `201`, Login — `200`, а запит `/api/v1/users/me` — `200` з даними створеного тестового профілю. | **NOT RUN** |
| ST3 | На `/problems` створити унікальну тестову задачу. Перевірити її появу в списку й натиснути F5. Після фіксації доказу прибрати створений тестовий запис. | POST повертає `201`. Задача з’являється без перезавантаження й залишається після F5. Наявність після нового GET підтверджує збереження на стенді. | **NOT RUN** |
| ST4 | Натиснути «Вийти». Відкрити напряму `/dashboard` і `/problems`. Перевірити статус `/api/v1/users/me` без заголовка Authorization. | Браузер очищає сесію та відкриває `/login`. Захищений API відповідає `401`. | **NOT RUN** |

### Докази для здачі

- **Рисунок 1.** [Додати скриншот Scrum-дошки з Done і Post-MVP.]
- **Рисунок 2.** [Додати ST1 з HTTPS URL, Console й Network.]
- **Рисунок 3.** [Додати ST2 та Dashboard тестового акаунта. Приховати тіло відповіді з JWT.]
- **Рисунок 4.** [Додати створену тестову задачу зі ST3 після F5.]
- **Рисунок 5.** [Додати Login після виходу й код `401` для ST4.]

Вимоги до здачі звірено з практичною №11 методички «Пр роб-1.pdf», с. 23–25. Після погодженого розгортання треба зафіксувати URL, підсумки ST1–ST4 і скриншот дошки. Вони потрібні для завершення практичної й погодження тега v1.0.0.
