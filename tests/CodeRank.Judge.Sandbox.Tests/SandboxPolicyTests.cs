using CodeRank.Judge.Sandbox;
using CodeRank.Judge.Worker;
using Xunit;

namespace CodeRank.Judge.Sandbox.Tests;

public sealed class SandboxPolicyTests
{
    [Theory]
    [InlineData("Compile", "compile-root", "compile", "Main.cpp", true)]
    [InlineData("Cpp17", "runtime-root", "cpp17", "program", false)]
    [InlineData("Python3", "runtime-root", "python3", "Main.py", true)]
    public void Command_mounts_only_the_intended_file_and_has_a_clean_environment(
        string phaseName, string root, string mode, string payload, bool noexec)
    {
        var phase = Enum.Parse<SandboxPhase>(phaseName);
        var plan = new NsJailPlan(phase, new SandboxLimits().For(phase), Guid.Parse("a138114a-7d57-44ee-a9f0-c7b1d7914cf9"));

        Assert.False(plan.StartInfo.UseShellExecute);
        Assert.Equal(SandboxLayout.Launcher, plan.StartInfo.FileName);
        Assert.Equal(plan.Directory, plan.StartInfo.WorkingDirectory);
        Assert.True(plan.StartInfo.RedirectStandardInput && plan.StartInfo.RedirectStandardOutput && plan.StartInfo.RedirectStandardError);
        Assert.Equal(new[]
        {
            "launch", plan.CgroupDirectory + "/cgroup.procs", plan.StatusFile, SandboxLayout.NsJail,
            "--config", plan.ConfigFile, "--seccomp_policy", plan.FilterFile,
            "--", "/job/coderank-sandbox-entry", mode, "33554432"
        }, plan.StartInfo.ArgumentList);
        var env = Assert.Single(plan.StartInfo.Environment);
        Assert.Equal("TMPDIR", env.Key);
        Assert.Equal(plan.Directory, env.Value);
        Assert.Equal(root, plan.Root.Split('/')[^1]);
        Assert.Contains($"dst: \"/job/{payload}\"", plan.Config);
        Assert.EndsWith($"noexec: {noexec.ToString().ToLowerInvariant()}\n}}", plan.Config.TrimEnd());
        Assert.Contains("clone_newnet: true", plan.Config);
        Assert.Contains("mount_proc: false", plan.Config);
        Assert.Contains("keep_caps: false", plan.Config);
        Assert.Contains("DEFAULT ERRNO(1)", plan.Filter);
        Assert.Contains("ERRNO(42) { unshare }", plan.Filter);
        Assert.Equal(phase == SandboxPhase.Compile, plan.Filter.Contains("clone {", StringComparison.Ordinal));
        Assert.DoesNotContain("@TEMP_", plan.Config);
        Assert.DoesNotContain("@COMPILE_ONLY@", plan.Filter);
        Assert.DoesNotContain('\r', plan.Config);
        Assert.DoesNotContain('\r', plan.Filter);
    }

    [Fact]
    public void Budgets_apply_cpu_memory_process_storage_and_stream_limits()
    {
        var limits = new SandboxLimits();
        var test = limits.For(SandboxPhase.Python3);
        var compile = limits.For(SandboxPhase.Compile);
        Assert.Equal(TimeSpan.FromSeconds(2), test.WallTime);
        Assert.Equal(TimeSpan.FromSeconds(15), compile.WallTime);
        Assert.Equal(768 * 1024 * 1024, compile.MemoryBytes);
        Assert.Equal(16 * 1024 * 1024, compile.StdoutBytes);
        Assert.Equal(64 * 1024, test.StdoutBytes);
        Assert.Equal(64 * 1024, test.StderrBytes);
        Assert.Equal(32, test.TemporaryMiB);
        var files = LinuxJobCgroup.Limits(test).ToDictionary();
        Assert.Equal("268435456", files["memory.max"]);
        Assert.Equal("0", files["memory.swap.max"]);
        Assert.Equal("1", files["memory.oom.group"]);
        Assert.Equal("32", files["pids.max"]);
        Assert.Equal("100000 100000", files["cpu.max"]);
    }

    [Fact]
    public void Reviewed_numeric_overrides_change_the_budget_without_a_path_override()
    {
        var limits = SandboxLimits.Parse("""{"CpuPercent":75,"TemporaryMiB":12,"StdoutKiB":24}""");
        var budget = limits.For(SandboxPhase.Cpp17);
        Assert.Equal(24 * 1024, budget.StdoutBytes);
        Assert.Equal("75000 100000", LinuxJobCgroup.Limits(budget).ToDictionary()["cpu.max"]);
        var plan = new NsJailPlan(SandboxPhase.Cpp17, budget, Guid.NewGuid());
        Assert.Contains("size=12582912,nr_inodes=4096", plan.Config);
    }

    [Theory]
    [InlineData("""{"Root":"/"}""")]
    [InlineData("""{"TemporaryMiB":33}""")]
    [InlineData("""{"MaxProcesses":0}""")]
    [InlineData("""{"TestMemoryMiB":0}""")]
    [InlineData("""{"StderrKiB":"64"}""")]
    [InlineData("""{"CpuPercent":100,"CpuPercent":1}""")]
    [InlineData("""{"CompileWallMilliseconds":50}""")]
    [InlineData("null")]
    public void Unsafe_or_ambiguous_configuration_is_rejected(string json) =>
        Assert.ThrowsAny<Exception>(() => SandboxLimits.Parse(json));

