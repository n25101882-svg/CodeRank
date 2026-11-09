# CodeRank · SCRUM-10 · Stage 2B2

## Архітектура і межі довіри

Stage 2B2 додає оцінювання до наявного leased queue. Worker під користувачем `coderank-worker` отримує приватний snapshot через HTTPS, підтримує lease і формує вердикт. Він володіє машинним ключем, lease token та очікуваними відповідями. Runner під окремим користувачем `coderank-sandbox` отримує код та один поточний input. C++17 компілюється один раз на сесію. Кожен тест C++17 або Python 3 виконується в новому NsJail.

```text
Submission → HTTPS API → MSSQL queue
                    ↑             ↓
                 complete     claim / heartbeat / payload
                    │             ↓
               trusted verdict ← Worker ↔ Unix socket ↔ offline Runner → NsJail
```

Мережевий Worker і Sandbox мають окремі systemd units. `coderank-judge-sandbox-worker.service` має `PrivateNetwork=yes`, AppArmor enforce, непривілейований UID, `NoNewPrivileges=yes` та порожні capability sets. Його supervisor перебуває в `/system.slice/coderank-judge-sandbox-worker.service/supervisor`, а кожен запуск має окремий cgroup із `cpu`, `memory` і `pids`. `/run/coderank-judge-sandbox-worker` є приватним робочим каталогом. Read-only rootfs, seccomp і namespaces залишаються частиною перевірок Sandbox. Підготовка C++ ELF потребує `PrivateTmp=yes` і backing mount приватного `/tmp` з дозволеним виконанням. Усередині jail робочий tmpfs має `noexec`, а ELF доступний через read-only bind. [Stage 2B1](JUDGE_SANDBOX_REPORT.md)

## Контракт IPC

