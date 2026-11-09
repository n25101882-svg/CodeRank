#!/usr/bin/env python3
"""Read-only QA observer for the Judge's delegated job cgroups."""

import argparse
import os
from pathlib import Path
import re
import time


LAYOUTS = {
    "worker": ("coderank-judge-sandbox-worker.service", "coderank-judge-sandbox-worker"),
    "smoke": ("coderank-judge-sandbox-smoke.service", "coderank-judge-sandbox"),
}
JOB = re.compile(r"job-[0-9a-f]{32}\Z")
STATES = frozenset("RSDZTtXxKWPI")
PERIOD_MS = 100
MAX_GROUPS = 8
MAX_PIDS = 16
MAX_LINES = 4096


def read_bounded(path, limit=8192):
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("QA sample exceeds its byte limit")
    return data


def counters(path):
    try:
        return {key: int(value) for key, value in
                (line.split() for line in read_bounded(path).splitlines()) if value.isdigit()}
    except (OSError, ValueError):
        return {}


def job_dirs(root):
    groups = []
    try:
        with os.scandir(root) as entries:
            for entry in entries:
                if JOB.fullmatch(entry.name) and entry.is_dir(follow_symlinks=False):
                    groups.append(Path(entry.path))
                    if len(groups) > MAX_GROUPS:
                        raise SystemExit("QA_OBSERVER_LIMIT job_directories")
    except FileNotFoundError:
        pass  # The service can remove its root while it stops.
    return sorted(groups)


def proc_stat(pid):
    try:
        # comm can contain spaces and ')'. Only the numeric suffix and state are relevant.
        fields = read_bounded(Path(f"/proc/{pid}/stat"), 4096).rpartition(b") ")[2].split()
        state = fields[0].decode("ascii")
        if state in STATES:
            return state, int(fields[19]), int(fields[36])  # starttime and processor
    except FileNotFoundError:
        return "gone", None, None
    except (OSError, ValueError, IndexError):
        pass
    return "unknown", None, None


def pending_kill(pid, expected_start):
    try:
        status = read_bounded(Path(f"/proc/{pid}/status"))
        signals = dict(re.findall(rb"^(SigPnd|ShdPnd):[ \t]*([0-9a-fA-F]+)$", status, re.M))
        if len(signals) == 2 and proc_stat(pid)[1] == expected_start:
            return int(any(int(value, 16) & (1 << 8) for value in signals.values()))
    except (OSError, ValueError):
        pass
    return "unknown"


def wait_channel(pid, expected_start):
    try:
        channel = read_bounded(Path(f"/proc/{pid}/wchan"), 128).strip()
        if re.fullmatch(rb"[A-Za-z0-9_.]{1,64}", channel) and proc_stat(pid)[1] == expected_start:
            return channel.decode("ascii")
    except (OSError, ValueError):
        pass
    return "unknown"


def task_in_group(pid, group):
    try:
        expected = b"0::/" + os.fsencode(group.relative_to("/sys/fs/cgroup"))
        return expected in read_bounded(Path(f"/proc/{pid}/cgroup"), 4096).splitlines()
    except (OSError, ValueError):
        return False


def sample_group(group):
    group_id = group.stat().st_ino
    events = counters(group / "cgroup.events")
    cpu = counters(group / "cpu.stat")
    try:
        pids = sorted({int(pid) for pid in read_bounded(group / "cgroup.procs", 4096).split()
                       if pid.isdigit() and 0 < int(pid) <= 2**31 - 1})
    except (OSError, ValueError):
        return group_id, events, cpu, None
    tasks = []
    for pid in pids[:MAX_PIDS]:
        state, start, processor = proc_stat(pid)
        if start is not None and not task_in_group(pid, group):
            state, start, processor = "moved_or_reused", None, None
        kill = pending_kill(pid, start) if start is not None else "unknown"
        channel = wait_channel(pid, start) if state == "D" else "-"
        tasks.append((pid, state, start, processor, kill, channel))
    if len(pids) > MAX_PIDS:
        print(f"QA_OBSERVER_LIMIT group={group_id} members_sampled={MAX_PIDS} members_total={len(pids)}", flush=True)
        raise SystemExit(2)
    return group_id, events, cpu, tasks


