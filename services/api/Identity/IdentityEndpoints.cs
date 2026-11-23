using System.Net.Mail;
using System.Security.Claims;
using GridWise.Api.Data;
using GridWise.Api.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Identity;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record MembershipView(Guid OrganizationId, string OrganizationName, string OrganizationSlug, string Role);

public sealed record MeView(Guid Id, string Email, string DisplayName, IReadOnlyList<MembershipView> Memberships);

public sealed record OrganizationView(Guid Id, string Name, string Slug, string Role);

public static class IdentityEndpoints
{
    public const int PasswordMaxLength = 1024;

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/v1");

        v1.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        // Cast so the handler's IResult is written; as a RequestDelegate it would be discarded.
        v1.MapPost("/auth/logout", (Delegate)LogoutAsync).RequireAuthorization();
        v1.MapGet("/me", GetMeAsync).RequireAuthorization();
        v1.MapGet("/organizations/{organizationId:guid}", GetOrganizationAsync).RequireAuthorization();

        return routes;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, HttpContext http, LoginService logins, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var result = await logins.SignInAsync(request.Email!, request.Password!, ct);
        if (result.Outcome != LoginOutcome.Succeeded || result.User is not { } user)
        {
            // The email is not logged: failed sign-ins are counted, not attributed.
            loggers.CreateLogger("GridWise.Api.Identity").LogInformation("Sign-in rejected");
            return ApiErrors.Result(http, StatusCodes.Status401Unauthorized, "invalid_credentials",
                "The email or password is incorrect.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Results.Ok(await LoadMeAsync(http.RequestServices.GetRequiredService<GridWiseDbContext>(), user.Id, ct));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMeAsync(HttpContext http, GridWiseDbContext db, CancellationToken ct)
    {
        if (http.User.GetUserId() is not { } userId || await LoadMeAsync(db, userId, ct) is not { } me)
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return ApiErrors.Unauthorized(http);
        }
        return Results.Ok(me);
    }

    private static async Task<IResult> GetOrganizationAsync(
        Guid organizationId, HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, context) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Operator, ct);
        if (decision != AccessDecision.Granted || context is null)
        {
            return ApiErrors.NotFound(http, "Organization not found.");
        }

        var organization = await db.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.Id, o.Name, o.Slug })
            .SingleAsync(ct);
        return Results.Ok(new OrganizationView(organization.Id, organization.Name, organization.Slug, context.Role.ToName()));
    }

    private static async Task<MeView?> LoadMeAsync(GridWiseDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .SingleOrDefaultAsync(ct);
        if (user is null)
        {
            return null;
        }

        var memberships = await db.Memberships
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Organization.Name)
            .Select(m => new { m.OrganizationId, m.Organization.Name, m.Organization.Slug, m.Role })
            .ToListAsync(ct);

        return new MeView(user.Id, user.Email, user.DisplayName,
            memberships.Select(m => new MembershipView(m.OrganizationId, m.Name, m.Slug, m.Role.ToName())).ToList());
    }

    internal static Dictionary<string, string[]> Validate(LoginRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            errors["email"] = ["Email is required."];
        }
        else if (email.Length > AppUser.EmailMaxLength || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
        {
            errors["email"] = ["Email is not a valid address."];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors["password"] = ["Password is required."];
        }
        else if (request.Password.Length > PasswordMaxLength)
        {
            errors["password"] = [$"Password must be at most {PasswordMaxLength} characters."];
        }
        return errors;
    }
}
