using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridWise.Api.Tests;

public sealed class QualityFlagApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Each test uses its own day so flags of one test never touch another's readings.
    private static readonly DateTimeOffset Base = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<(HttpClient Admin, Dictionary<string, string> Orgs)> AdminAsync()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var orgs = (await admin.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("memberships").EnumerateArray()
            .ToDictionary(m => m.GetProperty("organizationSlug").GetString()!, m => m.GetProperty("organizationId").GetString()!);
        return (admin, orgs);
    }

    private static async Task<Guid> MeterIdAsync(HttpClient client, string organizationId, string serial)
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/v1/organizations/{organizationId}/meters?limit=100");
        return page.GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("serial").GetString() == serial)
            .GetProperty("id").GetGuid();
    }

    private static DateTimeOffset Slot(int day, int slot) => Base.AddDays(day).AddMinutes(15 * slot);

    private static async Task IngestAsync(HttpClient client, string organizationId, Guid meterId, int day,
        IEnumerable<int> slots, Func<int, decimal>? kwh = null)
    {
        var readings = slots
            .Select(s => new { meterId, sequence = day * 1000 + s, readingStart = Slot(day, s), energyKwh = kwh?.Invoke(s) ?? 2m })
            .ToArray();
        var response = await client.PostAsJsonAsync($"/v1/organizations/{organizationId}/telemetry/batches", new { readings });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string FlagsUrl(string organizationId, Guid meterId, int day) =>
        $"/v1/organizations/{organizationId}/meters/{meterId}/quality-flags"
        + $"?from={Uri.EscapeDataString(Slot(day, 0).ToString("O"))}&to={Uri.EscapeDataString(Slot(day + 1, 0).ToString("O"))}";

    private static async Task<List<JsonElement>> FlagsAsync(HttpClient client, string organizationId, Guid meterId, int day) =>
        (await client.GetFromJsonAsync<JsonElement>(FlagsUrl(organizationId, meterId, day)))
            .GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task Missing_intervals_are_flagged_as_a_gap_and_no_zero_reading_is_stored()
    {
        var (admin, orgs) = await AdminAsync();
        var org = orgs["harbor-demo"];
        var meter = await MeterIdAsync(admin, org, "GW-HH-MAIN-01");

        await IngestAsync(admin, org, meter, 0, [0, 1, 2, 3, 8, 9]);

        var gap = Assert.Single(await FlagsAsync(admin, org, meter, 0));
        Assert.Equal("gap", gap.GetProperty("kind").GetString());
        Assert.Equal(Slot(0, 4), gap.GetProperty("intervalStart").GetDateTimeOffset());
        Assert.Equal(Slot(0, 8), gap.GetProperty("intervalEnd").GetDateTimeOffset());
        Assert.Equal(4, gap.GetProperty("missingIntervals").GetInt32());
        Assert.Equal(JsonValueKind.Null, gap.GetProperty("observedKwh").ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GridWiseDbContext>();
        var stored = await db.MeterReadings
            .Where(r => r.MeterId == meter && r.ReadingStart >= Slot(0, 0) && r.ReadingStart < Slot(1, 0))
            .ToListAsync();
        Assert.Equal(6, stored.Count);
        Assert.DoesNotContain(stored, r => r.ReadingStart >= Slot(0, 4) && r.ReadingStart < Slot(0, 8));
    }

    [Fact]
    public async Task Late_readings_shrink_and_then_clear_the_gap()
    {
        var (admin, orgs) = await AdminAsync();
        var org = orgs["harbor-demo"];
        var meter = await MeterIdAsync(admin, org, "GW-HH-HVAC-01");
        await IngestAsync(admin, org, meter, 1, [0, 1, 6, 7]);

        await IngestAsync(admin, org, meter, 1, [2, 3]);
        var shrunk = Assert.Single(await FlagsAsync(admin, org, meter, 1));
        Assert.Equal(Slot(1, 4), shrunk.GetProperty("intervalStart").GetDateTimeOffset());
        Assert.Equal(2, shrunk.GetProperty("missingIntervals").GetInt32());

        await IngestAsync(admin, org, meter, 1, [4, 5]);
        Assert.Empty(await FlagsAsync(admin, org, meter, 1));
    }

    [Fact]
    public async Task A_spike_is_flagged_and_the_stored_reading_is_unchanged()
    {
        var (admin, orgs) = await AdminAsync();
        var org = orgs["harbor-demo"];
        var meter = await MeterIdAsync(admin, org, "GW-QW-MAIN-01");

        await IngestAsync(admin, org, meter, 2, Enumerable.Range(0, 12), s => s == 10 ? 60m : 2m);

        var spike = Assert.Single(await FlagsAsync(admin, org, meter, 2));
        Assert.Equal("spike", spike.GetProperty("kind").GetString());
        Assert.Equal(Slot(2, 10), spike.GetProperty("intervalStart").GetDateTimeOffset());
        Assert.Equal(2010, spike.GetProperty("sequence").GetInt64());
        Assert.Equal(60m, spike.GetProperty("observedKwh").GetDecimal());
        Assert.Equal(2m, spike.GetProperty("baselineKwh").GetDecimal());
        Assert.Equal(1, spike.GetProperty("ruleVersion").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GridWiseDbContext>();
        var stored = await db.MeterReadings.SingleAsync(r => r.MeterId == meter && r.ReadingStart == Slot(2, 10));
        Assert.Equal(60m, stored.EnergyKwh);
    }

    [Fact]
    public async Task A_resend_leaves_flags_as_they_were()
    {
        var (admin, orgs) = await AdminAsync();
        var org = orgs["harbor-demo"];
        var meter = await MeterIdAsync(admin, org, "GW-QW-HVAC-01");
        await IngestAsync(admin, org, meter, 3, [0, 1, 5]);
        var before = Assert.Single(await FlagsAsync(admin, org, meter, 3));

        await IngestAsync(admin, org, meter, 3, [0, 1, 5]);

        var after = Assert.Single(await FlagsAsync(admin, org, meter, 3));
        Assert.Equal(before.GetProperty("id").GetGuid(), after.GetProperty("id").GetGuid());
        Assert.Equal(before.GetProperty("updatedAt").GetDateTimeOffset(), after.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Flags_are_paged_in_time_order_and_filtered_by_kind()
    {
        var (admin, orgs) = await AdminAsync();
        var org = orgs["lantern-estates"];
        var meter = await MeterIdAsync(admin, org, "GW-LH-MAIN-01");
        // Gaps at slots 12–13 and 16–17, then a spike at slot 20.
        await IngestAsync(admin, org, meter, 4, [.. Enumerable.Range(0, 12), 14, 15, 18, 19, 20], s => s == 20 ? 40m : 2m);

        var url = FlagsUrl(org, meter, 4);
        var first = await admin.GetFromJsonAsync<JsonElement>($"{url}&limit=2");
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await admin.GetFromJsonAsync<JsonElement>($"{url}&limit=2&cursor={cursor}");
        var spikes = await admin.GetFromJsonAsync<JsonElement>($"{url}&kind=spike");

        var starts = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(f => f.GetProperty("intervalStart").GetDateTimeOffset())
            .ToList();
        Assert.Equal(new[] { Slot(4, 12), Slot(4, 16), Slot(4, 20) }, starts);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(Slot(4, 20), Assert.Single(spikes.GetProperty("items").EnumerateArray())
            .GetProperty("intervalStart").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("kind=drift", "kind")]
    [InlineData("from=2026-03-01T00:00:00", "from")]
    [InlineData("from=yesterday", "from")]
    [InlineData("from=2026-03-02T00:00:00Z&to=2026-03-01T00:00:00Z", "to")]
    [InlineData("cursor=bm90LWEtY3Vyc29y", "cursor")]
    public async Task Invalid_filters_get_422(string query, string field)
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-MAIN-01");

        var response = await admin.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters/{meter}/quality-flags?{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("fields");
        Assert.True(fields.TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Operators_can_read_flags_but_only_inside_their_organization()
    {
        var (admin, orgs) = await AdminAsync();
        var harborMeter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-MAIN-01");
        var lanternMeter = await MeterIdAsync(admin, orgs["lantern-estates"], "GW-LH-MAIN-01");
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");

        var own = await operatorClient.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters/{harborMeter}/quality-flags");
        var otherOrg = await operatorClient.GetAsync($"/v1/organizations/{orgs["lantern-estates"]}/meters/{lanternMeter}/quality-flags");
        var meterOfOtherOrg = await admin.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters/{lanternMeter}/quality-flags");
        var anonymous = await factory.CreateClient().GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters/{harborMeter}/quality-flags");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherOrg.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, meterOfOtherOrg.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
