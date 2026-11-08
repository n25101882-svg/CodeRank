using System.Text.RegularExpressions;
using static CodeRank.Judge.Sandbox.NsJailMountOperation;

namespace CodeRank.Judge.Sandbox;

internal static partial class SandboxReceipt
{
    private const string QuotedMountArgument = @"'(?:\\.|[^'\\\r\n])*'";

    private static SandboxDiagnostic? ExplainMountFailure(string text)
    {
        var preceding = new Dictionary<string, SandboxDiagnostic>(StringComparer.Ordinal);
        SandboxDiagnostic? failure = null;
        foreach (var line in text.Split('\n'))
        {
            if (ReadLog(line, null, null, out var log) is not null) continue;
            var pid = log.Groups["pid"].Value;
            var site = log.Groups["function"].Value;
            var previous = preceding.GetValueOrDefault(pid);
            var current = ReadMountRecord(log.Groups["level"].ValueSpan[0], site, log.Groups["body"].Value,
                previous.MountOperation.HasValue ? previous : null);
            if (current is null)
            {
                preceding.Remove(pid);
                continue;
            }

            preceding[pid] = current.Value;
            // Directory helpers also have callers outside mount setup. Wait for a mount-specific record.
            if (site is not ("createDirRecursively" or "createDirAt"))
                failure = current;
        }
        return failure;
    }

    private static SandboxDiagnostic? ReadMountRecord(char level, string site, string body, SandboxDiagnostic? preceding)
    {
        // These summaries are LOG_E/LOG_W, without an errno. Only use evidence from the same child.
        if ((level, site, body) == ('E', "initCloneNs", "Failed to build mount tree"))
            return preceding ?? MountDiagnostic(BuildMountTree);
        if ((level, site, body) == ('E', "buildMountTree", "Failed to create root tmpfs"))
            return preceding is { MountOperation: Fsopen or Fsconfig or Fsmount, MountTarget: NsJailMountTarget.Tmpfs }
                ? preceding : MountDiagnostic(Mount, NsJailMountTarget.Tmpfs);
        if ((level, site) == ('E', "buildMountTree") && MandatoryMount().Match(body) is { Success: true } mandatory)
        {
            var target = MountTargetFromPath(mandatory.Groups["target"].Value);
            if (preceding is { MountOperation: Mount or OpenTree or MoveMount or Fsopen or Fsconfig or Fsmount or PrepareMountTarget } point &&
                point.MountTarget != NsJailMountTarget.MountTree &&
                (point.MountTarget is null || point.MountTarget == target))
                return point with { MountTarget = target };
            return MountDiagnostic(Mount, target);
        }
        if ((level, site) == ('E', "findWorkDir") &&
            body is "Failed to create work directory for 'root'" or "Failed to create work directory for 'tmp'")
            return MountDiagnostic(FindWorkDir, body.EndsWith("'tmp'", StringComparison.Ordinal)
                ? NsJailMountTarget.Tmpfs : NsJailMountTarget.MountTree);
        if (level == 'W' && site is ("mountSinglePoint" or "mountSinglePointAt") &&
            body.StartsWith("Failed to create parent directories for '", StringComparison.Ordinal) && body.EndsWith('\''))
            return preceding is { MountOperation: PrepareMountTarget } ? preceding : MountDiagnostic(PrepareMountTarget);

        // PLOG records append ": " and C-locale strerror. Paths and some legacy hints also contain colons.
        var separator = body.LastIndexOf(": ", StringComparison.Ordinal);
        if (separator < 0) return null;
        var command = MountCall().Match(body[..separator]);
        if (!command.Success) return null;
        var args = command.Groups["args"].Value;
        var paths = MountCallPaths().Match(args);
        var targetHint = command.Groups["target"].Success
            ? MountTargetFromPath(command.Groups["target"].Value)
            : MountTargetFromPath(paths.Groups["target"].Value) ?? MountTargetFromPath(paths.Groups["source"].Value);

        // Restrict errno attribution to call sites that return failure. applyMountFlags and the
        // new API's target creation warnings can be followed by successful operations.
        (NsJailMountOperation? Operation, NsJailMountTarget? Target) step = (level, site, command.Groups["call"].Value) switch
        {
            ('E', "buildMountTree", "mount") when args == "'/', MS_REC|MS_PRIVATE" =>
                (MakeMountPrivate, NsJailMountTarget.MountTree),
            ('E', "buildMountTree", "mount") when args.EndsWith("', tmpfs", StringComparison.Ordinal) =>
                (Mount, NsJailMountTarget.Tmpfs),
            ('E', "buildMountTree", "mount") when args.EndsWith("', MS_REMOUNT|MS_BIND|MS_RDONLY", StringComparison.Ordinal) ||
                HasReadOnlyBindRemountFlags(paths.Groups["flags"].Value) =>
                (Remount, NsJailMountTarget.MountTree),
            ('E', "buildMountTree", "statvfs") when paths.Groups["source"].Success =>
                (Statvfs, NsJailMountTarget.MountTree),
            ('E', "initCloneNs", "pivot_root") when PivotRootArguments().IsMatch(args) =>
                (PivotRoot, NsJailMountTarget.MountTree),
            ('E', "buildMountTree" or "initCloneNs", "umount2") => (Umount2, NsJailMountTarget.MountTree),
            ('E', "buildMountTree" or "initNsInternal", "chdir") => (Chdir, NsJailMountTarget.MountTree),
            ('E', "buildMountTree", "openat") => (PrepareMountTarget, NsJailMountTarget.MountTree),
            ('W', "mountSinglePoint" or "mountWithDynamicContent", "mount") when paths.Groups["target"].Success =>
                (Mount, targetHint),
            ('W', "remountPt" or "remountWithLegacyMount", "mount" or "statvfs") => (Remount, targetHint),
            ('W', "createMountTarget" or "createDirRecursively" or "mountWithDynamicContent", "mkdir" or "open" or "openat")
                or ('W', "createDirAt", "mkdirat") => (PrepareMountTarget, targetHint),
            ('W', "createDetachedTmpfs" or "createFilesystemMount", "fsopen") when paths.Groups["source"].Success =>
                (Fsopen, targetHint),
            ('W', "createDetachedTmpfs" or "createFilesystemMount", "fsmount") when paths.Groups["source"].Success =>
                (Fsmount, targetHint),
            ('W', "createDetachedTmpfs" or "createFilesystemMount", "fsconfig") when FsconfigArguments().IsMatch(args) =>
                (Fsconfig, site == "createDetachedTmpfs" ? NsJailMountTarget.Tmpfs : null),
            ('W', "doBindMountAt" or "openMountForRemount" or "mountDynamicContentAt", "open_tree") when paths.Success =>
                (OpenTree, targetHint),
            ('W', "doBindMountAt" or "mountSinglePointAt" or "mountDynamicContentAt", "move_mount")
                when paths.Groups["target"].Success || (args.Length == 0 && command.Groups["target"].Success) =>
                (MoveMount, targetHint),
            ('E', "buildMountTree", "move_mount")
                when paths.Groups["target"].Success && args.StartsWith("root_mfd -> '", StringComparison.Ordinal) =>
                (MoveMount, NsJailMountTarget.MountTree),
            ('E', "buildMountTree", "mount_setattr") when args == "root_fd, MOUNT_ATTR_RDONLY" =>
                (MountSetattr, NsJailMountTarget.MountTree),
            _ => (null, null)
        };
        return step.Operation is { } operation ? MountDiagnostic(operation, step.Target, ParseErrno(body[(separator + 2)..])) : null;
    }

