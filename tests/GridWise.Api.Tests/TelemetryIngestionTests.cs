using System.Globalization;
using GridWise.Api.Telemetry;

namespace GridWise.Api.Tests;

public sealed class TelemetryIngestionTests
{
    private static readonly Guid Meter = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid OtherMeter = Guid.Parse("0193a000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Start = new(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 3, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<Guid, int> Intervals = new Dictionary<Guid, int> { [Meter] = 15 };

    private static ValidReading Valid(int index, long sequence, int slot, decimal kwh, Guid? meter = null) =>
        new(index, meter ?? Meter, sequence, Start.AddMinutes(15 * slot), kwh);

    private static StoredReading Stored(long sequence, int slot, decimal kwh) =>
        new(Meter, sequence, Start.AddMinutes(15 * slot), kwh);

    [Fact]
    public void New_readings_are_all_inserted()
    {
        var plan = TelemetryIngestion.Plan([Valid(0, 10, 0, 1.5m), Valid(1, 11, 1, 1.6m)], []);

        Assert.Equal(2, plan.Inserts.Count);
        Assert.Equal(0, plan.Duplicates);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void An_identical_resend_is_a_duplicate_not_an_insert()
    {
        var plan = TelemetryIngestion.Plan([Valid(0, 10, 0, 1.5m), Valid(1, 11, 1, 1.6m)], [Stored(10, 0, 1.5m)]);

        Assert.Equal(11, Assert.Single(plan.Inserts).Sequence);
        Assert.Equal(1, plan.Duplicates);
        Assert.Empty(plan.Conflicts);
    }

    [Theory]
    [InlineData(0, "1.501")]
    [InlineData(1, "1.5")]
    public void A_stored_sequence_sent_with_other_values_is_a_conflict(int slot, string kwh)
    {
        var plan = TelemetryIngestion.Plan([Valid(0, 10, slot, decimal.Parse(kwh, CultureInfo.InvariantCulture))], [Stored(10, 0, 1.5m)]);

        Assert.Empty(plan.Inserts);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(TelemetryIngestion.SequenceReused, conflict.Code);
        Assert.Equal(0, conflict.Index);
    }

    [Fact]
    public void A_taken_interval_with_another_sequence_is_a_conflict()
    {
        var plan = TelemetryIngestion.Plan([Valid(0, 99, 0, 1.5m)], [Stored(10, 0, 1.5m)]);

        Assert.Empty(plan.Inserts);
        Assert.Equal(TelemetryIngestion.IntervalTaken, Assert.Single(plan.Conflicts).Code);
    }

    [Fact]
    public void Repeats_inside_one_batch_are_resolved_in_order()
    {
        var plan = TelemetryIngestion.Plan(
            [Valid(0, 10, 0, 1.5m), Valid(1, 10, 0, 1.5m), Valid(2, 10, 0, 2m), Valid(3, 12, 0, 1.5m)], []);

        Assert.Equal(0, Assert.Single(plan.Inserts).Index);
        Assert.Equal(1, plan.Duplicates);
        Assert.Equal(new[] { 2, 3 }, plan.Conflicts.Select(c => c.Index));
        Assert.Equal(new[] { TelemetryIngestion.SequenceReused, TelemetryIngestion.IntervalTaken }, plan.Conflicts.Select(c => c.Code));
    }

    [Fact]
    public void The_same_sequence_on_two_meters_does_not_clash()
    {
        var plan = TelemetryIngestion.Plan([Valid(0, 10, 0, 1.5m), Valid(1, 10, 0, 1.5m, OtherMeter)], []);

        Assert.Equal(2, plan.Inserts.Count);
    }

    [Fact]
    public void Valid_readings_are_normalized_to_utc()
    {
        var request = new TelemetryBatchRequest(
            [new ReadingInput(Meter, 5, new DateTimeOffset(2026, 3, 29, 3, 15, 0, TimeSpan.FromHours(2)), 0.25m)]);

        var (errors, readings) = TelemetryIngestion.Validate(request, Intervals, Now);

        Assert.Empty(errors);
        var reading = Assert.Single(readings);
        Assert.Equal(TimeSpan.Zero, reading.ReadingStart.Offset);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 15, 0, TimeSpan.Zero), reading.ReadingStart);
    }

