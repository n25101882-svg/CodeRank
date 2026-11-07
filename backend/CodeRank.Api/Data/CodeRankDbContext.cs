using CodeRank.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace CodeRank.Api.Data;

/// <summary>Maps the SQL-first dbo schema and subsequent CodeRank migrations.</summary>
public partial class CodeRankDbContext(DbContextOptions<CodeRankDbContext> options) : DbContext(options)
{
    public virtual DbSet<User> Users { get; set; }
    public virtual DbSet<UserProfile> UserProfiles { get; set; }
    public virtual DbSet<Olympiad> Olympiads { get; set; }
    public virtual DbSet<Problem> Problems { get; set; }
    public virtual DbSet<OlympiadProblem> OlympiadProblems { get; set; }
    public virtual DbSet<ProblemExample> ProblemExamples { get; set; }
    public virtual DbSet<Submission> Submissions { get; set; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // The database already exists: only map indexes explicitly present in its SQL schema.
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users", "dbo", table =>
            {
                table.HasCheckConstraint("CK_Users_Role", "[Role] IN (N'User', N'Admin')");
            });
            entity.HasKey(e => e.Id).HasName("PK_Users");
            entity.HasIndex(e => e.NormalizedEmail, "UX_Users_NormalizedEmail").IsUnique();

