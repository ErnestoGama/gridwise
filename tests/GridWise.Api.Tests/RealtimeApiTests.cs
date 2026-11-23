using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using GridWise.Api.Realtime;
using GridWise.Api.Telemetry;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GridWise.Api.Tests;

public sealed class RealtimeApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }

    private async Task<string> SessionCookieAsync(string email)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("gridwise.session=", StringComparison.Ordinal))
            .Split(';')[0];
    }

    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private HubConnection Connection(string? cookie)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, TelemetryHub.Path), options =>
            {
                // TestServer has no real sockets; long polling runs over its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (cookie is not null)
                {
                    options.Headers["Cookie"] = cookie;
                }
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private async Task<(HubConnection Connection, Channel<ReadingsIngestedMessage> Received)> ConnectedAsync(string email)
    {
        var connection = Connection(await SessionCookieAsync(email));
        var received = Channel.CreateUnbounded<ReadingsIngestedMessage>();
        connection.On<ReadingsIngestedMessage>(nameof(ITelemetryClient.ReadingsIngested), m => received.Writer.TryWrite(m));
        await connection.StartAsync();
        return (connection, received);
    }

    private static async Task<Dictionary<string, string>> OrganizationsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("memberships").EnumerateArray()
            .ToDictionary(m => m.GetProperty("organizationSlug").GetString()!, m => m.GetProperty("organizationId").GetString()!);

    private static async Task<(Guid BuildingId, Guid MeterId)> MeterAsync(HttpClient client, string organizationId, string serial)
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/v1/organizations/{organizationId}/meters?limit=100");
        var meter = page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("serial").GetString() == serial);
        return (meter.GetProperty("buildingId").GetGuid(), meter.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> IngestAsync(HttpClient admin, string organizationId, Guid meterId, int day, params int[] slots)
    {
        var readings = slots
            .Select(s => new
            {
                meterId, sequence = day * 1000 + s, readingStart = Base.AddDays(day).AddMinutes(15 * s), energyKwh = 1.5m + s,
            })
            .ToArray();
        var response = await admin.PostAsJsonAsync($"/v1/organizations/{organizationId}/telemetry/batches", new { readings });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("batchId").GetGuid();
    }

    private static async Task<ReadingsIngestedMessage> NextAsync(Channel<ReadingsIngestedMessage> received)
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await received.Reader.ReadAsync(timeout.Token);
    }

    private static async Task AssertNothingAsync(Channel<ReadingsIngestedMessage> received)
    {
        await Task.Delay(Quiet);
        Assert.False(received.Reader.TryRead(out _));
    }

    [Fact]
    public async Task A_connection_without_a_session_is_refused()
    {
        var connection = Connection(cookie: null);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task A_subscribed_member_receives_new_readings_of_the_building()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var org = (await OrganizationsAsync(admin))["harbor-demo"];
        var (building, meter) = await MeterAsync(admin, org, "GW-HH-MAIN-01");
        var (operatorConnection, received) = await ConnectedAsync("operator@gridwise.test");

        var subscription = await operatorConnection.InvokeAsync<BuildingSubscription>(
            nameof(TelemetryHub.SubscribeBuilding), Guid.Parse(org), building);
        Assert.Equal(building, subscription.BuildingId);

        var batchId = await IngestAsync(admin, org, meter, 0, 1, 0);

        var message = await NextAsync(received);
        Assert.Equal(Guid.Parse(org), message.OrganizationId);
        Assert.Equal(building, message.BuildingId);
        Assert.Equal(batchId, message.BatchId);
        var meterReadings = Assert.Single(message.Meters);
        Assert.Equal(meter, meterReadings.MeterId);
        Assert.Equal([Base, Base.AddMinutes(15)], meterReadings.Readings.Select(r => r.ReadingStart));
        Assert.Equal([1.5m, 2.5m], meterReadings.Readings.Select(r => r.EnergyKwh));
    }

    [Fact]
    public async Task Resent_readings_and_unsubscribed_buildings_send_nothing()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var org = (await OrganizationsAsync(admin))["harbor-demo"];
        var (building, meter) = await MeterAsync(admin, org, "GW-HH-HVAC-01");
        var (connection, received) = await ConnectedAsync("analyst@gridwise.test");
        await connection.InvokeAsync<BuildingSubscription>(nameof(TelemetryHub.SubscribeBuilding), Guid.Parse(org), building);

        await IngestAsync(admin, org, meter, 1, 0, 1);
        await NextAsync(received);

        // Every reading is a duplicate: nothing new to show.
        await IngestAsync(admin, org, meter, 1, 0, 1);
        await AssertNothingAsync(received);

        await connection.InvokeAsync(nameof(TelemetryHub.UnsubscribeBuilding), Guid.Parse(org), building);
        await IngestAsync(admin, org, meter, 1, 2);
        await AssertNothingAsync(received);
    }

    [Fact]
    public async Task Members_cannot_follow_buildings_of_other_organizations()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var orgs = await OrganizationsAsync(admin);
        var harbor = Guid.Parse(orgs["harbor-demo"]);
        var lantern = orgs["lantern-estates"];
        var (lanternBuilding, lanternMeter) = await MeterAsync(admin, lantern, "GW-LH-MAIN-01");
        var (connection, received) = await ConnectedAsync("operator@gridwise.test");

        // Not a member of Lantern Estates.
        var notMember = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<BuildingSubscription>(
            nameof(TelemetryHub.SubscribeBuilding), Guid.Parse(lantern), lanternBuilding));
        Assert.Contains(TelemetryHub.NotFoundError, notMember.Message);

        // A member of Harbor Demo naming another organization's building gets the same answer.
        var wrongOrganization = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<BuildingSubscription>(
            nameof(TelemetryHub.SubscribeBuilding), harbor, lanternBuilding));
        Assert.Contains(TelemetryHub.NotFoundError, wrongOrganization.Message);

        await IngestAsync(admin, lantern, lanternMeter, 2, 0);
        await AssertNothingAsync(received);
    }

    [Fact]
    public async Task Subscriptions_per_connection_are_capped()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var org = (await OrganizationsAsync(admin))["lantern-estates"];
        var buildings = new List<Guid>();
        for (var i = 0; i <= TelemetryHub.MaxSubscriptionsPerConnection; i++)
        {
            var created = await admin.PostAsJsonAsync($"/v1/organizations/{org}/buildings",
                new { name = $"Wall display {i:D2}", timeZone = "Europe/London" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            buildings.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }
        var (connection, _) = await ConnectedAsync("admin@gridwise.test");
        Task Subscribe(Guid building) =>
            connection.InvokeAsync<BuildingSubscription>(nameof(TelemetryHub.SubscribeBuilding), Guid.Parse(org), building);

        foreach (var building in buildings.Take(TelemetryHub.MaxSubscriptionsPerConnection))
        {
            await Subscribe(building);
        }
        // Repeating a subscription does not count again.
        await Subscribe(buildings[0]);

        var error = await Assert.ThrowsAsync<HubException>(() => Subscribe(buildings[^1]));
        Assert.Contains(TelemetryHub.TooManySubscriptionsError, error.Message);

        // Unsubscribing frees a place.
        await connection.InvokeAsync(nameof(TelemetryHub.UnsubscribeBuilding), Guid.Parse(org), buildings[0]);
        await Subscribe(buildings[^1]);
    }
}

