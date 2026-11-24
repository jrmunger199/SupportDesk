using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SupportDesk.Api.Infrastructure;

/// <summary>An expected failure with an HTTP meaning. Thrown by services, rendered as ProblemDetails by one handler.</summary>
public abstract class AppException(int status, string title, string detail) : Exception(detail)
{
    public int Status { get; } = status;

    public string Title { get; } = title;
}

public sealed class NotFoundException(string what)
    : AppException(StatusCodes.Status404NotFound, "Not found", $"{what} was not found.");

public sealed class ForbiddenException(string detail)
    : AppException(StatusCodes.Status403Forbidden, "Forbidden", detail);

public sealed class ConflictException(string detail)
    : AppException(StatusCodes.Status409Conflict, "Conflict", detail);

public sealed class UnprocessableException(string detail)
    : AppException(StatusCodes.Status422UnprocessableEntity, "Request cannot be processed", detail);

public sealed class UnauthorizedAppException(string detail)
    : AppException(StatusCodes.Status401Unauthorized, "Unauthorized", detail);

public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problems,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            AppException app => (app.Status, app.Title, app.Message),
            DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                "Conflict",
                "The ticket was changed by someone else. Reload it and try again."),

            // Raised by Kestrel and the request pipeline for client mistakes, such as a body over the size limit.
            // They already carry the right 4xx status; the message is replaced because it can describe internals.
            BadHttpRequestException bad => (
                bad.StatusCode,
                bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Payload too large" : "Bad request",
                bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? "The request body is too large."
                    : "The request could not be understood."),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred."),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(exception, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception while processing {Path}")]
    private partial void LogUnhandled(Exception exception, PathString path);
}
