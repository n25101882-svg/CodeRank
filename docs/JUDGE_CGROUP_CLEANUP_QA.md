# Issue №21 · CgroupCleanup Timeout

## Докази з Ubuntu QA

На Ubuntu 24.04 з 4 vCPU і PR №22 `34bb990` спроба №15 завершилася `TimeLimitExceeded`. Під час №16 Runner повідомив `CgroupCleanup/Timeout` на `cgroup.events`. У групі спостерігали `populated 1` і PID 1285 (`python3`) попри повторні `SIGKILL` від systemd. Після зупинки Worker №17–19 лишилися `Queued`. PID і залишкова група зникли через кілька хвилин. У журналі ядра зафіксовано `Long readout interval` близько 97,95 с. Попередній збій на 6 vCPU супроводжувався RCU stall і інтервалом близько 125,4 с.

Після перезапуску Runner і Worker №15 мала `TimeLimitExceeded`, №16 мала `SystemError` із `JudgeAttemptCount=2`, №17 спостерігалася в `Running` із `JudgeAttemptCount=1`, №18–19 лишалися `Queued`. Це знімок черги. Його потрібно зіставити з журналом резервування й завершення для конкретного запуску Worker. Стабільну обробку серії ще не підтверджено. [Issue №21](https://github.com/n25101882-svg/CodeRank/issues/21) · [PR №22](https://github.com/n25101882-svg/CodeRank/pull/22)

## Порядок очищення й межі діагнозу

1. Нативний launcher записує власний PID до `cgroup.procs` перед `execv` NsJail. `execv` зберігає PID. NsJail і його нащадки успадковують групу.
2. При скасуванні I/O, wall deadline або виході Runner зупиняє й чекає прямого нащадка до 3 с. `WaitForExitAsync` підтверджує завершення launcher/NsJail, а стан інших процесів підтверджується окремо. Навіть після таймауту очікування launcher Runner виконує `cgroup.kill`, але непідтверджений вихід блокує видалення групи й подальшу роботу. Це захищає від пізнього входу launcher до групи.
3. Після повернення запису `cgroup.kill` Runner читає `cgroup.events` з паузою 25 мс між читаннями. Вікно для групи становить 3 с за монотонним годинником. Якщо запізніле читання підтвердило `populated=0` і launcher завершився, очищення можна продовжити. Живий процес на момент дедлайну лишає групу для діагностики та спричиняє `SystemError`. Runner блокує наступне виконання, Worker припиняє `claim`. [.NET ProcessWaitState](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessWaitState.Unix.cs) · [Код `cgroup.kill`](https://github.com/torvalds/linux/blob/v6.8/kernel/cgroup/cgroup.c#L3706-L3773)

`cgroup.kill` генерує `SIGKILL`, після чого ядро завершує процеси асинхронно. `populated=1` разом із PID у `cgroup.procs` узгоджується з живим процесом, а зомбі після виходу не утримує `populated=1`. Затримка watchdog і RCU stall сумісні із затримкою планування або таймерів у VM. Вони не дають часової мітки надсилання `SIGKILL` чи виходу конкретного PID. Ліміти `cpu.max` у job-cgroup та фактичне throttling варто звірити окремо від квоти systemd Runner. Причину затримки потрібно підтвердити спільною трасою. [Семантика cgroup v2](https://www.kernel.org/doc/html/latest/admin-guide/cgroup-v2.html) · [Clocksource watchdog](https://gbmc.googlesource.com/linux/+/88fac17500f4ea49c7bac136cf1b27e7b9980075/kernel/time/clocksource.c) · [Діагностика RCU stall](https://docs.kernel.org/RCU/stallwarn.html)

## Що фіксує кандидат

- Лише в разі помилки очищення Sandbox додає `stop_reason`, розрахований `wall_due_ms`, `stop_ms`, `leader_pid`, результат очікування `leader_exit`, початок і повернення запису `kill_begin_ms` / `kill_end_ms`, кількість читань, останні `populated` / `sample_ms` і `max_poll_gap_ms`. Якщо запис не підтверджено, `kill_end_ms` відсутній. `leader_exit=confirmed` означає, що прямий процес підтверджено завершеним до `kill_begin_ms`. Точний момент виходу фіксує окрема подія ядра.
- Час запису включає відкриття файла й системний виклик. `max_poll_gap_ms` охоплює планування Runner, таймер і читання ядра. На Linux `Stopwatch` та Python `time.monotonic_ns()` використовують `CLOCK_MONOTONIC`. Зіставляй їх у межах одного завантаження VM. Якщо гостя повністю призупинено, обидва спостерігачі матимуть прогалину. Для розмежування затримки хоста й гостя потрібні метрики планування з гіпервізора. [.NET monotonic clock](https://github.com/dotnet/runtime/blob/v10.0.12/src/native/minipal/time.c)
- Read-only спостерігач поза Runner читає раз на 100 мс `cgroup.events`, `cgroup.procs`, CPU-лічильники та стан відомих PID. `members` має формат `PID/state/start_ticks/cpu/pending_kill/wchan`. Для `D` `wchan` містить лише обмежений символ ядра, коли він доступний. `CG_TASK_LEFT` фіксує спостережений стан PID при зникненні з групи, `CG_GROUP_GONE` фіксує зникнення групи, `CG_LAYOUT` фіксує кількість груп і job-каталогів scratch. Збір має межі тривалості, кількості груп, PID і рядків. `unknown` та `CG_WATCH_GAP` позначають неповноту даних.
- Додаткова BPF-траса позначає `cgroup.kill`, стан членів групи під час kill, `SIGKILL`, `sched_process_exit`, вхід у `release_task`, зміну `populated` та видалення групи. Фільтр бере лише групи, створені поточним supervisor. Логи містять числа й контрольовані поля. У Runtime збережено чинні таймаути, AppArmor, NsJail, cgroup limits і fail-closed.

Release build і publish на Windows пройшли без попереджень. Sandbox має 170 PASS, зокрема сім нових випадків, і 5 Linux SKIP. Worker має 72 PASS, зокрема регресію зупинки після інфраструктурного `SystemError`. Синтетичні перевірки спостерігача охопили стан PID, pending SIGKILL, прогалину планування та зникнення групи.

## Ubuntu QA

Виконуй лише на виділеній QA VM із синтетичними задачами в `CodeRankJudgeQA`. Команди нижче передбачають розпакований `CodeRank_Issue21_Cgroup_QA.tar.gz`. Якщо №17 або інша спроба ще зависла, спочатку запусти тільки read-only спостерігач із кроку 2 й збережи журнали з кроку 4. Збір із поточного моменту може показати залишковий стан. Для повного таймлайну він має стартувати до першої спроби.

### 1. Встанови діагностичну DLL у Stage 2B2

У QA-вікні перевір, що черга тестова, і зафіксуй активні заявки та їхні lease до зупинки. Встановлення вимагає, щоб після зупинки Runner у його cgroup не лишилося `job-*`. За наявності залишку збережи спостереження й дочекайся штатного завершення.

```bash
(
  set -euo pipefail
  sha256sum -c SHA256SUMS
  runner=coderank-judge-sandbox-worker.service
  worker=coderank-judge-worker.service
  socket=coderank-judge-sandbox-worker.socket
  target=/opt/coderank-judge-sandbox/app-pr22/CodeRank.Judge.Sandbox.dll
  systemctl show "$runner" -p ExecStart --value | grep -F "$target"
  test "$(realpath -e "$target")" = "$target"
  sudo systemctl stop "$worker" "$socket" "$runner"
  for group in "/sys/fs/cgroup/system.slice/$runner"/job-*; do
    if test -d "$group"; then
      echo 'QA_ABORT residual job cgroup'
      exit 1
    fi
  done
  backup=$(sudo mktemp -d /var/tmp/coderank-issue21.XXXXXXXX)
  sudo cp -a -- "$target" "$backup/"
  printf 'QA_BACKUP %s\n' "$backup"
  sudo install -o root -g root -m 0644 CodeRank.Judge.Sandbox.dll "$target"
  sudo cmp -- CodeRank.Judge.Sandbox.dll "$target"
  sudo systemctl start "$socket" "$runner"
  systemctl show "$runner" -p ActiveState -p MainPID -p ControlGroup -p PrivateNetwork -p NoNewPrivileges
  pid=$(systemctl show "$runner" -p MainPID --value)
  sudo grep -Fx 'coderank-judge-sandbox (enforce)' "/proc/$pid/attr/current"
)
```

`/opt/coderank-judge-sandbox/app/` і smoke unit використовують чинний Stage 2B1. Для повернення Stage 2B2 до попередньої DLL зупини ті самі три units, звір `target` і встанови збережену DLL із `QA_BACKUP` тим самим `install`. Тестову чергу після відмови запускай лише після перевірки, що залишки групи зникли.

### 2. Почни спостереження до запуску Worker

У першому терміналі в каталозі пакета запусти збір і дочекайся `CG_WATCH_READY`. Для контрольного прогону чинного offline smoke unit спостерігач приймає `smoke` замість `worker`.

```bash
umask 077
sudo python3 watch-cgroup-cleanup.py worker --seconds 360 | tee issue21-samples.log
```

У другому терміналі зафіксуй час початку та запусти Worker. Якщо діагностуєш активне зависання, пропусти `systemctl start` і збери доступний журнал поточного запуску.

```bash
qa_since=$(date --iso-8601=seconds)
sudo systemctl start coderank-judge-worker.service
```

На чистій QA-черзі подай одну синтетичну Python-заявку `while True: pass`. Після `TimeLimitExceeded` перевір зникнення її PID і групи та `CG_LAYOUT groups=0 scratch=0`. Потім подай п’ять таких заявок поспіль. Для кожної запиши ID, кінцевий статус, кількість спроб і факт очищення. Повтори серію з п’яти тричі на 4 vCPU. Після першого `SystemError` збережи журнал, стан решти черги й спостерігай за залишком до його зникнення або завершення вікна збору. Під час збору залишкових станів утримайся від перезапуску units.

### 3. Окремий повтор із BPF

Перші серії використовують лише read-only спостерігач. BPF може змінити таймінг, тому почни його в окремій серії, поки Runner активний і Worker ще не обробляє нові заявки. Після нового запуску Runner прочитай inode supervisor заново. Якщо `bpftrace` повідомить помилку сумісності, збережи її й продовжуй збір із кроку 2.

```bash
umask 077
supervisor=$(stat -Lc '%i' /sys/fs/cgroup/system.slice/coderank-judge-sandbox-worker.service/supervisor)
sudo timeout --signal=INT --kill-after=5s 360s bpftrace -q -B line trace-cgroup-cleanup.bt "$supervisor" |
  tee issue21-kernel-trace.log
```

Дочекайся `CG_TRACE_READY` перед заявками. Перший запис `cgroup.kill` у новій групі перевіряє доступ до порожньої групи перед `CG_ATTACH`. Для відмови зіставляй пізніший запис із `leader_pid` і часовими мітками Sandbox. Сортуй BPF-події за `ms`, враховуючи одночасні події з різних CPU. Успішний запис має `rc=1`. `CG_EXIT` показує вихід у ядрі, а `CG_REAP` показує виклик `release_task` для того ж PID. Для `CG_SIGKILL` поле `result` є кодом tracepoint. Воно не підтверджує завершення процесу. [Точки трасування ядра](https://github.com/torvalds/linux/blob/v6.8/include/trace/events/cgroup.h) · [Linux exit і reaping](https://github.com/torvalds/linux/blob/v6.8/kernel/exit.c)

### 4. Збережи доказ і звір таймлайн

```bash
sudo journalctl -u coderank-judge-sandbox-worker.service --since "$qa_since" --no-pager -o short-monotonic \
  --grep '^SystemError stage='
sudo journalctl -u coderank-judge-worker.service --since "$qa_since" --no-pager -o short-monotonic \
  --grep 'Reserved submission|Completion for submission|Judge evaluation halted|Lease for submission|Judge worker stopped'
sudo journalctl -k --since "$qa_since" --no-pager -o short-monotonic \
  --grep 'Long readout interval|rcu:.*stall|kthread.*starved'
systemctl show coderank-judge-sandbox-worker.service -p ActiveState -p Result -p ExecMainStatus -p CPUQuotaPerSecUSec
```

| Спостереження | Що перевірити |
| --- | --- |
| `CG_ATTACH` після cleanup `CG_KILL_RETURN` | Пізнє приєднання launcher. Перевір `leader_exit` та чи Runner зберіг fail-closed |
| `rc=1`, `populated=1`, PID у `R` або `D`, `CG_EXIT` запізнюється | Перевір pending SIGKILL, `wchan`, фактичне throttling і затримку vCPU/таймерів. `R` охоплює і runnable, і running |
| `CG_EXIT` відбувся, `CG_REAP` запізнюється, `populated=0` | Перевір стан `Z` та того, хто забирає exit status. Відокрем прямого launcher від Python |
| `CG_EXIT` відбувся, але `populated=1` | Перевір решту членів і точку переходу `CG_POPULATED`. `sched_process_exit` може передувати завершенню обліку cgroup |
| Великий `max_poll_gap_ms` одночасно з `CG_WATCH_GAP` і watchdog/RCU | Порівняй з метриками VM і хоста. При звичайному темпі зовнішніх зразків досліди окремо Runner та .NET wait |

Для приймання потрібні послідовні Ubuntu-прогони та відтворювані докази очищення після кожного `TimeLimitExceeded`. У відмові перевір, що після `Judge evaluation halted` той самий Worker більше не резервує заявки. Issue №21 залишається відкритим. Стан після встановлення цього кандидата на Ubuntu та BPF-прогін ще **NOT RUN**.
