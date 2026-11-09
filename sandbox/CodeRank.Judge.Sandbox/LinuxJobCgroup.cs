using System.Diagnostics;
using System.Globalization;

namespace CodeRank.Judge.Sandbox;

internal sealed class LinuxJobCgroup(string path, string parent)
{
    private static readonly string[] Controllers = ["cpu", "memory", "pids"];
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private bool created;
    private bool mayHaveProcesses;

    public void Create(SandboxBudget budget)
    {
        if (!HasControllers("cgroup.controllers"))
            throw new SandboxUnavailableException(SandboxStage.CgroupDelegation, control: "cgroup.controllers");
        if (!HasControllers("cgroup.subtree_control"))
            LinuxCgroupFiles.Write(parent, "cgroup.subtree_control", "+cpu +memory +pids", SandboxStage.CgroupDelegation);
        if (!HasControllers("cgroup.subtree_control"))
            throw new SandboxUnavailableException(SandboxStage.CgroupDelegation, control: "cgroup.subtree_control");
        if (Directory.Exists(path)) throw new SandboxUnavailableException(SandboxStage.CgroupCreate);
        try { Directory.CreateDirectory(path); }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
        {
            throw new SandboxUnavailableException(SandboxFailure.From(SandboxStage.CgroupCreate, error));
        }
        created = true;
        const SandboxStage stage = SandboxStage.CgroupSetup;
        foreach (var (file, value) in Limits(budget))
        {
            LinuxCgroupFiles.Write(path, file, value, stage);
            if (LinuxCgroupFiles.Read(path, file, stage).Trim() != value)
                throw new SandboxUnavailableException(stage, SandboxFault.InvalidData, control: file);
        }
        // Verify access to the kernel kill switch before enrolling any process.
        LinuxCgroupFiles.Write(path, "cgroup.kill", "1", stage);
        if (Populated(stage) || Counter("memory.events", "oom", stage) != 0 ||
            Counter("memory.events", "oom_kill", stage) != 0)
            throw new SandboxUnavailableException(stage, SandboxFault.InvalidData);
    }

    public void MarkLaunching() => mayHaveProcesses = true;

    public async Task<bool> StopAsync(Process process, bool started)
    {
        // Stop the launcher first so it cannot migrate into the group after cgroup.kill.
        var leaderFailure = await StopLeaderAsync(process, started);
        const SandboxStage stage = SandboxStage.CgroupCleanup;
        LinuxCgroupFiles.Write(path, "cgroup.kill", "1", stage);
        var elapsed = Stopwatch.StartNew();
        while (Populated(stage))
        {
            if (elapsed.Elapsed >= StopTimeout)
                throw new SandboxUnavailableException(stage, SandboxFault.Timeout, control: "cgroup.events");
            await Task.Delay(25);
        }
        if (leaderFailure is not null) throw new SandboxUnavailableException(leaderFailure);
        mayHaveProcesses = false;
        return Counter("memory.events", "oom", stage) > 0 || Counter("memory.events", "oom_kill", stage) > 0;
    }

    public void Remove()
    {
        if (!created) return;
        if (mayHaveProcesses) throw new SandboxUnavailableException(SandboxStage.CgroupCleanup);
        try { Directory.Delete(path); }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
        {
            throw new SandboxUnavailableException(SandboxFailure.From(SandboxStage.CgroupCleanup, error));
        }
    }

    internal static KeyValuePair<string, string>[] Limits(SandboxBudget budget) =>
    [
        new("memory.max", budget.MemoryBytes.ToString(CultureInfo.InvariantCulture)),
        new("memory.swap.max", "0"),
        new("memory.oom.group", "1"),
        new("pids.max", budget.Processes.ToString(CultureInfo.InvariantCulture)),
        new("cpu.max", (budget.CpuPercent * 1_000).ToString(CultureInfo.InvariantCulture) + " 100000")
    ];

    internal static long ParseCounter(IEnumerable<string> lines, string key)
    {
        foreach (var line in lines)
        {
            var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 2 && values[0] == key &&
                long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return number;
        }
        throw new SandboxUnavailableException();
    }

    private long Counter(string file, string name, SandboxStage stage)
    {
        var lines = LinuxCgroupFiles.Read(path, file, stage).Split('\n');
        try { return ParseCounter(lines, name); }
        catch (SandboxUnavailableException)
        {
            throw new SandboxUnavailableException(stage, SandboxFault.InvalidData, control: file);
        }
    }

    private bool Populated(SandboxStage stage) => Counter("cgroup.events", "populated", stage) != 0;

    private bool HasControllers(string control)
    {
        var present = LinuxCgroupFiles.Read(parent, control, SandboxStage.CgroupDelegation)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Controllers.All(controller => present.Contains(controller, StringComparer.Ordinal));
    }

    private static async Task<SandboxFailure?> StopLeaderAsync(Process process, bool started)
    {
        if (!started) return null;
        SandboxFailure? killFailure = null;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
        {
            killFailure = SandboxFailure.From(SandboxStage.ProcessCleanup, error);
        }
        try
        {
            await process.WaitForExitAsync().WaitAsync(StopTimeout);
            return null;
        }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
        {
            return killFailure ?? SandboxFailure.From(SandboxStage.ProcessCleanup, error);
        }
    }
}
