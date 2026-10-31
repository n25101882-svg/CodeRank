namespace CodeRank.Api.Models;

public partial class User
{
    public long Id { get; set; }

    public string Email { get; set; } = null!;

    public string NormalizedEmail { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string Role { get; set; } = "User";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public virtual UserProfile? UserProfile { get; set; }
    public virtual ICollection<Olympiad> CreatedOlympiads { get; set; } = new List<Olympiad>();
    public virtual ICollection<Problem> CreatedProblems { get; set; } = new List<Problem>();
}
