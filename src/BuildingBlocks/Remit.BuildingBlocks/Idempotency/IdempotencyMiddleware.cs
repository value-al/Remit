using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Remit.BuildingBlocks.Idempotency;

/// <summary>
/// Makes unsafe endpoints idempotent on an <c>Idempotency-Key</c> header (ADR-0002).
///
/// Rules, in order:
///  1. No header on a POST → 400. Callers that move money must opt in explicitly.
///  2. Key seen before, same request hash → replay the stored response, status and body.
///  3. Key seen before, different hash → 422. The client reused a key for a different request.
///  4. Key claimed by an in-flight request → 409. Retry after the first one completes.
///  5. Otherwise run the pipeline, capture the response, store it, return it.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, IIdempotencyStore store)
{
    public const string HeaderName = "Idempotency-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        var key = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"Missing required header: {HeaderName}.", context.RequestAborted);
            return;
        }

        var requestHash = await HashBodyAsync(context);
        var cancellationToken = context.RequestAborted;

        var stored = await store.GetAsync(key, cancellationToken);
        if (stored is not null)
        {
            if (!string.Equals(stored.RequestHash, requestHash, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await context.Response.WriteAsync("Idempotency-Key reused with a different request body.", cancellationToken);
                return;
            }

            context.Response.StatusCode = stored.StatusCode;
            context.Response.ContentType = stored.ContentType;
            context.Response.Headers["Idempotent-Replayed"] = "true";
            await context.Response.Body.WriteAsync(stored.Body, cancellationToken);
            return;
        }

        if (!await store.TryClaimAsync(key, requestHash, cancellationToken))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync("A request with this Idempotency-Key is still in progress.", cancellationToken);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);

            var body = buffer.ToArray();
            await store.CompleteAsync(
                key,
                new StoredResponse(requestHash, context.Response.StatusCode, context.Response.ContentType ?? "application/octet-stream", body),
                cancellationToken);

            context.Response.Body = originalBody;
            await originalBody.WriteAsync(body, cancellationToken);
        }
        catch
        {
            // A failed request must not pin the key; the client is entitled to retry it.
            await store.ReleaseAsync(key, CancellationToken.None);
            context.Response.Body = originalBody;
            throw;
        }
    }

    private static async Task<string> HashBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(context.Request.Body, context.RequestAborted);
        context.Request.Body.Position = 0;

        // The route participates in the hash: the same key must not be valid across endpoints.
        var routeBytes = Encoding.UTF8.GetBytes(context.Request.Path.ToString());
        return Convert.ToHexString(SHA256.HashData([.. hash, .. routeBytes]));
    }
}

public static class IdempotencyMiddlewareExtensions
{
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app) =>
        app.UseMiddleware<IdempotencyMiddleware>();
}