def cpu_steal():
    try:
        with Path("/proc/stat").open("rb") as stream:
            fields = stream.readline(512).split()
        return int(fields[8]) if fields[0] == b"cpu" else "unknown"
    except (OSError, ValueError, IndexError):
        return "unknown"


def observe(scope, seconds):
    unit, scratch = LAYOUTS[scope]
    root = Path("/sys/fs/cgroup/system.slice") / unit
    previous_layout = None
    previous_groups = set()
    previous_tasks = set()
    last = time.monotonic_ns() // 1_000_000
    deadline = last + seconds * 1000
    lines = 0

    def emit(event, timestamp, fields):
        nonlocal lines
        if lines >= MAX_LINES:
            raise SystemExit("QA_OBSERVER_LIMIT output_lines")
        print(f"{event} ms={timestamp} {fields}", flush=True)
        lines += 1

    emit("CG_WATCH_READY", last, f"scope={scope} interval_ms={PERIOD_MS} seconds={seconds}")
    while True:
        now = time.monotonic_ns() // 1_000_000
        if now - last >= 3 * PERIOD_MS:
            emit("CG_WATCH_GAP", now, f"gap_ms={now - last} cpu_steal_ticks={cpu_steal()}")
        if now >= deadline:
            emit("CG_WATCH_END", now, f"scope={scope}")
            return
        last = now
        groups = job_dirs(root)
        layout = len(groups), len(job_dirs(Path("/run") / scratch))
        if layout != previous_layout:
            emit("CG_LAYOUT", now, f"groups={layout[0]} scratch={layout[1]} cpu_steal_ticks={cpu_steal()}")
            previous_layout = layout
        active_groups = set()
        active_tasks = set()
        for group in groups:
            try:
                group_id, events, cpu, tasks = sample_group(group)
            except FileNotFoundError:
                continue
            active_groups.add(group_id)
            members = "unknown" if tasks is None else ",".join(
                f"{pid}/{state}/{start if start is not None else '-'}/{processor if processor is not None else '-'}/{kill}/{channel}"
                for pid, state, start, processor, kill, channel in tasks) or "none"
            if tasks is not None:
                active_tasks.update((group_id, pid, start) for pid, _, start, _, _, _ in tasks if start is not None)
            else:
                active_tasks.update(task for task in previous_tasks if task[0] == group_id)
            observed = time.monotonic_ns() // 1_000_000
            emit("CG_SAMPLE", observed, f"group={group_id} populated={events.get(b'populated', 'unknown')} "
                 f"frozen={events.get(b'frozen', 'unknown')} nr_throttled={cpu.get(b'nr_throttled', 'unknown')} "
                 f"throttled_usec={cpu.get(b'throttled_usec', 'unknown')} members={members}")
        for group_id, pid, start in sorted(previous_tasks - active_tasks):
            state, current_start, _ = proc_stat(pid)
            if current_start is not None and start != current_start:
                state = "pid_reused"
            emit("CG_TASK_LEFT", time.monotonic_ns() // 1_000_000,
                 f"group={group_id} pid={pid} start_ticks={start} state={state}")
        for group_id in sorted(previous_groups - active_groups):
            emit("CG_GROUP_GONE", time.monotonic_ns() // 1_000_000, f"group={group_id}")
        previous_groups, previous_tasks = active_groups, active_tasks
        time.sleep(PERIOD_MS / 1000)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scope", choices=LAYOUTS)
    parser.add_argument("--seconds", type=int, default=360)
    options = parser.parse_args()
    if not 5 <= options.seconds <= 600:
        parser.error("--seconds must be between 5 and 600")
    try:
        observe(options.scope, options.seconds)
    except OSError as error:
        raise SystemExit(f"QA_OBSERVER_IO errno={error.errno}") from None
