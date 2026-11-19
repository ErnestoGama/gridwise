using GridWise.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Identity;

public enum LoginOutcome
{
    Succeeded,

    /// <summary>Unknown email, wrong password or locked account. Callers must not tell these apart.</summary>
    Rejected,
}

public sealed record LoginResult(LoginOutcome Outcome, AppUser? User);

/// <summary>
/// Checks email/password pairs and locks an account for
/// <see cref="LockoutDuration"/> after <see cref="MaxFailedAttempts"/> failures in a row.
/// </summary>
public sealed class LoginService(GridWiseDbContext db, IPasswordHasher<AppUser> hasher, TimeProvider clock)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // Verified against when the email is unknown so both paths cost one hash check.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<AppUser>().HashPassword(Placeholder, Guid.NewGuid().ToString("N")));

    private static readonly AppUser Placeholder = new()
    {
        Email = "placeholder@invalid",
        NormalizedEmail = "PLACEHOLDER@INVALID",
        DisplayName = "placeholder",
    };

    public async Task<LoginResult> SignInAsync(string email, string password, CancellationToken ct)
    {
        var normalized = AppUser.Normalize(email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null)
        {
            hasher.VerifyHashedPassword(Placeholder, DummyHash.Value, password);
            return new LoginResult(LoginOutcome.Rejected, null);
        }

        var now = clock.GetUtcNow();
        if (user.LockoutEndsAt is { } until && until > now)
        {
            hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            return new LoginResult(LoginOutcome.Rejected, null);
        }

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockoutEndsAt = now + LockoutDuration;
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync(ct);
            return new LoginResult(LoginOutcome.Rejected, null);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
        }
        if (user.FailedLoginCount != 0 || user.LockoutEndsAt is not null
            || verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.FailedLoginCount = 0;
            user.LockoutEndsAt = null;
            await db.SaveChangesAsync(ct);
        }
        return new LoginResult(LoginOutcome.Succeeded, user);
    }
}
