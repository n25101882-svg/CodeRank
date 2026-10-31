namespace CodeRank.Api.Models;

public partial class UserProfile
{
    public long UserId { get; set; }

    public string FirstName { get; set; } = null!;

    public string LastName { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
