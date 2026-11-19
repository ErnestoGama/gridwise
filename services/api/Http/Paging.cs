using System.Text;

namespace GridWise.Api.Http;

/// <summary>A page of results. <c>nextCursor</c> is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// Keyset pagination over a unique text key (e.g. a building name). Cursors are
/// opaque base64url strings so clients cannot depend on their contents.
/// </summary>
public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
    private const int MaxCursorLength = 512;

    /// <summary>Reads <c>limit</c> and <c>cursor</c>; adds field errors for invalid values.</summary>
    public static (int Limit, string? After) Read(int? limit, string? cursor, Dictionary<string, string[]> errors)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"Limit must be between 1 and {MaxLimit}."];
        }

        string? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            after = Decode(cursor);
            if (after is null)
            {
                errors["cursor"] = ["Cursor is not valid."];
            }
        }
        return (size, after);
    }

    /// <summary>
    /// Builds a page from up to <paramref name="limit"/> + 1 rows fetched in key order;
    /// the extra row only signals that another page exists.
    /// </summary>
    public static Page<T> Build<T>(IReadOnlyList<T> rows, int limit, Func<T, string> key)
    {
        if (rows.Count <= limit)
        {
            return new Page<T>(rows, null);
        }
        var items = rows.Take(limit).ToList();
        return new Page<T>(items, Encode(key(items[^1])));
    }

    public static string Encode(string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? Decode(string cursor)
    {
        if (cursor.Length > MaxCursorLength)
        {
            return null;
        }
        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var key = new UTF8Encoding(false, true).GetString(bytes);
            return key.Length > 0 ? key : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }
}
