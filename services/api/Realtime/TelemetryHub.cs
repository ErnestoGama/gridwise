using GridWise.Api.Data;
using GridWise.Api.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Realtime;

/// <summary>Messages the server pushes to dashboard clients.</summary>
public interface ITelemetryClient
{
    /// <summary>New readings were stored for meters of a building the client follows.</summary>
    Task ReadingsIngested(ReadingsIngestedMessage message);
}

public sealed record BuildingSubscription(Guid OrganizationId, Guid BuildingId);

/// <summary>
/// Live telemetry for dashboards at <c>/hubs/telemetry</c>. A connection needs
/// a signed-in session and receives nothing until it subscribes to a building.
/// Each subscription checks membership of the organization and that the
/// building belongs to it, so updates only reach members. Unknown buildings
/// and other organizations fail the same way, so IDs cannot be probed.
/// </summary>
[Authorize]
public sealed class TelemetryHub(
    OrganizationAccess access, GridWiseDbContext db, ILogger<TelemetryHub> logger) : Hub<ITelemetryClient>
{
    public const string Path = "/hubs/telemetry";

    /// <summary>Upper bound per connection; a dashboard follows one building, a wall display a few.</summary>
    public const int MaxSubscriptionsPerConnection = 20;

    public const string NotFoundError = "not_found";
    public const string TooManySubscriptionsError = "too_many_subscriptions";

    private const string SubscriptionsKey = "gridwise.subscriptions";

    public static string BuildingGroup(Guid organizationId, Guid buildingId) =>
        $"building:{organizationId:N}:{buildingId:N}";

    public async Task<BuildingSubscription> SubscribeBuilding(Guid organizationId, Guid buildingId)
    {
        var ct = Context.ConnectionAborted;
        var subscriptions = Subscriptions();
        var group = BuildingGroup(organizationId, buildingId);
        if (!subscriptions.Contains(group) && subscriptions.Count >= MaxSubscriptionsPerConnection)
        {
            throw new HubException(TooManySubscriptionsError);
        }

        // Every member can watch live readings, the same as reading buildings and quality flags.
        var (decision, _) = await access.CheckAsync(Context.User!, organizationId, OrganizationRole.Operator, ct);
        var exists = decision == AccessDecision.Granted
            && await db.Buildings.AnyAsync(b => b.OrganizationId == organizationId && b.Id == buildingId, ct);
        if (!exists)
        {
            logger.LogInformation(
                "Realtime subscription refused for user {UserId} on organization {OrganizationId}",
                Context.User!.GetUserId(), organizationId);
            throw new HubException(NotFoundError);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group, ct);
        subscriptions.Add(group);
        return new BuildingSubscription(organizationId, buildingId);
    }

    public async Task UnsubscribeBuilding(Guid organizationId, Guid buildingId)
    {
        var group = BuildingGroup(organizationId, buildingId);
        if (Subscriptions().Remove(group))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        }
    }

    private HashSet<string> Subscriptions()
    {
        if (Context.Items.TryGetValue(SubscriptionsKey, out var value) && value is HashSet<string> existing)
        {
            return existing;
        }
        var created = new HashSet<string>(StringComparer.Ordinal);
        Context.Items[SubscriptionsKey] = created;
        return created;
    }
}
