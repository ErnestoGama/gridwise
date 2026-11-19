using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GridWise.Api.Tests;

public sealed class SitesApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<Dictionary<string, string>> OrganizationIdsAsync()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        return (await admin.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("memberships").EnumerateArray()
            .ToDictionary(m => m.GetProperty("organizationSlug").GetString()!, m => m.GetProperty("organizationId").GetString()!);
    }

    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");

    private static string UniqueName(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..12]}";

    [Fact]
    public async Task Members_list_their_own_seeded_buildings_with_meter_counts()
    {
        var orgs = await OrganizationIdsAsync();
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");

        var page = await operatorClient.GetFromJsonAsync<JsonElement>($"/v1/organizations/{orgs["harbor-demo"]}/buildings");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        var hall = Assert.Single(items, b => b.GetProperty("name").GetString() == "Harbor Hall");
        Assert.Equal("Europe/Madrid", hall.GetProperty("timeZone").GetString());
        Assert.Equal(2, hall.GetProperty("meterCount").GetInt32());
        Assert.DoesNotContain(items, b => b.GetProperty("name").GetString() == "Lantern House");
    }

    [Fact]
    public async Task Non_members_get_404_for_another_organizations_buildings_and_meters()
    {
        var orgs = await OrganizationIdsAsync();
        var analyst = await SignedInClientAsync("analyst@gridwise.test");

        var buildings = await analyst.GetAsync($"/v1/organizations/{orgs["lantern-estates"]}/buildings");
        var meters = await analyst.GetAsync($"/v1/organizations/{orgs["lantern-estates"]}/meters");

        Assert.Equal(HttpStatusCode.NotFound, buildings.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, meters.StatusCode);
        Assert.Equal("not_found", (await ErrorOf(buildings)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        var orgs = await OrganizationIdsAsync();

        var response = await factory.CreateClient().GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/buildings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("operator@gridwise.test")]
    [InlineData("manager@gridwise.test")]
    public async Task Only_admins_can_register_buildings(string email)
    {
        var orgs = await OrganizationIdsAsync();
        var client = await SignedInClientAsync(email);

        var response = await client.PostAsJsonAsync($"/v1/organizations/{orgs["harbor-demo"]}/buildings",
            new { name = UniqueName("Denied"), timeZone = "Europe/Madrid" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", (await ErrorOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_registers_a_building_and_a_meter_then_reads_them_back()
    {
        var orgs = await OrganizationIdsAsync();
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var name = UniqueName("Willow Annex");

        var created = await admin.PostAsJsonAsync($"/v1/organizations/{orgs["harbor-demo"]}/buildings",
            new { name = $"  {name} ", timeZone = "America/Chicago", floorAreaM2 = 4200 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var building = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, building.GetProperty("name").GetString());
        var buildingId = building.GetProperty("id").GetString();
        Assert.EndsWith($"/buildings/{buildingId}", created.Headers.Location!.ToString());

        var meterResponse = await admin.PostAsJsonAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters",
            new { buildingId, serial = "gw-wa-main-" + Guid.NewGuid().ToString("N")[..6], kind = "main" });
        Assert.Equal(HttpStatusCode.Created, meterResponse.StatusCode);
        var meter = await meterResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("GW-WA-MAIN-", meter.GetProperty("serial").GetString());
        Assert.Equal(15, meter.GetProperty("intervalMinutes").GetInt32());

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/v1/organizations/{orgs["harbor-demo"]}/buildings/{buildingId}");
        Assert.Equal(4200, detail.GetProperty("floorAreaM2").GetInt32());
        Assert.Equal(meter.GetProperty("id").GetString(), Assert.Single(detail.GetProperty("meters").EnumerateArray()).GetProperty("id").GetString());

        var fetched = await admin.GetAsync(meterResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task Duplicate_building_names_and_meter_serials_return_409()
    {
        var orgs = await OrganizationIdsAsync();
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var harbor = orgs["harbor-demo"];

        var building = await admin.PostAsJsonAsync($"/v1/organizations/{harbor}/buildings",
            new { name = "Harbor Hall", timeZone = "Europe/Madrid" });
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/v1/organizations/{harbor}/buildings");
        var hallId = detail.GetProperty("items").EnumerateArray()
            .Single(b => b.GetProperty("name").GetString() == "Harbor Hall").GetProperty("id").GetString();
        var meter = await admin.PostAsJsonAsync($"/v1/organizations/{harbor}/meters",
            new { buildingId = hallId, serial = "gw-hh-main-01", kind = "main" });

        Assert.Equal(HttpStatusCode.Conflict, building.StatusCode);
        Assert.Equal("building_name_taken", (await ErrorOf(building)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, meter.StatusCode);
        Assert.Equal("meter_serial_taken", (await ErrorOf(meter)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Same_serial_is_allowed_in_a_different_organization()
    {
        var orgs = await OrganizationIdsAsync();
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var lantern = orgs["lantern-estates"];
        var page = await admin.GetFromJsonAsync<JsonElement>($"/v1/organizations/{lantern}/buildings");
        var houseId = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString();

        var response = await admin.PostAsJsonAsync($"/v1/organizations/{lantern}/meters",
            new { buildingId = houseId, serial = "GW-HH-MAIN-01", kind = "lighting", intervalMinutes = 30 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_meter_cannot_be_placed_in_another_organizations_building()
    {
        var orgs = await OrganizationIdsAsync();
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var lanternPage = await admin.GetFromJsonAsync<JsonElement>($"/v1/organizations/{orgs["lantern-estates"]}/buildings");
        var lanternBuilding = lanternPage.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();

        var response = await admin.PostAsJsonAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters",
            new { buildingId = lanternBuilding, serial = "GW-CROSS-01", kind = "main" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = (await ErrorOf(response)).GetProperty("fields");
        Assert.Equal("Building not found.", fields.GetProperty("buildingId")[0].GetString());
    }

    [Fact]
    public async Task Invalid_building_returns_422_with_field_errors()
    {
        var orgs = await OrganizationIdsAsync();
        var admin = await SignedInClientAsync("admin@gridwise.test");

        var response = await admin.PostAsJsonAsync($"/v1/organizations/{orgs["harbor-demo"]}/buildings",
            new { name = "", timeZone = "Central European Standard Time", floorAreaM2 = -5 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = (await ErrorOf(response)).GetProperty("fields");
        Assert.True(fields.TryGetProperty("name", out _));
        Assert.True(fields.TryGetProperty("timeZone", out _));
        Assert.True(fields.TryGetProperty("floorAreaM2", out _));
    }

    [Fact]
    public async Task Buildings_page_through_with_a_cursor_in_name_order()
    {
        var orgs = await OrganizationIdsAsync();
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var url = $"/v1/organizations/{orgs["harbor-demo"]}/buildings";

        var names = new List<string>();
        string? cursor = null;
        do
        {
            var page = await admin.GetFromJsonAsync<JsonElement>(cursor is null ? $"{url}?limit=1" : $"{url}?limit=1&cursor={cursor}");
            names.AddRange(page.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("name").GetString()!));
            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
        }
        while (cursor is not null && names.Count < 100);

        Assert.Contains("Harbor Hall", names);
        Assert.Contains("Quarry Works", names);
        Assert.Equal(names.Distinct().Count(), names.Count);
        Assert.Equal(names.Order().ToList(), names);
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=101", "limit")]
    [InlineData("?cursor=%25%25", "cursor")]
    public async Task Invalid_paging_parameters_return_422(string query, string field)
    {
        var orgs = await OrganizationIdsAsync();
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");

        var response = await operatorClient.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await ErrorOf(response)).GetProperty("fields").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Meters_can_be_filtered_by_building()
    {
        var orgs = await OrganizationIdsAsync();
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");
        var harbor = orgs["harbor-demo"];
        var page = await operatorClient.GetFromJsonAsync<JsonElement>($"/v1/organizations/{harbor}/buildings");
        var quarryId = page.GetProperty("items").EnumerateArray()
            .Single(b => b.GetProperty("name").GetString() == "Quarry Works").GetProperty("id").GetString();

        var meters = await operatorClient.GetFromJsonAsync<JsonElement>($"/v1/organizations/{harbor}/meters?buildingId={quarryId}");

        Assert.Equal(new string?[] { "GW-QW-HVAC-01", "GW-QW-MAIN-01" },
            meters.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("serial").GetString()));
    }

    [Fact]
    public async Task Unknown_building_and_meter_return_404()
    {
        var orgs = await OrganizationIdsAsync();
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");

        var building = await operatorClient.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/buildings/{Guid.NewGuid()}");
        var meter = await operatorClient.GetAsync($"/v1/organizations/{orgs["harbor-demo"]}/meters/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, building.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, meter.StatusCode);
    }
}
