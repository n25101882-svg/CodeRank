using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeRank.Judge.Sandbox;

internal static partial class SandboxReceipt
{
    public static string? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> bytes = stackalloc byte[9];
            var count = file.ReadAtLeast(bytes, bytes.Length, false);
            return count == bytes.Length ? "?" : Encoding.ASCII.GetString(bytes[..count]);
        }
        catch (FileNotFoundException) { return null; }
    }

    internal static SandboxFailure? Explain(string? receipt, ReadOnlySpan<byte> stderr, bool timedOut)
    {
        if (receipt == "R") return null;
        if (receipt is null)
            return new(new(SandboxStage.LauncherBootstrap, timedOut ? SandboxFault.Timeout : SandboxFault.Rejected));
        if (receipt.Length == 0)
            return new(ExplainNsJailSetup(stderr, timedOut));
        if (receipt == "RF") return new(new(SandboxStage.JailProgramStart));

        var ready = receipt.StartsWith('R');
        var value = ready ? receipt.AsSpan(1) : receipt.AsSpan();
        if (value.Length is < 4 or > 7 || value[0] != 'F' || value[2] != ':' ||
            !int.TryParse(value[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var errno) || errno > 4095)
            return new(new(SandboxStage.Receipt, SandboxFault.InvalidData));

        var stage = (ready, value[1]) switch
        {
            (false, 'L') => SandboxStage.LauncherBootstrap,
            (false, 'C') => SandboxStage.LauncherCgroup,
            (false, 'A') => SandboxStage.JailAttestation,
            (false, 'M') => SandboxStage.JailMounts,
            (false, 'S') => SandboxStage.JailSeccomp,
            (true, 'E') => SandboxStage.JailProgramStart,
            _ => SandboxStage.Receipt
        };
        if (stage == SandboxStage.Receipt) return new(new(stage, SandboxFault.InvalidData));
        return new(new(stage, errno == 0 ? SandboxFault.Rejected : SandboxFault.Native, errno));
    }

    private static SandboxDiagnostic ExplainNsJailSetup(ReadOnlySpan<byte> stderr, bool timedOut)
    {
        // An empty receipt means the native entrypoint never attested and never ran the program.
        // Match only pinned NsJail diagnostics. The text and any paths are never returned.
        var text = Encoding.UTF8.GetString(stderr);
        if (text.Contains("Couldn't prepare sandboxing policy", StringComparison.Ordinal) ||
            text.Contains("seccomp(SECCOMP_SET_MODE_FILTER", StringComparison.Ordinal) ||
            text.Contains("prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER)", StringComparison.Ordinal))
            return new(SandboxStage.NsJailSeccomp);
        if (text.Contains("Failed to build mount tree", StringComparison.Ordinal) ||
            text.Contains("Failed to mount mandatory point", StringComparison.Ordinal) ||
            text.Contains("pivot_root(", StringComparison.Ordinal))
            return new(SandboxStage.NsJailMount);
        if (ExplainCloneFailure(text) is { } cloneFailure) return cloneFailure;
        if (text.Contains("clone(flags=", StringComparison.Ordinal) ||
            text.Contains("Couldn't initialize user namespace", StringComparison.Ordinal) ||
            text.Contains("Couldn't initialize net namespace", StringComparison.Ordinal) ||
            text.Contains("Couldn't initialize cgroup 2 user namespace", StringComparison.Ordinal))
            return new(SandboxStage.NsJailNamespace);
        return new(SandboxStage.NsJailSetup, timedOut ? SandboxFault.Timeout : SandboxFault.Rejected);
    }

    private static SandboxDiagnostic? ExplainCloneFailure(string text)
    {
        foreach (Match warning in CloneWarning().Matches(text))
        {
            if (ParseNamespaceFlags(warning.Groups[1].ValueSpan) is not { } flags) continue;
            // The launcher starts NsJail with a cleared environment. PLOG_W appends C-locale strerror text.
            var errno = warning.Groups[2].Value switch
            {
                "Operation not permitted" => NsJailCloneErrno.EPERM,
                "Argument list too long" => NsJailCloneErrno.E2BIG,
                "Resource temporarily unavailable" => NsJailCloneErrno.EAGAIN,
                "Cannot allocate memory" => NsJailCloneErrno.ENOMEM,
                "Permission denied" => NsJailCloneErrno.EACCES,
                "Device or resource busy" => NsJailCloneErrno.EBUSY,
                "File exists" => NsJailCloneErrno.EEXIST,
                "Invalid argument" => NsJailCloneErrno.EINVAL,
                "No space left on device" => NsJailCloneErrno.ENOSPC,
                "Function not implemented" => NsJailCloneErrno.ENOSYS,
                "Too many users" => NsJailCloneErrno.EUSERS,
                "Operation not supported" => NsJailCloneErrno.EOPNOTSUPP,
                _ => NsJailCloneErrno.Unknown
            };
            var fault = errno == NsJailCloneErrno.Unknown ? SandboxFault.Rejected : SandboxFault.Native;
            return new(SandboxStage.NsJailNamespace, fault, (int)errno, CloneFlags: flags);
        }
        return null;
    }

    private static NsJailNamespaceFlags? ParseNamespaceFlags(ReadOnlySpan<char> names)
    {
        var flags = NsJailNamespaceFlags.None;
        if (names.IsEmpty) return flags;
        foreach (var range in names.Split('|'))
        {
            if (!Enum.TryParse<NsJailNamespaceFlags>(names[range], out var flag) ||
                flag == NsJailNamespaceFlags.None || !Enum.IsDefined(flag) || (flags & flag) != 0)
                return null;
            flags |= flag;
        }
        return flags;
    }

    // NsJail 3.6 emits warnings to redirected stderr as [W][time][pid] function():line message.
    [GeneratedRegex(@"^\[W\]\[[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[+-][0-9]{4}\]" +
        @"\[[0-9]{1,10}\] runChild\(\):[0-9]{1,6} clone\(flags=([A-Z_|]{0,128})\) failed: ([^\r\n]{1,512})\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CloneWarning();
}
