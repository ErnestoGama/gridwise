using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GridWise.Api.Tests;

public sealed class ReadingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Each test uses its own day so readings of one test never show up in another's window.
    private static readonly DateTimeOffset Base = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
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

    private static DateTimeOffset Slot(int day, int slot) => Base.AddDays(day).AddMinutes(15 * slot);

    private static async Task IngestAsync(HttpClient admin, string organizationId, Guid meterId, int day, params int[] slots)
    {
        var readings = slots
            .Select(s => new { meterId, sequence = day * 1000 + s, readingStart = Slot(day, s), energyKwh = 1.5m + s })
            .ToArray();
        var response = await admin.PostAsJsonAsync($"/v1/organizations/{organizationId}/telemetry/batches", new { readings });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Url(string organizationId, Guid buildingId, DateTimeOffset from, DateTimeOffset to) =>
        $"/v1/organizations/{organizationId}/buildings/{buildingId}/readings"
        + $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    [Fact]
    public async Task Every_meter_of_the_building_is_listed_with_its_readings_in_time_order_and_gaps_left_out()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var org = (await OrganizationsAsync(admin))["harbor-demo"];
        var (building, main) = await MeterAsync(admin, org, "GW-HH-MAIN-01");
        await IngestAsync(admin, org, main, 0, 3, 0, 1, 6);

        var operatorClient = await SignedInClientAsync("operator@gridwise.test");
        var body = await operatorClient.GetFromJsonAsync<JsonElement>(Url(org, building, Slot(0, 0), Slot(1, 0)));

        Assert.Equal(building, body.GetProperty("buildingId").GetGuid());
        var meters = body.GetProperty("meters").EnumerateArray().ToList();
        Assert.Equal(["GW-HH-HVAC-01", "GW-HH-MAIN-01"], meters.Select(m => m.GetProperty("serial").GetString()));

        var hvac = meters[0];
        Assert.Empty(hvac.GetProperty("readings").EnumerateArray());
        Assert.Empty(hvac.GetProperty("qualityFlags").EnumerateArray());

        var mainView = meters[1];
        Assert.Equal(15, mainView.GetProperty("intervalMinutes").GetInt32());
        var readings = mainView.GetProperty("readings").EnumerateArray().ToList();
        Assert.Equal(
            [Slot(0, 0), Slot(0, 1), Slot(0, 3), Slot(0, 6)],
            readings.Select(r => r.GetProperty("readingStart").GetDateTimeOffset()));
        Assert.Equal(4.5m, readings[2].GetProperty("energyKwh").GetDecimal());
        Assert.Equal(3, readings[2].GetProperty("sequence").GetInt64());

        // Missing intervals stay unknown: two gap flags, no zero readings.
        var gaps = mainView.GetProperty("qualityFlags").EnumerateArray().ToList();
        Assert.Equal([Slot(0, 2), Slot(0, 4)], gaps.Select(g => g.GetProperty("intervalStart").GetDateTimeOffset()));
        Assert.All(gaps, g => Assert.Equal("gap", g.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task The_window_includes_its_start_excludes_its_end_and_keeps_flags_that_overlap_it()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var org = (await OrganizationsAsync(admin))["harbor-demo"];
        var (building, meter) = await MeterAsync(admin, org, "GW-QW-MAIN-01");
        // Gap over slots 2–5.
        await IngestAsync(admin, org, meter, 1, 0, 1, 6, 7, 8);

        var body = await admin.GetFromJsonAsync<JsonElement>(Url(org, building, Slot(1, 4), Slot(1, 7)));

        var view = body.GetProperty("meters").EnumerateArray()
            .Single(m => m.GetProperty("meterId").GetGuid() == meter);
        Assert.Equal([Slot(1, 6)],
            view.GetProperty("readings").EnumerateArray().Select(r => r.GetProperty("readingStart").GetDateTimeOffset()));
        var gap = Assert.Single(view.GetProperty("qualityFlags").EnumerateArray());
        Assert.Equal(Slot(1, 2), gap.GetProperty("intervalStart").GetDateTimeOffset());
        Assert.Equal(4, gap.GetProperty("missingIntervals").GetInt32());
    }

    [Theory]
    [InlineData(null, "2026-05-02T00:00:00Z", "from")]
    [InlineData("2026-05-01T00:00:00Z", null, "to")]
    [InlineData("2026-05-01T00:00:00", "2026-05-02T00:00:00Z", "from")]
    [InlineData("2026-05-02T00:00:00Z", "2026-05-01T00:00:00Z", "to")]
    [InlineData("2026-05-01T00:00:00Z", "2026-05-03T00:00:01Z", "to")]
    public async Task An_invalid_window_is_a_validation_error(string? from, string? to, string field)
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var org = (await OrganizationsAsync(admin))["harbor-demo"];
        var (building, _) = await MeterAsync(admin, org, "GW-HH-MAIN-01");
        var query = new List<string>();
        if (from is not null)
        {
            query.Add($"from={Uri.EscapeDataString(from)}");
        }
        if (to is not null)
        {
            query.Add($"to={Uri.EscapeDataString(to)}");
        }

        var response = await admin.GetAsync(
            $"/v1/organizations/{org}/buildings/{building}/readings?{string.Join('&', query)}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.True(error.GetProperty("fields").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_window_of_exactly_48_hours_is_accepted()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var org = (await OrganizationsAsync(admin))["harbor-demo"];
        var (building, _) = await MeterAsync(admin, org, "GW-HH-MAIN-01");

        var response = await admin.GetAsync(Url(org, building, Slot(2, 0), Slot(4, 0)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Buildings_of_other_organizations_and_non_members_get_not_found()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var orgs = await OrganizationsAsync(admin);
        var (lanternBuilding, lanternMeter) = await MeterAsync(admin, orgs["lantern-estates"], "GW-LH-MAIN-01");
        await IngestAsync(admin, orgs["lantern-estates"], lanternMeter, 5, 0, 1);

        // Another organization's building under the caller's own organization.
        var underOwn = await admin.GetAsync(Url(orgs["harbor-demo"], lanternBuilding, Slot(5, 0), Slot(6, 0)));
        Assert.Equal(HttpStatusCode.NotFound, underOwn.StatusCode);

        // A member of harbor-demo only, asking for lantern-estates.
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");
        var nonMember = await operatorClient.GetAsync(Url(orgs["lantern-estates"], lanternBuilding, Slot(5, 0), Slot(6, 0)));
        Assert.Equal(HttpStatusCode.NotFound, nonMember.StatusCode);

        var anonymous = await factory.CreateClient().GetAsync(Url(orgs["lantern-estates"], lanternBuilding, Slot(5, 0), Slot(6, 0)));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