public sealed class TelemetryNotificationTests
{
    private static readonly Guid Organization = Guid.Parse("0190a000-0000-7000-8000-000000000001");
    private static readonly Guid BuildingA = Guid.Parse("0190a000-0000-7000-8000-00000000000a");
    private static readonly Guid BuildingB = Guid.Parse("0190a000-0000-7000-8000-00000000000b");
    private static readonly Guid MeterA1 = Guid.Parse("0190a000-0000-7000-8000-0000000000a1");
    private static readonly Guid MeterA2 = Guid.Parse("0190a000-0000-7000-8000-0000000000a2");
    private static readonly Guid MeterB1 = Guid.Parse("0190a000-0000-7000-8000-0000000000b1");
    private static readonly DateTimeOffset T0 = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Readings_are_grouped_by_building_and_meter_in_time_order()
    {
        var inserted = new List<ValidReading>
        {
            new(0, MeterA1, 2, T0.AddMinutes(15), 2m),
            new(1, MeterB1, 7, T0, 9m),
            new(2, MeterA1, 1, T0, 1m),
            new(3, MeterA2, 4, T0, 3m),
        };
        var buildings = new Dictionary<Guid, Guid> { [MeterA1] = BuildingA, [MeterA2] = BuildingA, [MeterB1] = BuildingB };
        var batch = Guid.CreateVersion7();

        var messages = TelemetryNotifications.Build(Organization, batch, T0.AddHours(1), inserted, buildings, true);

        Assert.Equal([BuildingA, BuildingB], messages.Select(m => m.BuildingId));
        Assert.All(messages, m =>
        {
            Assert.Equal(Organization, m.OrganizationId);
            Assert.Equal(batch, m.BatchId);
            Assert.True(m.QualityFlagsChanged);
        });
        Assert.Equal([MeterA1, MeterA2], messages[0].Meters.Select(m => m.MeterId));
        Assert.Equal([1L, 2L], messages[0].Meters[0].Readings.Select(r => r.Sequence));
        Assert.Equal(9m, Assert.Single(Assert.Single(messages[1].Meters).Readings).EnergyKwh);
    }

    [Fact]
    public void Building_groups_include_the_organization()
    {
        Assert.Equal(
            "building:0190a000000070008000000000000001:0190a00000007000800000000000000a",
            TelemetryHub.BuildingGroup(Organization, BuildingA));
    }
}
