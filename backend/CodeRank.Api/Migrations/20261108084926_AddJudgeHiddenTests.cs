using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeRank.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddJudgeHiddenTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JudgeTestCases",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProblemId = table.Column<long>(type: "bigint", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Input = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpectedOutput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JudgeTestCases", x => x.Id);
                    table.CheckConstraint("CK_JudgeTestCases_InputSize", "DATALENGTH([Input]) <= 16384");
                    table.CheckConstraint("CK_JudgeTestCases_OutputSize", "DATALENGTH([ExpectedOutput]) <= 16384");
                    table.CheckConstraint("CK_JudgeTestCases_Position", "[Position] BETWEEN 1 AND 32");
                    table.ForeignKey(
                        name: "FK_JudgeTestCases_Problems",
                        column: x => x.ProblemId,
                        principalSchema: "dbo",
                        principalTable: "Problems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JudgeTestSnapshots",
                schema: "dbo",
                columns: table => new
                {
                    SubmissionId = table.Column<long>(type: "bigint", nullable: false),
                    TestCasesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JudgeTestSnapshots", x => x.SubmissionId);
                    table.CheckConstraint("CK_JudgeTestSnapshots_JsonSize", "DATALENGTH([TestCasesJson]) BETWEEN 4 AND 802816");
                    table.ForeignKey(
                        name: "FK_JudgeTestSnapshots_Submissions",
                        column: x => x.SubmissionId,
                        principalSchema: "dbo",
                        principalTable: "Submissions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "UX_JudgeTestCases_Problem_Position",
                schema: "dbo",
                table: "JudgeTestCases",
                columns: new[] { "ProblemId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [dbo].[JudgeTestCases] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [dbo].[JudgeTestSnapshots] WITH (TABLOCKX, HOLDLOCK))
                    THROW 50027, N'Archive private judge tests and snapshots before rolling back.', 1;
                """);

            migrationBuilder.DropTable(
                name: "JudgeTestCases",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "JudgeTestSnapshots",
                schema: "dbo");
        }
    }
}
