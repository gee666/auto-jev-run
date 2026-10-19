using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoJev.Local;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal const string ServiceId = "autojev-local-1";

#if WINDOWS
    [STAThread]
#endif
    public static async Task<int> Main(string[] args)
    {
        string? diagnosticRoot = null;
        try
        {
            var rootIndex = Array.IndexOf(args, "--root");
            var root = rootIndex >= 0 && rootIndex + 1 < args.Length
                ? Path.GetFullPath(args[rootIndex + 1]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
#if WINDOWS
            var command = args.FirstOrDefault() ?? "start";
#else
            var command = args.FirstOrDefault() ?? "serve";
#endif
            diagnosticRoot = root;
            StartupLog(root, $"Starting {command}; cwd={Environment.CurrentDirectory}; pid={Environment.ProcessId}.");
            var cfg = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "config", "server.json")), Json)!;
            if (args.Contains("--cpu")) cfg.Mode = "cpu";
            if (args.Contains("--cuda")) cfg.Mode = "cuda";
            cfg.Validate();
            Directory.CreateDirectory(Path.Combine(root, "state"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            var url = $"http://127.0.0.1:{cfg.Port}";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            if (command == "serve") return await Serve(root, cfg, CancellationToken.None);
#if WINDOWS
            if (command == "tray") return TrayHost.Run(root, cfg);
            if (command == "startup")
            {
                if (args.Contains("enable")) StartupRegistration.Set(root, true);
                else if (args.Contains("disable")) StartupRegistration.Set(root, false);
                Console.WriteLine(StartupRegistration.Enabled(root) ? "Windows sign-in startup enabled." : "Windows sign-in startup disabled.");
                return 0;
            }
#else
            if (command is "tray" or "startup" or "settings")
                throw new PlatformNotSupportedException("Tray, sign-in startup and settings UI require Windows. Edit config/server.json and run serve under a service manager on Linux.");
#endif
            if (command == "doctor")
            {
                Console.WriteLine($"Project: {root}\nProvider URL: {url}\nMode: {cfg.Mode}\nContext: {cfg.ContextTokens}");
                var required = new List<string> { cfg.ModelPath, Path.Combine(RuntimeDirectory(root, cfg), BackendExecutable) };
                if (cfg.VisionEnabled) required.AddRange([cfg.VisionProjectorPath, "runtime/media/ffmpeg" + ExecutableSuffix, "runtime/media/ffprobe" + ExecutableSuffix]);
                foreach (var rel in required)
                {
                    var path = Path.Combine(root, rel);
                    if (!File.Exists(path)) throw new FileNotFoundException("Required file is missing", path);
                    Console.WriteLine($"OK {rel} ({new FileInfo(path).Length:N0} bytes)");
                }
                Console.WriteLine("Use 'start' to start the API; the first inference request loads the model.");
                return 0;
            }
            if (command is "status" or "stop" or "restart")
            {
                var status = await Status(client, url, root);
                if (command == "status")
                {
                    Console.WriteLine(status?.ToJsonString(Json) ?? "AutoJev is stopped.");
                    return status?["status"]?.GetValue<string>() is "ready" or "unloaded" or "loading" ? 0 : 1;
                }
                if (status is not null)
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url + "/local/stop");
                    request.Headers.Add("X-AutoJev-Control", File.ReadAllText(Path.Combine(root, "state", "control.token")));
                    using var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    for (var i = 0; i < 100; i++)
                    {
                        await Task.Delay(200);
                        if (await Status(client, url, root) is null) break;
                    }
                    if (await Status(client, url, root) is not null) throw new Exception("Server did not stop. Inspect logs.");
                }
                Console.WriteLine("AutoJev is stopped.");
                if (command == "stop") return 0;
                command = "start";
            }
#if !WINDOWS
            if (command == "start")
            {
                var status = await Status(client, url, root);
                if (status is null) return await Serve(root, cfg, CancellationToken.None);
                if ((args.Contains("--cpu") || args.Contains("--cuda")) && status["mode"]?.GetValue<string>() != cfg.Mode)
                    throw new Exception($"Server is already running in {status["mode"]} mode. Use restart --{cfg.Mode} to switch.");
                Console.WriteLine($"AutoJev is already {status["status"]}. Provider URL: {url}");
                return 0;
            }
