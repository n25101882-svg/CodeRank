using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeRank.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Submissions",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    OlympiadId = table.Column<long>(type: "bigint", nullable: false),
                    ProblemId = table.Column<long>(type: "bigint", nullable: false),
                    Language = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, defaultValue: "Queued")
                        .Annotation("Relational:DefaultConstraintName", "DF_Submissions_Status"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Submissions_SubmittedAtUtc")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submissions", x => x.Id);
                    table.CheckConstraint("CK_Submissions_Language", "[Language] IN ('cpp17', 'python3')");
                    table.CheckConstraint("CK_Submissions_SourceCode", "DATALENGTH([SourceCode]) BETWEEN 2 AND 131072");
                    table.CheckConstraint("CK_Submissions_Status", "[Status] IN ('Queued', 'Running', 'Accepted', 'WrongAnswer', 'CompilationError', 'RuntimeError', 'TimeLimitExceeded', 'MemoryLimitExceeded', 'SystemError')");
                    table.ForeignKey(
                        name: "FK_Submissions_Olympiads",
                        column: x => x.OlympiadId,
                        principalSchema: "dbo",
                        principalTable: "Olympiads",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Submissions_Problems",
                        column: x => x.ProblemId,
                        principalSchema: "dbo",
                        principalTable: "Problems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Submissions_Users",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ProblemId",
                schema: "dbo",
                table: "Submissions",
                column: "ProblemId");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_Status_Id",
                schema: "dbo",
                table: "Submissions",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_User_Time_Id",
                schema: "dbo",
                table: "Submissions",
                columns: new[] { "UserId", "SubmittedAtUtc", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [dbo].[Submissions] WITH (TABLOCKX, HOLDLOCK))
                    THROW 50007, N'Cannot roll back AddSubmissions while attempts exist.', 1;
                """);
            migrationBuilder.DropTable(
                name: "Submissions",
                schema: "dbo");
        }
    }
}
