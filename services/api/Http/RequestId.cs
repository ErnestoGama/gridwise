namespace GridWise.Api.Http;

/// <summary>
/// Gives every request a correlation ID. A well-formed incoming
/// <c>X-Request-ID</c> is kept; anything else is replaced. The ID is echoed in
/// the response header, error bodies and the logging scope.
/// </summary>
public static class RequestId
{
    public const string HeaderName = "X-Request-ID";
    private const int MaxLength = 64;
    private static readonly object ItemKey = new();

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    public static bool IsAcceptable(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static IApplicationBuilder UseRequestId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var incoming = context.Request.Headers[HeaderName].ToString();
            var id = IsAcceptable(incoming) ? incoming : Guid.NewGuid().ToString("N");
            context.Items[ItemKey] = id;
            context.TraceIdentifier = id;
            // Set when the response starts so the exception handler, which clears headers, keeps it.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            });

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GridWise.Api.Requests");
            using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id }))
            {
                await next(context);
            }
        });
}