#else
            if (command is "start" or "settings")
            {
                var status = await Status(client, url, root);
                if (status is not null && (args.Contains("--cpu") || args.Contains("--cuda")) && status["mode"]?.GetValue<string>() != cfg.Mode)
                    throw new Exception($"Server is already running in {status["mode"]} mode. Use restart --{cfg.Mode} to switch.");
                Process? child = null;
                if (status is null)
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
                    foreach (var arg in new[] { "tray", "--root", root, "--" + cfg.Mode }) psi.ArgumentList.Add(arg);
                    child = Process.Start(psi) ?? throw new Exception("Could not launch AutoJev.");
                    Console.WriteLine($"Starting AutoJev. The model will load on demand. Provider URL: {url}");
                }
                else Console.WriteLine($"AutoJev is already {status["status"]}. Waiting for readiness.");
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < cfg.StartupTimeoutSeconds + 15)
                {
                    status = await Status(client, url, root);
                    var state = status?["status"]?.GetValue<string>();
                    if (state is "ready" or "unloaded" or "loading")
                    {
                        Console.WriteLine($"Ready: {url}\nAPI: {url}/v1/systemone\nPlayground: {url}/\nMode: {status!["mode"]}\nStop: AutoJev.cmd stop");
                        if (args.Contains("--open")) Process.Start(new ProcessStartInfo(url + "/") { UseShellExecute = true });
                        if (command == "settings")
                        {
                            using var settingsRequest = new HttpRequestMessage(HttpMethod.Post, url + "/local/settings/open");
                            settingsRequest.Headers.Add("X-AutoJev-Control", File.ReadAllText(Path.Combine(root, "state", "control.token")));
                            using var settingsResponse = await client.SendAsync(settingsRequest);
                            settingsResponse.EnsureSuccessStatusCode();
                        }
                        return 0;
                    }
                    if (state == "failed") throw new Exception(status?["error"]?.GetValue<string>() ?? "Backend failed.");
                    if (child?.HasExited == true) throw new Exception("Launcher exited before readiness. See logs/launcher.log.");
                    if ((int)watch.Elapsed.TotalSeconds % 15 == 0) Console.WriteLine($"Still loading ({watch.Elapsed.TotalSeconds:F0}s)...");
                    await Task.Delay(1000);
                }
                throw new TimeoutException("Startup wait expired; inspect 'status' and logs before retrying.");
            }
#endif
#if WINDOWS
            Console.WriteLine("AutoJev commands: start [--cpu|--cuda], stop, restart, settings, startup enable|disable|status, status, doctor, serve\nConfiguration: config/server.json");
#else
            Console.WriteLine("AutoJev commands: serve [--cpu|--cuda], start, stop, restart, status, doctor\nLinux start/restart run in the foreground. Configuration: config/server.json. AUTOJEV_HOST defaults to localhost; use an SSH tunnel for remote control.");
