using System.Data;
using System.Linq.Expressions;
using System.Text.Json;
using CodeRank.Api.Contracts.Judge;
using CodeRank.Api.Data;
using CodeRank.Api.Models;
using CodeRank.Judge.Protocol;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeRank.Api.Judge;

public sealed class JudgeQueueBusyException : Exception { }

public enum JudgeCompletionOutcome { Saved, LeaseLost, VerdictConflict }

internal enum JudgePayloadOutcome { Ready, LeaseLost, TestsUnavailable, InvalidPayload }
internal enum JudgePayloadFailureOutcome { Saved, LeaseLost, NotConfirmed }

public sealed class JudgeQueue(CodeRankDbContext db, TimeProvider clock, JudgeTestStore tests)
{
    private sealed record StoredPayload(long Id, long ProblemId, string Language, string SourceCode,
        DateTime? LeaseExpiresAtUtc, string? TestsJson);

    private const int MaxAttempts = 3;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(JudgePayloadContract.LeaseSeconds);
    private static readonly Expression<Func<Submission, JudgeJobLease>> ToLease = item =>
        new(item.Id, item.ProblemId, item.Language, item.JudgeLeaseToken!.Value,
            DateTime.SpecifyKind(item.JudgeLeaseExpiresAtUtc!.Value, DateTimeKind.Utc));

    public Task<JudgeJobLease?> ClaimAsync(string workerId, CancellationToken token) =>
        InTransactionAsync<JudgeJobLease?>(async now =>
        {
            await RecoverExpiredAsync(now, token);

            // A retry after a lost response or worker restart gets the same live lease.
            var existing = await db.Submissions.Where(item => item.Status == Submission.Running && item.JudgeWorkerId == workerId)
                .Select(ToLease).SingleOrDefaultAsync(token);
            if (existing is not null) return existing;

            var next = await db.Submissions.Where(item => item.Status == Submission.Queued).OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.ProblemId, item.Language }).FirstOrDefaultAsync(token);
            if (next is null) return null;

