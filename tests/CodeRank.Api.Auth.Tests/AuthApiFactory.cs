using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeRank.Api.Authentication;
using CodeRank.Api.Data;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeRank.Api.Auth.Tests;

internal sealed class TestKey : IJwtSigningKeySource
{
    public byte[] Bytes { get; } = RandomNumberGenerator.GetBytes(64);
    public string? Read() => Convert.ToBase64String(Bytes);
}
internal sealed class SqliteConflicts : IRegistrationConflictDetector
{
    public bool IsUniqueViolation(DbUpdateException error) =>
        error.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };
}
internal sealed class SqliteProblemDeletionConflicts : IProblemDeletionConflictDetector
{
    public bool IsConstraintViolation(DbUpdateException error) =>
        error.InnerException is SqliteException { SqliteExtendedErrorCode: 787 };
}
internal sealed class AuthTestDbContext(DbContextOptions<CodeRankDbContext> options) : CodeRankDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
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
                var name = check.Name!;
                var sql = check.Sql.Replace("N'", "'", StringComparison.Ordinal);
                entity.RemoveCheckConstraint(name);
                entity.AddCheckConstraint(name, sql);
            }
        }
    }
}
internal sealed class FailOnProfileInsert : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO \"UserProfiles\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Private database failure details must never reach the response.");
        return ValueTask.FromResult(result);
    }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO \"UserProfiles\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Private database failure details must never reach the response.");
        return ValueTask.FromResult(result);
    }
}
internal sealed class InsertRace(Func<CodeRankDbContext> otherContext) : SaveChangesInterceptor
{
    private bool inserted;
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (inserted) return result;
        var pending = eventData.Context!.ChangeTracker.Entries<CodeRank.Api.Models.User>()
            .SingleOrDefault(e => e.State == EntityState.Added)?.Entity;
        if (pending is null) return result;
        inserted = true;
        await using var other = otherContext();
        var winner = new CodeRank.Api.Models.User {
            Email = pending.Email, NormalizedEmail = pending.NormalizedEmail, PasswordHash = pending.PasswordHash,
            UserProfile = new() { FirstName = "Race", LastName = "Winner" }
        };
        other.Users.Add(winner);
        await other.SaveChangesAsync(cancellationToken);
        return result;
    }
}
internal sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection;
    private readonly string? sharedDatabase;
    private readonly string environmentName;
    public TestKey Key { get; } = new();
    public IInterceptor? Interceptor { get; set; }
    public TimeProvider? Clock { get; init; }
    public const string Issuer = "CodeRank.Tests";
    public const string Audience = "CodeRank.Tests.Client";
    public AuthApiFactory(string environmentName = "Testing", string? sharedDatabase = null)
    {
        this.environmentName = environmentName;
        this.sharedDatabase = sharedDatabase;
        connection = OpenableConnection(sharedDatabase ?? "Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = Context();
        context.Database.EnsureCreated(); // Exclusively this factory's isolated SQLite in-memory database.
    }
    public AuthTestDbContext Context(bool intercept = false)
    {
        var dbConnection = sharedDatabase is null ? connection : OpenableConnection(sharedDatabase);
        var options = new DbContextOptionsBuilder<CodeRankDbContext>()
            .UseSqlite(dbConnection, contextOwnsConnection: sharedDatabase is not null);
        if (intercept && Interceptor is not null) options.AddInterceptors(Interceptor);
        return new(options.Options);
    }

    private static SqliteConnection OpenableConnection(string connectionString)
    {
        var db = new SqliteConnection(connectionString);
        db.CreateCollation("Latin1_General_100_CI_AS", (a,b) => StringComparer.OrdinalIgnoreCase.Compare(a,b));
        db.CreateFunction<string, int>("DATALENGTH", Encoding.Unicode.GetByteCount, isDeterministic: true);
        return db;
    }
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration is available to the real entry point before Program's early checks.
        // App-configuration callbacks alone can run too late for an early connection-string check.
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string,string?> {
            [HostDefaults.EnvironmentKey] = environmentName,
            ["ConnectionStrings:DefaultConnection"] = "Server=unused.invalid;Database=NeverConnect;Integrated Security=True"
        }));
        return base.CreateHost(builder);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
        builder.ConfigureAppConfiguration((_,config) => config.AddInMemoryCollection(new Dictionary<string,string?> {
            ["ConnectionStrings:DefaultConnection"] = "Server=unused.invalid;Database=NeverConnect;Integrated Security=True",
            ["Jwt:Issuer"] = Issuer, ["Jwt:Audience"] = Audience, ["Jwt:AccessTokenMinutes"] = "15",
            ["Judge:Enabled"] = "false"
        }));
        builder.ConfigureTestServices(services => {
            services.Configure<KeyManagementOptions>(options => options.XmlRepository = new TestKeyRepository());
            services.RemoveAll<CodeRankDbContext>();
            services.RemoveAll<DbContextOptions<CodeRankDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CodeRankDbContext>>();
            services.AddScoped<CodeRankDbContext>(_ => Context(intercept: true));
            services.RemoveAll<IJwtSigningKeySource>();services.AddSingleton<IJwtSigningKeySource>(Key);
            services.RemoveAll<IRegistrationConflictDetector>();services.AddSingleton<IRegistrationConflictDetector,SqliteConflicts>();
            services.RemoveAll<IProblemDeletionConflictDetector>();
            services.AddSingleton<IProblemDeletionConflictDetector, SqliteProblemDeletionConflicts>();
            if (Clock is not null) services.AddSingleton<TimeProvider>(Clock);
        });
    }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }

    private sealed class TestKeyRepository : IXmlRepository
    {
        private readonly ConcurrentBag<XElement> elements = new();

        public IReadOnlyCollection<XElement> GetAllElements() =>
            elements.Select(element => new XElement(element)).ToArray();

        public void StoreElement(XElement element, string friendlyName) =>
            elements.Add(new XElement(element));
    }
}