    private static SandboxDiagnostic MountDiagnostic(NsJailMountOperation operation, NsJailMountTarget? target = null,
        NsJailErrno errno = NsJailErrno.Unknown) =>
        new(SandboxStage.NsJailMount, errno == NsJailErrno.Unknown ? SandboxFault.Rejected : SandboxFault.Native,
            (int)errno, MountOperation: operation, MountTarget: target);

    private static bool HasReadOnlyBindRemountFlags(string flags)
    {
        var names = flags.Split('|');
        return names.Contains("MS_REMOUNT", StringComparer.Ordinal) && names.Contains("MS_BIND", StringComparer.Ordinal) &&
            names.Contains("MS_RDONLY", StringComparer.Ordinal);
    }

    private static NsJailMountTarget? MountTargetFromPath(string path)
    {
        // Legacy destinations append "/" and the configured absolute path, producing a double slash.
        var legacySeparator = path.LastIndexOf("//", StringComparison.Ordinal);
        if (legacySeparator >= 0) path = path[(legacySeparator + 1)..];
        return path switch
        {
            SandboxLayout.CompileRoot or SandboxLayout.RuntimeRoot or "/" or "." => NsJailMountTarget.Rootfs,
            "tmpfs" or "/tmp" or "tmp" => NsJailMountTarget.Tmpfs,
            "/dev/null" or "dev/null" or "/job/Main.cpp" or "job/Main.cpp" or "/job/Main.py" or "job/Main.py"
                or "/job/program" or "job/program" => NsJailMountTarget.File,
            _ => null
        };
    }

    [GeneratedRegex(@"^Failed to mount mandatory point: '(?<target>[^'\r\n]*)'$", RegexOptions.CultureInvariant)]
    private static partial Regex MandatoryMount();

    [GeneratedRegex(@"^(?<call>[a-z0-9_]+)\((?<args>.*)\)(?: for '(?<target>[^'\r\n]*)'|" +
        @": try 'chmod o\+x' on source path|: procfs mount may fail if /proc has overmounts \(e\.g\., /dev/null on /proc/kcore\))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MountCall();

    [GeneratedRegex(@"^(?:'(?<source>[^'\r\n]*)'(?: -> '(?<target>[^'\r\n]*)'|, flags=(?<flags>\S+))?|" +
        @"(?:root_fd, |root_mfd -> )'(?<target>[^'\r\n]*)')$", RegexOptions.CultureInvariant)]
    private static partial Regex MountCallPaths();

    [GeneratedRegex("^" + QuotedMountArgument + ", " + QuotedMountArgument + "$", RegexOptions.CultureInvariant)]
    private static partial Regex PivotRootArguments();

    [GeneratedRegex(@"^(?:CMD_CREATE|size=[0-9]+|(?:source|flag)=" + QuotedMountArgument +
        "|key=" + QuotedMountArgument + ", value=" + QuotedMountArgument + ")$", RegexOptions.CultureInvariant)]
    private static partial Regex FsconfigArguments();
}
