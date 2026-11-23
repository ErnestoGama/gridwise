using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridWise.Api.Tests;

public sealed class TelemetryApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // A past day that includes the Europe/Madrid DST change; starts are UTC.
    private static readonly DateTimeOffset Day = new(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);

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

    private static object Reading(Guid meterId, long sequence, int slot, decimal kwh) =>
        new { meterId, sequence, readingStart = Day.AddMinutes(15 * slot), energyKwh = kwh };

    private static Task<HttpResponseMessage> PostBatchAsync(HttpClient client, string organizationId, params object[] readings) =>
        client.PostAsJsonAsync($"/v1/organizations/{organizationId}/telemetry/batches", new { readings });

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<int> StoredCountAsync(Guid meterId, long fromSequence, long toSequence)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GridWiseDbContext>();
        return await db.MeterReadings.CountAsync(r => r.MeterId == meterId && r.Sequence >= fromSequence && r.Sequence <= toSequence);
    }

    [Fact]
    public async Task Admin_ingests_a_batch_and_a_resend_stores_nothing_twice()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-MAIN-01");
        object[] batch = [Reading(meter, 100, 0, 12.5m), Reading(meter, 101, 1, 12.75m), Reading(meter, 102, 2, 0m)];

        var first = await PostBatchAsync(admin, orgs["harbor-demo"], batch);
        var resend = await PostBatchAsync(admin, orgs["harbor-demo"], batch);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await BodyOf(first);
        Assert.Equal(3, firstBody.GetProperty("received").GetInt32());
        Assert.Equal(3, firstBody.GetProperty("inserted").GetInt32());
        Assert.Equal(0, firstBody.GetProperty("duplicates").GetInt32());

        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        var resendBody = await BodyOf(resend);
        Assert.Equal(0, resendBody.GetProperty("inserted").GetInt32());
        Assert.Equal(3, resendBody.GetProperty("duplicates").GetInt32());
        Assert.Empty(resendBody.GetProperty("conflicts").EnumerateArray());

        Assert.Equal(3, await StoredCountAsync(meter, 100, 102));
    }

    [Fact]
    public async Task Changed_values_are_reported_as_conflicts_and_new_readings_still_land()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-HVAC-01");
        await PostBatchAsync(admin, orgs["harbor-demo"], Reading(meter, 200, 10, 4.2m));

        var response = await PostBatchAsync(admin, orgs["harbor-demo"],
            Reading(meter, 200, 10, 9.9m),
            Reading(meter, 299, 10, 4.2m),
            Reading(meter, 201, 11, 4.3m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(1, body.GetProperty("inserted").GetInt32());
        var conflicts = body.GetProperty("conflicts").EnumerateArray().ToList();
        Assert.Equal(new[] { 0, 1 }, conflicts.Select(c => c.GetProperty("index").GetInt32()));
        Assert.Equal(new[] { "sequence_reused", "interval_taken" }, conflicts.Select(c => c.GetProperty("code").GetString()));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GridWiseDbContext>();
        var stored = await db.MeterReadings.SingleAsync(r => r.MeterId == meter && r.Sequence == 200);
        Assert.Equal(4.2m, stored.EnergyKwh);
        Assert.Equal(0, await StoredCountAsync(meter, 299, 299));
    }

    [Fact]
    public async Task Invalid_readings_reject_the_whole_batch()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-QW-MAIN-01");

        var response = await admin.PostAsJsonAsync($"/v1/organizations/{orgs["harbor-demo"]}/telemetry/batches", new
        {
            readings = new object[]
            {
                Reading(meter, 300, 0, 1m),
                new { meterId = meter, sequence = 301, readingStart = Day.AddMinutes(7), energyKwh = 1m },
                Reading(meter, 302, 2, -1m),
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await BodyOf(response)).GetProperty("error");
        Assert.Equal("validation_failed", error.GetProperty("code").GetString());
        var fields = error.GetProperty("fields");
        Assert.True(fields.TryGetProperty("readings[1].readingStart", out _));
        Assert.True(fields.TryGetProperty("readings[2].energyKwh", out _));
        Assert.Equal(0, await StoredCountAsync(meter, 300, 302));
    }

    [Fact]
    public async Task A_meter_of_another_organization_is_not_found()
    {
        var (admin, orgs) = await AdminAsync();
        var lanternMeter = await MeterIdAsync(admin, orgs["lantern-estates"], "GW-LH-MAIN-01");

        var response = await PostBatchAsync(admin, orgs["harbor-demo"], Reading(lanternMeter, 400, 0, 1m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = (await BodyOf(response)).GetProperty("error").GetProperty("fields");
        Assert.Equal("Meter not found.", fields.GetProperty("readings[0].meterId")[0].GetString());
        Assert.Equal(0, await StoredCountAsync(lanternMeter, 400, 400));
    }

    [Fact]
    public async Task Readings_are_stored_against_the_route_organization_in_utc()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-QW-HVAC-01");

        var response = await PostBatchAsync(admin, orgs["harbor-demo"], new
        {
            meterId = meter,
            sequence = 500,
            readingStart = new DateTimeOffset(2026, 3, 28, 19, 15, 0, TimeSpan.FromHours(-5)),
            energyKwh = 2.125m,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GridWiseDbContext>();
        var stored = await db.MeterReadings.SingleAsync(r => r.MeterId == meter && r.Sequence == 500);
        Assert.Equal(Guid.Parse(orgs["harbor-demo"]), stored.OrganizationId);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 0, 15, 0, TimeSpan.Zero), stored.ReadingStart);
        Assert.Equal(TimeSpan.Zero, stored.ReadingStart.Offset);
    }

    [Fact]
    public async Task Empty_and_oversized_batches_get_422()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-MAIN-01");

        var empty = await PostBatchAsync(admin, orgs["harbor-demo"]);
        var oversized = await PostBatchAsync(admin, orgs["harbor-demo"],
            Enumerable.Range(0, 1001).Select(i => Reading(meter, 10_000 + i, i, 1m)).ToArray());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oversized.StatusCode);
        Assert.True((await BodyOf(oversized)).GetProperty("error").GetProperty("fields").TryGetProperty("readings", out _));
    }

    [Fact]
    public async Task Roles_below_admin_get_403_and_non_members_404()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-MAIN-01");
        var lanternMeter = await MeterIdAsync(admin, orgs["lantern-estates"], "GW-LH-MAIN-01");
        var manager = await SignedInClientAsync("manager@gridwise.test");

        var lowRole = await PostBatchAsync(manager, orgs["harbor-demo"], Reading(meter, 600, 0, 1m));
        var notMember = await PostBatchAsync(manager, orgs["lantern-estates"], Reading(lanternMeter, 600, 0, 1m));
        var anonymous = await PostBatchAsync(factory.CreateClient(), orgs["harbor-demo"], Reading(meter, 600, 0, 1m));

        Assert.Equal(HttpStatusCode.Forbidden, lowRole.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, await StoredCountAsync(meter, 600, 600));
    }

    private static Task<HttpResponseMessage> PostKeyedBatchAsync(HttpClient client, string organizationId, string key, params object[] readings)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/organizations/{organizationId}/telemetry/batches")
        {
            Content = JsonContent.Create(new { readings }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private async Task<int> BatchCountAsync(string organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GridWiseDbContext>();
        return await db.TelemetryBatches.CountAsync(b => b.OrganizationId == Guid.Parse(organizationId));
    }

    [Fact]
    public async Task Each_batch_is_recorded_with_its_conflicts_for_analysts_to_review()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-HVAC-01");
        await PostBatchAsync(admin, orgs["harbor-demo"], Reading(meter, 700, 20, 3m));

        var response = await PostBatchAsync(admin, orgs["harbor-demo"],
            Reading(meter, 700, 20, 3m),
            Reading(meter, 700, 21, 3m),
            Reading(meter, 701, 22, 3.1m));
        var batchId = (await BodyOf(response)).GetProperty("batchId").GetGuid();

        var analyst = await SignedInClientAsync("analyst@gridwise.test");
        var detail = await analyst.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/telemetry/batches/{batchId}");

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var body = await BodyOf(detail);
        Assert.Equal(3, body.GetProperty("received").GetInt32());
        Assert.Equal(1, body.GetProperty("inserted").GetInt32());
        Assert.Equal(1, body.GetProperty("duplicates").GetInt32());
        Assert.False(body.GetProperty("hasIdempotencyKey").GetBoolean());
        var conflict = Assert.Single(body.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(1, conflict.GetProperty("index").GetInt32());
        Assert.Equal("sequence_reused", conflict.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(conflict.GetProperty("message").GetString()));

        var list = await analyst.GetFromJsonAsync<JsonElement>($"/v1/organizations/{orgs["harbor-demo"]}/telemetry/batches?limit=1");
        Assert.Equal(batchId, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Batch_history_is_paged_newest_first()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["lantern-estates"], "GW-LH-MAIN-01");
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var response = await PostBatchAsync(admin, orgs["lantern-estates"], Reading(meter, 800 + i, 30 + i, 1m));
            ids.Add((await BodyOf(response)).GetProperty("batchId").GetGuid());
        }

        var url = $"/v1/organizations/{orgs["lantern-estates"]}/telemetry/batches?limit=2";
        var first = await admin.GetFromJsonAsync<JsonElement>(url);
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await admin.GetFromJsonAsync<JsonElement>($"{url}&cursor={cursor}");

        var seen = first.GetProperty("items").EnumerateArray()
            .Concat(second.GetProperty("items").EnumerateArray())
            .Select(b => b.GetProperty("id").GetGuid())
            .ToList();
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Equal(ids.AsEnumerable().Reverse(), seen.Take(3));

        var badCursor = await admin.GetAsync($"{url}&cursor=bm90LWEtZ3VpZA");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCursor.StatusCode);
    }

    [Fact]
    public async Task A_keyed_retry_returns_the_original_result_without_ingesting_again()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-QW-MAIN-01");
        object[] batch = [Reading(meter, 900, 40, 5.5m), Reading(meter, 901, 41, 5.25m)];

        var first = await PostKeyedBatchAsync(admin, orgs["harbor-demo"], "gw-hh-2026-03-29:900", batch);
        var batches = await BatchCountAsync(orgs["harbor-demo"]);
        var retry = await PostKeyedBatchAsync(admin, orgs["harbor-demo"], "gw-hh-2026-03-29:900", batch);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotent-Replayed")));

        var firstBody = await BodyOf(first);
        var retryBody = await BodyOf(retry);
        Assert.Equal(firstBody.GetProperty("batchId").GetGuid(), retryBody.GetProperty("batchId").GetGuid());
        Assert.Equal(2, retryBody.GetProperty("inserted").GetInt32());
        Assert.Equal(0, retryBody.GetProperty("duplicates").GetInt32());
        Assert.Equal(batches, await BatchCountAsync(orgs["harbor-demo"]));
        Assert.Equal(2, await StoredCountAsync(meter, 900, 901));
    }

    [Fact]
    public async Task A_key_reused_for_different_readings_gets_409_and_stores_nothing()
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-QW-HVAC-01");
        await PostKeyedBatchAsync(admin, orgs["harbor-demo"], "reused-key", Reading(meter, 1000, 50, 1m));

        var response = await PostKeyedBatchAsync(admin, orgs["harbor-demo"], "reused-key", Reading(meter, 1001, 51, 1m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_key_reused", (await BodyOf(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, await StoredCountAsync(meter, 1001, 1001));
    }

    [Fact]
    public async Task The_same_key_in_another_organization_is_independent()
    {
        var (admin, orgs) = await AdminAsync();
        var harborMeter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-MAIN-01");
        var lanternMeter = await MeterIdAsync(admin, orgs["lantern-estates"], "GW-LH-MAIN-01");

        var harbor = await PostKeyedBatchAsync(admin, orgs["harbor-demo"], "shared-key", Reading(harborMeter, 1100, 60, 1m));
        var lantern = await PostKeyedBatchAsync(admin, orgs["lantern-estates"], "shared-key", Reading(lanternMeter, 1100, 60, 1m));

        Assert.Equal(HttpStatusCode.OK, harbor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lantern.StatusCode);
        Assert.False(lantern.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, (await BodyOf(lantern)).GetProperty("inserted").GetInt32());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("bad/slash")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")]
    public async Task Malformed_idempotency_keys_get_422(string key)
    {
        var (admin, orgs) = await AdminAsync();
        var meter = await MeterIdAsync(admin, orgs["harbor-demo"], "GW-HH-MAIN-01");

        var response = await PostKeyedBatchAsync(admin, orgs["harbor-demo"], key, Reading(meter, 1200, 70, 1m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("error").GetProperty("fields").TryGetProperty("idempotencyKey", out _));
        Assert.Equal(0, await StoredCountAsync(meter, 1200, 1200));
    }

    [Fact]
    public async Task Batch_history_needs_analyst_and_stays_inside_the_organization()
    {
        var (admin, orgs) = await AdminAsync();
        var lanternMeter = await MeterIdAsync(admin, orgs["lantern-estates"], "GW-LH-MAIN-01");
        var lanternBatch = (await BodyOf(await PostBatchAsync(admin, orgs["lantern-estates"], Reading(lanternMeter, 1300, 80, 1m))))
            .GetProperty("batchId").GetGuid();
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");
        var analyst = await SignedInClientAsync("analyst@gridwise.test");

        var operatorList = await operatorClient.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/telemetry/batches");
        var otherOrgList = await analyst.GetAsync($"/v1/organizations/{orgs["lantern-estates"]}/telemetry/batches");
        var crossTenantDetail = await admin.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/telemetry/batches/{lanternBatch}");

        Assert.Equal(HttpStatusCode.Forbidden, operatorList.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherOrgList.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantDetail.StatusCode);
    }
}
