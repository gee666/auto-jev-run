using System.Net;
using System.Security.Cryptography;
using System.Text;
using AutoJev.Local;
using Microsoft.AspNetCore.Http;

const string secret = "admin:test-only-not-real-password-123";
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
DefaultHttpContext Context(string path = "/", string? credentials = null, string peer = "192.168.0.20", string? origin = null)
{
    var ctx = new DefaultHttpContext();
    ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
    ctx.Connection.LocalIpAddress = IPAddress.Parse("192.168.0.105");
    ctx.Request.Host = new HostString("192.168.0.105:8765");
    ctx.Request.Path = path;
    ctx.Request.Method = "GET";
    if (credentials is not null) ctx.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
    if (origin is not null) ctx.Request.Headers.Origin = origin;
    return ctx;
}
foreach (var path in new[] { "/", "/usage", "/local/dashboard-key", "/local/keys", "/local/usage", "/local/status" })
{
    var missing = Context(path);
    Check(!LanDashboardAuth.Apply(missing, hash) && missing.Response.StatusCode == 401, "password required");
    Check(missing.Response.Headers.WWWAuthenticate.ToString().StartsWith("Basic "), "browser login challenge");
    var bad = Context(path, "admin:wrong");
    Check(!LanDashboardAuth.Apply(bad, hash) && bad.Response.StatusCode == 401, "wrong password rejected");
    var good = Context(path, secret, origin: "http://192.168.0.105:8765");
    Check(LanDashboardAuth.Apply(good, hash), "correct password accepted");
    Check(BrowserOriginPolicy.Apply(good, ["*"], "0.0.0.0"), "authenticated LAN dashboard allowed");
    var evil = Context(path, secret, origin: "https://evil.invalid");
    Check(LanDashboardAuth.Apply(evil, hash) && !BrowserOriginPolicy.Apply(evil, ["*"], "0.0.0.0"), "cross-origin administration rejected");
    var publicPeer = Context(path, secret, "8.8.8.8");
    Check(!LanDashboardAuth.Apply(publicPeer, hash) && publicPeer.Response.StatusCode == 403, "public peer rejected");
    var disabled = Context(path);
    Check(LanDashboardAuth.Apply(disabled, null) && !BrowserOriginPolicy.Apply(disabled, ["*"], "0.0.0.0"), "LAN dashboard remains opt-in");
}
var api = Context("/v1/systemone");
Check(LanDashboardAuth.Apply(api, hash) && !LanDashboardAuth.IsAuthenticated(api), "API uses existing key authentication");
var forgedHost = Context(); forgedHost.Request.Host = new HostString("localhost:8765");
Check(!LanDashboardAuth.Apply(forgedHost, hash), "forged loopback Host cannot bypass login");
var malformed = Context(); malformed.Request.Headers.Authorization = "Basic !!invalid";
Check(!LanDashboardAuth.Apply(malformed, hash) && malformed.Response.StatusCode == 401, "malformed login rejected");
Console.WriteLine($"LAN dashboard authentication: {checks} checks passed.");
