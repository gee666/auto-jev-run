using AutoJev.Local;
using Microsoft.AspNetCore.Http;

const string Allowed = "chrome-extension://igkjmmohlilflmpifdodcanndbniocnb";
string[] origins = [Allowed];
var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
DefaultHttpContext Context(string origin = Allowed, string path = "/v1/systemone", string method = "POST", string host = "localhost:8765")
{
    var ctx = new DefaultHttpContext();
    ctx.Request.Host = new HostString(host);
    ctx.Request.Path = path;
    ctx.Request.Method = method;
    if (origin.Length > 0) ctx.Request.Headers.Origin = origin;
    return ctx;
}
foreach (var origin in new[] { "", "http://localhost:8765", Allowed })
{
    var ctx = Context(origin);
    Check(BrowserOriginPolicy.Apply(ctx, origins), "allowed origin reaches authentication");
    Check(ctx.Response.Headers.AccessControlAllowOrigin.ToString() == (origin == Allowed ? Allowed : ""), "exact ACAO");
    Check(!ctx.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"), "no cookie credentials");
}
foreach (var origin in new[] { "null", "https://other.test", "http://localhost:8766", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Allowed + "/", Allowed + ".evil", Allowed + ", https://other.test" })
{
    var ctx = Context(origin);
    Check(!BrowserOriginPolicy.Apply(ctx, origins) && ctx.Response.StatusCode == 403, "exact list rejects others");
    Check(!ctx.Response.Headers.ContainsKey("Access-Control-Allow-Origin"), "no rejected origin reflection");
}
foreach (var origin in new[] { "https://my-app.example", "http://localhost:3030", "http://192.168.1.5:8080", "moz-extension://my-app", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "null" })
{
    BrowserOriginPolicy.Validate([origin]);
    var exact = Context(origin);
    Check(BrowserOriginPolicy.Apply(exact, [origin]), "general exact origin allowed");
    Check(exact.Response.Headers.AccessControlAllowOrigin == origin, "general exact ACAO");
    var wildcard = Context(origin);
    Check(BrowserOriginPolicy.Apply(wildcard, ["*"]), "wildcard allows any app origin");
    Check(wildcard.Response.Headers.AccessControlAllowOrigin == "*", "wildcard ACAO");
    Check(!wildcard.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"), "wildcard has no cookie credentials");
}
foreach (var allowedList in new[] { origins, new[] { "*" } })
{
    foreach (var path in new[] { "/local/dashboard-key", "/local/keys", "/local/usage", "/local/settings/open", "/local/stop", "/local/unload", "/local/status", "/usage", "/", "/v1/systemone/anything" })
        Check(!BrowserOriginPolicy.Apply(Context(path: path), allowedList), "cross-origin management remains unavailable: " + path);
    Check(!BrowserOriginPolicy.Apply(Context(host: "other.test:8765"), allowedList), "Host restriction unchanged");
    Check(!BrowserOriginPolicy.Apply(Context(method: "GET"), allowedList), "wrong method rejected");
    foreach (var path in new[] { "/v1/models", "/health", "/v1/health" })
        Check(BrowserOriginPolicy.Apply(Context(path: path, method: "GET"), allowedList), "metadata route: " + path);
    var preflight = Context(method: "OPTIONS");
    preflight.Request.Headers.AccessControlRequestMethod = "POST";
    preflight.Request.Headers.AccessControlRequestHeaders = "authorization, content-type, x-api-key, x-autojev-prefix-cache, x-app";
    Check(!BrowserOriginPolicy.Apply(preflight, allowedList) && preflight.Response.StatusCode == 204, "preflight terminates without inference/auth");
    Check(preflight.Response.Headers.AccessControlAllowOrigin == (allowedList.Contains("*") ? "*" : Allowed), "preflight ACAO");
    Check(preflight.Response.Headers.AccessControlAllowHeaders.ToString().Contains("authorization"), "auth header allowed");
    Check(preflight.Response.Headers.AccessControlAllowMethods == "POST", "only endpoint method");
    foreach (var requested in new[] { "x-autojev-control", "cookie", "proxy-authorization", "sec-fetch-site", "bad header", "x-ok\r\nx-bad" })
    {
        var ctx = Context(method: "OPTIONS");
        ctx.Request.Headers.AccessControlRequestMethod = "POST";
        ctx.Request.Headers.AccessControlRequestHeaders = requested;
        Check(!BrowserOriginPolicy.Apply(ctx, allowedList) && ctx.Response.StatusCode == 403, "unsafe preflight header");
    }
}
Check(!BrowserOriginPolicy.Apply(Context(), []), "empty allowlist denies cross-origin");
var wrongMethod = Context(method: "OPTIONS");
wrongMethod.Request.Headers.AccessControlRequestMethod = "DELETE";
Check(!BrowserOriginPolicy.Apply(wrongMethod, ["*"]) && wrongMethod.Response.StatusCode == 403, "preflight method restricted");
foreach (var valid in new[] { origins, Array.Empty<string>(), new[] { "*" }, new[] { "https://app.example", "http://localhost:3030", Allowed }, new[] { "null" } })
    BrowserOriginPolicy.Validate(valid);
foreach (string[]? invalid in new string[]?[] { null, ["https://*.example"], [Allowed + "/"], ["not an origin"], ["https://app.example/path"], ["https://user:pass@app.example"], ["https://app.example?x=1"], ["https://app.example#fragment"], ["https://app.example\r\nX:evil"], [null!] })
{
    var threw = false;
    try { BrowserOriginPolicy.Validate(invalid); } catch { threw = true; }
    Check(threw, "invalid origin rejected at startup");
}
Console.WriteLine($"Origin policy: {checks} checks passed.");
