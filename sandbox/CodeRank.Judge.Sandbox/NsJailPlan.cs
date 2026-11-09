using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeRank.Judge.Sandbox;

internal sealed class SandboxLayout
{
    public const string Install = "/opt/coderank-judge-sandbox";
    public const string Launcher = Install + "/bin/coderank-sandbox-entry";
    public const string NsJail = Install + "/bin/nsjail";
    public const string CompileRoot = Install + "/compile-root";
    public const string RuntimeRoot = Install + "/runtime-root";
    public const string LimitsFile = Install + "/limits.json";
    public const string ExecutableScratch = "/tmp";
    public const string ExecutablePrefix = "coderank-judge-program-";
    public const string Profile = "coderank-judge-sandbox";
    public const string CgroupMount = "/sys/fs/cgroup";

    public static SandboxLayout Smoke { get; } = new("smoke", "/run/coderank-judge-sandbox");
    public static SandboxLayout Worker { get; } = new("worker", "/run/coderank-judge-sandbox-worker");
    public string Scratch { get; }
    public string UnitCgroup { get; }
    public string Cgroup => CgroupMount + UnitCgroup;

    private SandboxLayout(string service, string scratch)
    {
        UnitCgroup = "/system.slice/coderank-judge-sandbox-" + service + ".service";
        Scratch = scratch;
    }
}

internal sealed class NsJailPlan
{
    private static readonly string ConfigTemplate = LoadResource("nsjail.cfg.in");
    private static readonly string FilterTemplate = LoadResource("seccomp.kafel.in");

    public string Directory { get; }
    public string PayloadFile { get; }
    public string ConfigFile => Directory + "/jail.cfg";
    public string FilterFile => Directory + "/seccomp.kafel";
    public string StatusFile => Directory + "/status";
    public string CgroupDirectory { get; }
    public SandboxLayout Layout { get; }
    public string Root { get; }
    public string PayloadName { get; }
    public string Config { get; }
    public string Filter { get; }
    public ProcessStartInfo StartInfo { get; }

    public NsJailPlan(SandboxPhase phase, SandboxBudget budget, Guid invocation, SandboxLayout? layout = null)
    {
        if (invocation == Guid.Empty) throw new SandboxUnavailableException();
        var id = invocation.ToString("N");
        var name = "job-" + id;
        Layout = layout ?? SandboxLayout.Smoke;
        Directory = Layout.Scratch + "/" + name;
        // The service's PrivateTmp can back the read-only executable bind on hosts with noexec /run.
        PayloadFile = phase == SandboxPhase.Cpp17
            ? SandboxLayout.ExecutableScratch + "/" + SandboxLayout.ExecutablePrefix + id
            : Directory + "/payload";
        CgroupDirectory = Layout.Cgroup + "/" + name;
        var compile = phase == SandboxPhase.Compile;
        var mode = phase switch
        {
            SandboxPhase.Compile => "compile",
            SandboxPhase.Cpp17 => "cpp17",
            SandboxPhase.Python3 => "python3",
            _ => throw new SandboxUnavailableException()
        };
        Root = compile ? SandboxLayout.CompileRoot : SandboxLayout.RuntimeRoot;
        PayloadName = phase switch
        {
            SandboxPhase.Compile => "Main.cpp",
            SandboxPhase.Cpp17 => "program",
            _ => "Main.py"
        };
        var seconds = (int)Math.Ceiling(budget.WallTime.TotalSeconds);
        var temporaryBytes = (budget.TemporaryMiB * SandboxLimits.MiB).ToString(CultureInfo.InvariantCulture);
        Config = ConfigTemplate
            .Replace("@ROOT@", Root, StringComparison.Ordinal)
            .Replace("@PAYLOAD@", PayloadFile, StringComparison.Ordinal)
            .Replace("@NAME@", PayloadName, StringComparison.Ordinal)
            .Replace("@DATA_ONLY@", phase == SandboxPhase.Cpp17 ? "false" : "true", StringComparison.Ordinal)
            .Replace("@TEMP_BYTES@", temporaryBytes, StringComparison.Ordinal)
            .Replace("@TEMP_MIB@", budget.TemporaryMiB.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@WALL@", (seconds + 3).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@CPU@", (seconds + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Filter = FilterTemplate.Replace("@COMPILE_ONLY@", compile ? CompileCalls : "", StringComparison.Ordinal)
            .ReplaceLineEndings("\n");

        StartInfo = new(SandboxLayout.Launcher)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory
        };
        StartInfo.Environment.Clear();
        StartInfo.Environment["TMPDIR"] = Directory;
        foreach (var argument in new[]
        {
            "launch", CgroupDirectory + "/cgroup.procs", StatusFile, SandboxLayout.NsJail,
            "--config", ConfigFile, "--seccomp_policy", FilterFile,
            "--", "/job/coderank-sandbox-entry", mode, temporaryBytes
        })
            StartInfo.ArgumentList.Add(argument);
    }

    private static string LoadResource(string name)
    {
        using var stream = typeof(NsJailPlan).Assembly.GetManifestResourceStream(name)
            ?? throw new SandboxUnavailableException();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    // glibc fork/posix_spawn use clone on x86_64 when clone3 returns ENOSYS.
    private const string CompileCalls = """
        ALLOW {
          clone { (clone_flags & 0x7e02a000) == 0 },
          fork, vfork, wait4, waitid, pipe, pipe2
        }
        """;
}