    [Theory]
    [InlineData("R", false, false, false, 0, false, "Exited")]
    [InlineData("R", false, false, false, 125, false, "RuntimeError")]
    [InlineData("R", false, false, false, 1, true, "CompilationError")]
    [InlineData("R", false, false, true, 137, false, "TimeLimitExceeded")]
    [InlineData("R", true, false, false, 137, false, "MemoryLimitExceeded")]
    [InlineData("R", false, true, false, 137, true, "OutputLimitExceeded")]
    [InlineData("", false, false, false, 0, false, "SystemError")]
    [InlineData("RF", false, false, false, 125, false, "SystemError")]
    public void Only_an_attested_exit_can_be_attributed_to_student_code(string receipt, bool oom, bool overflow,
        bool timeout, int code, bool compile, string expected)
    {
        var phase = compile ? SandboxPhase.Compile : SandboxPhase.Python3;
        Assert.Equal(Enum.Parse<IsolatedCaseStatus>(expected),
            LinuxSandboxExecutor.Classify(phase, receipt, oom, overflow, timeout, code));
    }

    [Fact]
    public async Task Output_capture_triggers_abort_at_the_first_overflow_and_keeps_exact_limit()
    {
        using var overflow = new CancellationTokenSource();
        using var exact = new MemoryStream(new byte[1024]);
        Assert.Equal(1024, (await LinuxSandboxExecutor.CaptureAsync(exact, 1024, overflow, Token)).Length);
        Assert.False(overflow.IsCancellationRequested);

        using var excessive = new MemoryStream(new byte[128 * 1024]);
        Assert.Empty(await LinuxSandboxExecutor.CaptureAsync(excessive, 1024, overflow, Token));
        Assert.True(overflow.IsCancellationRequested);
        Assert.InRange(excessive.Position, 1025, 8192);
    }

    [Theory]
    [InlineData(16384, false)]
    [InlineData(9000, true)]
    public async Task Stderr_capture_reads_all_chunks_to_eof_or_keeps_the_bounded_prefix(int limit, bool exceedsLimit)
    {
        var bytes = Enumerable.Range(0, 16384).Select(value => (byte)(value % 251)).ToArray();
        using var overflow = new CancellationTokenSource();
        using var stream = new MemoryStream(bytes);
        var capture = await LinuxSandboxExecutor.CaptureStderrAsync(stream, limit, overflow, Token);

        Assert.Equal(bytes[..limit], capture.Bytes);
        Assert.Equal(exceedsLimit, overflow.IsCancellationRequested);
        Assert.Equal(exceedsLimit ? SandboxStderrStatus.LimitExceeded : SandboxStderrStatus.Complete, capture.Status);
    }

    [Fact]
    public async Task Interrupted_stderr_capture_reports_the_bytes_already_read()
    {
        var prefix = "partial diagnostic"u8.ToArray();
        using var overflow = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stream = new InterruptAfterPrefix(prefix, stop);
        var capture = await LinuxSandboxExecutor.CaptureStderrAsync(stream, 1024, overflow, stop.Token);

        Assert.Equal(prefix, capture.Bytes);
        Assert.Equal(SandboxStderrStatus.Interrupted, capture.Status);
        Assert.False(overflow.IsCancellationRequested);
    }

    [Fact]
    public void Cleanup_removes_a_scoped_directory_only_after_the_cgroup_is_gone()
    {
        var root = Directory.CreateTempSubdirectory("coderank-cleanup-").FullName;
        var job = Path.Combine(root, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var temporary = Path.Combine(job, "payload");
        File.WriteAllText(temporary, "synthetic");
        try
        {
            Assert.Throws<SandboxUnavailableException>(() => LinuxSandboxExecutor.RemoveResources(
                () => throw new SandboxUnavailableException(), root, job));
            Assert.True(File.Exists(temporary));
            Assert.Throws<SandboxUnavailableException>(() => LinuxSandboxExecutor.RemoveResources(() => { }, job, root));
            Assert.True(Directory.Exists(root));

            LinuxSandboxExecutor.RemoveResources(() => Assert.True(File.Exists(temporary)), root, job);
            Assert.False(Directory.Exists(job));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(job)) Directory.Delete(job);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Missing_cgroup_metrics_and_mounted_subtrees_fail_closed()
    {
        Assert.Equal(2, LinuxJobCgroup.ParseCounter(["low 0", "oom 2"], "oom"));
        Assert.Throws<SandboxUnavailableException>(() => LinuxJobCgroup.ParseCounter(["oom invalid"], "oom"));
        Assert.Throws<SandboxUnavailableException>(() => LinuxJobCgroup.ParseCounter(["max 0"], "oom"));
        const string root = "/opt/coderank-judge-sandbox/runtime-root";
        Assert.True(LinuxSandboxHost.HasMountedPath([$"3 2 0:1 / {root}/proc rw"], root));
        Assert.False(LinuxSandboxHost.HasMountedPath([$"3 2 0:1 / {root}-other/proc rw"], root));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class InterruptAfterPrefix(byte[] prefix, CancellationTokenSource stop) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return base.ReadAsync(buffer, cancellationToken);
            stop.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }
}
