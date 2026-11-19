using System.Text.RegularExpressions;

namespace GridWise.Api.Identity;

/// <summary>Entities whose created/updated timestamps are set by the DbContext.</summary>
public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A tenant. Every domain record belongs to exactly one organization.</summary>
public sealed class Organization : ITimestamped
{
    public const int NameMaxLength = 120;
    public const int SlugMaxLength = 63;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Membership> Memberships { get; } = [];
}

/// <summary>A person who can sign in. Access to data always goes through a <see cref="Membership"/>.</summary>
public sealed class AppUser : ITimestamped
{
    public const int EmailMaxLength = 254;
    public const int DisplayNameMaxLength = 100;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Email { get; set; }

    /// <summary>Uppercase invariant form of <see cref="Email"/>, unique across users.</summary>
    public required string NormalizedEmail { get; set; }

    public required string DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Membership> Memberships { get; } = [];

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();
}

/// <summary>Grants one user one role in one organization.</summary>
public sealed class Membership : ITimestamped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public OrganizationRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}

public static partial class OrganizationSlug
{
    /// <summary>Same rule as the <c>ck_organizations_slug</c> database constraint.</summary>
    public static bool IsValid(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= Organization.SlugMaxLength && Pattern().IsMatch(slug);

    // \z rather than $: in .NET, $ also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
