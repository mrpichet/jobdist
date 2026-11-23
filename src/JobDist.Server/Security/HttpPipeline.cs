using JobDist.Core.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JobDist.Server.Security;

/// <summary>
/// Global exception handler: maps business exceptions to RFC 7807 problem details and hides internals of unexpected errors
/// (no stack traces / SQL text reach clients).
/// </summary>
public sealed class JobDistExceptionHandler(IProblemDetailsService problemDetails, ILogger<JobDistExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Converts an exception into a problem-details response.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="exception">Unhandled exception.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when handled.</returns>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Step 1: classify.
        var (status, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden"),
            BadHttpRequestException b => (b.StatusCode, "Bad request"),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Client closed request"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        // Step 2: only unexpected errors are logged with the exception.
        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        // Step 3: problem details; business messages are safe to show, everything else is generic.
        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = exception is JobDistException ? exception.Message : null,
                Instance = httpContext.Request.Path
            }
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// Adds defensive response headers. API responses get a deny-all CSP; the Blazor WebAssembly UI gets a strict CSP that allows
/// only same-origin scripts plus <c>wasm-unsafe-eval</c> (required to instantiate WebAssembly).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ApiCsp = "default-src 'none'; frame-ancestors 'none'";
    private const string UiCsp = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                                 "font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Sets headers then calls the next middleware.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        var h = context.Response.Headers;
        var path = context.Request.Path;
        var isApi = path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp");
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h.ContentSecurityPolicy = isApi ? ApiCsp : UiCsp;
        if (isApi)
        {
            h.CacheControl = "no-store";
        }
        return next(context);
    }
}

/// <summary>
/// Action filter for worker endpoints: the route's <c>workerId</c> must match the secret <c>X-Worker-Token</c> issued at
/// registration. A fleet API key alone cannot act on behalf of another worker.
/// </summary>
public sealed class WorkerTokenFilter(WorkerRegistryService registry, WorkerReportTracker tracker) : IAsyncActionFilter
{
    /// <summary>Header carrying the per-worker token.</summary>
    public const string HeaderName = "X-Worker-Token";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Step 1: admins may act on any worker (e.g. unregister a dead node).
        if (context.HttpContext.User.IsInRole(Roles.Admin) && !context.HttpContext.Request.Headers.ContainsKey(HeaderName))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Step 2: verify token against the worker id from the route.
        if (!context.RouteData.Values.TryGetValue("workerId", out var raw) || !Guid.TryParse(raw?.ToString(), out var workerId))
        {
            context.Result = new BadRequestResult();
            return;
        }
        try
        {
            await registry.AuthenticateAsync(workerId, context.HttpContext.Request.Headers[HeaderName].ToString(), context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (ForbiddenException)
        {
            tracker.Rejected(workerId.ToString());
            context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = "Forbidden", Detail = "Invalid worker token." }) { StatusCode = 403 };
            return;
        }
        await next().ConfigureAwait(false);
    }
}
