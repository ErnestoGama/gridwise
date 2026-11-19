using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace GridWise.Api.Http;

/// <summary>
/// Consistent error body for every failed <c>/v1</c> request:
/// <c>{ "error": { "code", "message", "requestId", "fields"? } }</c>.
/// </summary>
public static class ApiErrors
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IResult Result(
        HttpContext context, int status, string code, string message,
        IReadOnlyDictionary<string, string[]>? fields = null) =>
        Results.Json(Body(context, code, message, fields), JsonOptions, statusCode: status);

    public static IResult Unauthorized(HttpContext context) =>
        Result(context, StatusCodes.Status401Unauthorized, "unauthenticated", "Sign in to continue.");

    public static IResult NotFound(HttpContext context, string message) =>
        Result(context, StatusCodes.Status404NotFound, "not_found", message);

    public static IResult Forbidden(HttpContext context) =>
        Result(context, StatusCodes.Status403Forbidden, "forbidden", "Your role does not allow this action.");

    public static IResult Conflict(HttpContext context, string code, string message) =>
        Result(context, StatusCodes.Status409Conflict, code, message);

    public static IResult Validation(HttpContext context, IReadOnlyDictionary<string, string[]> fields) =>
        Result(context, StatusCodes.Status422UnprocessableEntity, "validation_failed", "One or more fields are invalid.", fields);

    public static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(Body(context, code, message, null), JsonOptions));
    }

    /// <summary>
    /// Last-resort handler: malformed requests become 400, anything else 500.
    /// Exception messages are never written to the response.
    /// </summary>
    public static Task HandleExceptionAsync(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        return error is BadHttpRequestException bad
            ? WriteAsync(context, bad.StatusCode, "invalid_request", "The request could not be read.")
            : WriteAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "Something went wrong.");
    }

    private static object Body(HttpContext context, string code, string message, IReadOnlyDictionary<string, string[]>? fields) =>
        new { error = new { code, message, requestId = RequestId.Get(context), fields } };
}
