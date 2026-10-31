using CodeRank.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace CodeRank.Api.Data;

/// <summary>Maps the existing dbo schema created by database/CodeRank_ER_MVP_CreateSchema.sql.</summary>
public partial class CodeRankDbContext(DbContextOptions<CodeRankDbContext> options) : DbContext(options)
{
    public virtual DbSet<User> Users { get; set; }
    public virtual DbSet<UserProfile> UserProfiles { get; set; }
    public virtual DbSet<Olympiad> Olympiads { get; set; }
    public virtual DbSet<Problem> Problems { get; set; }
    public virtual DbSet<OlympiadProblem> OlympiadProblems { get; set; }
    public virtual DbSet<ProblemExample> ProblemExamples { get; set; }

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

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
