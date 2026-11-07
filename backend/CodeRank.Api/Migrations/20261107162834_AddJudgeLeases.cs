using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeRank.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddJudgeLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JudgeAttemptCount",
                schema: "dbo",
                table: "Submissions",
                type: "int",
                nullable: false,
                defaultValue: 0)
                .Annotation("Relational:DefaultConstraintName", "DF_Submissions_JudgeAttemptCount");

            migrationBuilder.AddColumn<DateTime>(
                name: "JudgeFinishedAtUtc",
                schema: "dbo",
                table: "Submissions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "JudgeLeaseExpiresAtUtc",
                schema: "dbo",
                table: "Submissions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "JudgeLeaseToken",
                schema: "dbo",
                table: "Submissions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "JudgeStartedAtUtc",
                schema: "dbo",
                table: "Submissions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JudgeWorkerId",
                schema: "dbo",
                table: "Submissions",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_RunningLeaseExpiry",
                schema: "dbo",
                table: "Submissions",
                column: "JudgeLeaseExpiresAtUtc",
                filter: "[Status] = 'Running'");

            migrationBuilder.CreateIndex(
                name: "UX_Submissions_RunningJudgeWorker",
                schema: "dbo",
                table: "Submissions",
                column: "JudgeWorkerId",
                unique: true,
                filter: "[Status] = 'Running' AND [JudgeWorkerId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Submissions_JudgeAttemptCount",
                schema: "dbo",
                table: "Submissions",
                sql: "[JudgeAttemptCount] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Submissions_JudgeLease",
                schema: "dbo",
                table: "Submissions",
                sql: "([JudgeWorkerId] IS NULL AND [JudgeLeaseToken] IS NULL AND [JudgeLeaseExpiresAtUtc] IS NULL)\nOR ([JudgeWorkerId] IS NOT NULL AND [JudgeLeaseToken] IS NOT NULL AND [JudgeAttemptCount] > 0\n    AND [JudgeStartedAtUtc] IS NOT NULL\n    AND (([Status] = 'Running' AND [JudgeLeaseExpiresAtUtc] IS NOT NULL AND [JudgeFinishedAtUtc] IS NULL)\n      OR ([Status] NOT IN ('Queued', 'Running') AND [JudgeLeaseExpiresAtUtc] IS NULL AND [JudgeFinishedAtUtc] IS NOT NULL)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [dbo].[Submissions] WITH (TABLOCKX, HOLDLOCK)
                    WHERE [JudgeAttemptCount] > 0 OR [Status] = 'Running')
                    THROW 50017, N'Cannot roll back AddJudgeLeases while judge history exists.', 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Submissions_RunningLeaseExpiry",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "UX_Submissions_RunningJudgeWorker",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Submissions_JudgeAttemptCount",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Submissions_JudgeLease",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "JudgeAttemptCount",
                schema: "dbo",
                table: "Submissions")
                .Annotation("Relational:DefaultConstraintName", "DF_Submissions_JudgeAttemptCount");

            migrationBuilder.DropColumn(
                name: "JudgeFinishedAtUtc",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "JudgeLeaseExpiresAtUtc",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "JudgeLeaseToken",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "JudgeStartedAtUtc",
                schema: "dbo",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "JudgeWorkerId",
                schema: "dbo",
                table: "Submissions");
        }
    }
}
