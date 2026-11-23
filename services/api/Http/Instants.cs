using System.Globalization;

namespace GridWise.Api.Http;

/// <summary>Reads time-window query parameters such as <c>from</c> and <c>to</c>.</summary>
public static class Instants
{
    /// <summary>
    /// Parses an ISO 8601 instant and returns it in UTC; adds a field error when
    /// the value is malformed. Empty values are treated as absent.
    /// </summary>
    public static DateTimeOffset? Read(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        // An explicit offset is required: a bare local time would depend on the server's zone.
        if (DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && HasExplicitOffset(value))
        {
            return parsed.ToUniversalTime();
        }
        errors[field] = ["Use an ISO 8601 date and time with an offset, e.g. 2026-03-29T00:00:00Z."];
        return null;
    }

    /// <summary>Ends in <c>Z</c> or <c>±hh:mm</c>.</summary>
    private static bool HasExplicitOffset(string value) =>
        value.EndsWith('Z')
        || (value.Length > 6 && (value[^6] == '+' || value[^6] == '-') && value[^3] == ':');
}
