using CodeRank.Api.Judge;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CodeRank.Api.Infrastructure;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        if (exception is JudgeQueueBusyException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable, Title = "Judge queue is temporarily busy."
            }, token);
            return true;
        }

        if (exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge, Title = "The request body is too large."
            }, token);
            return true;
        }

        // Never log provider exception messages, SQL parameters, request DTOs or credentials.
        logger.LogError("API request failed ({ExceptionType}); trace {TraceId}.", exception.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new ProblemDetails {
            Status = 500, Title = "The request could not be completed.", Extensions = { ["traceId"] = context.TraceIdentifier }
        }, token);
        return true;
    }
}
