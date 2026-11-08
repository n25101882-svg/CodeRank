using System.Text.Json;
using CodeRank.Api.Data;
using CodeRank.Judge.Protocol;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Judge;

public sealed class JudgeTestStore(CodeRankDbContext db)
{
    public async Task PinAsync(long submissionId, long problemId, CancellationToken token)
    {
        if (await db.JudgeTestSnapshots.AnyAsync(item => item.SubmissionId == submissionId, token))
            return;

        // The caller holds the queue transaction. Range locks keep a DBA's test edit out
        // of this read until the snapshot commits. SQLite takes a database write lock.
        var active = db.Database.IsSqlServer()
            ? db.JudgeTestCases.FromSql($"""
                SELECT [Id], [ProblemId], [Position], [Input], [ExpectedOutput], [IsActive]
                FROM [dbo].[JudgeTestCases] WITH (HOLDLOCK)
                WHERE [ProblemId] = {problemId} AND [IsActive] = 1
                """)
            : db.JudgeTestCases.Where(item => item.ProblemId == problemId && item.IsActive);

        var tests = await active.OrderBy(item => item.Position).Take(JudgePayloadContract.MaxTests + 1)
            .Select(item => new JudgePayloadTest
            {
                Id = item.Id,
                Position = item.Position,
                Input = item.Input,
                ExpectedOutput = item.ExpectedOutput
            }).ToListAsync(token);
        if (!JudgePayloadContract.AreValidTests(tests)) return;

        db.JudgeTestSnapshots.Add(new()
        {
            SubmissionId = submissionId,
            TestCasesJson = JsonSerializer.Serialize(tests, JudgePayloadContract.JsonOptions)
        });
        await db.SaveChangesAsync(token);
    }
}
