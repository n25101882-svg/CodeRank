using System.Text;
using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal sealed class DockerWorkspace : IDisposable
{
    private readonly FileStream session;
    private readonly string marker;
    public string DirectoryPath { get; }

    private DockerWorkspace(string directory, FileStream session)
    {
        DirectoryPath = directory;
        this.session = session;
        marker = Path.Combine(directory, "pending-container");
    }

    public static DockerWorkspace Open(string localAppData, string workerId)
    {
        if (!Path.IsPathFullyQualified(localAppData) || !WorkerCredentials.IsValidId(workerId))
            throw new InvalidOperationException("Invalid Docker Judge workspace.");
        var directory = Path.Combine(localAppData, "CodeRank", "JudgeDocker", "worker-" + workerId);
        Directory.CreateDirectory(directory);
        var session = File.Open(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var workspace = new DockerWorkspace(directory, session);
        try
        {
            if (File.Exists(workspace.marker))
                throw new InvalidOperationException("A Docker Judge container requires recovery.");
            // Use a separate CLI config so that Docker cannot inject the user's configured proxies into containers.
            File.WriteAllText(Path.Combine(directory, "config.json"), "{}");
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public void Record(string execution)
    {
        using var record = File.Open(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        record.Write(Encoding.ASCII.GetBytes(execution));
        record.Flush(flushToDisk: true);
    }

    public void ConfirmRemoved() => File.Delete(marker);
    public void Dispose() => session.Dispose();
}
