using System.Data;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeRank.Api.Data.Seeding;

public sealed class DatabaseSeeder(CodeRankDbContext db, IHostEnvironment environment,
    ISeedPasswordSource passwords, IPasswordHasher<User> hasher, ILogger<DatabaseSeeder> logger)
{
    public async Task<SeedSummary> RunAsync(bool dryRun = true, string? expectedDatabase = null,
        CancellationToken cancellationToken = default)
    {
        if (environment.IsProduction())
            throw new SeedException("Database seeding is forbidden in Production, including dry-run.");
        if (!dryRun && string.IsNullOrWhiteSpace(expectedDatabase))
            throw new SeedException("Write mode requires an explicit --seed-target=<database> confirmation.");
        if (db.ChangeTracker.Entries().Any())
            throw new SeedException("Seeder requires a dedicated, empty DbContext scope.");

        await db.Database.OpenConnectionAsync(cancellationToken);
        var database = db.Database.GetDbConnection().Database;
        if (new[] { "master", "model", "msdb", "tempdb" }.Contains(database, StringComparer.OrdinalIgnoreCase))
            throw new SeedException("Seeding a system database is forbidden.");
        if (expectedDatabase is not null && !string.Equals(database, expectedDatabase, StringComparison.Ordinal))
            throw new SeedException("The connected database does not match --seed-target. No writes performed.");

        if (dryRun)
        {
            var preview = await BuildPlanAsync(cancellationToken);
            LogSummary(preview, true);
            logger.LogInformation("Dry-run complete: no entities tracked, no seed passwords requested, no changes saved.");
            return preview.Summary(true);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await AcquireSeederLockAsync(cancellationToken);
            var plan = await BuildPlanAsync(cancellationToken);
            // Validate all required secrets before hashing or saving any row.
            var credentials = plan.UsersToCreate.Select(item =>
            {
                var password = passwords.GetPassword(item.PasswordKey);
                if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
                    throw new SeedException($"Configure {item.PasswordKey} in User Secrets or environment (minimum 12 characters).");
                return (item.User, Password: password!);
            }).ToArray();
            foreach (var item in credentials)
                item.User.PasswordHash = hasher.HashPassword(item.User, item.Password);

            db.AddRange(plan.NewRows);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
            LogSummary(plan, false);
            return plan.Summary(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task AcquireSeederLockAsync(CancellationToken token)
    {
        if (!db.Database.IsSqlServer()) return; // Relational SQLite tests use the transaction only.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = 15;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'CodeRank.DemoSeed.v1',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000;
            SELECT @result;
            """;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        if (result < 0) throw new SeedException("Could not acquire the seeder lock. Retry after the other operation completes.");
    }

    private async Task<Plan> BuildPlanAsync(CancellationToken token)
    {
        var plan = new Plan();
        var admin = await GetUserAsync(plan, DemoSeedData.AdminEmail, "Admin", "Administrator", "Seeding:AdminPassword", token);
        await GetUserAsync(plan, DemoSeedData.StudentEmail, "User", "Student", "Seeding:StudentPassword", token);
        var problems = new List<Problem>();
        foreach (var spec in DemoSeedData.Problems)
        {
            var matches = await db.Problems.AsNoTracking().Where(p => p.Title == spec.Title).ToListAsync(token);
            Require(matches.Count <= 1, $"Ambiguous problem title: {spec.Title}.");
            Problem problem;
            if (matches.Count == 0)
            {
                problem = new Problem { Title = spec.Title, Statement = spec.Statement, InputFormat = spec.InputFormat,
                    OutputFormat = spec.OutputFormat, Difficulty = spec.Difficulty };
                if (plan.IsNew(admin)) problem.CreatedByUser = admin;
                else problem.CreatedByUserId = admin.Id;
                plan.Add(problem);
            }
            else
            {
                problem = matches[0];
                Require(!plan.IsNew(admin) && problem.CreatedByUserId == admin.Id && problem.Title == spec.Title
                    && problem.Statement == spec.Statement && problem.InputFormat == spec.InputFormat
                    && problem.OutputFormat == spec.OutputFormat && problem.Difficulty == spec.Difficulty,
                    $"Problem content/owner conflict: {spec.Title}. Existing data preserved.");
                plan.Reuse(problem);
            }
            problems.Add(problem);
            var examples = plan.IsNew(problem) ? [] : await db.ProblemExamples.AsNoTracking()
                .Where(e => e.ProblemId == problem.Id).ToListAsync(token);
            for (var i = 0; i < spec.Examples.Length; i++)
            {
                var example = spec.Examples[i];
                var position = i + 1;
                var existing = examples.SingleOrDefault(e => e.Position == position);
                if (existing is null)
                {
                    var row = new ProblemExample { Position = position, InputData = example.Input,
                        ExpectedOutput = example.Output, Explanation = example.Explanation };
                    if (plan.IsNew(problem)) row.Problem = problem;
                    else row.ProblemId = problem.Id;
                    plan.Add(row);
                }
                else
                {
                    Require(existing.InputData == example.Input && existing.ExpectedOutput == example.Output
                        && existing.Explanation == example.Explanation,
                        $"Problem example conflict: {spec.Title}, position {position}. Existing data preserved.");
                    plan.Reuse(existing);
                }
            }
        }

        foreach (var spec in DemoSeedData.Contests)
        {
            var matches = await db.Olympiads.AsNoTracking().Where(o => o.Title == spec.Title).ToListAsync(token);
            Require(matches.Count <= 1, $"Ambiguous contest title: {spec.Title}.");
            Olympiad olympiad;
            if (matches.Count == 0)
            {
                olympiad = new Olympiad { Title = spec.Title, Description = spec.Description,
                    StartsAtUtc = spec.Starts, EndsAtUtc = spec.Ends, IsPublished = true };
                if (plan.IsNew(admin)) olympiad.CreatedByUser = admin;
                else olympiad.CreatedByUserId = admin.Id;
                plan.Add(olympiad);
            }
            else
            {
                olympiad = matches[0];
                Require(!plan.IsNew(admin) && olympiad.CreatedByUserId == admin.Id && olympiad.Title == spec.Title
                    && olympiad.Description == spec.Description && olympiad.StartsAtUtc == spec.Starts
                    && olympiad.EndsAtUtc == spec.Ends && olympiad.IsPublished,
                    $"Contest content/owner conflict: {spec.Title}. Existing data preserved.");
                plan.Reuse(olympiad);
            }
            var links = plan.IsNew(olympiad) ? [] : await db.OlympiadProblems.AsNoTracking()
                .Where(l => l.OlympiadId == olympiad.Id).ToListAsync(token);
            for (var i = 0; i < spec.Problems.Length; i++)
            {
                var (problemIndex, points) = spec.Problems[i];
                var problem = problems[problemIndex];
                var position = i + 1;
                var existing = plan.IsNew(problem) ? null : links.SingleOrDefault(l => l.ProblemId == problem.Id);
                if (existing is null)
                {
                    Require(!links.Any(l => l.Position == position),
                        $"Contest position already occupied: {spec.Title}, position {position}. Existing data preserved.");
                    var row = new OlympiadProblem { Position = position, MaxPoints = points, IsVisible = true };
                    if (plan.IsNew(olympiad)) row.Olympiad = olympiad;
                    else row.OlympiadId = olympiad.Id;
                    if (plan.IsNew(problem)) row.Problem = problem;
                    else row.ProblemId = problem.Id;
                    plan.Add(row);
                }
                else
                {
                    Require(existing.Position == position && existing.MaxPoints == points && existing.IsVisible,
                        $"Contest/problem link conflict: {spec.Title}, position {position}. Existing data preserved.");
                    plan.Reuse(existing);
                }
            }
        }
        return plan;
    }

    private async Task<User> GetUserAsync(Plan plan, string email, string role, string lastName,
        string passwordKey, CancellationToken token)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var matches = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized || u.Email.ToUpper() == normalized).ToListAsync(token);
        Require(matches.Count <= 1, $"Ambiguous demo account: {email}.");
        User user;
        if (matches.Count == 0)
        {
            user = new User { Email = email, NormalizedEmail = normalized, Role = role, IsActive = true };
            plan.Add(user);
            plan.UsersToCreate.Add((user, passwordKey));
        }
        else
        {
            user = matches[0];
            Require(string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
                && user.NormalizedEmail == normalized && user.Role == role && user.IsActive
                && !string.IsNullOrWhiteSpace(user.PasswordHash),
                $"Demo account email/role/active-state conflict: {email}. Existing account and password preserved.");
            plan.Reuse(user);
        }
        var profile = plan.IsNew(user) ? null : await db.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == user.Id, token);
        if (profile is null)
        {
            var row = new UserProfile { FirstName = "Demo", LastName = lastName };
            if (plan.IsNew(user)) row.User = user;
            else row.UserId = user.Id;
            plan.Add(row);
        }
        else plan.Reuse(profile); // Existing personal names are never overwritten.
        return user;
    }

    private void LogSummary(Plan plan, bool dryRun)
    {
        foreach (var table in plan.Created.Keys)
            logger.LogInformation("Seed {Mode}: {Table}: {NewCount} new, {ExistingCount} reused.",
                dryRun ? "preview" : "committed", table, plan.Created[table], plan.Reused[table]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new SeedException(message);
    }

    private sealed class Plan
    {
        private static readonly Dictionary<Type, string> TableNames = new()
        {
            [typeof(User)] = "Users", [typeof(UserProfile)] = "UserProfiles", [typeof(Olympiad)] = "Olympiads",
            [typeof(Problem)] = "Problems", [typeof(OlympiadProblem)] = "OlympiadProblems", [typeof(ProblemExample)] = "ProblemExamples"
        };
        private readonly HashSet<object> newRows = new(ReferenceEqualityComparer.Instance);
        public IEnumerable<object> NewRows => newRows;
        public List<(User User, string PasswordKey)> UsersToCreate { get; } = [];
        public Dictionary<string, int> Created { get; } = TableNames.Values.ToDictionary(n => n, _ => 0);
        public Dictionary<string, int> Reused { get; } = TableNames.Values.ToDictionary(n => n, _ => 0);
        public bool IsNew(object row) => newRows.Contains(row);
        public void Add(object row) { newRows.Add(row); Created[TableNames[row.GetType()]]++; }
        public void Reuse(object row) => Reused[TableNames[row.GetType()]]++;
        public SeedSummary Summary(bool dryRun) => new(dryRun,
            new Dictionary<string, int>(Created), new Dictionary<string, int>(Reused));
    }
}
