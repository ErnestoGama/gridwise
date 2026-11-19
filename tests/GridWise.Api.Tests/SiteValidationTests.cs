using GridWise.Api.Http;
using GridWise.Api.Sites;

namespace GridWise.Api.Tests;

public sealed class SiteValidationTests
{
    [Theory]
    [InlineData("Europe/Madrid", true)]
    [InlineData("America/Bogota", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("Romance Standard Time", false)]
    [InlineData(" Europe/Madrid", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_known_iana_time_zones_are_accepted(string? id, bool expected) =>
        Assert.Equal(expected, BuildingRules.IsValidTimeZone(id));

    [Theory]
    [InlineData("GW-104233", true)]
    [InlineData("ABC", true)]
    [InlineData("AB", false)]
    [InlineData("-GW1", false)]
    [InlineData("GW 1", false)]
    [InlineData("gw-1", false)]
    [InlineData("GW-1\n", false)]
    public void Serial_rule_matches_the_database_constraint(string serial, bool expected) =>
        Assert.Equal(expected, MeterRules.IsValidSerial(serial));

    [Fact]
    public void Serials_are_trimmed_and_uppercased() =>
        Assert.Equal("GW-104233", MeterRules.NormalizeSerial("  gw-104233 "));

    [Fact]
    public void Building_request_reports_every_invalid_field()
    {
        var errors = SiteEndpoints.Validate(new CreateBuildingRequest("   ", "Nowhere/Town", 0));

        Assert.Equal(new[] { "floorAreaM2", "name", "timeZone" }, errors.Keys.Order());
    }

    [Fact]
    public void Building_name_longer_than_the_limit_is_rejected()
    {
        var errors = SiteEndpoints.Validate(new CreateBuildingRequest(new string('a', Building.NameMaxLength + 1), "UTC", null));

        Assert.Equal("name", Assert.Single(errors.Keys));
    }

    [Fact]
    public void Meter_request_reports_every_invalid_field()
    {
        var errors = SiteEndpoints.Validate(new CreateMeterRequest(Guid.Empty, "x", "solar", 7));

        Assert.Equal(new[] { "buildingId", "intervalMinutes", "kind", "serial" }, errors.Keys.Order());
    }

    [Fact]
    public void Meter_interval_is_optional() =>
        Assert.Empty(SiteEndpoints.Validate(new CreateMeterRequest(Guid.NewGuid(), "GW-1001", "hvac", null)));

    [Theory]
    [InlineData("Harbor Hall")]
    [InlineData("Ñandú Annex – planta 2")]
    public void Cursors_round_trip(string key) =>
        Assert.Equal(key, Paging.Decode(Paging.Encode(key)));

    [Theory]
    [InlineData("%%%")]
    [InlineData("_w")]
    public void Malformed_cursors_are_rejected(string cursor) =>
        Assert.Null(Paging.Decode(cursor));

    [Fact]
    public void A_page_has_a_cursor_only_when_more_rows_exist()
    {
        var full = Paging.Build(new[] { "a", "b", "c" }, 2, s => s);
        var last = Paging.Build(new[] { "a", "b" }, 2, s => s);

        Assert.Equal(new[] { "a", "b" }, full.Items);
        Assert.Equal("b", Paging.Decode(full.NextCursor!));
        Assert.Null(last.NextCursor);
    }
}
