using GridWise.Api.Sites;

namespace GridWise.Api.Telemetry;

/// <summary>
/// Energy used by one meter during one interval. Readings are immutable once
/// stored: corrections arrive as quality flags, never as updates. A missing
/// interval has no row (unknown), it is never stored as zero.
/// </summary>
public sealed class MeterReading
{
    /// <summary>Copied from the meter; the composite foreign key keeps both in the same organization.</summary>
    public Guid OrganizationId { get; set; }

    public Guid MeterId { get; set; }

    /// <summary>UTC start of the interval, aligned to the meter's interval. Hypertable time column.</summary>
    public DateTimeOffset ReadingStart { get; set; }

    /// <summary>Interval counter sent by the meter; used to detect resent readings.</summary>
    public long Sequence { get; set; }

    /// <summary>Energy used in the interval, in kWh with three decimals.</summary>
    public decimal EnergyKwh { get; set; }

    /// <summary>When the API stored the reading. Set by the database.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public Meter Meter { get; set; } = null!;
}

public static class ReadingRules
{
    /// <summary>Upper bound for one interval, matching <c>numeric(12,3)</c> and the <c>ck_meter_readings_energy_kwh</c> check.</summary>
    public const decimal MaxEnergyKwh = 999_999_999.999m;

    /// <summary>Same rule as <c>ck_meter_readings_energy_kwh</c>: non-negative, at most three decimals.</summary>
    public static bool IsValidEnergy(decimal kwh) =>
        kwh >= 0 && kwh <= MaxEnergyKwh && decimal.Round(kwh, 3) == kwh;

    /// <summary>
    /// True when <paramref name="start"/> is in UTC and falls on a boundary of the
    /// meter interval (e.g. :00, :15, :30, :45 for 15 minutes). All supported
    /// intervals divide an hour, so the minute of the hour is enough.
    /// </summary>
    public static bool IsAlignedStart(DateTimeOffset start, int intervalMinutes) =>
        MeterRules.IntervalMinutes.Contains(intervalMinutes)
        && start.Offset == TimeSpan.Zero
        && start.Ticks % TimeSpan.TicksPerMinute == 0
        && start.Minute % intervalMinutes == 0;
}
