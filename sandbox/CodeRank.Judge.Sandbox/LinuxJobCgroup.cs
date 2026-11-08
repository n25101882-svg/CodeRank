using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeRank.Judge.Sandbox;

internal sealed class LinuxJobCgroup(string path)
{
    private static readonly string[] Controllers = ["cpu", "memory", "pids"];
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private bool created;
    private bool mayHaveProcesses;

    public void Create(SandboxBudget budget)
    {
        if (!HasControllers(SandboxLayout.Cgroup + "/cgroup.controllers"))
            throw new SandboxUnavailableException();
        if (!HasControllers(SandboxLayout.Cgroup + "/cgroup.subtree_control"))
            Write(SandboxLayout.Cgroup + "/cgroup.subtree_control", "+cpu +memory +pids");
        if (!HasControllers(SandboxLayout.Cgroup + "/cgroup.subtree_control") || Directory.Exists(path))
            throw new SandboxUnavailableException();

        Directory.CreateDirectory(path);
        created = true;
        foreach (var (file, value) in Limits(budget))
        {
            Write(path + "/" + file, value);
            if (File.ReadAllText(path + "/" + file).Trim() != value) throw new SandboxUnavailableException();
        }
        // Verify access to the kernel kill switch before enrolling any process.
        Write(path + "/cgroup.kill", "1");
        if (Populated() || Counter(path + "/memory.events", "oom") != 0 ||
            Counter(path + "/memory.events", "oom_kill") != 0)
            throw new SandboxUnavailableException();
    }

    public void MarkLaunching() => mayHaveProcesses = true;

    public async Task<bool> StopAsync(Process process, bool started)
    {
        // Stop the launcher first so it cannot migrate into the group after cgroup.kill.
        var leaderStopped = await StopLeaderAsync(process, started);
        Write(path + "/cgroup.kill", "1");
        var elapsed = Stopwatch.StartNew();
        while (Populated())
        {
            if (elapsed.Elapsed >= StopTimeout) throw new SandboxUnavailableException();
            await Task.Delay(25);
        }
        if (!leaderStopped) throw new SandboxUnavailableException();
        mayHaveProcesses = false;
        return Counter(path + "/memory.events", "oom") > 0 || Counter(path + "/memory.events", "oom_kill") > 0;
    }

    public void Remove()
    {
        if (!created) return;
        if (mayHaveProcesses) throw new SandboxUnavailableException();
        Directory.Delete(path);
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

    private static long Counter(string file, string name) => ParseCounter(File.ReadLines(file), name);

    private bool Populated() => Counter(path + "/cgroup.events", "populated") != 0;

    private static bool HasControllers(string file)
    {
        var present = File.ReadAllText(file).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Controllers.All(controller => present.Contains(controller, StringComparer.Ordinal));
    }

    private static void Write(string file, string value)
    {
        using var stream = new FileStream(file, System.IO.FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(Encoding.ASCII.GetBytes(value));
    }

    private static async Task<bool> StopLeaderAsync(Process process, bool started)
    {
        if (!started) return true;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try
        {
            await process.WaitForExitAsync().WaitAsync(StopTimeout);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
