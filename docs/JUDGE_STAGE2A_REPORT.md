# CodeRank · SCRUM-10, етап 2A. Приватні тести та Judge Payload API

База гілки `feature/judge-execution-stage2a` — `develop` після злиття PR №17 (`e430738`). Stage 1 зберігає формати `claim`, `heartbeat` і `complete`, правила відновлення черги та машинну автентифікацію. Звичайний API повертає `403 judge_execution_disabled` для запису вердикту. Виконання C++17 і Python 3 заплановане на етап 2B. [SCRUM-10](https://coderank.atlassian.net/browse/SCRUM-10) · [Протокол Stage 1](JUDGE_PROTOCOL_REPORT.md)

## 1. Дані, незмінність і контроль доступу

- `dbo.JudgeTestCases` зберігає приватні тести задачі. Поля — `Id`, `ProblemId`, `Position`, `Input`, `ExpectedOutput`, `IsActive`. Унікальна пара `(ProblemId, Position)` обмежує задачу 32 позиціями від 1 до 32, включно з неактивними. Вхідні дані й очікуваний вивід можуть бути порожніми. `ProblemExamples` і далі обслуговує лише публічні приклади.
- `dbo.JudgeTestSnapshots` зберігає один JSON-знімок на `SubmissionId`. На першій новій видачі, для якої є валідні тести, API зчитує активні записи за `Position` і фіксує їх у тій самій транзакції, що й lease. `HOLDLOCK` на SQL Server захищає набір від змін до коміту. Записи знімка зберігають ID, позицію, вхід і очікуваний вивід. Повторні запити, перезапуск та передавання спроби іншому Worker використовують зафіксований набір. Новий Submission отримує актуальний набір на момент своєї видачі. [Табличні підказки SQL Server](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17)
- Без валідного активного набору `claim` і `heartbeat` працюють за Stage 1, а payload повертає `409 judge_tests_unavailable`. Це також стосується чинної видачі, яка існувала до оновлення й не має знімка. Після завершення такого lease наступна нова видача може зафіксувати вже виправлений набір, якщо спроба ще підлягає повторній видачі.
- API перевіряє HTTPS, ключ ідентичності `CodeRankJudge`, статус `Running`, ID Worker, токен видачі та строк lease. Після читання сервер повторно звіряє строк, тому прострочення під час заблокованого читання теж дає відмову. Читання payload не збільшує лічильник спроб і не продовжує lease.
- Для кожного поля тесту діє ліміт 8 КіБ у UTF-8, для всього декодованого набору — 64 КіБ. Код має чинний ліміт 64 КіБ у UTF-8. SQL обмежує зберігання полів тестів до 16 384 байтів UTF-16, JSON-знімка — до 802 816 байтів UTF-16. Worker перевіряє ліміт JSON-відповіді 800 КіБ разом із її вмістом. Різні межі враховують кодування й екранування.
- Payload доступний лише через службовий API. Заголовки з ключем і токеном та тіла запитів і відповідей Judge API слід вилучити з логів проксі, HTTP-трасування й APM. Відповіді позначені `Cache-Control: no-store`. Worker журналює ID спроби, стан читання, тип помилки й HTTP-статус. Компрометація ідентичності Worker дає змогу отримати payload виданої йому спроби. Закінчення lease припиняє нові читання, а вже передані копії залишаються на довіреному вузлі.

## 2. Контракт payload

`GET /api/v1/judge/jobs/{id}/payload` приймає три заголовки. Тіло й query string мають бути порожніми.

```http
Authorization: CodeRankJudge <worker-key>
X-CodeRank-Worker: <worker-id>
X-CodeRank-Lease: <lease-uuid-from-claim>
```

Приклад форми відповіді на синтетичній спробі.

```json
{
  "id": 42,
  "problemId": 17,
  "language": "python3",
  "sourceCode": "<synthetic source>",
  "testCases": [
    { "id": 3, "position": 1, "input": "2 5\n", "expectedOutput": "7\n" }
  ]
}
```

| HTTP | Умова |
| --- | --- |
| `200` | Власник чинного lease отримує код і зафіксований набір. |
| `400` | Відсутній, невалідний або повторений заголовок lease, тіло чи query string. |
| `401` | API Judge вимкнений, транспорт або машинні дані недійсні. Student та Admin JWT також отримують `401`. |
| `409 lease_lost` | Невідома спроба, чужий Worker, невірний токен або сплив lease. |
| `409 judge_tests_unavailable` | Для чинного lease немає знімка тестів. |
| `422 judge_payload_invalid` | Збережений код або знімок не відповідає контракту. |
| `503` | Блокування черги тимчасово недоступне. Діє чинний `Retry-After: 5`. |

Worker читає payload при `JudgeWorker__FetchPayload=true`. За замовчуванням він виконує чинний цикл Stage 1. Клієнт зіставляє ID, задачу й мову з lease, перевіряє JSON, ліміти, порядок і унікальність тестів. Ліміт і 10-секундний тайм-аут діють і при потоковому читанні тіла. За `lease_lost` Worker повертається до claim. За `judge_tests_unavailable` або `judge_payload_invalid` він продовжує heartbeat і очікує втручання оператора. Тимчасові помилки мережі й протоколу повторюються з чинною затримкою. Після перезапуску той самий Worker ID відновлює чинний lease через claim і повторно читає той самий знімок. [Тайм-аут при `ResponseHeadersRead`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)

## 3. SQL-first оновлення та мінімальні дозволи

Міграція EF Core 10.0.12 `20261108084926_AddJudgeHiddenTests` додає дві таблиці, перевірки й унікальний індекс. FK тесту до `Problems` каскадно прибирає тести при дозволеному видаленні задачі. FK знімка до `Submissions` має `NO ACTION`. Відкат зупиняється, якщо будь-яка нова таблиця містить дані.

У копії SQL-скрипту DBA має явно встановити `@ExpectedDatabase` в ім’я погодженої БД і виконати весь файл в одному сеансі до запуску оновленого API. Скрипт перевіряє історію `AddJudgeLeases`, створює нові об’єкти й запис міграції в одній транзакції. При повторному запуску він звіряє наявність таблиць, полів, індексу та зовнішніх ключів. Рядки наявних таблиць, зокрема `Submissions`, залишаються без змін. [Захищений SQL-скрипт](../database/migrations/AddJudgeHiddenTests.sql)

Додаткові дозволи для наявного користувача БД `CodeRankAPI` з правами Stage 1.

```sql
GRANT SELECT ON OBJECT::dbo.JudgeTestCases TO [CodeRankAPI];
GRANT SELECT, INSERT ON OBJECT::dbo.JudgeTestSnapshots TO [CodeRankAPI];
-- Додати тільки якщо чинний SELECT на Submissions обмежений стовпцями і SourceCode відсутній:
-- GRANT SELECT (SourceCode) ON OBJECT::dbo.Submissions TO [CodeRankAPI];
```

Тести редагує DBA або окрема довірена роль. API потребує читання тестів та читання і вставки знімків. Worker має лише HTTPS-доступ до API. Права `UPDATE` і `DELETE` на нові таблиці API не потрібні. [Дозволи SQL Server](https://learn.microsoft.com/en-us/sql/t-sql/statements/grant-object-permissions-transact-sql?view=sql-server-ver17)

## 4. Збірка та результати тестів

| Перевірка | Фактичний результат |
| --- | --- |
| API, xUnit із SQLite in-memory та чинними loopback-тестами Kestrel | 322/322, з них 24 нові сценарії payload |
| Worker, xUnit із підставними HTTP-відповідями | 32/32, з них 22 нові сценарії клієнта та відновлення |
| Database Seeding, ізольовані xUnit-тести | 26/26 |
| Разом | 380/380 |
| Release solution і публікація API та Worker | Успішно. Збірка з 0 попереджень і 0 помилок. |
| EF Core `has-pending-model-changes` | Модель відповідає знімку міграції. |
| SQL Server ScriptDom | Синтаксично перевірено DBA-скрипт, вкладений DDL, згенеровані EF forward і rollback та запит з `HOLDLOCK`. Операції forward збігаються зі згенерованими EF Core. |
| Stage 2A на `CodeRankJudgeQA` та Ubuntu `judge01` | NOT RUN. Ручний QA нижче. |

Тести API покривають права доступу, відмову після спливу lease під час читання, повтор без нової спроби, стабільність набору після змін і перезапуску, відсутні та завеликі тести, безпечні помилки й відсутність приватних полів у публічній умові та історії. Тести Worker перевіряють невідповідні DTO, завелике тіло з `Content-Length` і без нього, зависання тіла після заголовків, втрату lease, restart і сумісність з лімітом Stage 1. Два наявні loopback-тести спочатку заблокувала пісочниця. Повторний запуск із дозволеним loopback увійшов у наведений підсумок.

```powershell
dotnet build CodeRank.Api.slnx -c Release
dotnet test tests/CodeRank.Api.Auth.Tests -c Release
dotnet test tests/CodeRank.Judge.Worker.Tests -c Release
dotnet test tests/CodeRank.Api.Seeding.Tests -c Release
dotnet publish backend/CodeRank.Api -c Release -o ./artifacts/stage2a/api
dotnet publish worker/CodeRank.Judge.Worker -c Release -p:UseAppHost=false -o ./artifacts/stage2a/worker
```

На Ubuntu для переносимого Worker потрібен `Microsoft.AspNetCore.App` 10.x. Пакет запускається командою `dotnet CodeRank.Judge.Worker.dll` із каталогу публікації.

## 5. Ручний QA на окремій MSSQL

1. Підготуй ізольовану `CodeRankJudgeQA` з SQL-first схемою та застосованою `AddJudgeLeases`. Виконай захищений скрипт під DBA. Звір два нові об’єкти та один запис `20261108084926_AddJudgeHiddenTests` в історії, повтори запуск і перевір, що кількість наявних спроб не змінилася. Видай права з розділу 3 лише в цій БД.
2. Для синтетичної задачі опублікованої активної олімпіади створи тести на позиціях 1, 2, 3 з активними 1 і 3 та неактивною 2. Увімкни `Judge__Enabled=true` для API з QA-підключенням і двома окремими машинними ідентичностями. Передай тестові ключі через захищене середовище. Створи одну синтетичну спробу через `POST /api/v1/olympiads/{olympiadId}/problems/{problemId}/submissions`.
3. Зроби claim і два GET під тим самим Worker. Приклад для PowerShell 7 використовує змінні середовища QA та виводить лише статус, ID і позиції. Звір у SSMS один знімок і `JudgeAttemptCount = 1`. Відредагуй активний тест, повтори GET і перевір незмінність набору. Перезапусти API або Worker з тією самою ідентичністю й повтори перевірку під чинним lease.

```powershell
$jobs = $env:JudgeWorker__ApiBaseUrl.TrimEnd('/') + '/api/v1/judge/jobs'
$headers = @{
    Authorization = 'CodeRankJudge ' + $env:JudgeWorker__Key
    'X-CodeRank-Worker' = $env:JudgeWorker__WorkerId
}
$job = Invoke-RestMethod -Method Post -Uri "$jobs/claim" -Headers $headers
if ($null -eq $job.id) { throw 'QA queue returned no submission.' }
$headers['X-CodeRank-Lease'] = $job.leaseToken
$result = Invoke-WebRequest -Uri "$jobs/$($job.id)/payload" -Headers $headers
$payload = $result.Content | ConvertFrom-Json
[pscustomobject]@{ Status = $result.StatusCode; Id = $payload.id; Positions = $payload.testCases.position -join ',' }
```

4. Підміни токен на випадковий UUID, спробуй із другим Worker ID та зачекай спливу lease. Очікуй однаковий `409 lease_lost`, а під JWT або неправильним ключем — `401`. Зроби новий claim після спливу lease та перевір новий токен і той самий набір тестів. Старий токен має й далі отримувати `409`.
5. На іншій синтетичній задачі перевір відсутній або завеликий активний набір. Claim і heartbeat успішні, payload повертає `409 judge_tests_unavailable`. Додай валідний набір до наступної видачі й звір `200`. Перевір також публічну умову, історію спроб, логи API й Worker та `403` від `complete`.
6. У двох SQL-сеансах QA перевір блокування запису тестів під час створення знімка й відсутність часткового набору після відкату транзакції. Після цього запусти опублікований Worker на ізольованій Ubuntu 24.04 VM з `JudgeWorker__FetchPayload=true` та HTTPS. Очікуй `Ready` і heartbeat. Один Worker ID використовує один активний процес.

## 6. План етапу 2B

`IIsolatedJudgeRunner` описує межу для майбутнього довіреного адаптера. Він прийматиме мову, вихідний код і вхідні дані. Результат міститиме статус і обмежений stdout. Очікуваний вивід, ключі й HTTP-клієнт залишатимуться у Worker. Реалізація Runner і публікація вердикту належать наступному PR.

1. Перевірити й зафіксувати версію `nsjail` на Ubuntu 24.04. Його Linux namespaces, seccomp і підтримка лімітів дають контрольовану основу. Запускати Runner як окремий непривілейований systemd-сервіс із власним UID і локальним IPC з обмеженими правами. Worker зберігає мережевий доступ до API, а код виконується в ізольованому network namespace без мережевого доступу. Окремі mount, PID та user namespaces, мінімальний набір syscalls, очищені змінні середовища та закриті зайві файлові дескриптори. [nsjail](https://github.com/google/nsjail)
2. На Ubuntu 24.04 врахувати обмеження непривілейованих user namespaces у AppArmor. Перевірити окремий профіль для Runner та делегування cgroup v2 лише його піддерева. Відхиляти запуск, коли namespaces, seccomp, AppArmor чи потрібні контролери не налаштовані. Зберегти глобальні політики ядра й відсутність root-привілеїв у студентського процесу. [Ubuntu 24.04 LTS](https://discourse.ubuntu.com/t/ubuntu-24-04-lts-noble-numbat-release-notes/39890/1) · [cgroup v2](https://docs.kernel.org/admin-guide/cgroup-v2.html)
3. Надати мінімальний rootfs тільки для читання. Для компіляції C++17 включити зафіксований `g++`, заголовки та бібліотеки. Для запуску C++ — потрібні runtime-бібліотеки, для Python 3 — інтерпретатор і стандартну бібліотеку. Запускати компілятор в окремій пісочниці та передавати до нової пісочниці лише артефакт. Для кожного тесту подавати один вхід через stdin, зіставляти обмежений stdout з очікуваним виводом у довіреному Worker. Приватний набір, конфігурація, хостові каталоги й сокети сервісів не монтуються. Запис дозволити лише в обмежений тимчасовий каталог спроби.
4. Окремо обмежити компіляцію й кожен запуск за wall time, CPU time і часткою CPU, пам’яттю, swap, кількістю процесів, розміром диска та stdout/stderr. За тайм-ауту, втрати lease чи перевищення ліміту знищувати весь cgroup через `cgroup.kill`, перевіряти, що він порожній, і видаляти тимчасові файли. Fail-closed також потрібен при збої очищення, встановлення ліміту або профілю.
5. До ввімкнення запису вердиктів перевірити на VM валідні C++17/Python 3, помилки компіляції, зациклення, fork bomb, переповнення stdout, читання файлів, мережеві спроби, відсутність витоків ключа, сплив lease, cleanup і відмову при вимкнених механізмах ізоляції. Почати з однієї синтетичної спроби одночасно. Спільні папки й буфер обміну VM залишити вимкненими.

## 7. Змінені файли й відкриті обмеження

| Каталог | Файли |
| --- | --- |
| `shared/judge/` | `JudgePayloadContract.cs` |
| `backend/CodeRank.Api/` | `CodeRank.Api.csproj`, `Program.cs`, `Contracts/Submissions/SubmissionRequests.cs`, `Controllers/JudgeJobsController.cs`, `Data/CodeRankDbContext.cs`, `Judge/JudgeQueue.cs`, `Judge/JudgeTestStore.cs`, `Models/JudgeTestCase.cs`, `Models/JudgeTestSnapshot.cs` |
| `backend/CodeRank.Api/Migrations/` | `20261108084926_AddJudgeHiddenTests.cs`, `20261108084926_AddJudgeHiddenTests.Designer.cs`, `CodeRankDbContextModelSnapshot.cs` |
| `database/migrations/` | `AddJudgeHiddenTests.sql` |
| `worker/CodeRank.Judge.Worker/` | `CodeRank.Judge.Worker.csproj`, `JudgeApiClient.cs`, `JudgeWorker.cs`, `WorkerSettings.cs`, `IIsolatedJudgeRunner.cs` |
| `tests/CodeRank.Api.Auth.Tests/` | `JudgeJobsApiTests.cs`, `JudgePayloadApiTests.cs` |
| `tests/CodeRank.Judge.Worker.Tests/` | `JudgeWorkerTests.cs`, `JudgePayloadClientTests.cs` |
| `docs/` | `JUDGE_STAGE2A_REPORT.md` |

Знімок займає місце на кожну підготовлену спробу. Для релізу треба погодити строк зберігання, очищення й доступ до резервних копій із приватними тестами. SQL Server-перевірки блокувань і дозволів та запуск на Ubuntu для Stage 2A ще належить провести на виділеному QA-середовищі. Запуск Worker на робочій черзі потребує окремого погодження.
