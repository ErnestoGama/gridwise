using GridWise.Api.Data;
using GridWise.Api.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GridWise.Api.Tests;

/// <summary>
/// Runs the real API pipeline with the EF Core in-memory provider in place of
/// PostgreSQL, seeded with the demo organizations and users.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "synthetic-demo-password";
    private readonly string _databaseName = $"gridwise-tests-{Guid.NewGuid():N}";

    static ApiFactory()
    {
        // Program reads these before the host is built; nothing connects to them in these tests.
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", "localhost:6379,abortConnect=false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<GridWiseDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<GridWiseDbContext>>();
            services.AddDbContext<GridWiseDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(Password, CancellationToken.None);
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}
