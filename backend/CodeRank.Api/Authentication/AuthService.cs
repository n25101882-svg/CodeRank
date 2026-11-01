using System.Security.Cryptography;
using CodeRank.Api.Contracts.Auth;
using CodeRank.Api.Data;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Authentication;

public sealed class AuthService(CodeRankDbContext db, IPasswordHasher<User> hasher,
    IRegistrationConflictDetector conflicts, JwtTokenIssuer tokens)
{
    // Equalize expensive password verification work for unknown accounts without a fixed password.
    private static readonly User DummyUser = new();
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public async Task<UserResponse?> RegisterAsync(RegisterRequest request, CancellationToken token)
    {
        var email = request.Email.Trim();
        var normalized = NormalizeEmail(email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, token)) return null;
        var user = new User { Email = email, NormalizedEmail = normalized, Role = "User", IsActive = true,
            UserProfile = new UserProfile { FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim() } };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        try
        {
            // One relational SaveChanges atomically inserts Users and its UserProfiles dependent.
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateException error) when (conflicts.IsUniqueViolation(error))
        {
            db.ChangeTracker.Clear();
            // Report an email conflict only if that email actually exists, including an insert race.
            if (await db.Users.AsNoTracking().AnyAsync(u => u.NormalizedEmail == normalized, token)) return null;
            throw;
        }
        return ToResponse(user);
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken token)
    {
        var normalized = NormalizeEmail(request.Email);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, token);
        PasswordVerificationResult verified;
        try { verified = hasher.VerifyHashedPassword(user ?? DummyUser, user?.PasswordHash ?? DummyHash, request.Password); }
        catch (Exception error) when (error is FormatException or ArgumentException) { return null; }
        if (user is null || !user.IsActive || verified == PasswordVerificationResult.Failed) return null;
        return tokens.Issue(user);
    }

    public async Task<UserResponse?> CurrentUserAsync(long id, CancellationToken token)
    {
        return await db.Users.AsNoTracking().Where(u => u.Id == id && u.IsActive)
            .Select(u => new UserResponse(u.Id, u.Email,
                u.UserProfile == null ? null : u.UserProfile.FirstName,
                u.UserProfile == null ? null : u.UserProfile.LastName, u.Role))
            .SingleOrDefaultAsync(token);
    }

    private static UserResponse ToResponse(User user) =>
        new(user.Id, user.Email, user.UserProfile?.FirstName, user.UserProfile?.LastName, user.Role);
}
