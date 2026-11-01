using CodeRank.Api.Authentication;
using CodeRank.Api.Infrastructure;
using CodeRank.Api.Data.Seeding;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Identity;
using CodeRank.Api.Data;
using Microsoft.EntityFrameworkCore;

SeedCommandOptions seedOptions;
try
{
    seedOptions = SeedCommandOptions.Parse(args);
}
catch (SeedException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}

var builder = WebApplication.CreateBuilder(seedOptions.HostArguments);
if (seedOptions.Requested && builder.Environment.IsProduction())
{
    Console.Error.WriteLine("Database seeding is forbidden in Production, including dry-run.");
    Environment.ExitCode = 1;
    return;
}

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Configure ConnectionStrings:DefaultConnection through User Secrets or ConnectionStrings__DefaultConnection.");
}

builder.Services.AddDbContext<CodeRankDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
        sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo")));

builder.Services.AddScoped<ISeedPasswordSource, LocalSeedPasswordSource>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<DatabaseSeeder>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCodeRankAuthentication(builder.Configuration);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddControllers();
const string frontendDevelopmentCors = "FrontendDevelopment";
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddPolicy(frontendDevelopmentCors, policy =>
        policy.WithOrigins("http://localhost:5173")
            .WithMethods("GET", "POST")
            .WithHeaders("Content-Type", "Authorization")));
}
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (seedOptions.Requested)
{
    Environment.ExitCode = await SeedCommand.RunAsync(app.Services, seedOptions);
    return;
}

if (app.Configuration.GetValue<bool>("Database:CheckConnection"))
{
    Environment.ExitCode = await DatabaseConnectionCheck.RunAsync(app.Services);
    return;
}

// Resolve JWT only after independent CLI modes have returned, before any HTTP startup.
try
{
    _ = app.Services.GetRequiredService<JwtSettings>();
}
catch (JwtConfigurationException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}

// Fail before starting HTTP when the configured database is unavailable.
var databaseCheckExitCode = await DatabaseConnectionCheck.RunAsync(app.Services);
if (databaseCheckExitCode != 0)
{
    Environment.ExitCode = databaseCheckExitCode;
    return;
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRouting();
if (app.Environment.IsDevelopment())
{
    app.UseCors(frontendDevelopmentCors);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Expose the real entry point to WebApplicationFactory integration tests.
public partial class Program { }
