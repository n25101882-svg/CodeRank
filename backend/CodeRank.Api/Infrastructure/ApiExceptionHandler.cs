using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CodeRank.Api.Infrastructure;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        // Never log provider exception messages, SQL parameters, request DTOs or credentials.
        logger.LogError("API request failed ({ExceptionType}); trace {TraceId}.", exception.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new ProblemDetails {
            Status = 500, Title = "The request could not be completed.", Extensions = { ["traceId"] = context.TraceIdentifier }
        }, token);
        return true;
    }
}
