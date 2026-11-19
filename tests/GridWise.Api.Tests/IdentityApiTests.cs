using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GridWise.Api.Tests;

public sealed class IdentityApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("error");
    }

    [Fact]
    public async Task Login_returns_profile_and_sets_a_strict_http_only_session_cookie()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/auth/login",
            new { email = "operator@gridwise.test", password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("gridwise.session="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("operator@gridwise.test", me.GetProperty("email").GetString());
        Assert.False(me.TryGetProperty("passwordHash", out _));
        var membership = Assert.Single(me.GetProperty("memberships").EnumerateArray());
        Assert.Equal("harbor-demo", membership.GetProperty("organizationSlug").GetString());
        Assert.Equal("operator", membership.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Email_is_matched_case_insensitively()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/v1/auth/login",
            new { email = "Manager@GridWise.test", password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("analyst@gridwise.test", "wrong-password")]
    [InlineData("nobody@gridwise.test", ApiFactory.Password)]
    public async Task Wrong_password_and_unknown_email_get_the_same_401(string email, string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/v1/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("invalid_credentials", error.GetProperty("code").GetString());
        Assert.Equal("The email or password is incorrect.", error.GetProperty("message").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Missing_and_invalid_fields_return_422_with_field_errors()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/v1/auth/login", new { email = "not-an-email" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("validation_failed", error.GetProperty("code").GetString());
        var fields = error.GetProperty("fields");
        Assert.True(fields.TryGetProperty("email", out _));
        Assert.True(fields.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Malformed_json_returns_400_envelope()
    {
        using var content = new StringContent("{\"email\":", Encoding.UTF8, "application/json");
        var response = await factory.CreateClient().PostAsync("/v1/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await ErrorOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Me_without_a_session_returns_401_json_instead_of_a_redirect()
    {
        var response = await factory.CreateClient().GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("unauthenticated", error.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Request-ID").Single(), error.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var client = await SignedInClientAsync("manager@gridwise.test");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Admin_membership_list_covers_both_demo_organizations()
    {
        var admin = await (await SignedInClientAsync("admin@gridwise.test")).GetFromJsonAsync<JsonElement>("/v1/me");
        var slugs = admin.GetProperty("memberships").EnumerateArray()
            .Select(m => m.GetProperty("organizationSlug").GetString()).ToArray();

        Assert.Equal(new string?[] { "harbor-demo", "lantern-estates" }, slugs);
    }

    [Fact]
    public async Task Organization_is_visible_to_members_and_hidden_from_everyone_else()
    {
        var admin = await SignedInClientAsync("admin@gridwise.test");
        var orgIds = (await admin.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("memberships").EnumerateArray()
            .ToDictionary(m => m.GetProperty("organizationSlug").GetString()!, m => m.GetProperty("organizationId").GetString()!);
        var operatorClient = await SignedInClientAsync("operator@gridwise.test");

        var own = await operatorClient.GetAsync($"/v1/organizations/{orgIds["harbor-demo"]}");
        var other = await operatorClient.GetAsync($"/v1/organizations/{orgIds["lantern-estates"]}");
        var missing = await operatorClient.GetAsync($"/v1/organizations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("operator", (await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await ErrorOf(other)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("trace-abc_123.4", "trace-abc_123.4")]
    [InlineData("bad id with spaces", null)]
    public async Task Request_id_is_echoed_when_well_formed_and_replaced_otherwise(string sent, string? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.TryAddWithoutValidation("X-Request-ID", sent);

        var response = await factory.CreateClient().SendAsync(request);

        var echoed = response.Headers.GetValues("X-Request-ID").Single();
        if (expected is null)
        {
            Assert.NotEqual(sent, echoed);
            Assert.Matches("^[0-9a-f]{32}$", echoed);
        }
        else
        {
            Assert.Equal(expected, echoed);
        }
    }
}
