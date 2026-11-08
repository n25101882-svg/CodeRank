using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeRank.Judge.Sandbox;

internal static partial class SandboxReceipt
{
    private static SandboxDiagnostic? ExplainNamespaceFailure(string text)
    {
        if (!text.Contains("clone", StringComparison.Ordinal) &&
            !text.Contains("Couldn't initialize", StringComparison.Ordinal))
            return null;

        var lines = text.Split('\n');
        var clone = ExplainCloneFailure(lines);
        if (clone is { CloneFlags: not null }) return clone;
        if (ExplainParentFailure(lines) is { } parent) return parent;
        if (clone is not null) return clone;

        return text.Contains("Couldn't initialize user namespace", StringComparison.Ordinal) ||
            text.Contains("Couldn't initialize net namespace", StringComparison.Ordinal) ||
            text.Contains("Couldn't initialize cgroup user namespace", StringComparison.Ordinal) ||
            text.Contains("Couldn't initialize cgroup 2 user namespace", StringComparison.Ordinal)
            ? new(SandboxStage.NsJailNamespace, NamespaceDetail: NsJailNamespaceDetail.NamespaceRecord)
            : null;
    }

    private static SandboxDiagnostic? ExplainCloneFailure(string[] lines)
    {
        NsJailNamespaceDetail? mismatch = null;
        foreach (var line in lines)
        {
            if (!CloneCandidate().IsMatch(line)) continue;
            if (ReadLog(line, 'W', "runChild", out var log) is { } headerFailure)
            {
                mismatch ??= headerFailure;
                continue;
            }

            var warning = CloneWarning().Match(log.Groups["body"].Value);
            if (!warning.Success)
            {
                mismatch ??= NsJailNamespaceDetail.CloneMessage;
                continue;
            }
            if (ParseNamespaceFlags(warning.Groups["flags"].ValueSpan) is not { } flags)
            {
                mismatch ??= NsJailNamespaceDetail.CloneFlags;
                continue;
            }

            var errno = ParseErrno(warning.Groups["error"].Value);
            return new(SandboxStage.NsJailNamespace,
                errno == NsJailCloneErrno.Unknown ? SandboxFault.Rejected : SandboxFault.Native,
                (int)errno, CloneFlags: flags,
                NamespaceDetail: errno == NsJailCloneErrno.Unknown ? NsJailNamespaceDetail.CloneErrno : null);
        }

        return mismatch is { } detail ? new(SandboxStage.NsJailNamespace, NamespaceDetail: detail) : null;
    }

