using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeRank.Judge.Sandbox;

internal enum SandboxPhase { Compile, Cpp17, Python3 }

internal sealed record SandboxLimits
{
    public int TestWallMilliseconds { get; init; } = 2_000;
    public int TestMemoryMiB { get; init; } = 256;
    public int CompileWallMilliseconds { get; init; } = 15_000;
    public int CompileMemoryMiB { get; init; } = 768;
    public int CpuPercent { get; init; } = 100;
    public int MaxProcesses { get; init; } = 32;
    public int StdoutKiB { get; init; } = 64;
    public int StderrKiB { get; init; } = 64;
    public int TemporaryMiB { get; init; } = 32;

    internal const int MiB = 1024 * 1024;
    internal const int ArtifactBytes = 16 * MiB;

    public SandboxBudget For(SandboxPhase phase)
    {
        Validate();
        var compile = phase == SandboxPhase.Compile;
        return new(
            TimeSpan.FromMilliseconds(compile ? CompileWallMilliseconds : TestWallMilliseconds),
            (compile ? CompileMemoryMiB : TestMemoryMiB) * MiB,
            CpuPercent, MaxProcesses, compile ? ArtifactBytes : StdoutKiB * 1024,
            StderrKiB * 1024, TemporaryMiB);
    }

    public static SandboxLimits Parse(string json)
    {
        var limits = JsonSerializer.Deserialize<SandboxLimits>(json, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            MaxDepth = 2
        }) ?? throw new SandboxUnavailableException();
        limits.Validate();
        return limits;
    }

    public void Validate()
    {
        if (TestWallMilliseconds is < 250 or > 10_000 ||
            TestMemoryMiB is < 64 or > 512 ||
            CompileWallMilliseconds < TestWallMilliseconds || CompileWallMilliseconds > 30_000 ||
            CompileMemoryMiB < TestMemoryMiB || CompileMemoryMiB > 1024 ||
            CpuPercent is < 10 or > 100 || MaxProcesses is < 8 or > 32 ||
            StdoutKiB is < 1 or > 64 || StderrKiB is < 1 or > 64 ||
            TemporaryMiB is < 1 or > 32)
            throw new SandboxUnavailableException();
    }
}

internal sealed record SandboxBudget(TimeSpan WallTime, int MemoryBytes, int CpuPercent, int Processes,
    int StdoutBytes, int StderrBytes, int TemporaryMiB);
