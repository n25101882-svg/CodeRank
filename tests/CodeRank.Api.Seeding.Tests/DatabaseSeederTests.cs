using CodeRank.Api.Data.Seeding;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodeRank.Api.Seeding.Tests;

public sealed class DatabaseSeederTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task CreatesCompleteGraphAndIdentityCompatibleHashes()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        var passwords = new TestPasswords(); var log = new TestLogger();
        await using var db = fixture.Context();
        var result = await SeedTestDatabase.Seeder(db, passwords, log).RunAsync(false, "main", cancellationToken: Token);
        Assert.False(result.DryRun); Assert.Equal(32, result.Created.Values.Sum());
        Assert.Equal(new[] {2,2,2,5,6,15}, await fixture.CountsAsync(Token));
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(Token);
        var hasher = new PasswordHasher<User>();
        foreach (var user in users)
        {
            var password = user.Role == "Admin" ? passwords.Admin : passwords.Student;
            Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, user.PasswordHash, password));
            Assert.NotEqual(password, user.PasswordHash);
            Assert.Equal(user.Email.ToUpperInvariant(), user.NormalizedEmail);
            Assert.DoesNotContain(log.Lines, line => line.Contains(password, StringComparison.Ordinal));
        }
        Assert.All(await db.Olympiads.AsNoTracking().ToListAsync(Token), o => Assert.True(o.EndsAtUtc > o.StartsAtUtc && o.IsPublished));
        Assert.All(await db.Problems.AsNoTracking().ToListAsync(Token), problem => Assert.False(string.IsNullOrWhiteSpace(problem.Statement)));
        Assert.Contains(await db.ProblemExamples.AsNoTracking().ToListAsync(Token), e => e.InputData.Contains('\n'));
    }

    [Fact]
    public async Task RepeatedRunCreatesNothingAndPreservesPasswords()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using var db = fixture.Context();
        await SeedTestDatabase.Seeder(db, new TestPasswords()).RunAsync(false, "main", cancellationToken: Token);
        var hashes = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.PasswordHash).ToArrayAsync(Token);
        var result = await SeedTestDatabase.Seeder(db, new TestPasswords(forbidden:true)).RunAsync(false, "main", cancellationToken: Token);
        Assert.Equal(0, result.Created.Values.Sum()); Assert.Equal(32, result.Reused.Values.Sum());
        Assert.Equal(hashes, await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.PasswordHash).ToArrayAsync(Token));
        Assert.Equal(new[] {2,2,2,5,6,15}, await fixture.CountsAsync(Token));
    }

    [Fact]
    public async Task PreviewReadsNoPasswordsTracksNothingAndWritesNothing()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using var db = fixture.Context();
        var passwords = new TestPasswords(forbidden:true);
        var result = await SeedTestDatabase.Seeder(db, passwords).RunAsync(cancellationToken: Token);
        Assert.True(result.DryRun); Assert.Equal(32, result.Created.Values.Sum());
        Assert.Equal(0, passwords.Reads); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(new int[6], await fixture.CountsAsync(Token));
    }

    [Fact]
    public async Task MissingPasswordsAbortWithoutPartialRows()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using var db = fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db, new TestPasswords {Missing=true}).RunAsync(false,"main", cancellationToken: Token));
        Assert.Equal(new int[6], await fixture.CountsAsync(Token));
    }

    [Theory]
    [InlineData("User", "ADMIN@CODERANK.TEST", true)]
    [InlineData("Admin", "admin@coderank.test", true)]
    [InlineData("Admin", "ADMIN@CODERANK.TEST", false)]
    public async Task RoleConflictDoesNotElevateOrOverwriteExistingAccount(string role, string normalized, bool active)
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        var passwords = new TestPasswords();
        await using (var setup = fixture.Context())
        {
            var user = new User {Email="admin@coderank.test",NormalizedEmail=normalized,Role=role,IsActive=active};
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user,passwords.Admin);
            setup.Users.Add(user); await setup.SaveChangesAsync(Token);
        }
        await using var db = fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,passwords).RunAsync(false,"main", cancellationToken: Token));
        var kept = await db.Users.AsNoTracking().SingleAsync(Token);
        Assert.Equal(role, kept.Role); Assert.Equal(normalized, kept.NormalizedEmail); Assert.Equal(active, kept.IsActive);
        Assert.Equal(new[] {1,0,0,0,0,0}, await fixture.CountsAsync(Token));
    }

    [Fact]
    public async Task ChangedContentCausesConflictRatherThanOverwrite()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using (var setup = fixture.Context())
        {
            await SeedTestDatabase.Seeder(setup,new TestPasswords()).RunAsync(false,"main", cancellationToken: Token);
            var problem=await setup.Problems.FirstAsync(Token); problem.Statement="User-edited statement.";
            await setup.SaveChangesAsync(Token);
        }
        await using var db=fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true)).RunAsync(false,"main", cancellationToken: Token));
        Assert.Contains(await db.Problems.AsNoTracking().ToListAsync(Token), p => p.Statement=="User-edited statement.");
        Assert.Equal(new[] {2,2,2,5,6,15}, await fixture.CountsAsync(Token));
    }

    [Fact]
    public async Task AmbiguousNaturalKeyIsRejected()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using (var setup = fixture.Context())
        {
            await SeedTestDatabase.Seeder(setup,new TestPasswords()).RunAsync(false,"main", cancellationToken: Token);
            var original=await setup.Problems.AsNoTracking().FirstAsync(Token);
            setup.Problems.Add(new Problem {Title=original.Title, Statement=original.Statement, CreatedByUserId=original.CreatedByUserId});
            await setup.SaveChangesAsync(Token);
        }
        await using var db=fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true)).RunAsync(false,"main", cancellationToken: Token));
        Assert.Equal(6, await db.Problems.CountAsync(Token));
    }

    [Fact]
    public async Task ChangedLinkIsPreservedAndRejected()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using (var setup = fixture.Context())
        {
            await SeedTestDatabase.Seeder(setup,new TestPasswords()).RunAsync(false,"main", cancellationToken: Token);
            (await setup.OlympiadProblems.FirstAsync(Token)).MaxPoints=777; await setup.SaveChangesAsync(Token);
        }
        await using var db=fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true)).RunAsync(false,"main", cancellationToken: Token));
        Assert.Contains(await db.OlympiadProblems.AsNoTracking().ToListAsync(Token), l => l.MaxPoints==777);
    }

    [Fact]
    public async Task ChangedExampleIsPreservedAndRejected()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using (var setup = fixture.Context())
        {
            await SeedTestDatabase.Seeder(setup,new TestPasswords()).RunAsync(false,"main", cancellationToken: Token);
            (await setup.ProblemExamples.FirstAsync(Token)).ExpectedOutput="Changed by user"; await setup.SaveChangesAsync(Token);
        }
        await using var db=fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true)).RunAsync(false,"main", cancellationToken: Token));
        Assert.Contains(await db.ProblemExamples.AsNoTracking().ToListAsync(Token), e => e.ExpectedOutput=="Changed by user");
    }

    [Fact]
    public async Task MidSaveFailureRollsBackParentsAndChildren()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        var failure = new FailOnExampleInsert();
        await using var db=fixture.Context(failure);
        await Assert.ThrowsAnyAsync<Exception>(() => SeedTestDatabase.Seeder(db,new TestPasswords()).RunAsync(false,"main", cancellationToken: Token));
        Assert.True(failure.Triggered); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(new int[6], await fixture.CountsAsync(Token));
    }

    [Fact]
    public async Task MissingProfileIsAddedWithoutResettingPasswordOrExistingNames()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        string adminHash;
        await using (var setup=fixture.Context())
        {
            await SeedTestDatabase.Seeder(setup,new TestPasswords()).RunAsync(false,"main", cancellationToken: Token);
            var admin=await setup.Users.SingleAsync(u => u.Role=="Admin", Token);adminHash=admin.PasswordHash;
            setup.UserProfiles.Remove(await setup.UserProfiles.SingleAsync(p => p.UserId==admin.Id, Token));
            (await setup.UserProfiles.SingleAsync(p => p.UserId!=admin.Id, Token)).FirstName="Existing name";
            await setup.SaveChangesAsync(Token);
        }
        await using var db=fixture.Context();
        var result=await SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true)).RunAsync(false,"main", cancellationToken: Token);
        Assert.Equal(1,result.Created.Values.Sum()); Assert.Equal(1,result.Created["UserProfiles"]);
        Assert.Equal(adminHash,(await db.Users.AsNoTracking().SingleAsync(u => u.Role=="Admin", Token)).PasswordHash);
        Assert.Contains(await db.UserProfiles.AsNoTracking().ToListAsync(Token), p => p.FirstName=="Existing name");
    }

    [Fact]
    public async Task ProductionIsBlockedBeforeOpeningConnection()
    {
        await using var fixture=new SeedTestDatabase();
        await using var db=fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true),environment:Environments.Production).RunAsync(cancellationToken: Token));
        Assert.Equal(System.Data.ConnectionState.Closed,db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("WrongDatabase")]
    public async Task WriteRequiresCorrectExplicitDatabase(string? name)
    {
        await using var fixture=new SeedTestDatabase();await fixture.InitializeAsync(Token);
        await using var db=fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true)).RunAsync(false,name, cancellationToken: Token));
        Assert.Equal(new int[6],await fixture.CountsAsync(Token));
    }

    [Theory]
    [InlineData("--seed --apply --seed-target=main --dry-run=true")]
    [InlineData("--seed --apply=false")]
    [InlineData("--Seed")]
    [InlineData("--apply")]
    [InlineData("--seed --apply")]
    [InlineData("--seed --apply --dry-run --seed-target=main")]
    [InlineData("--seed-target=main")]
    public void InvalidCommandsDoNotEnableWrites(string command) =>
        Assert.Throws<SeedException>(() => SeedCommandOptions.Parse(command.Split(' ')));


    [Fact]
    public async Task OccupiedPositionIsNotOverwritten()
    {
        await using var fixture = new SeedTestDatabase(); await fixture.InitializeAsync(Token);
        await using (var setup = fixture.Context())
        {
            await SeedTestDatabase.Seeder(setup,new TestPasswords()).RunAsync(false,"main",Token);
            var contest=await setup.Olympiads.SingleAsync(o => o.Title=="CodeRank Beginner Contest",Token);
            var displaced=await setup.OlympiadProblems.SingleAsync(l => l.OlympiadId==contest.Id && l.Position==1,Token);
            setup.OlympiadProblems.Remove(displaced);await setup.SaveChangesAsync(Token);
            var other=await setup.Problems.SingleAsync(p => p.Title=="Числа Фібоначчі",Token);
            setup.OlympiadProblems.Add(new OlympiadProblem {OlympiadId=contest.Id,ProblemId=other.Id,Position=1,MaxPoints=100});
            await setup.SaveChangesAsync(Token);
        }
        await using var db=fixture.Context();
        await Assert.ThrowsAsync<SeedException>(() => SeedTestDatabase.Seeder(db,new TestPasswords(forbidden:true)).RunAsync(false,"main",Token));
        Assert.Equal(new[] {2,2,2,5,6,15},await fixture.CountsAsync(Token));
    }

    [Fact]
    public void PlainSeedCommandDefaultsToDryRun()
    {
        var parsed=SeedCommandOptions.Parse(["--seed"]);
        Assert.True(parsed.Requested);Assert.True(parsed.DryRun);Assert.Empty(parsed.HostArguments);
        var startup=SeedCommandOptions.Parse([]);Assert.False(startup.Requested);
        var apply=SeedCommandOptions.Parse(["--seed","--apply","--seed-target=main"]);
        Assert.False(apply.DryRun);Assert.Equal("main",apply.TargetDatabase);
    }
}