Системний systemd відкриває pathname-сокет `/run/coderank-judge-ipc/sandbox.sock` з режимом `0660` та власником `root:coderank-worker`. Worker перевіряє через `SO_PEERCRED`, що слухач належить UID 0 і PID 1. Sandbox приймає з’єднання тільки від локального UID `coderank-worker`. Служба перевіряє activation metadata і встановлює `FD_CLOEXEC` для успадкованого слухача. Дочірні jail отримують поточний stdin і службовий receipt. [Linux Unix sockets](https://man7.org/linux/man-pages/man7/unix.7.html) · [systemd socket activation](https://manpages.ubuntu.com/manpages/noble/man3/sd_listen_fds.3.html)

Кадр має байт типу, 32-бітну big-endian довжину й тіло. Довжина перевіряється перед виділенням пам’яті. Текст декодується як строгий UTF-8.

| Кадр | Напрямок | Тіло |
| --- | --- | --- |
| `Start` | Worker → Runner | Версія `1`, код мови `1` для C++17 або `2` для Python 3, до 64 КіБ джерела |
| `Ready` | Runner → Worker | Порожнє тіло після підготовки. У разі помилки підготовки Runner відповідає `Result` |
| `Input` | Worker → Runner | До 8 КіБ поточного вводу. До 32 випадків і 64 КіБ вводу за сесію |
| `Result` | Runner → Worker | Байт статусу й до 64 КіБ stdout для `Exited`. Для решти статусів передається тільки байт статусу |
| `End` | Worker → Runner | Порожнє тіло після останнього результату |

Worker чекає на результат перед передаванням наступного вводу. Під час підготовки або виконання Runner стежить за сокетом. EOF або передчасний кадр скасовує активну операцію. Таймаут звичайного IPC вводу-виводу становить 10 с, очікування підготовки — 45 с, обмін для одного випадку — 25 с. Сесія обмежена 15 хвилинами. Окремі wall, CPU, memory та output ліміти Sandbox діють усередині цих меж.

## Вердикт і збереження

Worker порівнює stdout із `ExpectedOutput` у порядку snapshot. Він нормалізує `CRLF` до `LF`, ігнорує одне необов’язкове завершальне `LF` в кожному тексті та порівнює результат ordinal. Пробіли, регістр, порожні рядки, самостійний `CR` та решта символів значущі. Перший випадок із помилкою виконання або невідповідним stdout визначає студентський вердикт. `Accepted` вимагає збігу на всіх випадках.

| Результат Sandbox або порівняння | Вердикт Submission |
| --- | --- |
| Усі `Exited`, stdout збігається | `Accepted` |
| `Exited`, stdout відрізняється | `WrongAnswer` |
| Помилка компіляції | `CompilationError` |
| Помилка виконання, невалідний UTF-8 stdout або `OutputLimitExceeded` | `RuntimeError` |
| Перевищення часу | `TimeLimitExceeded` |
| Перевищення пам’яті | `MemoryLimitExceeded` |
| Системна відмова Runner, збій IPC або неповний набір результатів | `SystemError` |

Інфраструктурна відмова має пріоритет над студентським результатом. За чинного lease Worker повідомляє `SystemError` і зупиняє опитування черги. На рівні API completion вимкнено за замовчуванням. Для запису потрібні чинні машинні credentials, явний прапорець `Judge:Completion:Enabled`, Worker ID у списку дозволених та актуальна пара власника й токена lease. API використовує наявну атомарну операцію черги. Студентська історія отримує фінальний статус через чинний API.

## Lease і cancellation

- Перед виконанням Worker підтверджує lease через heartbeat. Під час роботи він поновлює його в окремій задачі кожні 15 с за замовчуванням.
- Сервер видає lease на 60 с. Worker ставить локальну монотонну межу на 55-й секунді від початку успішного запиту heartbeat. Нове успішне підтвердження пересуває межу.
- Для тимчасового збою HTTPS є одна повторна спроба через 1 с у межах підтвердженого вікна. Після `409 lease_lost` або вичерпання безпечного вікна Worker скасовує IPC. Runner припиняє процеси через `cgroup.kill` і перевіряє очищення. При непідтвердженому поновленні Worker зупиняє опитування. При підтвердженій втраті lease він може перейти до нового claim без вердикту для старого lease.
- Перед `complete` Worker дочікується активного heartbeat і підтверджує lease ще раз. Якщо HTTP-відповідь на completion втрачена, він повторює той самий токен і вердикт без нового виконання. Сервер повертає `204` для ідентичного повтору й відхиляє застарілий lease або зміну збереженого вердикту.
- `TestsUnavailable`, `InvalidPayload`, `LeaseLost` і `FailPayloadAsync` лишаються в наявному протоколі. Payload, який API відхилив, до Sandbox не надсилається. [Judge protocol](JUDGE_PROTOCOL_REPORT.md) · [Payload failure handling](JUDGE_PAYLOAD_FAILURE_FIX.md)

## Перевірки

| Перевірка | Результат | Середовище |
| --- | --- | --- |
| API | **351/351 PASS** | TestServer, ізольована SQLite і loopback Kestrel |
| Worker | **72/72 PASS** | Синтетичні HTTP, IPC і Runner |
| Sandbox | **155/155 PASS** | Планування jail, протокол скасування й підставний executor |
| Seeder | **26/26 PASS** | Ізольовані автоматизовані тести |
| Release build і framework-dependent publish | **PASS** | API, Worker, Sandbox. Збірка без помилок і попереджень |
| Відмова Windows | **PASS** | `--expect-unavailable` підтвердив відмову. `--serve` і Worker з увімкненим оцінюванням завершилися з кодом 1 |
| Ubuntu 24.04 + MSSQL end-to-end | **NOT RUN** | QA VM відхилила SSH-автентифікацію. Потрібні socket activation, AppArmor, NsJail і окрема QA БД |

Усі **604 автоматизовані тести** виконано через xUnit v3. Два тести Kestrel з першої спроби були заблоковані обмеженням доступу до loopback. Повторний повний API-набір після дозволу пройшов. Окремий тест із прискореним годинником покриває 15 с компіляції та 32 випадки по 2 с, heartbeat під час роботи й один запис фінального результату. SQLite-тести перевіряють конкуренцію, атомарний completion, ідемпотентність і приватність студентської історії. Локальні перевірки моделюють IPC. Фактичні `SO_PEERCRED`, systemd та AppArmor для Stage 2B2 очікують Linux QA.

## Розгортання й перевірка на QA VM

1. Підготуй виділену Ubuntu 24.04 x86-64 VM і окрему `CodeRankJudgeQA` із чинними lease та snapshot міграціями. Перед змінами звір `SELECT DB_NAME()` в QA-сесії. Використовуй синтетичну олімпіаду й приватні тести, а також HTTPS-сертифікат QA API, якому довіряє Worker. [Встановлення Stage 2B1](JUDGE_SANDBOX_REPORT.md) · [Snapshot QA](JUDGE_STAGE2A_REPORT.md)
2. Збери framework-dependent пакети. Скопіюй їх у каталоги, визначені systemd units. Пакет API встанови на окремий QA API. Runner використовує наявні pinned NsJail 3.6, launcher і rootfs зі Stage 2B1.

```bash
dotnet publish backend/CodeRank.Api -c Release -p:UseAppHost=false -o artifacts/qa-api
dotnet publish worker/CodeRank.Judge.Worker -c Release -p:UseAppHost=false -o artifacts/qa-worker
dotnet publish sandbox/CodeRank.Judge.Sandbox -c Release -p:UseAppHost=false -o artifacts/qa-sandbox

sudo adduser --system --group --no-create-home --home /nonexistent --shell /usr/sbin/nologin coderank-worker
sudo install -d -o root -g root -m 0755 /opt/coderank-judge-worker /opt/coderank-judge-sandbox/app
sudo install -o root -g root -m 0644 artifacts/qa-worker/* /opt/coderank-judge-worker/
sudo install -o root -g root -m 0644 artifacts/qa-sandbox/* /opt/coderank-judge-sandbox/app/
bash sandbox/linux/check-config.sh
sudo install -m 0644 sandbox/linux/coderank-judge-sandbox.apparmor /etc/apparmor.d/coderank-judge-sandbox
sudo apparmor_parser -r -K /etc/apparmor.d/coderank-judge-sandbox
sudo install -m 0644 sandbox/linux/coderank-judge-sandbox-worker.socket \
  sandbox/linux/coderank-judge-sandbox-worker.service sandbox/linux/coderank-judge-worker.service /etc/systemd/system/
sudo install -d -o root -g root -m 0700 /etc/coderank
sudo touch /etc/coderank/judge-worker.env
sudo chown root:root /etc/coderank/judge-worker.env
sudo chmod 0600 /etc/coderank/judge-worker.env
```

3. Задай QA API такі параметри через його приватний провайдер. Заповни QA connection string і JWT окремо. Використай окремий випадковий ключ із 32 байтів у Base64 для `judgeqa`.

```text
Judge__Enabled=true
Judge__Workers__judgeqa__CurrentKey=<QA machine key>
Judge__Completion__Enabled=true
Judge__Completion__Workers__0=judgeqa
```

   На VM заповни `/etc/coderank/judge-worker.env` через `sudoedit`. Той самий ключ отримують QA API та мережевий Worker. Sandbox unit використовує окремий UID і власне середовище.

```text
JudgeWorker__ApiBaseUrl=https://qa-api.example.invalid/
JudgeWorker__WorkerId=judgeqa
JudgeWorker__Key=<QA machine key>
JudgeWorker__FetchPayload=true
JudgeWorker__EvaluateSubmissions=true
```

4. Перевір конфігурацію і smoke Stage 2B1, потім запусти socket і Worker.

```bash
sudo systemd-analyze verify /etc/systemd/system/coderank-judge-sandbox-worker.socket \
  /etc/systemd/system/coderank-judge-sandbox-worker.service /etc/systemd/system/coderank-judge-worker.service
sudo systemctl daemon-reload
sudo grep -Fx 'coderank-judge-sandbox (enforce)' /sys/kernel/security/apparmor/profiles
sudo systemctl start --wait coderank-judge-sandbox-smoke.service
sudo systemctl show coderank-judge-sandbox-smoke.service -p Result -p ExecMainStatus
sudo systemctl start coderank-judge-sandbox-worker.socket
sudo systemctl start coderank-judge-worker.service
```

5. Подай синтетичні C++17 і Python 3 рішення через UI або `POST /api/v1/olympiads/{olympiadId}/problems/{problemId}/submissions` з полями `language` і `sourceCode`. Зафіксуй `Queued → Running → Accepted` та результат у `GET /api/v1/submissions/{id}`. Перевір WrongAnswer, compile/runtime error, deadline, OOM і перевищення stdout. Для 32 випадків із сумарним часом понад 60 с зафіксуй поновлення lease під час виконання.
6. Перевір lease loss і повтор completion на синтетичних спробах. Після контрольованого закінчення або передачі lease старий Worker має припинити виконання й не записувати вердикт. При втраченій відповіді на `complete` перевір `204` повтору й незмінний час завершення. Спробуй підключитися до Unix-сокета від стороннього UID та перевір відмову. З журналу збережи лише службові коди й статуси.

## Відомі ризики й приймання

Періодичний `CgroupCleanup Timeout` лишається окремим ризиком. У разі непідтвердженого очищення Sandbox припиняє обробку. Для приймання Stage 2B2 потрібен підтверджений прогін із реальним AppArmor, systemd socket activation, NsJail і записом вердикту в QA MSSQL. [Issue №21](https://github.com/n25101882-svg/CodeRank/issues/21)

## Змінені файли

- `/` — `.gitattributes`.
- `backend/CodeRank.Api/Controllers/` — `JudgeJobsController.cs`.
- `backend/CodeRank.Api/Judge/` — `JudgeCompletionPolicy.cs`, `JudgeQueue.cs`, `JudgeWorkerSettings.cs`.
- `backend/CodeRank.Api/` — `Program.cs`.
- `docs/` — `JUDGE_PROTOCOL_REPORT.md`, `JUDGE_SANDBOX_REPORT.md`, `JUDGE_STAGE_2B2_REPORT.md`.
- `sandbox/CodeRank.Judge.Sandbox/` — `CodeRank.Judge.Sandbox.csproj`, `LinuxJobCgroup.cs`, `LinuxSandboxExecutor.cs`, `LinuxSandboxHost.cs`, `LinuxSmokeTests.cs`, `NsJailPlan.cs`, `NsJailRunner.cs`, `Program.cs`, `SandboxIpcServer.cs`.
- `sandbox/linux/` — `check-config.sh`, `coderank-judge-sandbox-worker.service`, `coderank-judge-sandbox-worker.socket`, `coderank-judge-sandbox.apparmor`, `coderank-judge-worker.service`.
- `shared/judge/` — `JudgeIpc.cs`, `JudgePayloadContract.cs`, `LinuxJudgeIdentity.cs`.
- `tests/CodeRank.Api.Auth.Tests/` — `JudgeJobsApiTests.cs`, `JudgePayloadApiTests.cs`.
- `tests/CodeRank.Judge.Sandbox.Tests/` — `NsJailRunnerTests.cs`, `SandboxPolicyTests.cs`.
- `tests/CodeRank.Judge.Worker.Tests/` — `JudgeIpcClientTests.cs`, `JudgeWorkerTests.cs`, `LeasedSubmissionJudgeTests.cs`, `SubmissionGraderTests.cs`.
- `worker/CodeRank.Judge.Worker/` — `CodeRank.Judge.Worker.csproj`, `IIsolatedJudgeRunner.cs`, `JudgeApiClient.cs`, `JudgeWorker.cs`, `LeasedSubmissionJudge.cs`, `Program.cs`, `SubmissionGrader.cs`, `UnixJudgeRunner.cs`, `WorkerSettings.cs`.

## Коміти й PR

- Ізольований Runner, IPC і systemd socket. [a2996ec](https://github.com/n25101882-svg/CodeRank/commit/a2996ec08c2aa9fbcc0087676c69d609e228a897)
- Worker, вердикти, lease і API completion. [6e631d1](https://github.com/n25101882-svg/CodeRank/commit/6e631d1204cc7328fdfcbcd8b80c4b0aa5e36501)
- Документація та реалізація на гілці `feature/judge-worker-integration`. [PR №22](https://github.com/n25101882-svg/CodeRank/pull/22) · [SCRUM-10](https://coderank.atlassian.net/browse/SCRUM-10) · [Коміти PR](https://github.com/n25101882-svg/CodeRank/pull/22/commits)