            entity.Property(e => e.Id).HasColumnType("bigint").IsRequired().UseIdentityColumn(1, 1);
            entity.Property(e => e.Email).HasColumnType("nvarchar(256)").HasMaxLength(256).IsRequired();
            entity.Property(e => e.NormalizedEmail).HasColumnType("nvarchar(256)").HasMaxLength(256).IsRequired().UseCollation("Latin1_General_100_CI_AS");
            entity.Property(e => e.PasswordHash).HasColumnType("nvarchar(512)").HasMaxLength(512).IsRequired();
            entity.Property(e => e.Role).HasColumnType("nvarchar(16)").HasMaxLength(16).IsRequired().HasDefaultValue("User", "DF_Users_Role");
            entity.Property(e => e.IsActive).HasColumnType("bit").IsRequired().HasDefaultValue(true, "DF_Users_IsActive");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2").IsRequired().HasDefaultValueSql("SYSUTCDATETIME()", "DF_Users_CreatedAtUtc");
        });

        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("UserProfiles", "dbo");
            entity.HasKey(e => e.UserId).HasName("PK_UserProfiles");

            entity.Property(e => e.UserId).HasColumnType("bigint").IsRequired().ValueGeneratedNever();
            entity.Property(e => e.FirstName).HasColumnType("nvarchar(100)").HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastName).HasColumnType("nvarchar(100)").HasMaxLength(100).IsRequired();

            entity.HasOne(e => e.User).WithOne(u => u.UserProfile)
                .HasForeignKey<UserProfile>(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_UserProfiles_Users");
        });

        modelBuilder.Entity<Olympiad>(entity =>
        {
            entity.ToTable("Olympiads", "dbo", table =>
            {
                table.HasCheckConstraint("CK_Olympiads_DateRange", "[EndsAtUtc] > [StartsAtUtc]");
            });
            entity.HasKey(e => e.Id).HasName("PK_Olympiads");
            entity.HasIndex(e => new { e.IsPublished, e.StartsAtUtc }, "IX_Olympiads_PublishedStart");

            entity.Property(e => e.Id).HasColumnType("bigint").IsRequired().UseIdentityColumn(1, 1);
            entity.Property(e => e.Title).HasColumnType("nvarchar(200)").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasColumnType("nvarchar(max)");
            entity.Property(e => e.StartsAtUtc).HasColumnType("datetime2").IsRequired();
            entity.Property(e => e.EndsAtUtc).HasColumnType("datetime2").IsRequired();
            entity.Property(e => e.IsPublished).HasColumnType("bit").IsRequired().HasDefaultValue(false, "DF_Olympiads_IsPublished");
            entity.Property(e => e.CreatedByUserId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2").IsRequired().HasDefaultValueSql("SYSUTCDATETIME()", "DF_Olympiads_CreatedAtUtc");

            entity.HasOne(e => e.CreatedByUser).WithMany(u => u.CreatedOlympiads)
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("FK_Olympiads_Users");
        });

        modelBuilder.Entity<Problem>(entity =>
        {
            entity.ToTable("Problems", "dbo", table =>
            {
                table.HasCheckConstraint("CK_Problems_Difficulty", "[Difficulty] IS NULL OR [Difficulty] IN (N'Easy', N'Medium', N'Hard')");
            });
            entity.HasKey(e => e.Id).HasName("PK_Problems");

            entity.Property(e => e.Id).HasColumnType("bigint").IsRequired().UseIdentityColumn(1, 1);
            entity.Property(e => e.Title).HasColumnType("nvarchar(200)").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Statement).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(e => e.InputFormat).HasColumnType("nvarchar(max)");
            entity.Property(e => e.OutputFormat).HasColumnType("nvarchar(max)");
            entity.Property(e => e.Difficulty).HasColumnType("nvarchar(16)").HasMaxLength(16);
            entity.Property(e => e.CreatedByUserId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2").IsRequired().HasDefaultValueSql("SYSUTCDATETIME()", "DF_Problems_CreatedAtUtc");

            entity.HasOne(e => e.CreatedByUser).WithMany(u => u.CreatedProblems)
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("FK_Problems_Users");
        });

        modelBuilder.Entity<OlympiadProblem>(entity =>
        {
            entity.ToTable("OlympiadProblems", "dbo", table =>
            {
                table.HasCheckConstraint("CK_OlympiadProblems_Position", "[Position] > 0");
                table.HasCheckConstraint("CK_OlympiadProblems_MaxPoints", "[MaxPoints] > 0");
            });
            entity.HasKey(e => new { e.OlympiadId, e.ProblemId }).HasName("PK_OlympiadProblems");
            entity.HasIndex(e => new { e.OlympiadId, e.Position }, "UX_OlympiadProblems_Position").IsUnique();

            entity.Property(e => e.OlympiadId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.ProblemId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.Position).HasColumnType("int").IsRequired();
            entity.Property(e => e.MaxPoints).HasColumnType("int").IsRequired().HasDefaultValue(100, "DF_OlympiadProblems_MaxPoints");
            entity.Property(e => e.IsVisible).HasColumnType("bit").IsRequired().HasDefaultValue(true, "DF_OlympiadProblems_IsVisible");

            entity.HasOne(e => e.Olympiad).WithMany(o => o.OlympiadProblems)
                .HasForeignKey(e => e.OlympiadId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_OlympiadProblems_Olympiads");
            entity.HasOne(e => e.Problem).WithMany(p => p.OlympiadProblems)
                .HasForeignKey(e => e.ProblemId)
                .OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("FK_OlympiadProblems_Problems");
        });

        modelBuilder.Entity<ProblemExample>(entity =>
        {
            entity.ToTable("ProblemExamples", "dbo", table =>
            {
                table.HasCheckConstraint("CK_ProblemExamples_Position", "[Position] > 0");
            });
            entity.HasKey(e => e.Id).HasName("PK_ProblemExamples");
            entity.HasIndex(e => new { e.ProblemId, e.Position }, "UX_ProblemExamples_Position").IsUnique();

            entity.Property(e => e.Id).HasColumnType("bigint").IsRequired().UseIdentityColumn(1, 1);
            entity.Property(e => e.ProblemId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.Position).HasColumnType("int").IsRequired();
            entity.Property(e => e.InputData).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(e => e.ExpectedOutput).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(e => e.Explanation).HasColumnType("nvarchar(max)");

            entity.HasOne(e => e.Problem).WithMany(p => p.Examples)
                .HasForeignKey(e => e.ProblemId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_ProblemExamples_Problems");
        });

        modelBuilder.Entity<Submission>(entity =>
        {
            entity.ToTable("Submissions", "dbo", table =>
            {
                table.HasCheckConstraint("CK_Submissions_Language",
                    $"[Language] IN ('{Submission.Cpp17}', '{Submission.Python3}')");
                // nvarchar uses at most two UTF-16 bytes per allowed UTF-8 byte.
                table.HasCheckConstraint("CK_Submissions_SourceCode", "DATALENGTH([SourceCode]) BETWEEN 2 AND 131072");
                table.HasCheckConstraint("CK_Submissions_Status",
                    $"[Status] IN ('{Submission.Queued}', 'Running', 'Accepted', 'WrongAnswer', 'CompilationError', " +
                    "'RuntimeError', 'TimeLimitExceeded', 'MemoryLimitExceeded', 'SystemError')");
                table.HasCheckConstraint("CK_Submissions_JudgeAttemptCount", "[JudgeAttemptCount] >= 0");
                table.HasCheckConstraint("CK_Submissions_JudgeLease", """
                    ([JudgeWorkerId] IS NULL AND [JudgeLeaseToken] IS NULL AND [JudgeLeaseExpiresAtUtc] IS NULL)
                    OR ([JudgeWorkerId] IS NOT NULL AND [JudgeLeaseToken] IS NOT NULL AND [JudgeAttemptCount] > 0
                        AND [JudgeStartedAtUtc] IS NOT NULL
                        AND (([Status] = 'Running' AND [JudgeLeaseExpiresAtUtc] IS NOT NULL AND [JudgeFinishedAtUtc] IS NULL)
                          OR ([Status] NOT IN ('Queued', 'Running') AND [JudgeLeaseExpiresAtUtc] IS NULL AND [JudgeFinishedAtUtc] IS NOT NULL)))
                    """.ReplaceLineEndings("\n"));
            });
            entity.HasKey(e => e.Id).HasName("PK_Submissions");
            entity.HasIndex(e => new { e.UserId, e.SubmittedAtUtc, e.Id }, "IX_Submissions_User_Time_Id")
                .IsDescending(false, true, true);
            entity.HasIndex(e => e.ProblemId, "IX_Submissions_ProblemId");
            entity.HasIndex(e => new { e.Status, e.Id }, "IX_Submissions_Status_Id");
            entity.HasIndex(e => e.JudgeWorkerId, "UX_Submissions_RunningJudgeWorker").IsUnique()
                .HasFilter("[Status] = 'Running' AND [JudgeWorkerId] IS NOT NULL");
            entity.HasIndex(e => e.JudgeLeaseExpiresAtUtc, "IX_Submissions_RunningLeaseExpiry")
                .HasFilter("[Status] = 'Running'");

            entity.Property(e => e.Id).HasColumnType("bigint").UseIdentityColumn(1, 1);
            entity.Property(e => e.UserId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.OlympiadId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.ProblemId).HasColumnType("bigint").IsRequired();
            entity.Property(e => e.Language).HasColumnType("varchar(16)").HasMaxLength(16).IsRequired();
            entity.Property(e => e.SourceCode).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(e => e.Status).HasColumnType("varchar(32)").HasMaxLength(32).IsRequired()
                .HasDefaultValue(Submission.Queued, "DF_Submissions_Status");
            entity.Property(e => e.SubmittedAtUtc).HasColumnType("datetime2").IsRequired()
                .HasDefaultValueSql("SYSUTCDATETIME()", "DF_Submissions_SubmittedAtUtc");
            entity.Property(e => e.JudgeWorkerId).HasColumnType("varchar(64)").HasMaxLength(64);
            entity.Property(e => e.JudgeLeaseToken).HasColumnType("uniqueidentifier");
            entity.Property(e => e.JudgeLeaseExpiresAtUtc).HasColumnType("datetime2");
            entity.Property(e => e.JudgeAttemptCount).HasColumnType("int")
                .HasDefaultValue(0, "DF_Submissions_JudgeAttemptCount");
            entity.Property(e => e.JudgeStartedAtUtc).HasColumnType("datetime2");
            entity.Property(e => e.JudgeFinishedAtUtc).HasColumnType("datetime2");

            // Keep attempts independent of the mutable OlympiadProblems link.
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Submissions_Users");
            entity.HasOne<Olympiad>().WithMany().HasForeignKey(e => e.OlympiadId)
                .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Submissions_Olympiads");
            entity.HasOne<Problem>().WithMany().HasForeignKey(e => e.ProblemId)
                .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Submissions_Problems");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
