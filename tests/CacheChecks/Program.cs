using System.Text.Json;
using AutoJev.Local;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
void RejectConfig(string json)
{
    var threw = false;
    try { JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Validate(); }
    catch { threw = true; }
    Check(threw, "invalid configured mode/TTL rejected: " + json);
}
Settings Config(string json)
{
    var cfg = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    cfg.Validate();
    return cfg;
}
void Resolve(string defaultMode, StringValues? requested, StringValues? legacy, string? expected)
{
    var headers = new HeaderDictionary();
    if (requested.HasValue) headers.Add("x-autojev-cache-mode", requested.Value);
    if (legacy.HasValue) headers.Add("x-autojev-prefix-cache", legacy.Value);
    var ok = CacheModePolicy.TryResolve(headers, defaultMode, out var mode, out var error);
    Check(ok == (expected is not null), "resolve acceptance");
    Check(expected is null ? !string.IsNullOrEmpty(error) : mode == expected && error is null, "resolve value/error");
}
foreach (var mode in new[] { "none", "options", "context" })
{
    Check(CacheModePolicy.TryParse(mode, out var parsed) && parsed == mode, "canonical parser");
    Check(CacheModePolicy.TryParse(" " + mode.ToUpperInvariant() + " ", out parsed) && parsed == mode, "case/whitespace normalization");
    Resolve(mode, null, null, mode);
    foreach (var legacy in new[] { "true", "false", "invalid", "1", "0" })
        Resolve("none", mode, legacy, mode);
    Resolve("options", mode, new StringValues(new[] { "true", "false" }), mode);
    Resolve(mode, null, "true", "options");
    Resolve(mode, null, " FALSE ", "none");
    Check(CacheModePolicy.ForRequest(mode, true) == "none", "images bypass each mode");
    Check(CacheModePolicy.ForRequest(mode, false) == mode, "text retains mode");
    foreach (var enabled in new[] { false, true })
    {
        var cfg = Config(JsonSerializer.Serialize(new { cacheMode = mode, prefixCacheEnabled = enabled }));
        Check(cfg.EffectiveCacheMode == mode && cfg.PrefixCacheEnabled == enabled, "explicit setting wins, legacy field preserved");
        var roundtrip = Config(JsonSerializer.Serialize(cfg, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Check(roundtrip.EffectiveCacheMode == mode && roundtrip.PrefixCacheSeconds == 300, "settings roundtrip");
        Check(!JsonSerializer.Serialize(cfg, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Contains("effectiveCacheMode"), "computed setting not persisted");
    }
}
foreach (var invalid in new string?[] { null, "", " ", "default", "server", "true", "false", "1", "0", "context,options", "contexts" })
{
    Check(!CacheModePolicy.TryParse(invalid, out _), "invalid mode rejected");
    if (invalid is not null)
    {
        Resolve("options", invalid, "true", null);
        RejectConfig(JsonSerializer.Serialize(new { cacheMode = invalid }));
    }
}
Resolve("none", new StringValues(new[] { "context", "options" }), "true", null);
Resolve("context", StringValues.Empty, "true", null);
foreach (var legacy in new[] { "", "invalid", "1", "0", "true,false" }) Resolve("context", null, legacy, null);
Resolve("context", null, new StringValues(new[] { "true", "false" }), null);
Check(Config("{}").EffectiveCacheMode == "none", "absent config defaults none");
foreach (var enabled in new[] { false, true })
{
    var expected = enabled ? "options" : "none";
    Check(Config(JsonSerializer.Serialize(new { prefixCacheEnabled = enabled })).EffectiveCacheMode == expected, "legacy config fallback");
    Check(Config(JsonSerializer.Serialize(new { cacheMode = (string?)null, prefixCacheEnabled = enabled })).EffectiveCacheMode == expected, "null config fallback");
}
Check(Config("{\"cacheMode\":\" CONTEXT \",\"prefixCacheSeconds\":1}").CacheMode == "context", "config normalized on validation");
Check(Config("{\"prefixCacheSeconds\":3600}").PrefixCacheSeconds == 3600, "TTL upper bound unchanged");
RejectConfig("{\"cacheMode\":1}");
RejectConfig("{\"prefixCacheSeconds\":0}");
RejectConfig("{\"prefixCacheSeconds\":3601}");

// Request overrides and image bypass must not leak into later requests/defaults.
var shared = Config("{\"cacheMode\":\"context\",\"prefixCacheEnabled\":true}");
foreach (var requested in new[] { "none", "options", "context" })
{
    var headers = new HeaderDictionary { [CacheModePolicy.Header] = requested };
    Check(CacheModePolicy.TryResolve(headers, shared.EffectiveCacheMode, out var requestMode, out _), "request resolves independently");
    Check(CacheModePolicy.ForRequest(requestMode, true) == "none", "image request scope");
    Check(shared.EffectiveCacheMode == "context" && shared.PrefixCacheEnabled, "default unmodified");
    Check(CacheModePolicy.TryResolve(new HeaderDictionary(), shared.EffectiveCacheMode, out var nextMode, out _) && nextMode == "context", "next request uses server default");
}

// New header is allowed/exposed without relaxing management or bypassing auth.
foreach (var origins in new[] { new[] { "*" }, new[] { "https://app.example" } })
{
    DefaultHttpContext Context(string method = "POST", string path = "/v1/systemone")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("localhost:8765");
        ctx.Request.Headers.Origin = "https://app.example";
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        return ctx;
    }
    var request = Context();
    Check(BrowserOriginPolicy.Apply(request, origins), "inference continues to auth");
    foreach (var header in new[] { CacheModePolicy.Header, "X-AutoJev-Cached-Tokens", "X-AutoJev-Cache-Write-Tokens", "X-AutoJev-Checkpoint-Bytes" })
        Check(request.Response.Headers.AccessControlExposeHeaders.ToString().Split(", ").Contains(header), "cache response header exposed");
    Check(request.Response.Headers.AccessControlAllowOrigin == (origins[0] == "*" ? "*" : origins[0]), "wildcard/exact origin preserved");
    Check(!request.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"), "no credential relaxation");
    var preflight = Context("OPTIONS");
    preflight.Request.Headers.AccessControlRequestMethod = "POST";
    preflight.Request.Headers.AccessControlRequestHeaders = "content-type, authorization, x-autojev-cache-mode, x-autojev-prefix-cache";
    Check(!BrowserOriginPolicy.Apply(preflight, origins) && preflight.Response.StatusCode == 204, "cache preflight");
    Check(preflight.Response.Headers.AccessControlAllowHeaders.ToString().Contains("x-autojev-cache-mode"), "mode allowed");
    var management = Context(path: "/local/dashboard-key");
    Check(!BrowserOriginPolicy.Apply(management, origins) && management.Response.StatusCode == 403, "management remains local");
}
Console.WriteLine($"Cache policy/settings/scope/CORS: {checks} checks passed.");
