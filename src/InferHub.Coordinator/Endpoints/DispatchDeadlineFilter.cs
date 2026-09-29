using System.Text.Json;
using InferHub.Shared.OpenAi;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// Phase 89 D5. A tool dispatch that runs out of the hub's clock is a <c>504</c> naming the deadline,
/// in the route's own error dialect — chat has answered that way since v1.x, and the tool routes
/// never caught the exception at all, so their caller got a bodyless <c>500</c>.
/// </summary>
/// <remarks>
/// A filter rather than a <c>catch</c> beside each of the seven dispatch calls, because the seven
/// would drift and the eighth would be forgotten. <b>Only before the response has started</b>: a
/// stream that ran out of time mid-body has already sent its <c>200</c>, and ends the way each stream
/// already ends on an error.
/// </remarks>
internal static class DispatchDeadlineFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary><c>/api/tools/{capability}</c>: the native <c>{"error": "..."}</c> shape.</summary>
    public static async ValueTask<object?> Native(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (TimeoutException ex) when (!context.HttpContext.Response.HasStarted)
        {
            return ToolEndpoints.Error(StatusCodes.Status504GatewayTimeout, ex.Message);
        }
    }

    /// <summary><c>/v1/audio/*</c>: OpenAI's envelope, with a code a client can branch on.</summary>
    public static async ValueTask<object?> OpenAi(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (TimeoutException ex) when (!context.HttpContext.Response.HasStarted)
        {
            return Results.Json(
                OpenAiErrorEnvelope.Create(ex.Message, OpenAiErrorTypes.ApiError, code: "deadline_exceeded"),
                JsonOptions,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }
}
