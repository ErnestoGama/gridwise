using System.Globalization;
using GridWise.Api.Telemetry;

namespace GridWise.Api.Tests;

public sealed class ReadingQualityTests
{
    private static readonly Guid Meter = Guid.Parse("0193a4c0-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Day = new(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Readings at the given 15-minute slots of <see cref="Day"/>; sequence = slot.</summary>
    private static List<StoredReading> Slots(IEnumerable<int> slots, Func<int, decimal>? kwh = null) =>
        slots.Select(s => new StoredReading(Meter, s, Day.AddMinutes(15 * s), kwh?.Invoke(s) ?? 2m)).ToList();

    private static decimal Kwh(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static IReadOnlyList<QualityFinding> Evaluate(IEnumerable<StoredReading> readings, int fromSlot = 0, int toSlot = 1000) =>
        ReadingQuality.Evaluate(readings, 15, Day.AddMinutes(15 * fromSlot), Day.AddMinutes(15 * toSlot));

    [Fact]
    public void Missing_intervals_between_readings_are_one_gap_and_never_a_zero_reading()
    {
        var readings = Slots([0, 1, 2, 10, 11]);

        var gap = Assert.Single(Evaluate(readings));

        Assert.Equal(ReadingQuality.Gap, gap.Kind);
        Assert.Equal(Day.AddMinutes(15 * 3), gap.IntervalStart);
        Assert.Equal(Day.AddMinutes(15 * 10), gap.IntervalEnd);
        Assert.Equal(7, gap.MissingIntervals);
        Assert.Null(gap.ObservedKwh);
        Assert.Null(gap.Sequence);
    }

    [Fact]
    public void Contiguous_readings_and_the_time_after_the_last_reading_are_not_gaps()
    {
        Assert.Empty(Evaluate(Slots(Enumerable.Range(0, 20))));
    }

    [Fact]
    public void A_reading_far_above_the_last_day_is_a_spike_with_its_baseline()
    {
        var readings = Slots(Enumerable.Range(0, 12), s => s == 10 ? 50m : 2m);

        var spike = Assert.Single(Evaluate(readings));

        Assert.Equal(ReadingQuality.Spike, spike.Kind);
        Assert.Equal(Day.AddMinutes(150), spike.IntervalStart);
        Assert.Equal(Day.AddMinutes(165), spike.IntervalEnd);
        Assert.Equal(10, spike.Sequence);
        Assert.Equal(50m, spike.ObservedKwh);
        Assert.Equal(2m, spike.BaselineKwh);
    }

    [Fact]
    public void Too_little_history_gives_no_baseline_and_no_spike()
    {
        var readings = Slots(Enumerable.Range(0, ReadingQuality.MinBaselineReadings + 1),
            s => s == ReadingQuality.MinBaselineReadings - 1 ? 50m : 2m);

        Assert.Empty(Evaluate(readings));
    }

    [Theory]
    [InlineData("8", "2", true)]
    [InlineData("7.999", "2", false)]
    [InlineData("1", "0", true)]
    [InlineData("0.9", "0", false)]
    [InlineData("1.1", "0.2", false)]
    public void Spikes_need_both_the_factor_and_the_minimum_increase(string kwh, string baseline, bool expected) =>
        Assert.Equal(expected, ReadingQuality.IsSpike(Kwh(kwh), Kwh(baseline)));

    [Fact]
    public void Readings_older_than_the_look_back_are_not_part_of_the_baseline()
    {
        // 96 slots = 24 hours. Slots 0–9 are high but more than a day before slot 106.
        var readings = Slots(Enumerable.Range(0, 107), s => s < 10 ? 40m : s == 106 ? 40m : 2m);

        var spike = Assert.Single(Evaluate(readings, fromSlot: 100));

        Assert.Equal(106, spike.Sequence);
        Assert.Equal(2m, spike.BaselineKwh);
    }

    [Fact]
    public void Only_flags_starting_inside_the_range_are_returned()
    {
        var readings = Slots([0, 5, 6, 20, 30]);

        var flags = Evaluate(readings, fromSlot: 5, toSlot: 25);

        Assert.Equal(new[] { Day.AddMinutes(15 * 7), Day.AddMinutes(15 * 21) }, flags.Select(f => f.IntervalStart));
    }

    [Fact]
    public void Results_do_not_depend_on_input_order()
    {
        var readings = Slots([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 14, 15], s => s == 15 ? 30m : 2m);
        var shuffled = readings.AsEnumerable().Reverse().ToList();

        Assert.Equal(Evaluate(readings), Evaluate(shuffled));
        Assert.Equal(new[] { ReadingQuality.Gap, ReadingQuality.Spike }, Evaluate(readings).Select(f => f.Kind));
    }

    [Theory]
    [InlineData(new[] { "1" }, "1")]
    [InlineData(new[] { "3", "1", "2" }, "2")]
    [InlineData(new[] { "1", "2", "3", "4" }, "2.5")]
    [InlineData(new[] { "0.001", "0.002" }, "0.002")]
    public void Median_is_rounded_to_stored_precision(string[] values, string expected) =>
        Assert.Equal(Kwh(expected), ReadingQuality.Median(values.Select(Kwh)));

    [Fact]
    public void Affected_range_starts_at_the_previous_reading_and_covers_the_look_back_after()
    {
        var first = Day.AddHours(2);
        var last = Day.AddHours(3);

        var (from, to) = ReadingQuality.AffectedRange(first, last, Day, 15);
        var (fromWithoutHistory, _) = ReadingQuality.AffectedRange(first, last, null, 15);

        Assert.Equal(Day, from);
        Assert.Equal(last + ReadingQuality.SpikeLookback + TimeSpan.FromMinutes(15), to);
        Assert.Equal(first, fromWithoutHistory);
    }

    [Fact]
    public void Unsupported_intervals_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReadingQuality.Evaluate(Slots([0, 1]), 7, Day, Day.AddDays(1)));
    }
}
