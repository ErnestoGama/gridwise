using GridWise.Api.Data;
using GridWise.Api.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Seeding;

/// <summary>
/// Creates two fictional organizations and one demo user per role. Running it
/// again changes nothing that already exists. Emails use the reserved
/// <c>.test</c> domain so they can never reach a real mailbox.
/// </summary>
public sealed class DemoSeeder(GridWiseDbContext db, IPasswordHasher<AppUser> hasher)
{
    public const string PasswordVariable = "GRIDWISE_DEMO_PASSWORD";
    public const int MinPasswordLength = 12;

    public static readonly (string Slug, string Name)[] Organizations =
    [
        ("harbor-demo", "Harbor Demo Properties"),
        ("lantern-estates", "Lantern Estates"),
    ];

    /// <summary>Demo users and their memberships. Only the admin can see the second organization.</summary>
    public static readonly (string Email, string DisplayName, (string Slug, OrganizationRole Role)[] Memberships)[] Users =
    [
        ("operator@gridwise.test", "Olivia Operator", [("harbor-demo", OrganizationRole.Operator)]),
        ("analyst@gridwise.test", "Arjun Analyst", [("harbor-demo", OrganizationRole.Analyst)]),
        ("manager@gridwise.test", "Maya Manager", [("harbor-demo", OrganizationRole.Manager)]),
        ("admin@gridwise.test", "Ada Admin",
            [("harbor-demo", OrganizationRole.Admin), ("lantern-estates", OrganizationRole.Admin)]),
    ];

    public async Task<int> SeedAsync(string password, CancellationToken ct)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"The demo password must be at least {MinPasswordLength} characters.", nameof(password));
        }

        var created = 0;
        var organizations = new Dictionary<string, Organization>();
        foreach (var (slug, name) in Organizations)
        {
            var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Slug == slug, ct);
            if (organization is null)
            {
                organization = new Organization { Name = name, Slug = slug };
                db.Organizations.Add(organization);
                created++;
            }
            organizations[slug] = organization;
        }

        foreach (var (email, displayName, memberships) in Users)
        {
            var normalized = AppUser.Normalize(email);
            var user = await db.Users.Include(u => u.Memberships).SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
            if (user is null)
            {
                user = new AppUser { Email = email, NormalizedEmail = normalized, DisplayName = displayName };
                user.PasswordHash = hasher.HashPassword(user, password);
                db.Users.Add(user);
                created++;
            }

            foreach (var (slug, role) in memberships)
            {
                var organization = organizations[slug];
                if (user.Memberships.Any(m => m.OrganizationId == organization.Id))
                {
                    continue;
                }
                db.Memberships.Add(new Membership { OrganizationId = organization.Id, UserId = user.Id, Role = role });
                created++;
            }
        }

        await db.SaveChangesAsync(ct);
        return created;
    }
}
