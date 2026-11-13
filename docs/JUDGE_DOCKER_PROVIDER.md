# CodeRank · Stage 2B2 · Docker Execution Provider

## Архітектура

Windows Worker отримує чинний lease і знімок приватних тестів через HTTPS. `SubmissionGrader` порівнює stdout у Worker, а `LeasedSubmissionJudge` поновлює lease та повторює ідентичний запит completion за тимчасової помилки. API приймає completion лише для явно дозволених машинних ідентичностей. Чинна атомарна операція черги зберігає один вердикт для lease. Інфраструктурна відмова має пріоритет над результатом тесту та зупиняє Worker. SQL-схема й контракти API збережені. [Джерело логіки оцінювання](https://github.com/n25101882-svg/CodeRank/tree/ca2f8102dfcf48f23a325f051efd2155e3fdda6e/worker/CodeRank.Judge.Worker)

Docker Desktop використовується лише для компіляції та виконання. Worker керує локальним Linux Engine через Windows pipe `dockerDesktopLinuxEngine`. На початку оцінювання він перевіряє підтримку обмежень і штатного seccomp. Попередньо завантажені `python:3.12-slim` і `gcc:14` перевіряються на відсутність оголошених томів. Контейнери заявки використовують локальний ID образу, розв’язаний до першого запуску. [Docker Engine info](https://docs.docker.com/reference/cli/docker/system/info/)

Через stdin передаються байти коду або C++ ELF і лише поточний тестовий ввід. C++17 компілюється в одному контейнері, ELF тримається в обмеженому буфері Worker. Кожен тест має новий контейнер і власний tmpfs. Worker зіставляє UTF-8 stdout з очікуваним виводом, нормалізуючи CRLF і одне завершальне LF. Пробіли та решта порожніх рядків значущі.

## Ізоляція та межі

- `--network none`, `--ipc none`, приватний cgroup namespace, UID/GID `65534:65534`, `--cap-drop ALL`, `no-new-privileges` і штатний seccomp.
- Read-only rootfs і tmpfs `/tmp` до 32 МіБ з `nosuid,nodev`. Виконуваний tmpfs потрібен лише для C++ ELF. Студентський контейнер не отримує bind mounts, Docker socket, каталоги CodeRank, машинний ключ чи очікувані результати. Окремий порожній CLI config виключає автоматичне передавання host proxy settings. [Docker tmpfs](https://docs.docker.com/engine/storage/tmpfs/) · [Docker proxy configuration](https://docs.docker.com/engine/cli/proxy/)
- 1 CPU, 32 PID, 768 МіБ RAM для компіляції та 256 МіБ для тесту. Загальний ліміт RAM і swap дорівнює ліміту RAM. Ліміти stdout і stderr — 64 КіБ кожен, з окремим 16 МіБ буфером для ELF. Docker log driver `none`. [Docker resource limits](https://docs.docker.com/engine/containers/resource_constraints/)
- Worker обмежує attach 15 с для компіляції та 2 с для тесту, включно зі стартом і передаванням даних. `timeout` усередині контейнера має резервну межу 20 с або 5 с для випадку зникнення клієнта. Сесія заявки обмежена 15 хвилинами. До початку студентського етапу відмова класифікується як `SystemError`.
- Docker Desktop призначений тут для локального QA із синтетичними програмами. Конфігурація не є атестованим sandbox для публічного виконання недовіреного коду. Резервний `timeout` працює всередині того самого контейнера. Головне підтвердження завершення дає доступний Docker Engine.

Контейнери мають унікальне ім’я `coderank-judge-<execution>` та мітки `io.coderank.judge.worker` й `io.coderank.judge.execution`. Перед стартом Worker звіряє ID, ім’я і мітки. Після виконання або скасування він видаляє тільки перевірений ID і підтверджує його відсутність у списку контейнерів. Команди очищення мають власні дедлайни й завершуються незалежно від токена lease.

До `docker create` Worker записує локальний `pending-container` під ексклюзивним блокуванням у `%LOCALAPPDATA%\CodeRank\JudgeDocker\worker-<id>`. Після підтвердженого очищення цей файл видаляється. Якщо Engine недоступний, видалення неможливо підтвердити. Worker фіксує інфраструктурну відмову, а маркер блокує запуск під тим самим Windows-акаунтом і Worker ID до відновлення. Для кожного Worker ID використовуй один Windows-акаунт і один екземпляр Worker.

## Запуск на Windows 11

Використовуй PowerShell 7, Docker Desktop у режимі Linux containers із WSL2 і .NET 10 SDK. Запусти перевірки з кореня репозиторію.

```powershell
$dockerExe = (Get-Command docker.exe -ErrorAction Stop).Source
$judgeEngine = 'npipe:////./pipe/dockerDesktopLinuxEngine'
& $dockerExe --host $judgeEngine version --format '{{.Server.Os}} {{.Server.Version}}'
& $dockerExe --host $judgeEngine info --format '{{.OSType}}|{{.MemoryLimit}}|{{.SwapLimit}}|{{.CPUCfsQuota}}|{{.PidsLimit}}|{{json .SecurityOptions}}'
$imageFormat = '{"Id":{{json .Id}},"Os":{{json .Os}},"Config":{{json .Config}}}'
foreach ($tag in 'python:3.12-slim', 'gcc:14') {
    $imageJson = & $dockerExe --host $judgeEngine image inspect --format $imageFormat $tag
    if ($LASTEXITCODE -ne 0) { throw "Image inspect failed for $tag." }
    $image = $imageJson | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    if ($null -eq $image -or $image['Id'] -cnotmatch '^sha256:[0-9a-f]{64}$' -or $image['Os'] -cne 'linux' -or
        $image['Config'] -isnot [System.Collections.IDictionary]) { throw "Unexpected image metadata for $tag." }
    $volumes = $image['Config']['Volumes']
    if ($null -ne $volumes -and ($volumes -isnot [System.Collections.IDictionary] -or $volumes.Count -ne 0)) {
        throw "Image $tag declares volumes or has invalid volume metadata."
    }
    [pscustomobject]@{ Image = $tag; Id = $image['Id']; OS = $image['Os']; Volumes = 0 }
}
```

Очікуються `linux`, чотири значення `true`, опція `name=seccomp,profile=builtin` і `0` томів у кожного образу. Worker приймає відсутній `Config.Volumes`, `null` або порожній об’єкт. Оголошені томи, некоректні метадані та stdout понад 64 КіБ призводять до `SystemError` до створення контейнера. Образи завантажуй окремим явним кроком із довіреного джерела. Провайдер використовує `--pull never`.

Для повного E2E підготуй лише `CodeRankJudgeQA`. Звір `SELECT DB_NAME()` та наявність `AddJudgeLeases` і `AddJudgeHiddenTests`. Використовуй синтетичну олімпіаду й приватні тести та чинні мінімальні SQL-права. [Підготовка QA БД](JUDGE_STAGE2A_REPORT.md#5-ручний-qa-на-окремій-mssql)

У терміналі QA API після перевірки QA Connection String та довіреного локального HTTPS задай окремий машинний ключ. Ключ має бути випадковим Base64 від 32 байтів. Вводь однаковий тестовий ключ у два термінали.

```powershell
$env:Judge__Enabled = 'true'
$env:Judge__Completion__Enabled = 'true'
$env:Judge__Completion__Workers__0 = 'dockerqa01'
$qaKey = Read-Host 'QA machine key' -AsSecureString
$env:Judge__Workers__dockerqa01__CurrentKey = [Net.NetworkCredential]::new('', $qaKey).Password
Remove-Variable qaKey
dotnet run --project backend/CodeRank.Api -c Release --launch-profile https
```

У другому терміналі запусти Worker напряму на Windows. Якщо QA API використовує інший HTTPS-порт, підстав його адресу.

```powershell
$env:JudgeWorker__ApiBaseUrl = 'https://localhost:7160/'
$env:JudgeWorker__WorkerId = 'dockerqa01'
$env:JudgeWorker__FetchPayload = 'true'
$env:JudgeWorker__EvaluateSubmissions = 'true'
$env:JudgeWorker__DockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
$qaKey = Read-Host 'QA machine key' -AsSecureString
$env:JudgeWorker__Key = [Net.NetworkCredential]::new('', $qaKey).Password
Remove-Variable qaKey
dotnet run --project worker/CodeRank.Judge.Worker -c Release
```

API і Worker також читають свої локальні User Secrets у `Development`. Для Worker використовуй префікс `JudgeWorker:` і `DOTNET_ENVIRONMENT=Development`. Для API машинний ключ має шлях `Judge:Workers:dockerqa01:CurrentKey`. Змінні середовища мають пріоритет. Після QA закрий виділені термінали або видали змінні з ключами.

## Реальний QA

Почни з opt-in тестів із синтетичними програмами. Вони працюють із реальним локальним Docker Engine, а перевірка втрати lease використовує імітований HTTP-відгук `409 lease_lost`. MSSQL і робочі облікові дані для цього набору не потрібні. Якщо Docker CLI відсутній у `PATH`, перед запуском задай його повний шлях у `JudgeWorker__DockerExecutable`.

```powershell
$env:CODERANK_DOCKER_QA = '1'
try {
    dotnet run --project tests/CodeRank.Judge.Worker.Tests -c Release -- -class CodeRank.Judge.Worker.Tests.DockerDesktopQaTests -showLiveOutput
    if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop QA failed.' }
} finally {
    Remove-Item Env:CODERANK_DOCKER_QA
}
```

Для розбору відмови скопіюй рядки `DockerExecutableSource=`, `DockerOperation=` та `FailureStage=` з першого невдалого сценарію. `FailureStage` і `Reason` показують, яку перевірку відхилено. Записи Docker CLI містять `End`, `ExitCode`, розмір захоплених потоків і `StderrHint` із фіксованого переліку фраз. Підказка шукається в перших 1024 байтах stderr. Вивід програми та аргументи команд залишаються поза журналом.

Для `container start` поле `Stdin` відображає результат локального запису й закриття потоку. `ReadyMarker` показує, чи починається stderr із маркера завершеного staging, чи перед ним є інші байти, чи має він CRLF, чи відсутній. За невдалого запуску процесу `NativeStartError`, якщо доступний, містить числовий код ОС.

### Тимчасовий запис stderr для Python Accepted

На локальній QA-станції запусти окремий тест із фіксованою програмою `print(2 + 2)` та порожнім stdin тест-кейса. Потрібні обидві змінні opt-in. Якщо Docker CLI встановлено поза `PATH`, збережи перевірене значення `JudgeWorker__DockerExecutable`.

```powershell
$env:CODERANK_DOCKER_QA = '1'
$env:CODERANK_DOCKER_QA_RAW_STDERR = '1'
try {
    dotnet run --project tests/CodeRank.Judge.Worker.Tests -c Release -- -method CodeRank.Judge.Worker.Tests.DockerDesktopQaTests.Python_accepted -showLiveOutput
    if ($LASTEXITCODE -ne 0) { throw 'Python Accepted QA failed.' }
} finally {
    Remove-Item Env:CODERANK_DOCKER_QA, Env:CODERANK_DOCKER_QA_RAW_STDERR
}
```

Скопіюй `PythonAcceptedCreate`, `PythonAcceptedStderr` і `FailureStage`, якщо він є. `PythonAcceptedCreate` показує кількість CR і LF в аргументі `/bin/sh -c`. `PythonAcceptedStderr` показує тип завершення, exit code, стан stdin та до 256 байтів stderr у вигляді екранованого тексту й hex. Hex зберігає точні байти, зокрема символи завершення рядка. Запис вмикається лише для attach цієї фіксованої програми в тестовій збірці.

Для зняття початкового stderr повтори той самий тест у окремому checkout діагностичного коміту з CRLF у `DockerExecutionProvider.cs`. На виправленій гілці очікуються `ScriptCR=0`, `End=Exited`, `ExitCode=0`, `Stdin=Written`, `ReadyMarker=Prefix` і результат `Accepted`. [Діагностичний коміт до LF-виправлення](https://github.com/n25101882-svg/CodeRank/commit/77cb1e090058d0cd563d2a08d25a8696db5952ff)

### Перевірка CRLF у staging-скрипті

На `fb7012a` Windows QA підтвердив `FailureStage=Start`, `Reason=ReadyPrefixMissing`, exit code 2 і 36 байтів stderr. Ручний запуск пройшов create, attach, inspect і cleanup. Контрольний дочірній процес на Windows отримав скрипт через `ArgumentList` без зміни байтів і зчитав фіксований payload через `BaseStream` до EOF. Фактичні прапорці attach у Worker — `container start --attach --interactive`.

У збірці старого Worker із CRLF команда починалася з `set -eu\r\n`. C# зберігає CR у багаторядковому літералі. Для `dash` він стає недопустимим прапорцем `set` ще до staging. Код `dash` пояснює exit code 2 і прогнозований 36-байтний текст `coderank: 1: set: Illegal option -\r\n`. Для звірки точних байтів з Windows QA потрібен наведений вище opt-in прогін. [Рядкові літерали C#](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/lexical-structure#6456-string-literals) · [Парсер параметрів dash](https://sources.debian.org/src/dash/0.5.12-12/src/options.c/) · [Код помилок dash](https://raw.githubusercontent.com/herbertx/dash/master/src/error.c)

Worker нормалізує до LF лише вбудовані shell-скрипти перед `container create`. Регресійні перевірки Python, компіляції C++ і запуску ELF відхилили старі скрипти в CRLF-збірці. Після виправлення всі тести Worker пройшли окремо зі збірками з LF і CRLF.

### E2E на тестовій БД

Потім у QA UI подай синтетичні рішення для Python Accepted, C++17 Accepted, WrongAnswer, CompilationError, RuntimeError і TimeLimitExceeded. Для MemoryLimitExceeded використовуй синтетичне виділення понад 256 МіБ. Звір `Queued → Running → verdict`, а після першого timeout подай п’ять заявок поспіль. Для ручного cancellation натисни Ctrl+C у терміналі Worker під час виконання контрольованої заявки, дочекайся завершення очищення та звір стан lease через API.

Для сценарію недоступного daemon використовуй виділений QA-сеанс, у якому Linux Engine вже зупинено. Залиш дві синтетичні заявки в `CodeRankJudgeQA` і запусти Worker. Перша заявка має отримати `SystemError`, якщо її lease ще чинний. Worker має зупинитися, а наступна заявка лишитися в черзі. При втраті daemon після створення контейнера перевір повідомлення `Cleanup` і збережений маркер.

Після кожної серії перевір список контейнерів, обидві мітки та відсутність маркера для відповідного Worker ID. Порожній scoped список і успішний exit code потрібні для підтвердження очищення.

```powershell
$dockerExe = (Get-Command docker.exe -ErrorAction Stop).Source
$judgeEngine = 'npipe:////./pipe/dockerDesktopLinuxEngine'
& $dockerExe --host $judgeEngine ps -a --no-trunc --filter 'label=io.coderank.judge.worker=dockerqa01'
& $dockerExe --host $judgeEngine ps -a --no-trunc --filter 'label=io.coderank.judge.worker=coderankdockersmoke'
Test-Path -LiteralPath "$env:LOCALAPPDATA\CodeRank\JudgeDocker\worker-dockerqa01\pending-container"
Test-Path -LiteralPath "$env:LOCALAPPDATA\CodeRank\JudgeDocker\worker-coderankdockersmoke\pending-container"
```

У SSMS звір лише статуси та лічильники спроб у тестовій БД.

```sql
IF DB_NAME() <> N'CodeRankJudgeQA'
    THROW 50000, 'Expected CodeRankJudgeQA', 1;

SELECT TOP (20) Id, Language, Status, JudgeAttemptCount, JudgeLeaseExpiresAtUtc
FROM dbo.Submissions
ORDER BY Id DESC;
```

### Відновлення після непідтвердженого очищення

Зупини Worker з відповідним ID та віднови доступ до Linux Engine. Прочитай `pending-container` як ID виконання. Якщо результат `create` було втрачено й контейнера ще не видно, підтвердь завершення невідомого запиту через контрольований перезапуск виділеного QA Engine перед очищенням маркера.

```powershell
$qaWorkerId = 'dockerqa01'  # Для opt-in тесту використовуй coderankdockersmoke.
$marker = "$env:LOCALAPPDATA\CodeRank\JudgeDocker\worker-$qaWorkerId\pending-container"
$execution = (Get-Content -LiteralPath $marker -Raw -ErrorAction Stop).Trim()
if ($execution -cnotmatch '^[0-9a-f]{32}$') { throw 'Invalid recovery marker.' }
$filter = "label=io.coderank.judge.execution=$execution"
$ids = @(& $dockerExe --host $judgeEngine ps -a -q --no-trunc --filter $filter)
if ($LASTEXITCODE -ne 0 -or $ids.Count -gt 1) { throw 'Container ownership is unconfirmed.' }

if ($ids.Count -eq 1) {
    $id = $ids[0]
    if ($id -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid container ID.' }
    $format = '{{.Id}}|{{.Name}}|{{index .Config.Labels "io.coderank.judge.worker"}}|{{index .Config.Labels "io.coderank.judge.execution"}}'
    $identity = & $dockerExe --host $judgeEngine inspect --type container --format $format $id
    if ($LASTEXITCODE -ne 0 -or $identity -cne "$id|/coderank-judge-$execution|$qaWorkerId|$execution") {
        throw 'Container ownership is unconfirmed.'
    }
    & $dockerExe --host $judgeEngine container rm --force $id
    if ($LASTEXITCODE -ne 0) { throw 'Container removal failed.' }
    $byId = @(& $dockerExe --host $judgeEngine ps -a -q --no-trunc --filter "id=$id")
    if ($LASTEXITCODE -ne 0 -or $byId.Count -ne 0) { throw 'Container removal is unconfirmed.' }
}

$remaining = @(& $dockerExe --host $judgeEngine ps -a -q --no-trunc --filter $filter)
if ($LASTEXITCODE -ne 0 -or $remaining.Count -ne 0) { throw 'Container removal is unconfirmed.' }
Remove-Item -LiteralPath $marker
```

## Перевірки

| Перевірка | Результат |
| --- | --- |
| Release build API, Worker і offline Sandbox | PASS, 0 попереджень і 0 помилок |
| Worker | 114 PASS із LF і 114 PASS із CRLF, 10 opt-in сценаріїв SKIP у кожному прогоні |
| API з ізольованою SQLite на `fb7012a` | 349 PASS, 2 loopback-тести Kestrel NOT RUN |
| Seeder на `fb7012a` | 26 PASS |
| Stage 2B1 unit на `fb7012a` | 151 PASS |
| Publish Windows Worker | PASS |
| Перший Docker Desktop QA на Windows 11 (`864ecbb`) | 10/10 FAIL. Docker CLI повідомив про відсутній ключ `Volumes` у шаблоні `image inspect` |
| Третій Docker Desktop QA на Windows 11 (`a8edffb`) | 8 FAIL, 2 SKIP. Виконані сценарії повернули `SystemError`, етап відмови не зафіксовано |
| Docker Desktop QA на `fb7012a` | `Start / ReadyPrefixMissing`. Exit code 2, stderr 36 байтів, `Stdin=Written`, `ReadyMarker=Missing`. Очищення підтверджено |
| Docker Desktop QA з LF-виправленням і сирим stderr для Python Accepted | NOT RUN |
| Повний E2E з `CodeRankJudgeQA`, фактичний `docker ps -a` і live daemon failure | NOT RUN |

Автоматизовані перевірки з імітованим Docker CLI охоплюють відсутні, порожні, оголошені й некоректні томи та перевищення ліміту метаданих. Контейнерні ліміти, живий cleanup і приймання на MSSQL потребують прогонів вище.
