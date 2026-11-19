using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Seeding;

/// <summary>
/// Maintenance commands run with <c>dotnet run -- &lt;command&gt;</c> instead of starting the web server:
/// <list type="bullet">
/// <item><c>migrate</c> applies pending EF Core migrations.</item>
/// <item><c>seed-demo</c> applies migrations, then adds the demo organizations and users.</item>
/// </list>
/// </summary>
public static class CommandLine
{
    private static readonly string[] Commands = ["migrate", "seed-demo"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GridWiseDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("GridWise.Api.CommandLine");

        await db.Database.MigrateAsync();
        logger.LogInformation("Migrations applied");
        if (args[0] == "migrate")
        {
            return 0;
        }

        var password = Environment.GetEnvironmentVariable(DemoSeeder.PasswordVariable);
        if (string.IsNullOrEmpty(password) || password.Length < DemoSeeder.MinPasswordLength)
        {
            logger.LogError("Set {Variable} to a password of at least {Length} characters", DemoSeeder.PasswordVariable, DemoSeeder.MinPasswordLength);
            return 2;
        }

        var created = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(password, CancellationToken.None);
        logger.LogInformation("Demo data ready ({Created} records created)", created);
        return 0;
    }
}
