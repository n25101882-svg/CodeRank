# CodeRank · SCRUM-10, Stage 2B1. Ізольований Linux Runner

## Архітектура та межі

`CodeRank.Judge.Sandbox` — окремий .NET 10 процес для ручного запуску в Ubuntu Server 24.04 x86-64. Він використовує наявний `IIsolatedJudgeRunner`. CLI приймає тільки `--integration` із заздалегідь заданими синтетичними сценаріями та `--expect-unavailable` для негативної перевірки. Звичайний Worker і службові маршрути черги працюють окремо. Результат `Exited` означає завершення з кодом 0 і валідний UTF-8 stdout. Порівняння з очікуваним результатом належить Stage 2B2. [SCRUM-10](https://coderank.atlassian.net/browse/SCRUM-10)

- **Ізоляція.** NsJail 3.6 із зафіксованим комітом `f78475530b46d0186111a9096b30725f816b55fe` створює user, mount, PID, network, IPC, UTS і cgroup namespaces. UID/GID 65534 всередині відображаються на окремий непривілейований обліковий запис `coderank-sandbox` у VM. Вузьке правило `userns create` належить профілю сервісу в enforce. Runner перевіряє активний профіль, глобальне обмеження user namespaces і відсутність привілеїв у процесу. [Реліз NsJail](https://github.com/google/nsjail/releases/tag/3.6) · [AppArmor в Ubuntu](https://documentation.ubuntu.com/security/security-features/privilege-restriction/apparmor/)
- **Мережа й файлові межі.** Сервіс має `PrivateNetwork=yes`. Усередині jail діє окрема мережа з вимкненим loopback та seccomp, що блокує сокети. Корінь — мінімальна Ubuntu з read-only, nosuid і nodev. Runner відхиляє rootfs із вкладеними монтуваннями. Доступні лише rootfs, один файл програми, `/dev/null` і приватний tmpfs. `/proc`, `/sys`, каталоги Worker, спільні папки VirtualBox і сокети хоста не монтуються. Поточний вхід подається через stdin. [Схема параметрів NsJail](https://github.com/google/nsjail/blob/3.6/config.proto)
- **Межа довіри.** Нативний launcher входить у вже обмежений cgroup перед `exec` NsJail і закриває зайві дескриптори. Всередині jail довірений entrypoint перевіряє PID 1, UID/GID, capabilities, NoNewPrivs, активний seccomp, унікальну відмову `unshare`, відмову `socket` та прапорці монтувань. Службовий дескриптор позначається close-on-exec і не потрапляє до програми. Машинний ключ, lease та очікувані відповіді залишаються у Worker, доступ до MSSQL — в API. Довірений Runner приймає список входів, а кожен дочірній jail отримує лише один із них.
- **Компіляція й виконання.** C++17 компілюється `g++ -std=c++17` в окремому jail із toolchain. Після успішної компіляції launcher передає статичний ELF через обмежений канал. Для кожного тесту відкривається новий runtime jail з одним read-only артефактом і входом. Python 3 стартує з `-I -B -S -u` в тому самому runtime-профілі. Профілі Kafel використовують allowlist. Компілятор може створювати підпроцеси без нових namespaces, runtime блокує створення процесів і потоків. [Kafel](https://github.com/google/kafel)
- **Завершення.** Runner зупиняє launcher, виконує `cgroup.kill` і підтверджує `populated 0` до очищення файлів. Дедлайн і переповнення потоку переривають виконання під час читання. `memory.events` відрізняє OOM від інших завершень. Відсутність атестації, належних лімітів або підтвердженого cleanup дає `SystemError` і блокує подальші спроби в цьому Runner. [cgroup v2](https://docs.kernel.org/admin-guide/cgroup-v2.html) · [Делегування systemd](https://manpages.ubuntu.com/manpages/noble/man5/systemd.resource-control.5.html)

## Ліміти

| Ресурс | Виконання тесту | Компіляція C++ |
| --- | --- | --- |
| Wall time, включно зі стартом jail | 2 с | 15 с |
| Пам’ять cgroup v2, без swap | 256 МіБ | 768 МіБ |
| CPU cgroup | До одного ядра, `cpu.max=100000 100000` | Так само |
| Процеси й потоки в cgroup | 32, включно з launcher | 32 |
| stdout | 64 КіБ | 16 МіБ для ELF |
| stderr | 64 КіБ | 64 КіБ для діагностики |
| Приватний tmpfs | 32 МіБ, до 4096 inode | Так само |
| Одночасні Submission | Один на процес і службовий cgroup | Так само |

Ліміти CPU й пам’яті діють також на дочірні процеси. NsJail додатково ставить `RLIMIT_CPU` на 1 с і власний wall timeout на 3 с вище округленого дедлайну. Core dumps вимкнено, відкриті файли обмежені до 64. Ліміти stdout і stderr рахуються в байтах. Кількість та розмір входів і джерела обмежені чинним Judge-контрактом. Статуси — `CompilationError`, `RuntimeError`, `TimeLimitExceeded`, `MemoryLimitExceeded`, `OutputLimitExceeded`, `SystemError` і `Exited`. Cancellation перериває роботу, а відмова в cleanup має пріоритет і дає `SystemError`.

Опційний `/opt/coderank-judge-sandbox/limits.json` належить root і може містити тільки числові властивості `TestWallMilliseconds`, `TestMemoryMiB`, `CompileWallMilliseconds`, `CompileMemoryMiB`, `CpuPercent`, `MaxProcesses`, `StdoutKiB`, `StderrKiB`, `TemporaryMiB`. Межі визначає `SandboxLimits`. Шляхи, команди й політики зафіксовані в коді та вбудованих шаблонах.

## Ручне встановлення на виділеній Ubuntu 24.04 QA VM

Потрібні x86-64, kernel із namespaces, seccomp та AppArmor, unified cgroup v2, systemd 255 і .NET 10. Запускай команди з копії цієї гілки на окремій VM із синтетичними даними. У VirtualBox від’єднай спільні папки, буфер обміну й перетягування. Для перевірок використовуй VM без доступу до робочої черги або MSSQL.

Збірка від звичайного користувача. Пакети встановлює адміністратор. Для `dotnet publish` потрібен SDK 10, на VM для запуску достатньо `Microsoft.NETCore.App` 10.

```bash
sudo apt install autoconf bison flex gcc g++ git make libtool pkg-config \
  libprotobuf-dev protobuf-compiler libnl-route-3-dev debootstrap ubuntu-keyring \
  apparmor apparmor-utils

git clone --no-checkout https://github.com/google/nsjail.git /tmp/coderank-nsjail-3.6 &&
git -C /tmp/coderank-nsjail-3.6 checkout --detach f78475530b46d0186111a9096b30725f816b55fe &&
git -C /tmp/coderank-nsjail-3.6 submodule update --init --recursive &&
test "$(git -C /tmp/coderank-nsjail-3.6/kafel rev-parse HEAD)" = 76d0f41bf3eb5c4008713d64b9767b461a9129a3 &&
make -C /tmp/coderank-nsjail-3.6 -j2

mkdir -p artifacts/sandbox
cc -std=c17 -O2 -Wall -Wextra -Werror -static -Wl,-z,noexecstack \
  sandbox/linux/coderank-sandbox-entry.c -o artifacts/sandbox/coderank-sandbox-entry
dotnet publish sandbox/CodeRank.Judge.Sandbox -c Release -p:UseAppHost=false -o artifacts/sandbox/app
```

Адміністративне налаштування для нової QA VM. `debootstrap` будує два окремі дерева з пакунків Ubuntu. Перед запуском перевір, що вони не містять доданих секретів, сокетів або активних монтувань.

```bash
prefix=/opt/coderank-judge-sandbox
sudo adduser --system --group --no-create-home --home /nonexistent \
  --shell /usr/sbin/nologin coderank-sandbox
sudo install -d -o root -g root -m 0755 "$prefix/bin" "$prefix/app"
sudo install -o root -g root -m 0755 /tmp/coderank-nsjail-3.6/nsjail "$prefix/bin/"
sudo install -o root -g root -m 0755 artifacts/sandbox/coderank-sandbox-entry "$prefix/bin/"
sudo install -o root -g root -m 0644 artifacts/sandbox/app/* "$prefix/app/"

sudo debootstrap --arch=amd64 --variant=minbase --include=g++,libc6-dev,binutils \
  noble "$prefix/compile-root" https://archive.ubuntu.com/ubuntu/
sudo debootstrap --arch=amd64 --variant=minbase --include=python3 \
  noble "$prefix/runtime-root" https://archive.ubuntu.com/ubuntu/
for tree in compile-root runtime-root; do
  sudo install -d -o root -g root -m 0755 "$prefix/$tree/job"
  sudo install -o root -g root -m 0755 "$prefix/bin/coderank-sandbox-entry" "$prefix/$tree/job/"
done
sudo install -o root -g root -m 0644 /dev/null "$prefix/compile-root/job/Main.cpp"
sudo install -o root -g root -m 0644 /dev/null "$prefix/runtime-root/job/Main.py"
sudo install -o root -g root -m 0644 /dev/null "$prefix/runtime-root/job/program"

sudo install -m 0644 sandbox/linux/coderank-judge-sandbox.apparmor /etc/apparmor.d/coderank-judge-sandbox
sudo apparmor_parser -r /etc/apparmor.d/coderank-judge-sandbox
sudo install -m 0644 sandbox/linux/coderank-judge-sandbox-smoke.service /etc/systemd/system/
sudo systemd-analyze verify /etc/systemd/system/coderank-judge-sandbox-smoke.service
sudo systemctl daemon-reload
```

`Delegate=cpu memory pids` і `DelegateSubgroup=supervisor` залишають supervisor поза cgroup кожного тесту. Обліковий запис не потребує setuid, capabilities або доступу до Docker. Якщо Noble відхиляє запуск, звір `aa-status`, `systemd-analyze verify`, mount flags, делегування та записи `apparmor="DENIED"` у журналі ядра. За потреби погоджуй точкове правило для цього профілю за фактичним denial. Зберігай режим enforce і чинні глобальні обмеження user namespaces. [Синтаксис профілів Noble](https://manpages.ubuntu.com/manpages/noble/man5/apparmor.d.5.html)

## Перевірки

```bash
dotnet build CodeRank.Api.slnx -c Release
dotnet test tests/CodeRank.Judge.Sandbox.Tests -c Release

# Позитивний інтеграційний набір з реальним NsJail та cgroup v2.
sudo systemctl start --wait coderank-judge-sandbox-smoke.service
sudo journalctl -u coderank-judge-sandbox-smoke.service -b --no-pager

# Негативний запуск від того самого UID поза делегованим сервісом і його профілем.
sudo -u coderank-sandbox env -i DOTNET_EnableDiagnostics=0 /usr/bin/dotnet \
  /opt/coderank-judge-sandbox/app/CodeRank.Judge.Sandbox.dll --expect-unavailable
```

Позитивний набір перевіряє C++17 з двома входами й закритим службовим FD, синтаксичну помилку, Python stdin/stdout і виняток, нескінченний цикл, OOM, перевищення обох потоків, відмову `fork`, недоступність `127.0.0.1:7160`, ізоляцію синтетичних файлів і середовища та ліміт tmpfs. Останній сценарій скасовує процес після атестації й перевіряє очищення. Після кожного сценарію suite звіряє відсутність тимчасових каталогів і cgroup. Очікувані підсумки — `13/13 Linux sandbox checks passed.` і `PASS isolation prerequisite refusal`.

| Перевірка | Фактичний результат у Codex |
| --- | --- |
| Release `CodeRank.Api.slnx` та publish .NET Sandbox | Успішно, 0 попереджень і 0 помилок у Release build |
| .NET unit-тести Runner | 35/35 |
| Негативний CLI на Windows | PASS, відмова до запуску будь-якого дочірнього процесу |
| Шаблони NsJail і Kafel | Статично звірено 40 полів зі схемою NsJail і 101 назву syscall з таблицею Kafel для x86-64. |
| Нативна Linux-збірка, реальні NsJail, seccomp, cgroup v2 та AppArmor | **NOT RUN**. WSL повернув `E_ACCESSDENIED`. |

## Ризики та Stage 2B2

Головна відкрита перевірка — фактична сумісність rootless NsJail, AppArmor і системного делегування на цільовій VM. Поки Linux suite не пройдено, Runner призначений для синтетичного QA. Довіреними залишаються ядро VM, адміністратор, NsJail, встановлені toolchain і runtime. Оновлення ядра, NsJail або rootfs потребує повторного QA. Ізоляція не усуває ризик вразливостей ядра, гіпервізора чи побічних каналів.

У Stage 2B2 під’єднати Runner через локальний IPC із контролем UID, залишити порівняння stdout та очікуваних відповідей у довіреній частині й пов’язати cancellation зі спливом lease. Узгодити правила порівняння, обробку `SystemError` і повторів. Перед ввімкненням `complete` провести повний Linux QA ізоляції та інтеграційний тест черги на синтетичних даних.
