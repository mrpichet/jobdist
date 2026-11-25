using System.Text.Json;
using JobDist.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace JobDist.Server.Security;

/// <summary>
/// <c>Idempotency-Key</c> support for POST endpoints: the first response (2xx) is stored for 24 hours per caller + route + key.
/// A retry with the same key and body gets the stored response (header <c>Idempotent-Replayed: true</c>); the same key with a
/// different body gets 422. Requests without the header run normally.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory
{
    /// <summary>Header name.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <inheritdoc />
    public bool IsReusable => true;

    /// <inheritdoc />
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider);
}

/// <summary>Resource filter behind <see cref="IdempotentAttribute"/> (runs before model binding, so it can read the raw body).</summary>
public sealed class IdempotencyFilter(IdempotencyService idempotency, IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json) : IAsyncResourceFilter
{
    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[IdempotentAttribute.HeaderName].ToString();
        if (string.IsNullOrEmpty(key))
        {
            await next().ConfigureAwait(false);
            return;
        }
        if (key.Length > 100 || key.Any(char.IsControl))
        {
            context.Result = new BadRequestObjectResult(new ProblemDetails { Status = 400, Title = "Validation failed", Detail = "Idempotency-Key must be 1-100 printable characters." });
            return;
        }

        // Step 1: hash the raw body (buffered so model binding can read it again).
        http.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
        http.Request.Body.Position = 0;
        var hash = IdempotencyService.Hash(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        var principal = http.User.CallerId();
        var route = $"{http.Request.Method} {http.Request.Path.Value?.ToLowerInvariant()}";

        // Step 2: replay a stored response (422 on a different body comes from the service as an exception).
        var stored = await idempotency.FindAsync(principal, route, key, hash, http.RequestAborted).ConfigureAwait(false);
        if (stored is not null)
        {
            http.Response.Headers["Idempotent-Replayed"] = "true";
            context.Result = new ContentResult { StatusCode = stored.StatusCode, Content = stored.ResponseBody, ContentType = "application/json; charset=utf-8" };
            return;
        }

        // Step 3: run and store a successful result.
        var executed = await next().ConfigureAwait(false);
        if (executed.Exception is null && executed.Result is ObjectResult { StatusCode: null or (>= 200 and < 300) } result)
        {
            var body = JsonSerializer.Serialize(result.Value, result.Value?.GetType() ?? typeof(object), json.Value.JsonSerializerOptions);
            await idempotency.SaveAsync(principal, route, key, hash, result.StatusCode ?? 200, body, http.RequestAborted).ConfigureAwait(false);
        }
    }
}
