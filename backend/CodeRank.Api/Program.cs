using CodeRank.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:CheckConnection"))
{
    Environment.ExitCode = await DatabaseConnectionCheck.RunAsync(app.Services);
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
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
