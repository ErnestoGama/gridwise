namespace GridWise.Api.Identity;

/// <summary>
/// Role a user holds inside one organization. Roles are ordered: each role can
/// do everything the roles before it can.
/// </summary>
public enum OrganizationRole
{
    /// <summary>Monitors sites and responds to alerts.</summary>
    Operator = 1,

    /// <summary>Studies baselines, anomalies and costs.</summary>
    Analyst = 2,

    /// <summary>Reviews scenarios and approves recommendations.</summary>
    Manager = 3,

    /// <summary>Manages buildings, meters and access.</summary>
    Admin = 4,
}

public static class OrganizationRoles
{
    public static readonly IReadOnlyList<string> Names =
        Enum.GetValues<OrganizationRole>().Select(ToName).ToArray();

    /// <summary>True when <paramref name="held"/> grants at least <paramref name="required"/>.</summary>
    public static bool Satisfies(this OrganizationRole held, OrganizationRole required) =>
        Enum.IsDefined(held) && Enum.IsDefined(required) && held >= required;

    /// <summary>Lowercase name used in the database and in API responses.</summary>
    public static string ToName(this OrganizationRole role) => role switch
    {
        OrganizationRole.Operator => "operator",
        OrganizationRole.Analyst => "analyst",
        OrganizationRole.Manager => "manager",
        OrganizationRole.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown organization role."),
    };

    public static OrganizationRole FromName(string name) => name switch
    {
        "operator" => OrganizationRole.Operator,
        "analyst" => OrganizationRole.Analyst,
        "manager" => OrganizationRole.Manager,
        "admin" => OrganizationRole.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown organization role."),
    };
}
