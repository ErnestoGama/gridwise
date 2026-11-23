using System.Globalization;
using GridWise.Api.Data;
using GridWise.Api.Migrations;
using GridWise.Api.Sites;
using GridWise.Api.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace GridWise.Api.Tests;

public sealed class TelemetrySchemaTests
{
    [Theory]
    [InlineData("0", true)]
    [InlineData("1.234", true)]
    [InlineData("999999999.999", true)]
    [InlineData("-0.001", false)]
    [InlineData("1.2345", false)]
    [InlineData("1000000000", false)]
    public void Energy_rule_matches_the_column_and_check(string kwh, bool expected) =>
        Assert.Equal(expected, ReadingRules.IsValidEnergy(decimal.Parse(kwh, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("2026-03-29T00:45:00+00:00", 15, true)]
    [InlineData("2026-03-29T01:00:00+00:00", 60, true)]
    [InlineData("2026-03-29T00:50:00+00:00", 10, true)]
    [InlineData("2026-03-29T00:50:00+00:00", 15, false)]
    [InlineData("2026-03-29T00:45:30+00:00", 15, false)]
    [InlineData("2026-03-29T02:45:00+02:00", 15, false)]
    [InlineData("2026-03-29T00:45:00+00:00", 7, false)]
    public void Reading_start_must_be_utc_and_on_an_interval_boundary(string start, int interval, bool expected) =>
        Assert.Equal(expected, ReadingRules.IsAlignedStart(DateTimeOffset.Parse(start, CultureInfo.InvariantCulture), interval));

    [Fact]
    public void Readings_are_keyed_by_meter_and_time_and_tied_to_a_meter_of_the_same_organization()
    {
        using var db = NpgsqlContext();
        var reading = db.Model.FindEntityType(typeof(MeterReading))!;

        Assert.Equal("meter_readings", reading.GetTableName());
        Assert.Equal(
            new[] { nameof(MeterReading.MeterId), nameof(MeterReading.ReadingStart) },
            reading.FindPrimaryKey()!.Properties.Select(p => p.Name));

        var fk = Assert.Single(reading.GetForeignKeys());
        Assert.Equal(typeof(Meter), fk.PrincipalEntityType.ClrType);
        Assert.Equal(
            new[] { nameof(MeterReading.OrganizationId), nameof(MeterReading.MeterId) },
            fk.Properties.Select(p => p.Name));
        Assert.Equal(
            new[] { nameof(Meter.OrganizationId), nameof(Meter.Id) },
            fk.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);

        var energy = reading.FindProperty(nameof(MeterReading.EnergyKwh))!;
        Assert.Equal(12, energy.GetPrecision());
        Assert.Equal(3, energy.GetScale());
    }

    [Fact]
    public void Migration_turns_the_table_into_a_hypertable_and_blocks_updates()
    {
        var up = new AddMeterReadingsHypertable().UpOperations.ToList();

        var createTable = up.FindIndex(o => o is CreateTableOperation { Name: "meter_readings" });
        var hypertable = up.FindIndex(o =>
            o is SqlOperation { Sql: var sql } && sql.Contains("create_hypertable('meter_readings'", StringComparison.Ordinal));
        var trigger = up.FindIndex(o =>
            o is SqlOperation { Sql: var sql } && sql.Contains("BEFORE UPDATE ON meter_readings", StringComparison.Ordinal));

        Assert.True(createTable >= 0, "meter_readings is created");
        Assert.True(hypertable > createTable, "the hypertable is created after the table");
        Assert.True(trigger > hypertable, "the update trigger is created after the hypertable");
        Assert.Contains($"INTERVAL '{AddMeterReadingsHypertable.ChunkInterval}'",
            ((SqlOperation)up[hypertable]).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_rollback_drops_readings_before_the_meter_key_they_depend_on()
    {
        var down = new AddMeterReadingsHypertable().DownOperations.ToList();

        var dropTable = down.FindIndex(o => o is DropTableOperation { Name: "meter_readings" });
        var dropKey = down.FindIndex(o => o is DropUniqueConstraintOperation { Name: "ak_meters_organization_id_id" });

        Assert.InRange(dropTable, 0, dropKey - 1);
    }

    [Fact]
    public void Batches_have_a_per_organization_idempotency_key_and_conflicts_cascade_with_their_batch()
    {
        using var db = NpgsqlContext();
        var batch = db.Model.FindEntityType(typeof(TelemetryBatch))!;
        var conflict = db.Model.FindEntityType(typeof(TelemetryBatchConflict))!;

        var keyIndex = batch.GetIndexes().Single(i => i.GetDatabaseName() == "ux_telemetry_batches_organization_idempotency_key");
        Assert.True(keyIndex.IsUnique);
        Assert.Equal("idempotency_key IS NOT NULL", keyIndex.GetFilter());
        Assert.Equal(
            new[] { nameof(TelemetryBatch.OrganizationId), nameof(TelemetryBatch.IdempotencyKey) },
            keyIndex.Properties.Select(p => p.Name));

        var submittedBy = batch.GetForeignKeys().Single(fk => fk.Properties.Single().Name == nameof(TelemetryBatch.SubmittedByUserId));
        Assert.Equal(DeleteBehavior.SetNull, submittedBy.DeleteBehavior);

        Assert.Equal(
            new[] { nameof(TelemetryBatchConflict.BatchId), nameof(TelemetryBatchConflict.ReadingIndex) },
            conflict.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(conflict.GetForeignKeys()).DeleteBehavior);
    }

    [Fact]
    public void Migration_check_constraints_match_the_model()
    {
        using var db = NpgsqlContext();
        var up = new AddTelemetryBatches().UpOperations.ToList();
        var checks = up.OfType<CreateTableOperation>()
            .SelectMany(t => t.CheckConstraints)
            .ToDictionary(c => c.Name, c => c.Sql);

        foreach (var type in new[] { typeof(TelemetryBatch), typeof(TelemetryBatchConflict) })
        {
            foreach (var check in db.Model.FindEntityType(type)!.GetCheckConstraints())
            {
                Assert.Equal(check.Sql, checks[check.ModelName]);
            }
        }
    }

    [Fact]
    public void Batch_rollback_drops_conflicts_before_batches_and_keeps_readings()
    {
        var down = new AddTelemetryBatches().DownOperations.ToList();

        var conflicts = down.FindIndex(o => o is DropTableOperation { Name: "telemetry_batch_conflicts" });
        var batches = down.FindIndex(o => o is DropTableOperation { Name: "telemetry_batches" });

        Assert.InRange(conflicts, 0, batches - 1);
        Assert.DoesNotContain(down, o => o is DropTableOperation { Name: "meter_readings" });
    }

    private static GridWiseDbContext NpgsqlContext() =>
        new(new DbContextOptionsBuilder<GridWiseDbContext>().UseNpgsql("Host=localhost;Database=unused").Options,
            TimeProvider.System);
}
