using GridWise.Api.Data;
using GridWise.Api.Http;
using GridWise.Api.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GridWise.Api.Sites;

public sealed record CreateBuildingRequest(string? Name, string? TimeZone, int? FloorAreaM2);

public sealed record CreateMeterRequest(Guid? BuildingId, string? Serial, string? Kind, int? IntervalMinutes);

public sealed record BuildingView(
    Guid Id, string Name, string TimeZone, int? FloorAreaM2, int MeterCount, DateTimeOffset CreatedAt);

public sealed record MeterView(
    Guid Id, Guid BuildingId, string Serial, string Kind, int IntervalMinutes, DateTimeOffset CreatedAt);

public sealed record BuildingDetailView(
    Guid Id, string Name, string TimeZone, int? FloorAreaM2, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<MeterView> Meters);

/// <summary>
/// Buildings and meters registry. Every member can read; only admins can
/// register buildings and meters. The organization always comes from the route.
/// </summary>
public static class SiteEndpoints
{
    public const int DefaultIntervalMinutes = 15;

    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder routes)
    {
        var org = routes.MapGroup("/v1/organizations/{organizationId:guid}").RequireAuthorization();

        org.MapGet("/buildings", ListBuildingsAsync);
        org.MapPost("/buildings", CreateBuildingAsync);
        org.MapGet("/buildings/{buildingId:guid}", GetBuildingAsync);
        org.MapGet("/meters", ListMetersAsync);
        org.MapPost("/meters", CreateMeterAsync);
        org.MapGet("/meters/{meterId:guid}", GetMeterAsync);

        return routes;
    }

    private static async Task<IResult> ListBuildingsAsync(
        Guid organizationId, int? limit, string? cursor,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Operator, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var errors = new Dictionary<string, string[]>();
        var (size, after) = Paging.Read(limit, cursor, errors);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var query = db.Buildings.Where(b => b.OrganizationId == organizationId);
        if (after is not null)
        {
            query = query.Where(b => string.Compare(b.Name, after) > 0);
        }
        var rows = await query
            .OrderBy(b => b.Name)
            .Take(size + 1)
            .Select(b => new BuildingView(b.Id, b.Name, b.TimeZone, b.FloorAreaM2, b.Meters.Count, b.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(Paging.Build(rows, size, b => b.Name));
    }

    private static async Task<IResult> CreateBuildingAsync(
        Guid organizationId, CreateBuildingRequest request,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Admin, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var name = request.Name!.Trim();
        if (await db.Buildings.AnyAsync(b => b.OrganizationId == organizationId && b.Name == name, ct))
        {
            return BuildingNameTaken(http);
        }

        var building = new Building
        {
            OrganizationId = organizationId,
            Name = name,
            TimeZone = request.TimeZone!,
            FloorAreaM2 = request.FloorAreaM2,
        };
        db.Buildings.Add(building);
        if (!await TrySaveAsync(db, ct))
        {
            return BuildingNameTaken(http);
        }

        return Results.Created(
            $"/v1/organizations/{organizationId}/buildings/{building.Id}",
            new BuildingView(building.Id, building.Name, building.TimeZone, building.FloorAreaM2, 0, building.CreatedAt));
    }

    private static async Task<IResult> GetBuildingAsync(
        Guid organizationId, Guid buildingId,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Operator, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var building = await db.Buildings
            .Where(b => b.OrganizationId == organizationId && b.Id == buildingId)
            .Select(b => new { b.Id, b.Name, b.TimeZone, b.FloorAreaM2, b.CreatedAt, b.UpdatedAt })
            .SingleOrDefaultAsync(ct);
        if (building is null)
        {
            return ApiErrors.NotFound(http, "Building not found.");
        }

        var meters = await db.Meters
            .Where(m => m.OrganizationId == organizationId && m.BuildingId == buildingId)
            .OrderBy(m => m.Serial)
            .Select(m => new MeterView(m.Id, m.BuildingId, m.Serial, m.Kind, m.IntervalMinutes, m.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(new BuildingDetailView(
            building.Id, building.Name, building.TimeZone, building.FloorAreaM2, building.CreatedAt, building.UpdatedAt,
            meters));
    }

    private static async Task<IResult> ListMetersAsync(
        Guid organizationId, Guid? buildingId, int? limit, string? cursor,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Operator, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var errors = new Dictionary<string, string[]>();
        var (size, after) = Paging.Read(limit, cursor, errors);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var query = db.Meters.Where(m => m.OrganizationId == organizationId);
        if (buildingId is { } building)
        {
            query = query.Where(m => m.BuildingId == building);
        }
        if (after is not null)
        {
            query = query.Where(m => string.Compare(m.Serial, after) > 0);
        }
        var rows = await query
            .OrderBy(m => m.Serial)
            .Take(size + 1)
            .Select(m => new MeterView(m.Id, m.BuildingId, m.Serial, m.Kind, m.IntervalMinutes, m.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(Paging.Build(rows, size, m => m.Serial));
    }

    private static async Task<IResult> GetMeterAsync(
        Guid organizationId, Guid meterId,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Operator, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var meter = await db.Meters
            .Where(m => m.OrganizationId == organizationId && m.Id == meterId)
            .Select(m => new MeterView(m.Id, m.BuildingId, m.Serial, m.Kind, m.IntervalMinutes, m.CreatedAt))
            .SingleOrDefaultAsync(ct);
        return meter is null ? ApiErrors.NotFound(http, "Meter not found.") : Results.Ok(meter);
    }

    private static async Task<IResult> CreateMeterAsync(
        Guid organizationId, CreateMeterRequest request,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Admin, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var errors = Validate(request);
        // A building from another organization is reported exactly like a missing one.
        if (request.BuildingId is { } buildingId
            && !await db.Buildings.AnyAsync(b => b.OrganizationId == organizationId && b.Id == buildingId, ct))
        {
            errors["buildingId"] = ["Building not found."];
        }
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var serial = MeterRules.NormalizeSerial(request.Serial!);
        if (await db.Meters.AnyAsync(m => m.OrganizationId == organizationId && m.Serial == serial, ct))
        {
            return SerialTaken(http);
        }

        var meter = new Meter
        {
            OrganizationId = organizationId,
            BuildingId = request.BuildingId!.Value,
            Serial = serial,
            Kind = request.Kind!,
            IntervalMinutes = request.IntervalMinutes ?? DefaultIntervalMinutes,
        };
        db.Meters.Add(meter);
        if (!await TrySaveAsync(db, ct))
        {
            return SerialTaken(http);
        }

        return Results.Created(
            $"/v1/organizations/{organizationId}/meters/{meter.Id}",
            new MeterView(meter.Id, meter.BuildingId, meter.Serial, meter.Kind, meter.IntervalMinutes, meter.CreatedAt));
    }

    internal static Dictionary<string, string[]> Validate(CreateBuildingRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors["name"] = ["Name is required."];
        }
        else if (name.Length > Building.NameMaxLength || name.Any(char.IsControl))
        {
            errors["name"] = [$"Name must be at most {Building.NameMaxLength} printable characters."];
        }

        if (string.IsNullOrEmpty(request.TimeZone))
        {
            errors["timeZone"] = ["Time zone is required."];
        }
        else if (!BuildingRules.IsValidTimeZone(request.TimeZone))
        {
            errors["timeZone"] = ["Time zone must be an IANA ID such as Europe/Madrid."];
        }

        if (request.FloorAreaM2 is { } area && (area < 1 || area > Building.MaxFloorAreaM2))
        {
            errors["floorAreaM2"] = [$"Floor area must be between 1 and {Building.MaxFloorAreaM2} m²."];
        }
        return errors;
    }

    internal static Dictionary<string, string[]> Validate(CreateMeterRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.BuildingId is null || request.BuildingId == Guid.Empty)
        {
            errors["buildingId"] = ["Building is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Serial))
        {
            errors["serial"] = ["Serial is required."];
        }
        else if (!MeterRules.IsValidSerial(MeterRules.NormalizeSerial(request.Serial)))
        {
            errors["serial"] = ["Serial must be 3 to 40 letters, digits or hyphens and start with a letter or digit."];
        }

        if (string.IsNullOrEmpty(request.Kind))
        {
            errors["kind"] = ["Kind is required."];
        }
        else if (!MeterRules.Kinds.Contains(request.Kind))
        {
            errors["kind"] = [$"Kind must be one of: {string.Join(", ", MeterRules.Kinds)}."];
        }

        if (request.IntervalMinutes is { } interval && !MeterRules.IntervalMinutes.Contains(interval))
        {
            errors["intervalMinutes"] = [$"Interval must be one of: {string.Join(", ", MeterRules.IntervalMinutes)} minutes."];
        }
        return errors;
    }

    /// <summary>
    /// Saves, turning a unique-index violation into <c>false</c>. The pre-checks
    /// give friendly errors; this covers two requests racing past them.
    /// </summary>
    private static async Task<bool> TrySaveAsync(GridWiseDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    private static IResult BuildingNameTaken(HttpContext http) =>
        ApiErrors.Conflict(http, "building_name_taken", "A building with this name already exists in the organization.");

    private static IResult SerialTaken(HttpContext http) =>
        ApiErrors.Conflict(http, "meter_serial_taken", "A meter with this serial already exists in the organization.");
}
