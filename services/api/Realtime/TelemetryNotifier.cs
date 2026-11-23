using GridWise.Api.Telemetry;
using Microsoft.AspNetCore.SignalR;

namespace GridWise.Api.Realtime;

public sealed record LiveReading(long Sequence, DateTimeOffset ReadingStart, decimal EnergyKwh);

public sealed record LiveMeterReadings(Guid MeterId, IReadOnlyList<LiveReading> Readings);

/// <summary>
/// One message per building and batch. <c>QualityFlagsChanged</c> tells the
/// dashboard to reload the flags of these meters; flags are not repeated here.
/// </summary>
public sealed record ReadingsIngestedMessage(
    Guid OrganizationId, Guid BuildingId, Guid BatchId, DateTimeOffset ReceivedAt,
    bool QualityFlagsChanged, IReadOnlyList<LiveMeterReadings> Meters);

public static class TelemetryNotifications
{
    /// <summary>Groups the stored readings of a batch by building, then meter, in time order.</summary>
    public static IReadOnlyList<ReadingsIngestedMessage> Build(
        Guid organizationId, Guid batchId, DateTimeOffset receivedAt, IReadOnlyList<ValidReading> inserted,
        IReadOnlyDictionary<Guid, Guid> meterBuildings, bool qualityFlagsChanged) =>
        inserted
            .GroupBy(r => meterBuildings[r.MeterId])
            .OrderBy(building => building.Key)
            .Select(building => new ReadingsIngestedMessage(
                organizationId, building.Key, batchId, receivedAt, qualityFlagsChanged,
                building
                    .GroupBy(r => r.MeterId)
                    .OrderBy(meter => meter.Key)
                    .Select(meter => new LiveMeterReadings(
                        meter.Key,
                        meter.OrderBy(r => r.ReadingStart)
                            .Select(r => new LiveReading(r.Sequence, r.ReadingStart, r.EnergyKwh))
                            .ToList()))
                    .ToList()))
            .ToList();
}

/// <summary>
/// Pushes committed readings to the building groups of <see cref="TelemetryHub"/>.
/// Called only after the ingestion transaction commits, so clients never see
/// readings that were rolled back. A failed push is logged and does not fail
/// the batch: the readings are stored and a dashboard reload shows them.
/// No cancellation token: once committed, the push happens even if the
/// sender has gone away.
/// </summary>
public sealed class TelemetryNotifier(
    IHubContext<TelemetryHub, ITelemetryClient> hub, ILogger<TelemetryNotifier> logger)
{
    public async Task PublishAsync(IReadOnlyList<ReadingsIngestedMessage> messages)
    {
        foreach (var message in messages)
        {
            try
            {
                await hub.Clients
                    .Group(TelemetryHub.BuildingGroup(message.OrganizationId, message.BuildingId))
                    .ReadingsIngested(message);
            }
            catch (Exception ex)
            {
                // IDs only: reading values are not logged.
                logger.LogWarning(ex,
                    "Realtime update for batch {BatchId}, building {BuildingId} could not be sent",
                    message.BatchId, message.BuildingId);
            }
        }
    }
}
