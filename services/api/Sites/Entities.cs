using System.Text.RegularExpressions;
using GridWise.Api.Identity;

namespace GridWise.Api.Sites;

/// <summary>A building owned by one organization. Its time zone drives local-time reports and tariffs.</summary>
public sealed class Building : ITimestamped
{
    public const int NameMaxLength = 120;
    public const int TimeZoneMaxLength = 64;
    public const int MaxFloorAreaM2 = 2_000_000;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }

    /// <summary>IANA time zone ID, e.g. <c>Europe/Madrid</c>.</summary>
    public required string TimeZone { get; set; }

    public int? FloorAreaM2 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public List<Meter> Meters { get; } = [];
}

/// <summary>
/// An electricity meter in a building. Meters report energy in kWh per fixed
/// interval; readings arrive later through telemetry ingestion.
/// </summary>
public sealed class Meter : ITimestamped
{
    public const int SerialMaxLength = 40;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Copied from the building; the composite foreign key keeps both in the same organization.</summary>
    public Guid OrganizationId { get; set; }

    public Guid BuildingId { get; set; }

    /// <summary>Uppercase serial, unique within the organization.</summary>
    public required string Serial { get; set; }

    public required string Kind { get; set; }
    public int IntervalMinutes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Building Building { get; set; } = null!;
}

public static partial class MeterRules
{
    /// <summary>What a meter measures. Same list as the <c>ck_meters_kind</c> constraint.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["main", "hvac", "lighting", "plug", "process"];

    /// <summary>Supported reading intervals in minutes. Same list as <c>ck_meters_interval_minutes</c>.</summary>
    public static readonly IReadOnlyList<int> IntervalMinutes = [5, 10, 15, 30, 60];

    public static string NormalizeSerial(string serial) => serial.Trim().ToUpperInvariant();

    /// <summary>Same rule as the <c>ck_meters_serial</c> constraint; expects a normalized serial.</summary>
    public static bool IsValidSerial(string? serial) =>
        !string.IsNullOrEmpty(serial) && serial.Length <= Meter.SerialMaxLength && SerialPattern().IsMatch(serial);

    // \z rather than $: in .NET, $ also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9-]{2,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();
}

public static class BuildingRules
{
    /// <summary>
    /// True for IANA time zone IDs known to this machine (e.g. <c>America/Bogota</c>, <c>UTC</c>).
    /// Windows IDs such as <c>Romance Standard Time</c> are rejected so stored values work everywhere.
    /// </summary>
    public static bool IsValidTimeZone(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= Building.TimeZoneMaxLength
        && id == id.Trim()
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
        && zone.HasIanaId
        && zone.Id == id;
}