#endif
            return command is "help" or "--help" ? 0 : 2;
        }
        catch (Exception ex) { StartupLog(diagnosticRoot, ex.ToString()); Console.Error.WriteLine(ex.Message); return 1; }
    }

    internal static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : "";
    internal static string BackendExecutable => "llama-server" + ExecutableSuffix;
    internal static string RuntimeDirectory(string root, Settings cfg) => Path.Combine(root, "runtime", (OperatingSystem.IsWindows() ? "windows-" : "linux-") + cfg.Mode);
    internal static bool StartupEnabled(string root)
    {
#if WINDOWS
        return StartupRegistration.Enabled(root);
#else
        return false;
#endif
    }

    internal static void WriteSecret(string path, string secret)
    {
        // Set permissions before writing so a permissive umask cannot expose a new token.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new StreamWriter(stream);
        writer.Write(secret);
    }

    internal static void StartupLog(string? root, string message)
    {
        try
        {
            var directory = root is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoJev") : Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoJev");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup.log"), $"{DateTimeOffset.Now:O} root={root} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    internal static async Task<JsonObject?> Status(HttpClient client, string url, string root)
    {
        JsonObject? status;
        try
        {
            using var response = await client.GetAsync(url + "/local/status");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The configured port belongs to a different service.");
            status = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { throw new Exception("The configured port is occupied but is not responding. No process was stopped."); }
        catch (JsonException) { throw new Exception("The configured port belongs to a different service. No process was stopped."); }
        if (status?["service"]?.GetValue<string>() != ServiceId || !string.Equals(status["root"]?.GetValue<string>(), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new Exception("The configured port belongs to another service/project. Change config/server.json.");
        return status;
    }

    internal static async Task<int> Serve(string root, Settings cfg, CancellationToken stop, Action? openSettings = null)
    {
        using var instance = new FileStream(Path.Combine(root, "state", "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var logPath = Path.Combine(root, "logs", "launcher.log");
        using var writer = new RollingLogWriter(logPath);
        Console.SetOut(TextWriter.Synchronized(writer));
        Console.SetError(Console.Out);
        var control = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        WriteSecret(Path.Combine(root, "state", "control.token"), control);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            var host = cfg.ListenHost;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) options.ListenLocalhost(cfg.Port);
            else
            {
                var address = IPAddress.Parse(host);
                options.Listen(address, cfg.Port);
                // CLI/tray control always connects through IPv4 loopback. Wildcards already include it.
                if (!address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                    options.Listen(IPAddress.Loopback, cfg.Port);
            }
            options.Limits.MaxRequestBodySize = cfg.MaxBodyBytes;
        });
        await using var app = builder.Build();
        using var usage = new UsageStore(root);
        using var backend = new Backend(root, cfg);
        var bridge = new Bridge(backend, cfg, app.Lifetime.ApplicationStopping);
        app.Use(async (ctx, next) =>
        {
            if (!BrowserOriginPolicy.Apply(ctx, cfg.EffectiveAllowedOrigins, cfg.ListenHost)) return;
            ctx.Response.Headers["X-TypeSafe-Request-Id"] = Guid.NewGuid().ToString("N");
            ctx.Response.Headers["X-AutoJev-Provider"] = "local-autojev-27b-q4_k_m";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            UsageMeasurement? measurement = null;
            try
            {
                if (HttpMethods.IsPost(ctx.Request.Method) && string.Equals(ctx.Request.Path.Value?.TrimEnd('/'), "/v1/systemone", StringComparison.OrdinalIgnoreCase))
                {
                    if (ctx.Request.Headers.Authorization.Count == 0 && ctx.Request.Headers["X-API-Key"].Count == 0)
                    { await Error(ctx, 401, "A valid local API key is required."); return; }
                    measurement = new UsageMeasurement();
                    var identity = usage.Begin(measurement, ctx);
                    ctx.Items[UsageMeasurement.Item] = measurement;
                    ctx.Response.Headers["X-TypeSafe-Request-Id"] = measurement.Id;
                    if (!identity.Allowed) { await Error(ctx, 401, "A valid local API key is required. Missing, unknown, revoked or deleted keys are rejected."); return; }
                }
                await next(ctx);
            }
            catch (BadHttpRequestException ex) { await Error(ctx, ex.StatusCode, "Invalid or oversized HTTP request."); }
            catch (JsonException) { await Error(ctx, 400, "Request must be a valid JSON object."); }
            catch (OperationCanceledException) { if (!ctx.RequestAborted.IsCancellationRequested) await Error(ctx, 504, "Local inference timed out."); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{DateTimeOffset.Now:O} {ex.GetType().Name}: {ex.Message}");
                if (!ctx.Response.HasStarted) await Error(ctx, 500, "Local server error. See logs/launcher.log.");
            }
            finally { if (measurement is not null) usage.Finish(measurement, ctx.Response.StatusCode, ctx.RequestAborted.IsCancellationRequested); }
        });
        usage.Map(app);
        object View()
        {
            var queue = bridge.QueueStatus;
            return new { service = ServiceId, usageTrackingError = usage.LastError, root, status = backend.State, error = backend.ErrorMessage, mode = cfg.Mode, model = "autojev-27b-q4_k_m", contextTokens = cfg.ContextTokens, pid = Environment.ProcessId, backendPid = backend.Pid, url = $"http://127.0.0.1:{cfg.Port}", queuedRequests = queue.Active + queue.Waiting, activeRequests = queue.Active, waitingRequests = queue.Waiting, maxQueuedRequests = cfg.MaxQueuedRequests, queueTimeoutSeconds = cfg.QueueTimeoutSeconds, requestTimeoutSeconds = cfg.RequestTimeoutSeconds, cacheMode = cfg.EffectiveCacheMode, prefixCacheEnabled = cfg.PrefixCacheEnabled, prefixCacheSeconds = cfg.PrefixCacheSeconds, visionEnabled = cfg.VisionEnabled, maxImages = cfg.VisionEnabled ? 4 : 0, imageMaxTokens = cfg.ImageMaxTokens, maxBodyBytes = cfg.MaxBodyBytes, idleUnloadSeconds = cfg.IdleUnloadSeconds, startupEnabled = StartupEnabled(root), tray = openSettings is not null };
        }
        app.MapGet("/local/status", () => Results.Json(View()));
        foreach (var path in new[] { "/health", "/v1/health" }) app.MapGet(path, (HttpContext ctx) =>
            Results.Json(BrowserOriginPolicy.IsLocalControl(ctx) ? View() : new { service = ServiceId, status = backend.State }, statusCode: backend.State == "failed" ? 503 : 200));
        app.MapGet("/v1/models", () => Results.Json(new { models = new[] { "autojev", "autojev-27b-q4_k_m", "jev-latest", "jev-preview", "jev-1.13.0" }.Select(name => new { name, release_date = "2026-09-26", description = "Local AutoJev-27B Q4_K_M. Jev names are compatibility aliases, not the hosted Jev model." }) }));
        app.MapGet("/usage", () => Results.Content(File.ReadAllText(Path.Combine(root, "app", "usage.html")), "text/html"));
        app.MapGet("/docs", () => Results.Content(File.ReadAllText(Path.Combine(root, "app", "api.md")), "text/markdown; charset=utf-8"));
        app.MapGet("/", () => Results.Content(File.ReadAllText(Path.Combine(root, "app", "index.html")), "text/html"));
        app.MapPost("/v1/systemone", bridge.Handle);
        app.MapPost("/local/settings/open", async ctx =>
        {
            if (ctx.Request.Headers["X-AutoJev-Control"].ToString() != control) { await Error(ctx, 403, "Use the tray menu or AutoJev.cmd settings."); return; }
            if (openSettings is null) { await Error(ctx, 409, "Settings require tray mode."); return; }
            openSettings();
            await ctx.Response.WriteAsJsonAsync(new { status = "opened" });
        });
        app.MapPost("/local/unload", async ctx =>
        {
            if (ctx.Request.Headers["X-AutoJev-Control"].ToString() != control) { await Error(ctx, 403, "Use the tray menu."); return; }
            if (!await bridge.UnloadIfIdle()) { await Error(ctx, 409, "Requests are active. Try again after they finish."); return; }
            await ctx.Response.WriteAsJsonAsync(new { status = backend.State });
        });
        app.MapPost("/local/stop", async ctx =>
        {
            if (ctx.Request.Headers["X-AutoJev-Control"].ToString() != control) { await Error(ctx, 403, "Use AutoJev.cmd stop to stop this server."); return; }
            await ctx.Response.WriteAsJsonAsync(new { status = "stopping" });
            _ = Task.Run(async () => { await Task.Delay(200); app.Lifetime.StopApplication(); });
        });
        app.Lifetime.ApplicationStopping.Register(backend.Stop);
        using var stopRegistration = stop.Register(app.Lifetime.StopApplication);
        try
        {
            await app.StartAsync();
            Console.WriteLine($"{DateTimeOffset.Now:O} API listening on {cfg.ListenHost}:{cfg.Port}; model unloaded, idle timeout {cfg.IdleUnloadSeconds}s.");
            if (!cfg.ListenHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !IPAddress.IsLoopback(IPAddress.Parse(cfg.ListenHost)))
                Console.WriteLine("WARNING: LAN API uses unencrypted HTTP. Use a trusted network/firewall or prefer loopback with an SSH tunnel. Dashboard and control routes accept loopback connections only. Do not proxy untrusted traffic to loopback.");
            var idle = bridge.WatchIdle(app.Lifetime.ApplicationStopping);
            await app.WaitForShutdownAsync();
            await idle;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally { backend.Stop(); }
        return 0;
    }

    internal static async Task Error(HttpContext ctx, int code, string message)
    {
        if (ctx.Response.HasStarted || ctx.RequestAborted.IsCancellationRequested) return;
        ctx.Response.StatusCode = code;
        await ctx.Response.WriteAsJsonAsync(new { error = new { message, type = "local_api_error", code }, detail = message });
    }
}

internal sealed class Backend(string root, Settings cfg) : IDisposable
{
    private Process? process;
#if WINDOWS
    private WindowsJob? job;
#endif
    private TextWriter? log;
    private volatile string state = "unloaded";
    private string? error;
    private readonly object sync = new();
    public readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false })
    { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri($"http://127.0.0.1:{cfg.BackendPort}") };
    public string State { get { lock (sync) { if (state == "ready" && process is { HasExited: true }) { state = "failed"; error = "Inference process exited. The next request will retry."; } return state; } } }
    public string? ErrorMessage => error;
    public bool Ready => State == "ready";
    public int? Pid { get { lock (sync) return process is { HasExited: false } ? process.Id : null; } }

    // The bridge holds its inference gate for loading, inference and unloading.
    public async Task StartAsync(CancellationToken stop)
    {
        if (Ready) return;
        Stop();
        try
        {
            state = "loading"; error = null;
            var model = Path.GetFullPath(Path.Combine(root, cfg.ModelPath));
            if (!File.Exists(model)) throw new FileNotFoundException("Model file missing", model);
            var runtime = Program.RuntimeDirectory(root, cfg);
            var probe = new TcpListener(IPAddress.Loopback, cfg.BackendPort);
            try { probe.Start(); } finally { probe.Stop(); }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var keyPath = Path.Combine(root, "state", "backend.token");
            Program.WriteSecret(keyPath, token);
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var psi = new ProcessStartInfo(Path.Combine(runtime, Program.BackendExecutable))
            { WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var args = new List<string> { "-m", model, "--system-one", "--host", "127.0.0.1", "--port", cfg.BackendPort.ToString(), "--api-key-file", keyPath, "-c", cfg.ContextTokens.ToString(), "-np", "1", "-t", cfg.Threads.ToString(), "-tb", cfg.Threads.ToString(), "-b", "2048", "-ub", "512" };
            if (cfg.VisionEnabled)
            {
                var projector = Path.GetFullPath(Path.Combine(root, cfg.VisionProjectorPath));
                if (!File.Exists(projector)) throw new FileNotFoundException("Vision component missing. Run scripts/install_vision.py or disable images in Settings.", projector);
                args.AddRange(["--mmproj", projector, "--image-min-tokens", "64", "--image-max-tokens", cfg.ImageMaxTokens.ToString()]);
                args.AddRange(["--video-ffmpeg-dir", Path.Combine(root, "runtime", "media")]);
                if (cfg.Mode == "cpu") args.Add("--no-mmproj-offload");
            }
            args.AddRange(cfg.Mode == "cuda" ? ["--fit", "on", "--fit-target", cfg.GpuReserveMiB.ToString()] : ["-ngl", "0", "--fit", "off"]);
            args.AddRange(["--cache-ram", "0"]);
            psi.Environment["AUTOJEV_CACHE_MODE"] = cfg.EffectiveCacheMode;
            psi.Environment["AUTOJEV_OPTIONS_FIRST"] = cfg.EffectiveCacheMode == "options" ? "1" : "0";
            psi.Environment["AUTOJEV_PREFIX_CACHE"] = cfg.EffectiveCacheMode == "options" ? "1" : "0";
            psi.Environment["AUTOJEV_PREFIX_CACHE_SECONDS"] = cfg.PrefixCacheSeconds.ToString();
            foreach (var arg in args) psi.ArgumentList.Add(arg);
#if WINDOWS
            psi.Environment["PATH"] = runtime + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System) + ";" + Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            psi.Environment["TEMP"] = psi.Environment["TMP"] = Path.Combine(root, "state");
#else
            // Keep system tools and CUDA dependencies available on Linux.
            psi.Environment["PATH"] = runtime + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            psi.Environment["LD_LIBRARY_PATH"] = runtime + (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") is { Length: > 0 } libraries ? Path.PathSeparator + libraries : "");
            var temp = Path.Combine(root, "tmp");
            Directory.CreateDirectory(temp);
            psi.Environment["TMPDIR"] = psi.Environment["TEMP"] = psi.Environment["TMP"] = temp;
#endif
            psi.Environment["LLAMA_CACHE"] = Path.Combine(root, "state", "cache");
            Process running;
            lock (sync)
            {
                stop.ThrowIfCancellationRequested();
                log = TextWriter.Synchronized(new RollingLogWriter(Path.Combine(root, "logs", "backend.log")));
                var output = log;
#if WINDOWS
                job = new WindowsJob();
#endif
                running = new Process { StartInfo = psi };
                process = running;
                running.OutputDataReceived += (_, e) => { if (e.Data is not null) output.WriteLine(e.Data); };
                running.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.WriteLine(e.Data); };
                running.Start();
#if WINDOWS
                job.Add(running);
#endif
                running.BeginOutputReadLine(); running.BeginErrorReadLine();
            }
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < cfg.StartupTimeoutSeconds)
            {
                stop.ThrowIfCancellationRequested();
                lock (sync) { if (process != running || running.HasExited) throw new Exception("Inference process exited during loading. See logs/backend.log."); }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await Client.GetAsync("/health", timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        lock (sync) { stop.ThrowIfCancellationRequested(); if (process != running) throw new OperationCanceledException(); state = "ready"; }
                        Console.WriteLine($"{DateTimeOffset.Now:O} Model ready after {watch.Elapsed.TotalSeconds:F1}s.");
                        return;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                await Task.Delay(500, stop);
            }
            throw new TimeoutException("Model startup timeout. Inspect logs/backend.log.");
        }
        catch (OperationCanceledException) { Stop(); throw; }
        catch (Exception ex) { Stop(); error = ex.Message; state = "failed"; Console.Error.WriteLine(ex.Message); throw; }
    }

    public void Stop()
    {
        lock (sync)
        {
            state = "unloading";
            try
            {
                if (process is not null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); }
                    catch (InvalidOperationException) { /* Process creation failed before a PID was assigned. */ }
                }
            }
            finally
            {
#if WINDOWS
                job?.Dispose(); job = null;
#endif
                process?.Dispose(); process = null;
                log?.Dispose(); log = null;
                state = "unloaded";
            }
        }
    }
    public void Dispose() { Stop(); Client.Dispose(); }
}


