using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using CodeRank.Api.Data;
using CodeRank.Api.Data.Seeding;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeRank.Api.Seeding.Tests;

internal sealed class TestEnvironment(string name = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "CodeRank.Api";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TestPasswords(bool forbidden = false) : ISeedPasswordSource
{
    public string Admin { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string Student { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public bool Missing { get; set; }
    public int Reads { get; private set; }
    public string? GetPassword(string key)
    {
        Reads++;
        if (forbidden) throw new InvalidOperationException("Password source must not be read.");
        if (Missing) return null;
        return key == "Seeding:AdminPassword" ? Admin : Student;
    }
}

internal sealed class TestLogger : ILogger<DatabaseSeeder>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
}

internal sealed class FailOnExampleInsert : DbCommandInterceptor
{
    public bool Triggered { get; private set; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO \"ProblemExamples\"", StringComparison.Ordinal))
        {
            Triggered = true;
            throw new InvalidOperationException("Injected failure during SaveChanges.");
        }
        return ValueTask.FromResult(result);
    }
}

internal sealed class SeedTestContext(DbContextOptions<CodeRankDbContext> options) : CodeRankDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Test-only SQL dialect adaptation; production model/schema remain untouched.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                property.SetColumnType(type == typeof(string) || type == typeof(DateTime) || type == typeof(Guid)
                    ? "TEXT" : "INTEGER");
                if (property.GetDefaultValueSql() is not null) property.SetDefaultValueSql("CURRENT_TIMESTAMP");
            }
            foreach (var check in entity.GetCheckConstraints().ToArray())
            {
                var name = check.Name ?? throw new InvalidOperationException("Missing test constraint name.");
                var sql = check.Sql.Replace("N'", "'", StringComparison.Ordinal);
                entity.RemoveCheckConstraint(name);
                entity.AddCheckConstraint(name, sql);
            }
        }
    }
}

internal sealed class SeedTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await connection.OpenAsync(token);
        connection.CreateCollation("Latin1_General_100_CI_AS", (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a,b));
        connection.CreateFunction<string, int>("DATALENGTH", Encoding.Unicode.GetByteCount, isDeterministic: true);
        await using var db = Context();
        await db.Database.EnsureCreatedAsync(token); // Exclusively this private in-memory test DB.
    }
    public SeedTestContext Context(DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<CodeRankDbContext>().UseSqlite(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    public static DatabaseSeeder Seeder(CodeRankDbContext db, TestPasswords passwords, TestLogger? logger = null,
        string environment = "Development") => new(db, new TestEnvironment(environment), passwords,
            new PasswordHasher<User>(), logger ?? new TestLogger());
    public async Task<int[]> CountsAsync(CancellationToken token = default)
    {
        await using var db = Context();
        return [await db.Users.CountAsync(token), await db.UserProfiles.CountAsync(token), await db.Olympiads.CountAsync(token),
            await db.Problems.CountAsync(token), await db.OlympiadProblems.CountAsync(token), await db.ProblemExamples.CountAsync(token)];
    }
    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
