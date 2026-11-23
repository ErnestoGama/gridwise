using System.Text.Json;
using GridWise.Api.Data;
using GridWise.Api.Http;
using GridWise.Api.Identity;
using GridWise.Api.Realtime;
using GridWise.Api.Seeding;
using GridWise.Api.Sites;
using GridWise.Api.Telemetry;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var redis = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<GridWiseDbContext>(options => options.UseNpgsql(postgres));
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<OrganizationAccess>();
builder.Services.AddScoped<DemoSeeder>();

// Malformed bodies raise BadHttpRequestException so they get the error envelope in every environment.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "gridwise.session";
        options.Cookie.HttpOnly = true;
        // Strict same-site cookies keep other sites from sending authenticated requests (CSRF).
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // An API never redirects to a login page: answer with the JSON envelope instead.
        options.Events.OnRedirectToLogin = context => ApiErrors.WriteAsync(
            context.HttpContext, StatusCodes.Status401Unauthorized, "unauthenticated", "Sign in to continue.");
        options.Events.OnRedirectToAccessDenied = context => ApiErrors.WriteAsync(
            context.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "Your role does not allow this action.");
    });
builder.Services.AddAuthorization();

builder.Services.AddSignalR(options =>
{
    // Clients only send subscribe/unsubscribe calls with two IDs.
    options.MaximumReceiveMessageSize = 4 * 1024;
});
builder.Services.AddSingleton<TelemetryNotifier>();

builder.Services.AddHealthChecks()
    .AddNpgSql(postgres, name: "database", timeout: TimeSpan.FromSeconds(3))
    .AddRedis(redis, name: "redis", timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();

if (CommandLine.IsCommand(args))
{
    return await CommandLine.RunAsync(app, args);
}

app.UseRequestId();
app.UseExceptionHandler(handler => handler.Run(ApiErrors.HandleExceptionAsync));
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
    ResponseWriter = HealthResponse.WriteAsync,
});
app.MapIdentityEndpoints();
app.MapSiteEndpoints();
app.MapTelemetryEndpoints();
app.MapQualityFlagEndpoints();
app.MapReadingEndpoints();
app.MapHub<TelemetryHub>(TelemetryHub.Path, options =>
{
    // A connection outlives its HTTP request; close it when the session cookie expires.
    options.CloseOnAuthenticationExpiration = true;
});

await app.RunAsync();
return 0;

/// <summary>
/// Writes the overall status and one status per check. Exception details are
/// left out so connection strings and hostnames never reach the response.
/// </summary>
internal static class HealthResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString()),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}

public partial class Program;