    private static SandboxDiagnostic? ExplainParentFailure(string[] lines)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.Contains("Couldn't initialize ", StringComparison.Ordinal) ||
                ReadLog(line, 'E', "initParent", out var log) is not null)
                continue;
            var failure = ParentNamespaceError().Match(log.Groups["body"].Value);
            if (!failure.Success || !IsPositiveInt(failure.Groups["child"].ValueSpan)) continue;
            var kind = failure.Groups["component"].Value;
            if (kind == "user" &&
                ExplainUserMappingFailure(lines.AsSpan(0, index), log.Groups["pid"].Value, failure.Groups["child"].Value)
                    is { } mapping)
                return mapping;

            var detail = kind switch
            {
                "user" => NsJailNamespaceDetail.ParentUserNamespace,
                "net" => NsJailNamespaceDetail.ParentNetNamespace,
                _ => NsJailNamespaceDetail.ParentCgroupNamespace
            };
            return new(SandboxStage.NsJailNamespace, NamespaceDetail: detail);
        }
        return null;
    }

    private static SandboxDiagnostic? ExplainUserMappingFailure(ReadOnlySpan<string> preceding, string loggerPid, string childPid)
    {
        // Only trust the lower-level PLOG_E from this parent for this child's proc mapping.
        for (var index = preceding.Length - 1; index >= 0; index--)
        {
            var line = preceding[index];
            if (!line.Contains("writeBufToFile()", StringComparison.Ordinal) ||
                ReadLog(line, 'E', "writeBufToFile", out var log) is not null ||
                log.Groups["pid"].Value != loggerPid)
                continue;
            var failure = UserMappingError().Match(log.Groups["body"].Value);
            if (!failure.Success || failure.Groups["child"].Value != childPid) continue;

            var detail = (failure.Groups["open"].Success, failure.Groups["map"].Value) switch
            {
                (true, "uid_map") => NsJailNamespaceDetail.OpenUidMap,
                (false, "uid_map") => NsJailNamespaceDetail.WriteUidMap,
                (true, "gid_map") => NsJailNamespaceDetail.OpenGidMap,
                (false, "gid_map") => NsJailNamespaceDetail.WriteGidMap,
                (true, _) => NsJailNamespaceDetail.OpenSetgroups,
                _ => NsJailNamespaceDetail.WriteSetgroups
            };
            var errno = ParseErrno(failure.Groups["error"].Value);
            return new(SandboxStage.NsJailNamespace,
                errno == NsJailCloneErrno.Unknown ? SandboxFault.Rejected : SandboxFault.Native,
                (int)errno, NamespaceDetail: detail);
        }
        return null;
    }

    private static NsJailNamespaceDetail? ReadLog(string raw, char level, string callSite, out Match log)
    {
        var line = raw.Trim();
        var color = level == 'W' ? "\u001b[0;33m" : "\u001b[1;31m";
        const string reset = "\u001b[0m";
        if (line.StartsWith(color, StringComparison.Ordinal)) line = line[color.Length..].TrimStart();
        if (line.EndsWith(reset, StringComparison.Ordinal)) line = line[..^reset.Length].TrimEnd();
        log = NsJailLogLine().Match(line);
        if (!log.Success || log.Groups["level"].ValueSpan[0] != level) return NsJailNamespaceDetail.CloneEnvelope;

        var timestamp = log.Groups["timestamp"].Value;
        if (timestamp != "[Time conv error]" && !NsJailTimestamp().IsMatch(timestamp))
            return NsJailNamespaceDetail.CloneTimestamp;
        if (!IsPositiveInt(log.Groups["pid"].ValueSpan)) return NsJailNamespaceDetail.ClonePid;
        if (log.Groups["function"].Value != callSite || !IsPositiveInt(log.Groups["line"].ValueSpan))
            return NsJailNamespaceDetail.CloneCallSite;
        return null;
    }

    private static bool IsPositiveInt(ReadOnlySpan<char> text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;

    private static NsJailNamespaceFlags? ParseNamespaceFlags(ReadOnlySpan<char> names)
    {
        names = names.Trim();
        var flags = NsJailNamespaceFlags.None;
        if (names.IsEmpty) return flags;
        // The pinned formatter prefixes an unknown-only mask with "|".
        if (names[0] == '|')
        {
            names = names[1..].TrimStart();
            if (!names.StartsWith("0x", StringComparison.Ordinal)) return null;
        }

        foreach (var range in names.Split('|'))
        {
            var part = names[range].Trim();
            if (part.StartsWith("0x", StringComparison.Ordinal))
            {
                if (range.End.GetOffset(names.Length) != names.Length || part.Length is < 3 or > 18 ||
                    !ulong.TryParse(part[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits) ||
                    ((ulong)flags & bits) != 0)
                    return null;
                return flags | (NsJailNamespaceFlags)bits;
            }

            if (!Enum.TryParse<NsJailNamespaceFlags>(part, out var flag) ||
                flag == NsJailNamespaceFlags.None || !Enum.IsDefined(flag) ||
                !part.SequenceEqual(flag.ToString()) || (flags & flag) != 0)
                return null;
            flags |= flag;
        }
        return flags;
    }

    // The launcher clears the environment. PLOG_W/PLOG_E append C-locale strerror text.
    private static NsJailCloneErrno ParseErrno(string error) => error switch
    {
        "Operation not permitted" => NsJailCloneErrno.EPERM,
        "No such file or directory" => NsJailCloneErrno.ENOENT,
        "No such process" => NsJailCloneErrno.ESRCH,
        "Argument list too long" => NsJailCloneErrno.E2BIG,
        "Resource temporarily unavailable" => NsJailCloneErrno.EAGAIN,
        "Cannot allocate memory" => NsJailCloneErrno.ENOMEM,
        "Permission denied" => NsJailCloneErrno.EACCES,
        "Device or resource busy" => NsJailCloneErrno.EBUSY,
        "File exists" => NsJailCloneErrno.EEXIST,
        "Invalid argument" => NsJailCloneErrno.EINVAL,
        "No space left on device" => NsJailCloneErrno.ENOSPC,
        "Read-only file system" => NsJailCloneErrno.EROFS,
        "Function not implemented" => NsJailCloneErrno.ENOSYS,
        "Too many users" => NsJailCloneErrno.EUSERS,
        "Operation not supported" => NsJailCloneErrno.EOPNOTSUPP,
        _ => NsJailCloneErrno.Unknown
    };

    // NsJail 3.6 emits [level][time][pid] function():line message, with optional SGR decoration.
    [GeneratedRegex(@"^\[(?<level>[WE])\][ \t]*\[(?<timestamp>\[Time conv error\]|[^\[\]\r\n]{1,48})\][ \t]*" +
        @"\[(?<pid>[^\[\]\r\n]{1,12})\][ \t]+(?<function>[A-Za-z_][A-Za-z_0-9]{0,63})\(\)[ \t]*:[ \t]*" +
        @"(?<line>[0-9]{1,6})[ \t]+(?<body>[^\r\n]{1,1024})$", RegexOptions.CultureInvariant)]
    private static partial Regex NsJailLogLine();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:[+-][0-9]{4})?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NsJailTimestamp();

    [GeneratedRegex(@"\bclone[ \t]*\([ \t]*flags\b", RegexOptions.CultureInvariant)]
    private static partial Regex CloneCandidate();

    [GeneratedRegex(@"^clone[ \t]*\([ \t]*flags[ \t]*=[ \t]*(?<flags>[^)\r\n]{0,192})\)[ \t]+failed[ \t]*:[ \t]*" +
        @"(?<error>[^\r\n]{1,512})$", RegexOptions.CultureInvariant)]
    private static partial Regex CloneWarning();

    [GeneratedRegex(@"^Couldn't initialize (?<component>user|net|cgroup(?: 2)? user) namespace for pid=(?<child>[0-9]{1,10})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParentNamespaceError();

    [GeneratedRegex(@"^Couldn't (?:(?<open>open) '/proc/(?<child>[0-9]{1,10})/(?<map>uid_map|gid_map|setgroups)' for writing|" +
        @"write '[0-9]{1,20}' bytes to file '/proc/(?<child>[0-9]{1,10})/(?<map>uid_map|gid_map|setgroups)' " +
        @"\(fd='[0-9]{1,10}'\)): (?<error>[^\r\n]{1,512})$", RegexOptions.CultureInvariant)]
    private static partial Regex UserMappingError();
}