internal sealed class Bridge(Backend backend, Settings cfg, CancellationToken stopping)
{
    private readonly FifoGate gate = new(cfg.MaxQueuedRequests);
    private long lastUse = Stopwatch.GetTimestamp();
    public (int Active, int Waiting) QueueStatus => gate.Snapshot;
    public Task<bool> UnloadIfIdle(bool requireExpired = false)
    {
        using var lease = gate.TryEnterIdle();
        if (lease is null) return Task.FromResult(false);
        if (requireExpired && Stopwatch.GetElapsedTime(lastUse).TotalSeconds < cfg.IdleUnloadSeconds) return Task.FromResult(false);
        if (backend.State is "ready" or "failed")
        {
            backend.Stop();
            Console.WriteLine($"{DateTimeOffset.Now:O} Model unloaded; API remains available.");
        }
        return Task.FromResult(true);
    }
    public async Task WatchIdle(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(stop)) await UnloadIfIdle(requireExpired: true); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async Task Handle(HttpContext ctx)
    {
        if (!ctx.Request.HasJsonContentType()) { await Program.Error(ctx, 415, "Content-Type must be application/json."); return; }
        if (ctx.Request.ContentLength > cfg.MaxBodyBytes) { await Program.Error(ctx, 413, "Request body exceeds the local size limit."); return; }
        if (!CacheModePolicy.TryResolve(ctx.Request.Headers, cfg.EffectiveCacheMode, out var cacheMode, out var cacheError))
        { await Program.Error(ctx, 422, cacheError!); return; }
        ctx.Response.Headers[CacheModePolicy.Header] = "none";
        FifoGate.Lease? lease = null;
        var usedBackend = false;
        var queuedAt = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        if (cfg.QueueTimeoutSeconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(cfg.QueueTimeoutSeconds));
        try
        {
            lease = await gate.EnterAsync(timeout.Token);
            if (UsageMeasurement.From(ctx) is { } entered) entered.QueueMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
            ctx.Response.Headers["X-AutoJev-Queue-Milliseconds"] = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            // Waiting has its own deadline. Parse only the active request so a
            // large backlog does not retain a JSON document per waiting caller.
            timeout.CancelAfter(TimeSpan.FromSeconds(cfg.RequestTimeoutSeconds));
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: timeout.Token) as JsonObject;
            if (body is null) { await Program.Error(ctx, 400, "Request must be a JSON object."); return; }
            if (body["model"] is not null && (body["model"] is not JsonValue mv || !mv.TryGetValue<string>(out _))) { await Program.Error(ctx, 422, "model must be a string."); return; }
            var model = body["model"]?.GetValue<string>() ?? "jev-latest";
            if (!(model is "autojev" or "autojev-27b" or "autojev-27b-q4_k_m" || model.StartsWith("jev-", StringComparison.Ordinal))) { await Program.Error(ctx, 404, "Unknown local model. Use autojev or a jev-* compatibility alias."); return; }
            if (body.ContainsKey("images"))
            {
                if (body["images"] is not JsonArray images || images.Count > 4) { await Program.Error(ctx, 422, "images must be an array containing at most four PNG, JPEG, or WebP data URIs."); return; }
                if (images.Count > 0 && !cfg.VisionEnabled) { await Program.Error(ctx, 422, "Image input is disabled. Enable images in tray Settings."); return; }
                foreach (var image in images)
                {
                    if (image is not JsonValue iv || !iv.TryGetValue<string>(out var uri) || uri is null) { await Program.Error(ctx, 422, "Each image must be a base64 data URI."); return; }
                    var comma = uri.IndexOf(',');
                    if (comma < 0 || uri[..comma] is not ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/webp;base64")) { await Program.Error(ctx, 422, "Images must be PNG, JPEG, or WebP base64 data URIs; URLs and file paths are not accepted."); return; }
                    if (uri.Length - comma - 1 > 12_000_000) { await Program.Error(ctx, 413, "An encoded image exceeds 12 MB."); return; }
                    try
                    {
                        var decoded = Convert.FromBase64String(uri[(comma + 1)..]);
                        if (decoded.Length == 0 || decoded.Length > 8_000_000) { await Program.Error(ctx, 413, "Each image must contain between 1 byte and 8 MB of data."); return; }
                    }
                    catch (FormatException) { await Program.Error(ctx, 422, "Image data contains invalid base64."); return; }
                }
            }
            cacheMode = CacheModePolicy.ForRequest(cacheMode, body["images"] is JsonArray { Count: > 0 });
            body["model"] = "autojev";
            if (body["questions"] is not JsonObject questions || questions.Count == 0) { await Program.Error(ctx, 422, "questions must be a nonempty object."); return; }
            if (body["state"] is not (JsonObject or JsonArray) && !(body["state"] is JsonValue sv && sv.TryGetValue<string>(out _))) { await Program.Error(ctx, 422, "state must be text, an object, or an array."); return; }
            // TypeSafe permits a one-level score. It is deterministic and needs no inference.
            var deterministic = new JsonObject();
            foreach (var (id, q) in questions.ToArray())
            {
                if (q is JsonObject qo && qo["type"]?.ToString() == "score" && qo["criteria"] is JsonArray { Count: 1 } levels)
                {
                    if (levels[0] is null || levels[0] is JsonValue value && !value.TryGetValue<string>(out _)) { await Program.Error(ctx, 422, "Score level must be text, an object, or an array."); return; }
                    deterministic[id] = new JsonObject { ["type"] = "score", ["score"] = 0.0, ["confidence"] = 1.0, ["legend"] = new JsonObject { ["0"] = levels[0]!.DeepClone() }, ["probabilities"] = new JsonObject { ["0"] = 1.0 } };
                    questions.Remove(id);
                }
            }
            JsonObject result;
            if (questions.Count == 0) result = new JsonObject { ["answers"] = new JsonObject(), ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 } };
            else
            {
                usedBackend = true;
                try { await backend.StartAsync(timeout.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { await Program.Error(ctx, 503, backend.ErrorMessage ?? "Model could not load. Check logs/backend.log."); return; }
                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/systemone") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                request.Headers.Add(CacheModePolicy.Header, cacheMode);
                request.Headers.Add(CacheModePolicy.LegacyHeader, cacheMode == "options" ? "1" : "0");
                using var response = await backend.Client.SendAsync(request, timeout.Token);
                // Only native can confirm an applied text-cache mode. Older runtimes
                // without this response header leave the playground indicator unknown.
                if (body["images"] is not JsonArray { Count: > 0 })
                {
                    ctx.Response.Headers.Remove(CacheModePolicy.Header);
                    if (response.Headers.TryGetValues(CacheModePolicy.Header, out var appliedValues) &&
                        CacheModePolicy.TryParse(string.Join(",", appliedValues), out var appliedMode))
                        ctx.Response.Headers[CacheModePolicy.Header] = appliedMode;
                }
                foreach (var header in new[] { "X-AutoJev-Cached-Tokens", "X-AutoJev-Cache-Write-Tokens", "X-AutoJev-Checkpoint-Bytes" })
                    if (response.Headers.TryGetValues(header, out var values)) ctx.Response.Headers[header] = values.First();
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode) { ctx.Response.StatusCode = (int)response.StatusCode; ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsync(text, timeout.Token); return; }
                result = JsonNode.Parse(text)!.AsObject();
            }
            foreach (var (id, answer) in deterministic) result["answers"]![id] = answer!.DeepClone();
            result["model"] = "autojev-27b-q4_k_m";
            if (UsageMeasurement.From(ctx) is { } measured)
            {
                measured.InputTokens = long.TryParse(result["usage"]?["input_tokens"]?.ToString(), out var inputCount) ? inputCount : null;
                measured.OutputTokens = long.TryParse(result["usage"]?["output_tokens"]?.ToString(), out var outputCount) ? outputCount : null;
                measured.CacheReadTokens = !usedBackend ? 0 : long.TryParse(ctx.Response.Headers["X-AutoJev-Cached-Tokens"], out var read) ? read : null;
                measured.CacheWriteTokens = !usedBackend ? 0 : long.TryParse(ctx.Response.Headers["X-AutoJev-Cache-Write-Tokens"], out var written) ? written : null;
            }
            await ctx.Response.WriteAsJsonAsync(result, timeout.Token);
        }
        catch (FifoGate.QueueFullException) { ctx.Response.Headers.RetryAfter = "2"; await Program.Error(ctx, 429, "Local request queue is full. Retry after the indicated delay."); }
        catch (OperationCanceledException) when (lease is null && !ctx.RequestAborted.IsCancellationRequested && !stopping.IsCancellationRequested)
        { await Program.Error(ctx, 504, "Timed out waiting in the local request queue."); }
        catch (HttpRequestException) { await Program.Error(ctx, 502, "Inference backend disconnected. Check /local/status and logs/backend.log."); }
        finally
        {
            if (lease is null && UsageMeasurement.From(ctx) is { } waiting) waiting.QueueMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
            if (usedBackend) lastUse = Stopwatch.GetTimestamp(); lease?.Dispose();
        }
    }
}
