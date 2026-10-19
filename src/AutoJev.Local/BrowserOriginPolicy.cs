using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace AutoJev.Local;

internal static class BrowserOriginPolicy
{
    public static void Validate(string[]? origins)
    {
        if (origins is null || origins.Any(origin => !ValidOrigin(origin)))
            throw new Exception("allowedOrigins must contain exact origins, '*' for all origins, or 'null' for opaque origins. Do not include paths, credentials, queries or fragments.");
    }

    private static bool ValidOrigin(string? origin)
    {
        if (origin is "*" or "null") return true;
        return origin is not null && !origin.Contains('*') && !origin.Any(char.IsWhiteSpace) && !origin.EndsWith('/') &&
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host.Length > 0 &&
            uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private static bool LoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(Normalize(address)));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // Host/forwarded headers alone never grant management access. LAN access requires a verified login.
    public static bool IsLocalControl(HttpContext ctx) =>
        LanDashboardAuth.IsAuthenticated(ctx) ||
        (ctx.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(Normalize(peer)) && LoopbackHost(ctx.Request.Host.Host));

    private static bool ValidHost(HttpContext ctx, string? listenHost)
    {
        var host = ctx.Request.Host.Host;
        if (LoopbackHost(host)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address) || !IPAddress.TryParse(listenHost, out var binding)) return false;
        if (binding.Equals(IPAddress.Any) || binding.Equals(IPAddress.IPv6Any))
            return ctx.Connection.LocalIpAddress is { } local && Normalize(address).Equals(Normalize(local));
        return Normalize(address).Equals(Normalize(binding));
    }

    // Returns true only when normal routing/authentication should continue.
    public static bool Apply(HttpContext ctx, string[] allowedOrigins, string? listenHost = null)
    {
        if (!ValidHost(ctx, listenHost)) return Reject(ctx, 403);
        var path = ctx.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
        var method = path switch
        {
            "/v1/systemone" => "POST",
            "/v1/models" or "/health" or "/v1/health" => "GET",
            _ => null,
        };
        // Without a verified dashboard login, LAN exposes only inference and minimal metadata.
        // This also protects all future routes unless they are explicitly added above.
        if (!IsLocalControl(ctx) && method is null) return Reject(ctx, 403);
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || origin == $"http://{ctx.Request.Host}") return true;
        var allowAll = allowedOrigins.Contains("*", StringComparer.Ordinal);
        if (!allowAll && !allowedOrigins.Contains(origin, StringComparer.Ordinal)) return Reject(ctx, 403);
        var responseOrigin = allowAll ? "*" : origin;

        // Cross-origin access never includes management routes or the dashboard key.
        if (method is null) return Reject(ctx, 403);
        ctx.Response.Headers.Vary = "Origin, Access-Control-Request-Method, Access-Control-Request-Headers";
        if (HttpMethods.IsOptions(ctx.Request.Method))
        {
            if (ctx.Request.Headers.AccessControlRequestMethod.ToString() != method) return Reject(ctx, 403);
            var requested = ctx.Request.Headers.AccessControlRequestHeaders.ToString();
            if (requested.Length > 4096) return Reject(ctx, 403);
            var headers = requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (headers.Any(header => !Regex.IsMatch(header, "\\A[!#$%&'*+.^_`|~0-9A-Za-z-]+\\z") ||
                header.Equals("X-AutoJev-Control", StringComparison.OrdinalIgnoreCase) ||
                header.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                header.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                header.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase))) return Reject(ctx, 403);
            ctx.Response.Headers.AccessControlAllowOrigin = responseOrigin;
            ctx.Response.Headers.AccessControlAllowMethods = method;
            if (headers.Length > 0) ctx.Response.Headers.AccessControlAllowHeaders = string.Join(", ", headers);
            ctx.Response.Headers.AccessControlMaxAge = "600";
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return false;
        }
        if (ctx.Request.Method != method) return Reject(ctx, 403);
        ctx.Response.Headers.AccessControlAllowOrigin = responseOrigin;
        ctx.Response.Headers.AccessControlExposeHeaders = "X-TypeSafe-Request-Id, X-AutoJev-Provider, X-AutoJev-Cache-Mode, X-AutoJev-Cached-Tokens, X-AutoJev-Cache-Write-Tokens, X-AutoJev-Checkpoint-Bytes, X-AutoJev-Queue-Milliseconds, Retry-After";
        // No Allow-Credentials. The existing API-key authentication still runs.
        return true;
    }

    private static bool Reject(HttpContext ctx, int status)
    {
        ctx.Response.StatusCode = status;
        return false;
    }
}
