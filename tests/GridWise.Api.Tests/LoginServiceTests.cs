using GridWise.Api.Data;
using GridWise.Api.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace GridWise.Api.Tests;

public sealed class LoginServiceTests : IDisposable
{
    private const string Email = "analyst@gridwise.test";
    private const string Password = "synthetic-demo-password";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero));
    private readonly PasswordHasher<AppUser> _hasher = new();
    private readonly GridWiseDbContext _db;
    private readonly LoginService _logins;

    public LoginServiceTests()
    {
        var options = new DbContextOptionsBuilder<GridWiseDbContext>()
            .UseInMemoryDatabase($"login-{Guid.NewGuid():N}")
            .Options;
        _db = new GridWiseDbContext(options, _clock);
        var user = new AppUser { Email = Email, NormalizedEmail = AppUser.Normalize(Email), DisplayName = "Arjun Analyst" };
        user.PasswordHash = _hasher.HashPassword(user, Password);
        _db.Users.Add(user);
        _db.SaveChanges();
        _logins = new LoginService(_db, _hasher, _clock);
    }

    public void Dispose() => _db.Dispose();

    private Task<LoginResult> Login(string password, string email = Email) =>
        _logins.SignInAsync(email, password, CancellationToken.None);

    private AppUser StoredUser() => _db.Users.AsNoTracking().Single();

    [Fact]
    public async Task Correct_password_succeeds()
    {
        var result = await Login(Password);

        Assert.Equal(LoginOutcome.Succeeded, result.Outcome);
        Assert.Equal(Email, result.User!.Email);
    }

    [Fact]
    public async Task Unknown_email_is_rejected_without_a_user()
    {
        var result = await Login(Password, "nobody@gridwise.test");

        Assert.Equal(LoginOutcome.Rejected, result.Outcome);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Failures_are_counted_and_reset_by_a_success()
    {
        await Login("wrong-1");
        await Login("wrong-2");
        Assert.Equal(2, StoredUser().FailedLoginCount);

        await Login(Password);

        Assert.Equal(0, StoredUser().FailedLoginCount);
    }

    [Fact]
    public async Task Fifth_failure_locks_the_account_even_for_the_right_password()
    {
        for (var i = 0; i < LoginService.MaxFailedAttempts; i++)
        {
            await Login($"wrong-{i}");
        }

        var stored = StoredUser();
        Assert.Equal(_clock.GetUtcNow() + LoginService.LockoutDuration, stored.LockoutEndsAt);
        Assert.Equal(LoginOutcome.Rejected, (await Login(Password)).Outcome);

        _clock.Advance(LoginService.LockoutDuration - TimeSpan.FromSeconds(1));
        Assert.Equal(LoginOutcome.Rejected, (await Login(Password)).Outcome);
    }

    [Fact]
    public async Task Lockout_expires_and_a_success_clears_it()
    {
        for (var i = 0; i < LoginService.MaxFailedAttempts; i++)
        {
            await Login($"wrong-{i}");
        }

        _clock.Advance(LoginService.LockoutDuration);

        Assert.Equal(LoginOutcome.Succeeded, (await Login(Password)).Outcome);
        Assert.Null(StoredUser().LockoutEndsAt);
    }

    [Fact]
    public async Task Attempts_during_a_lockout_do_not_extend_it()
    {
        for (var i = 0; i < LoginService.MaxFailedAttempts; i++)
        {
            await Login($"wrong-{i}");
        }
        var lockedUntil = StoredUser().LockoutEndsAt;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await Login("wrong-again");

        Assert.Equal(lockedUntil, StoredUser().LockoutEndsAt);
        Assert.Equal(0, StoredUser().FailedLoginCount);
    }

    [Fact]
    public async Task Timestamps_come_from_the_clock()
    {
        var created = StoredUser().CreatedAt;
        _clock.Advance(TimeSpan.FromHours(1));

        await Login("wrong");

        var stored = StoredUser();
        Assert.Equal(created, stored.CreatedAt);
        Assert.Equal(created + TimeSpan.FromHours(1), stored.UpdatedAt);
    }
}
