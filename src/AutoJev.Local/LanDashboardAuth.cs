using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AutoJev.Local;

internal static class LanDashboardAuth
{
    private static readonly object Authenticated = new();
    public static bool IsAuthenticated(HttpContext ctx) => ctx.Items.ContainsKey(Authenticated);

    public static byte[]? LoadPasswordHash()
    {
        var file = Environment.GetEnvironmentVariable("AUTOJEV_DASHBOARD_PASSWORD_FILE");
        if (string.IsNullOrWhiteSpace(file)) return null;
        var password = File.ReadAllText(file).Trim();
        if (password.Length < 16) throw new Exception("Dashboard password must contain at least 16 characters.");
        return SHA256.HashData(Encoding.UTF8.GetBytes("admin:" + password));
    }

    public static bool Apply(HttpContext ctx, byte[]? passwordHash)
    {
        if (passwordHash is null || BrowserOriginPolicy.IsLocalControl(ctx)) return true;
        var path = ctx.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
        // Inference keeps its existing Bearer/X-API-Key authentication.
        if (path is "/v1/systemone" or "/v1/models" or "/health" or "/v1/health") return true;
        if (!PrivatePeer(ctx.Connection.RemoteIpAddress)) { ctx.Response.StatusCode = 403; return false; }
        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) && authorization.Length < 2048)
        {
            try
            {
                var credentials = Convert.FromBase64String(authorization[6..].Trim());
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(credentials), passwordHash))
                {
                    ctx.Items[Authenticated] = true;
                    return true;
                }
            }
            catch (FormatException) { }
        }
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"AutoJev dashboard\", charset=\"UTF-8\"";
        ctx.Response.Headers.CacheControl = "no-store";
        return false;
    }

    private static bool PrivatePeer(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            : (bytes[0] & 0xfe) == 0xfc || ip.IsIPv6LinkLocal;
    }
}
