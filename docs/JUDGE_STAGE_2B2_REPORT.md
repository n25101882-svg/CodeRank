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

У першому Ubuntu QA systemd передав правильні `LISTEN_PID`, `LISTEN_FDS=1`, `LISTEN_FDNAMES=judge-sandbox` і FD 3. AppArmor відхилив `getsockname` для `/run/coderank-judge-ipc/sandbox.sock` з `EACCES` і `disconnected path`. Конструктор .NET `Socket(SafeSocketHandle)` викликає `getsockname` під час імпорту. AppArmor також може перевіряти шлях цього сокета під час `accept`, тому перевірки наявності файла й метаданих активації не усувають збій. [Код .NET Socket](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L102-L155) · [Перевірки AF_UNIX в AppArmor](https://code.googlesource.com/linux/torvalds/linux/+/12091470c6b4c1c14b2de12dcbae2ada6cb6d20b/security/apparmor/af_unix.c#325)

Для Stage 2B2 системний systemd відкриває абстрактний AF_UNIX stream `@coderank-judge-sandbox-worker` у мережевому namespace хоста й передає FD 3 до Sandbox. Ядро зберігає адресу з початковим байтом NUL, тому доступ до listener не потребує пошуку шляху в mount namespace Sandbox. Sandbox вимагає правильний PID, рівно один FD з іменем `judge-sandbox`, `FD_CLOEXEC`, точну адресу, AF_UNIX, SOCK_STREAM і стан прослуховування. Worker вимагає UID 0 і PID 1 через `SO_PEERCRED`. Sandbox читає кадри лише після перевірки `SO_PEERCRED` на UID `coderank-worker`. Дочірні jail отримують поточний stdin і службовий receipt. [systemd socket](https://manpages.ubuntu.com/manpages/noble/man5/systemd.socket.5.html) · [Linux Unix sockets](https://man7.org/linux/man-pages/man7/unix.7.html)

Абстрактний сокет не має файлового режиму `0660`. Локальні процеси в мережевому namespace хоста можуть ініціювати з’єднання або зайняти ім’я, поки socket unit зупинено. Перевірка облікових даних ядра відсікає сторонній UID до читання IPC і відхиляє підставний listener на боці Worker. Локальний DoS лишається можливим. Зміна застосовується на виділеній QA VM. AppArmor, `PrivateNetwork=yes`, налаштування NsJail і Stage 2B1 збережено. [Linux Unix sockets](https://man7.org/linux/man-pages/man7/unix.7.html)

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
| Sandbox | **163 PASS, 5 SKIP** | Windows. Додано перевірки unit і метаданих активації. П’ять перевірок реальних AF_UNIX listener і `SO_PEERCRED` потребують Linux |
| Seeder | **26/26 PASS** | Ізольовані автоматизовані тести |
| Release build і framework-dependent publish | **PASS** | Збірка API, Worker і Sandbox без помилок і попереджень. Оновлені пакети Worker і Sandbox |
| Відмова Windows | **PASS** | `--expect-unavailable` підтвердив відмову. `--serve` і Worker з увімкненим оцінюванням завершилися з кодом 1 |
| Ubuntu 24.04 до IPC-виправлення | **Stage 2B1 14/14 PASS, Stage 2B2 заблоковано** | Stage 2B2 завершився `Configuration/Rejected` після `getsockname` з `EACCES` |
| Ubuntu 24.04 після IPC-виправлення | **FAIL** під час серії | Worker отримав `Completion … Saved` для №17. Діагностичний Runner відтворив `CgroupCleanup Timeout` під час №18. Серійне приймання ще не пройдено |

Через xUnit v3 пройшли **612 тестів**, п’ять Linux-специфічних пропущено. Повний API-набір пройшов після надання доступу до loopback для Kestrel. Окремий тест із прискореним годинником покриває 15 с компіляції та 32 випадки по 2 с, heartbeat під час роботи й один запис фінального результату. SQLite-тести перевіряють конкуренцію, атомарний completion, ідемпотентність і приватність студентської історії.

На Linux із .NET 10 SDK перевірки IPC можна запустити окремо через `dotnet test tests/CodeRank.Judge.Sandbox.Tests -c Release --filter FullyQualifiedName~SandboxIpcActivationTests`. Вони використовують тимчасові імена в abstract namespace. systemd activation і AppArmor enforce перевіряються на QA VM за кроками нижче.

## Розгортання й перевірка на QA VM

1. Підготуй виділену Ubuntu 24.04 x86-64 VM і окрему `CodeRankJudgeQA` із чинними lease та snapshot міграціями. Перед змінами звір `SELECT DB_NAME()` в QA-сесії. Використовуй синтетичну олімпіаду й приватні тести, а також HTTPS-сертифікат QA API, якому довіряє Worker. [Встановлення Stage 2B1](JUDGE_SANDBOX_REPORT.md) · [Snapshot QA](JUDGE_STAGE2A_REPORT.md)
2. З одного коміту PR №22 збери framework-dependent пакети. Для повторного QA на підготовленій VM потрібні пакети Worker і Sandbox та новий `.socket`. Перейди з ними до кроку 4. Runner використовує встановлені pinned NsJail 3.6, launcher і rootfs Stage 2B1.

```bash
dotnet publish backend/CodeRank.Api -c Release -p:UseAppHost=false -o artifacts/qa-api
dotnet publish worker/CodeRank.Judge.Worker -c Release -p:UseAppHost=false -o artifacts/qa-worker
dotnet publish sandbox/CodeRank.Judge.Sandbox -c Release -p:UseAppHost=false -o artifacts/qa-sandbox
```

   Лише для першого встановлення Stage 2B2 підготуй QA API та встанови units і профіль PR №22 на виділеній VM. Якщо цей профіль уже діє на QA VM, повторне завантаження не потрібне.

```bash
sudo adduser --system --group --no-create-home --home /nonexistent --shell /usr/sbin/nologin coderank-worker
bash sandbox/linux/check-config.sh
sudo install -m 0644 sandbox/linux/coderank-judge-sandbox.apparmor /etc/apparmor.d/coderank-judge-sandbox
sudo apparmor_parser -r -K /etc/apparmor.d/coderank-judge-sandbox
sudo install -m 0644 sandbox/linux/coderank-judge-sandbox-worker.service \
  sandbox/linux/coderank-judge-worker.service /etc/systemd/system/
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

4. У каталозі з пакетами виконай блок. Він зупиняє лише Stage 2B2, зберігає копії його файлів і встановлює обидва пакети разом. Профіль AppArmor, smoke unit і `/opt/coderank-judge-sandbox/app/` Stage 2B1 залишаються на місці.

```bash
socket_unit=coderank-judge-sandbox-worker.socket
runner_unit=coderank-judge-sandbox-worker.service
worker_unit=coderank-judge-worker.service
qa_sandbox=/opt/coderank-judge-sandbox/app-pr22
qa_worker=/opt/coderank-judge-worker/app-pr22

(
  set -eu
  test -f artifacts/qa-sandbox/CodeRank.Judge.Sandbox.dll
  test -f artifacts/qa-worker/CodeRank.Judge.Worker.dll
  grep -Fx 'ListenStream=@coderank-judge-sandbox-worker' "sandbox/linux/$socket_unit"
  test "$(realpath -m "$qa_sandbox")" = "$qa_sandbox"
  test "$(realpath -m "$qa_worker")" = "$qa_worker"
  stage2b1_sha=$(sudo sha256sum /opt/coderank-judge-sandbox/app/CodeRank.Judge.Sandbox.dll)
  sudo systemctl stop "$worker_unit" "$runner_unit"
  if systemctl is-active --quiet "$socket_unit"; then sudo systemctl stop "$socket_unit"; fi
  backup=$(sudo mktemp -d /var/tmp/coderank-pr22-ipc.XXXXXXXX)
  for path in "$qa_sandbox" "$qa_worker" "/etc/systemd/system/$socket_unit" \
    "/etc/systemd/system/$runner_unit.d/90-pr22-qa.conf" "/etc/systemd/system/$worker_unit.d/90-pr22-qa.conf"; do
    if sudo test -e "$path"; then sudo cp -a --parents -- "$path" "$backup/"; fi
  done
  printf 'QA_BACKUP %s\n' "$backup"
  sudo install -d -o root -g root -m 0755 "$qa_sandbox" "$qa_worker"
  sudo install -o root -g root -m 0644 artifacts/qa-sandbox/* "$qa_sandbox/"
  sudo install -o root -g root -m 0644 artifacts/qa-worker/* "$qa_worker/"
  sudo install -m 0644 "sandbox/linux/$socket_unit" /etc/systemd/system/

  sudo install -d "/etc/systemd/system/$runner_unit.d" "/etc/systemd/system/$worker_unit.d"
  printf '[Service]\nExecStart=\nExecStart=/usr/bin/dotnet %s/CodeRank.Judge.Sandbox.dll --serve\n' "$qa_sandbox" |
    sudo tee "/etc/systemd/system/$runner_unit.d/90-pr22-qa.conf" >/dev/null
  printf '[Service]\nExecStart=\nExecStart=/usr/bin/dotnet %s/CodeRank.Judge.Worker.dll\n' "$qa_worker" |
    sudo tee "/etc/systemd/system/$worker_unit.d/90-pr22-qa.conf" >/dev/null

  sudo systemd-analyze verify "/etc/systemd/system/$socket_unit" \
    "/etc/systemd/system/$runner_unit" "/etc/systemd/system/$worker_unit"
  sudo systemctl daemon-reload
  systemctl show "$runner_unit" -p ExecStart --value | grep -F "$qa_sandbox/CodeRank.Judge.Sandbox.dll"
  systemctl show "$worker_unit" -p ExecStart --value | grep -F "$qa_worker/CodeRank.Judge.Worker.dll"
  test "$(sudo sha256sum /opt/coderank-judge-sandbox/app/CodeRank.Judge.Sandbox.dll)" = "$stage2b1_sha"
)
```

5. Після успішного встановлення перевір готовність listener та відхилення стороннього UID. Проба використовує `nobody` і не передає кадрів. Очікуються `ActiveState=active`, `PrivateNetwork=yes`, `PrivateTmp=yes`, профіль процесу `coderank-judge-sandbox (enforce)` і рядок `IPC_REJECTED_FOREIGN_UID`.

```bash
qa_since=$(date --iso-8601=seconds)
sudo grep -Fx 'coderank-judge-sandbox (enforce)' /sys/kernel/security/apparmor/profiles
sudo systemctl start "$socket_unit" "$runner_unit"
systemctl show "$socket_unit" -p ActiveState -p Listen
systemctl show "$runner_unit" -p ActiveState -p Result -p ExecMainStatus -p PrivateNetwork -p PrivateTmp -p NoNewPrivileges
runner_pid=$(systemctl show "$runner_unit" -p MainPID --value)
sudo grep -Fx 'coderank-judge-sandbox (enforce)' "/proc/$runner_pid/attr/current"
sudo -u nobody python3 - <<'PY'
import socket
import struct

with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
    client.settimeout(5)
    client.connect("\0coderank-judge-sandbox-worker")
    pid, uid, _ = struct.unpack("iII", client.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
    if (pid, uid) != (1, 0) or client.recv(1) != b"":
        raise SystemExit("IPC_QA_FAILED")
    print("IPC_REJECTED_FOREIGN_UID")
PY
systemctl is-active "$runner_unit"
sudo journalctl -k --since "$qa_since" --no-pager \
  --grep 'apparmor="DENIED".*operation="getsockname".*profile="coderank-judge-sandbox"'
sudo journalctl -u "$runner_unit" --since "$qa_since" --no-pager -o cat --grep 'SystemError stage='
```

6. Якщо обидва журнали не містять відповідних відмов і Worker налаштований на QA API з `CodeRankJudgeQA`, виконай `sudo systemctl start "$worker_unit"`. Подай синтетичні C++17 і Python 3 рішення через UI або `POST /api/v1/olympiads/{olympiadId}/problems/{problemId}/submissions` з полями `language` і `sourceCode`. Зафіксуй `Queued → Running → Accepted` та результат у `GET /api/v1/submissions/{id}`. Перевір WrongAnswer, compile/runtime error, deadline, OOM і перевищення stdout. Для 32 випадків із сумарним часом понад 60 с зафіксуй поновлення lease під час виконання.
7. Перевір lease loss і повтор completion на синтетичних спробах. Після контрольованого закінчення або передачі lease старий Worker має припинити виконання й не записувати вердикт. При втраченій відповіді на `complete` перевір `204` повтору й незмінний час завершення. З журналу збережи лише службові коди й статуси.
8. Для контрольного прогону Stage 2B1 виконай `sudo systemctl start --wait coderank-judge-sandbox-smoke.service`, перевір `systemctl show coderank-judge-sandbox-smoke.service -p Result -p ExecMainStatus` і підсумок `14/14` у журналі smoke unit.

## Відомі ризики й приймання

Періодичний `CgroupCleanup Timeout` відтворено на 4 vCPU з діагностичним Runner. Зафіксовано 120,914 с між розрахованим wall deadline і входом у cleanup, а також RCU stall під час зупинки. У разі непідтвердженого очищення Sandbox припиняє обробку. Для приймання Stage 2B2 потрібні серійні Linux-прогони з реальним AppArmor, systemd socket activation, NsJail і записом вердикту в QA MSSQL. [Issue №21](https://github.com/n25101882-svg/CodeRank/issues/21) · [Таймлайн збою та повторний QA](JUDGE_CGROUP_CLEANUP_QA.md)

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
- `tests/CodeRank.Judge.Sandbox.Tests/` — `CodeRank.Judge.Sandbox.Tests.csproj`, `NsJailRunnerTests.cs`, `SandboxIpcActivationTests.cs`, `SandboxPolicyTests.cs`.
- `tests/CodeRank.Judge.Worker.Tests/` — `JudgeIpcClientTests.cs`, `JudgeWorkerTests.cs`, `LeasedSubmissionJudgeTests.cs`, `SubmissionGraderTests.cs`.
- `worker/CodeRank.Judge.Worker/` — `CodeRank.Judge.Worker.csproj`, `IIsolatedJudgeRunner.cs`, `JudgeApiClient.cs`, `JudgeWorker.cs`, `LeasedSubmissionJudge.cs`, `Program.cs`, `SubmissionGrader.cs`, `UnixJudgeRunner.cs`, `WorkerSettings.cs`.

## Коміти й PR

- Ізольований Runner, IPC і systemd socket. [a2996ec](https://github.com/n25101882-svg/CodeRank/commit/a2996ec08c2aa9fbcc0087676c69d609e228a897)
- Worker, вердикти, lease і API completion. [6e631d1](https://github.com/n25101882-svg/CodeRank/commit/6e631d1204cc7328fdfcbcd8b80c4b0aa5e36501)
- Документація та реалізація на гілці `feature/judge-worker-integration`. [PR №22](https://github.com/n25101882-svg/CodeRank/pull/22) · [SCRUM-10](https://coderank.atlassian.net/browse/SCRUM-10) · [Коміти PR](https://github.com/n25101882-svg/CodeRank/pull/22/commits)
