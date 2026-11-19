using System.Security.Claims;
using GridWise.Api.Data;
using GridWise.Api.Http;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Identity;

public sealed record OrganizationContext(Guid OrganizationId, Guid UserId, OrganizationRole Role);

public enum AccessDecision
{
    Granted,

    /// <summary>The user is not a member. Reported as 404 so organization IDs cannot be probed.</summary>
    NotMember,

    /// <summary>The user is a member but their role is too low. Reported as 403.</summary>
    RoleTooLow,
}

/// <summary>
/// Resolves the caller's membership for an organization taken from the route.
/// The organization is never read from the request body.
/// </summary>
public sealed class OrganizationAccess(GridWiseDbContext db)
{
    public async Task<(AccessDecision Decision, OrganizationContext? Context)> CheckAsync(
        ClaimsPrincipal principal, Guid organizationId, OrganizationRole required, CancellationToken ct)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return (AccessDecision.NotMember, null);
        }

        var role = await db.Memberships
            .Where(m => m.OrganizationId == organizationId && m.UserId == userId)
            .Select(m => (OrganizationRole?)m.Role)
            .SingleOrDefaultAsync(ct);

        if (role is null)
        {
            return (AccessDecision.NotMember, null);
        }
        return role.Value.Satisfies(required)
            ? (AccessDecision.Granted, new OrganizationContext(organizationId, userId, role.Value))
            : (AccessDecision.RoleTooLow, null);
    }
}

public static class AccessDecisions
{
    /// <summary>The error response for a refused decision: 404 for non-members, 403 for a role that is too low.</summary>
    public static IResult ToError(this AccessDecision decision, HttpContext http) => decision switch
    {
        AccessDecision.RoleTooLow => ApiErrors.Forbidden(http),
        _ => ApiErrors.NotFound(http, "Organization not found."),
    };
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