            var leaseToken = Guid.NewGuid();
            var expires = now.Add(LeaseDuration);
            var updated = await db.Submissions.Where(item => item.Id == next.Id && item.Status == Submission.Queued)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, Submission.Running)
                    .SetProperty(item => item.JudgeWorkerId, workerId)
                    .SetProperty(item => item.JudgeLeaseToken, leaseToken)
                    .SetProperty(item => item.JudgeLeaseExpiresAtUtc, expires)
                    .SetProperty(item => item.JudgeAttemptCount, item => item.JudgeAttemptCount + 1)
                    .SetProperty(item => item.JudgeStartedAtUtc, now)
                    .SetProperty(item => item.JudgeFinishedAtUtc, (DateTime?)null), token);
            if (updated != 1) throw new JudgeQueueBusyException();
            await tests.PinAsync(next.Id, next.ProblemId, token);
            return new JudgeJobLease(next.Id, next.ProblemId, next.Language, leaseToken, expires);
        }, token);

    internal Task<(JudgePayloadOutcome Outcome, JudgePayload? Payload)> ReadPayloadAsync(
        long id, string workerId, Guid leaseToken, CancellationToken token) =>
        InTransactionAsync<(JudgePayloadOutcome, JudgePayload?)>(async now =>
        {
            // A blocked read can outlast the lease, even while the queue lock prevents reassignment.
            var data = await LoadPayloadAsync(ActiveLease(LeasedBy(id, workerId, leaseToken), now), token);
            return data is null || data.LeaseExpiresAtUtc <= await ReadUtcAsync(token)
                ? (JudgePayloadOutcome.LeaseLost, null)
                : ValidatePayload(data);
        }, token);

    internal Task<JudgePayloadFailureOutcome> FailPayloadAsync(
        long id, string workerId, Guid leaseToken, string reason, CancellationToken token) =>
        InTransactionAsync(async now =>
        {
            var owned = LeasedBy(id, workerId, leaseToken);
            var data = await LoadPayloadAsync(owned.Where(item =>
                (item.Status == Submission.Running && item.JudgeLeaseExpiresAtUtc > now) ||
                (item.Status == Submission.SystemError && item.JudgeFinishedAtUtc != null &&
                    item.JudgeLeaseExpiresAtUtc == null)), token);
            if (data is null) return JudgePayloadFailureOutcome.LeaseLost;

            var (payloadState, _) = ValidatePayload(data);
            var replay = data.LeaseExpiresAtUtc is null;
            var checkedAt = replay ? now : await ReadUtcAsync(token);
            if (data.LeaseExpiresAtUtc <= checkedAt) return JudgePayloadFailureOutcome.LeaseLost;
            if ((reason, payloadState) is not (
                (JudgePayloadContract.TestsUnavailableCode, JudgePayloadOutcome.TestsUnavailable) or
                (JudgePayloadContract.InvalidPayloadCode, JudgePayloadOutcome.InvalidPayload)))
                return JudgePayloadFailureOutcome.NotConfirmed;

            // The existing owner and token identify a completed request without renewing the lease.
            return replay || await FinishActiveLeaseAsync(owned, Submission.SystemError, checkedAt, token)
                ? JudgePayloadFailureOutcome.Saved
                : JudgePayloadFailureOutcome.LeaseLost;
        }, token, IsolationLevel.Serializable);

    public Task<JudgeHeartbeat?> HeartbeatAsync(long id, string workerId, Guid leaseToken, CancellationToken token) =>
        InTransactionAsync<JudgeHeartbeat?>(async now =>
        {
            var active = ActiveLease(LeasedBy(id, workerId, leaseToken), now);
            var currentExpiry = await active.Select(item => item.JudgeLeaseExpiresAtUtc).SingleOrDefaultAsync(token);
            if (currentExpiry is not DateTime current) return null;

            var proposed = now.Add(LeaseDuration);
            var expires = current > proposed ? DateTime.SpecifyKind(current, DateTimeKind.Utc) : proposed;
            var updated = await active.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.JudgeLeaseExpiresAtUtc, expires), token);
            return updated == 1 ? new JudgeHeartbeat(expires) : null;
        }, token);

    public Task<JudgeCompletionOutcome> CompleteAsync(long id, string workerId, Guid leaseToken, string verdict,
        CancellationToken token) => InTransactionAsync(async now =>
        {
            var owned = LeasedBy(id, workerId, leaseToken);
            var current = await owned.Select(item => new { item.Status, item.JudgeFinishedAtUtc }).SingleOrDefaultAsync(token);
            if (current is null) return JudgeCompletionOutcome.LeaseLost;
            if (current.JudgeFinishedAtUtc is not null)
                return current.Status == verdict ? JudgeCompletionOutcome.Saved : JudgeCompletionOutcome.VerdictConflict;

            // Retain the successful owner and token for a replay of the same verdict.
            return await FinishActiveLeaseAsync(owned, verdict, now, token)
                ? JudgeCompletionOutcome.Saved : JudgeCompletionOutcome.LeaseLost;
        }, token);

    private Task<StoredPayload?> LoadPayloadAsync(IQueryable<Submission> submissions, CancellationToken token) =>
        (from item in submissions
         join snapshot in db.JudgeTestSnapshots on item.Id equals snapshot.SubmissionId into snapshots
         from snapshot in snapshots.DefaultIfEmpty()
         select new StoredPayload(item.Id, item.ProblemId, item.Language, item.SourceCode, item.JudgeLeaseExpiresAtUtc,
             snapshot == null ? null : snapshot.TestCasesJson)).SingleOrDefaultAsync(token);

    private static (JudgePayloadOutcome Outcome, JudgePayload? Payload) ValidatePayload(StoredPayload data)
    {
        if (data.TestsJson is null) return (JudgePayloadOutcome.TestsUnavailable, null);
        if (data.TestsJson.Length > JudgePayloadContract.MaxSnapshotCharacters)
            return (JudgePayloadOutcome.InvalidPayload, null);

        List<JudgePayloadTest>? testCases;
        try { testCases = JsonSerializer.Deserialize<List<JudgePayloadTest>>(data.TestsJson, JudgePayloadContract.JsonOptions); }
        catch (JsonException) { return (JudgePayloadOutcome.InvalidPayload, null); }
        if (testCases is null) return (JudgePayloadOutcome.InvalidPayload, null);

        var payload = new JudgePayload
        {
            Id = data.Id,
            ProblemId = data.ProblemId,
            Language = data.Language,
            SourceCode = data.SourceCode,
            TestCases = testCases
        };
        return JudgePayloadContract.IsValid(payload)
            ? (JudgePayloadOutcome.Ready, payload)
            : (JudgePayloadOutcome.InvalidPayload, null);
    }

    private Task<DateTime> ReadUtcAsync(CancellationToken token) => db.Database.IsSqlServer()
        ? db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(token)
        : Task.FromResult(clock.GetUtcNow().UtcDateTime);

    private IQueryable<Submission> LeasedBy(long id, string workerId, Guid leaseToken) =>
        db.Submissions.Where(item => item.Id == id && item.JudgeWorkerId == workerId && item.JudgeLeaseToken == leaseToken);

    private static IQueryable<Submission> ActiveLease(IQueryable<Submission> items, DateTime now) =>
        items.Where(item => item.Status == Submission.Running && item.JudgeLeaseExpiresAtUtc > now);

    private static async Task<bool> FinishActiveLeaseAsync(
        IQueryable<Submission> owned, string verdict, DateTime now, CancellationToken token) =>
        await ActiveLease(owned, now).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, verdict)
            .SetProperty(item => item.JudgeFinishedAtUtc, now)
            .SetProperty(item => item.JudgeLeaseExpiresAtUtc, (DateTime?)null), token) == 1;

    private Task<int> RecoverExpiredAsync(DateTime now, CancellationToken token) =>
        db.Submissions.Where(item => item.Status == Submission.Running &&
            (item.JudgeLeaseExpiresAtUtc == null || item.JudgeLeaseExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, item => item.JudgeAttemptCount >= MaxAttempts ? Submission.SystemError : Submission.Queued)
                .SetProperty(item => item.JudgeFinishedAtUtc, item => item.JudgeAttemptCount >= MaxAttempts ? (DateTime?)now : null)
                .SetProperty(item => item.JudgeWorkerId, (string?)null)
                .SetProperty(item => item.JudgeLeaseToken, (Guid?)null)
                .SetProperty(item => item.JudgeLeaseExpiresAtUtc, (DateTime?)null), token);

    private async Task<T> InTransactionAsync<T>(
        Func<DateTime, Task<T>> action, CancellationToken token, IsolationLevel isolation = IsolationLevel.Unspecified)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(isolation, token);
            // SQLite uses its write transaction in the isolated integration tests.
            var now = db.Database.IsSqlServer()
                ? await LockSqlServerQueueAsync(transaction, token)
                : clock.GetUtcNow().UtcDateTime;
            var result = await action(now);
            await transaction.CommitAsync(token);
            return result;
        }
        catch (SqlException error) when (error.Number is -2 or 1205 or 1222)
        {
            throw new JudgeQueueBusyException();
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: -2 or 1205 or 1222 })
        {
            throw new JudgeQueueBusyException();
        }
    }

    private async Task<DateTime> LockSqlServerQueueAsync(IDbContextTransaction transaction, CancellationToken token)
    {
        // All queue writers use the same database-scoped lock, including across API replicas.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandTimeout = 10;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'CodeRank.JudgeQueue.v1',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000, @DbPrincipal = N'public';
            SELECT CASE WHEN @result >= 0 THEN SYSUTCDATETIME() END;
            """;
        if (await command.ExecuteScalarAsync(token) is not DateTime now) throw new JudgeQueueBusyException();
        return DateTime.SpecifyKind(now, DateTimeKind.Utc);
    }
}
