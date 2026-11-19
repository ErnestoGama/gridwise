using GridWise.Api.Data;
using GridWise.Api.Identity;
using GridWise.Api.Sites;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Seeding;

/// <summary>
/// Creates two fictional organizations, one demo user per role and a few
/// buildings with meters. Running it
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

    /// <summary>Demo buildings with one main and one HVAC meter each, in different time zones.</summary>
    public static readonly (string Slug, string Name, string TimeZone, int FloorAreaM2, (string Serial, string Kind)[] Meters)[] Buildings =
    [
        ("harbor-demo", "Harbor Hall", "Europe/Madrid", 6400, [("GW-HH-MAIN-01", "main"), ("GW-HH-HVAC-01", "hvac")]),
        ("harbor-demo", "Quarry Works", "America/Bogota", 3800, [("GW-QW-MAIN-01", "main"), ("GW-QW-HVAC-01", "hvac")]),
        ("lantern-estates", "Lantern House", "Europe/London", 2900, [("GW-LH-MAIN-01", "main"), ("GW-LH-HVAC-01", "hvac")]),
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

        foreach (var (slug, name, timeZone, floorArea, meters) in Buildings)
        {
            var organization = organizations[slug];
            var building = await db.Buildings.Include(b => b.Meters)
                .SingleOrDefaultAsync(b => b.OrganizationId == organization.Id && b.Name == name, ct);
            if (building is null)
            {
                building = new Building { OrganizationId = organization.Id, Name = name, TimeZone = timeZone, FloorAreaM2 = floorArea };
                db.Buildings.Add(building);
                created++;
            }

            foreach (var (serial, kind) in meters)
            {
                if (building.Meters.Any(m => m.Serial == serial)
                    || await db.Meters.AnyAsync(m => m.OrganizationId == organization.Id && m.Serial == serial, ct))
                {
                    continue;
                }
                db.Meters.Add(new Meter
                {
                    OrganizationId = organization.Id,
                    BuildingId = building.Id,
                    Serial = serial,
                    Kind = kind,
                    IntervalMinutes = SiteEndpoints.DefaultIntervalMinutes,
                });
                created++;
            }
        }

        await db.SaveChangesAsync(ct);
        return created;
    }
}