    [Fact]
    public void Each_invalid_field_is_reported_by_index()
    {
        var request = new TelemetryBatchRequest(
        [
            new ReadingInput(Meter, 1, Start.AddMinutes(7), 1m),
            new ReadingInput(Meter, -1, Start, -0.5m),
            new ReadingInput(OtherMeter, 2, Start, 1m),
            new ReadingInput(null, null, null, null),
            null,
            new ReadingInput(Meter, 3, Now, 1m),
            new ReadingInput(Meter, 4, Start, 1.2345m),
        ]);

        var (errors, readings) = TelemetryIngestion.Validate(request, Intervals, Now);

        Assert.Empty(readings);
        Assert.Equal(
            new[]
            {
                "readings[0].readingStart", "readings[1].sequence", "readings[1].energyKwh", "readings[2].meterId",
                "readings[3].meterId", "readings[3].sequence", "readings[3].readingStart", "readings[3].energyKwh",
                "readings[4]", "readings[5].readingStart", "readings[6].energyKwh",
            }.Order(),
            errors.Keys.Order());
        Assert.Equal("Meter not found.", errors["readings[2].meterId"][0]);
        Assert.Equal("Reading interval has not ended yet.", errors["readings[5].readingStart"][0]);
    }

    [Fact]
    public void An_interval_that_ended_within_the_clock_skew_is_accepted()
    {
        // The interval ends three minutes after "now", inside the five-minute skew.
        var now = Start.AddMinutes(12);
        var request = new TelemetryBatchRequest([new ReadingInput(Meter, 1, Start, 1m)]);

        var (errors, _) = TelemetryIngestion.Validate(request, Intervals, now);

        Assert.Empty(errors);
    }

    [Fact]
    public void Empty_and_oversized_batches_are_rejected()
    {
        var empty = TelemetryIngestion.Validate(new TelemetryBatchRequest([]), Intervals, Now);
        var missing = TelemetryIngestion.Validate(new TelemetryBatchRequest(null), Intervals, Now);
        var oversized = TelemetryIngestion.Validate(
            new TelemetryBatchRequest(Enumerable.Range(0, TelemetryIngestion.MaxReadingsPerBatch + 1)
                .Select(i => (ReadingInput?)new ReadingInput(Meter, i, Start.AddMinutes(15 * i), 1m)).ToList()),
            Intervals, Now);

        Assert.True(empty.Errors.ContainsKey("readings"));
        Assert.True(missing.Errors.ContainsKey("readings"));
        Assert.Single(oversized.Errors);
        Assert.True(oversized.Errors.ContainsKey("readings"));
    }

    [Fact]
    public void Field_errors_are_capped()
    {
        var request = new TelemetryBatchRequest(
            Enumerable.Range(0, 200).Select(_ => (ReadingInput?)null).ToList());

        var (errors, _) = TelemetryIngestion.Validate(request, Intervals, Now);

        Assert.Equal(TelemetryIngestion.MaxReportedErrors, errors.Count);
    }

    [Fact]
    public void Fingerprint_ignores_offset_and_trailing_zeros()
    {
        var utc = Valid(0, 10, 0, 12.5m);
        var sameInstant = utc with { ReadingStart = utc.ReadingStart.ToOffset(TimeSpan.FromHours(2)), EnergyKwh = 12.500m };

        Assert.Equal(TelemetryIngestion.Fingerprint([utc]), TelemetryIngestion.Fingerprint([sameInstant]));
        Assert.Matches("^[0-9a-f]{64}$", TelemetryIngestion.Fingerprint([utc]));
    }

    [Fact]
    public void Fingerprint_changes_with_any_value_or_the_order()
    {
        ValidReading[] batch = [Valid(0, 10, 0, 1.5m), Valid(1, 11, 1, 1.6m)];
        var baseline = TelemetryIngestion.Fingerprint(batch);

        Assert.NotEqual(baseline, TelemetryIngestion.Fingerprint([batch[1], batch[0]]));
        Assert.NotEqual(baseline, TelemetryIngestion.Fingerprint([batch[0] with { EnergyKwh = 1.501m }, batch[1]]));
        Assert.NotEqual(baseline, TelemetryIngestion.Fingerprint([batch[0] with { Sequence = 12 }, batch[1]]));
        Assert.NotEqual(baseline, TelemetryIngestion.Fingerprint([batch[0] with { MeterId = OtherMeter }, batch[1]]));
        Assert.NotEqual(baseline, TelemetryIngestion.Fingerprint([batch[0]]));
    }

    [Fact]
    public void Every_conflict_code_has_a_message()
    {
        foreach (var code in TelemetryIngestion.ConflictCodes)
        {
            Assert.False(string.IsNullOrWhiteSpace(TelemetryIngestion.ConflictMessage(code)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => TelemetryIngestion.ConflictMessage("unknown"));
    }

    [Theory]
    [InlineData("gw-hh-2026-03-29:0042", true)]
    [InlineData("A.b_c:d-1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("trailing-newline\n", false)]
    [InlineData("é", false)]
    public void Idempotency_key_rule(string key, bool expected) =>
        Assert.Equal(expected, IdempotencyKeys.IsValid(key));

    [Fact]
    public void Idempotency_keys_are_at_most_64_characters()
    {
        Assert.True(IdempotencyKeys.IsValid(new string('k', 64)));
        Assert.False(IdempotencyKeys.IsValid(new string('k', 65)));
    }
}
