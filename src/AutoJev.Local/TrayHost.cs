using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AutoJev.Local;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Name(string root) => "AutoJev-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant())))[..8];
    public static bool Enabled(string root)
    {
        try
        {
            var xml = WithFolder(folder =>
            {
                dynamic? task = null;
                try { task = folder.GetTask(TaskName(root)); return (string)task.Xml; }
                catch (Exception ex) when ((uint)ex.HResult == 0x80070002) { return ""; }
                finally { Release(task); }
            });
            if (xml.Length > 0)
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                var ns = doc.Root!.Name.Namespace;
                return doc.Root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value != "false";
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name(root)) is string value && value == Command(root);
    }
    private static string TaskName(string root) => Name(root) + "-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    private static void Release(object? value) { if (value is not null && System.Runtime.InteropServices.Marshal.IsComObject(value)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(value); }
    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        dynamic? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            service!.Connect(); folder = service.GetFolder(@"\");
            return action(folder);
        }
        finally { Release(folder); Release(service); }
    }
    private static string Command(string root) => $"\"{Path.Combine(root, "app", "AutoJev.exe")}\" tray --root \"{root}\"";
    public static void Set(string root, bool enabled)
    {
        WithFolder(folder =>
        {
            if (enabled)
            {
                string E(string text) => System.Security.SecurityElement.Escape(text)!;
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                var xml = $"""
                    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                      <RegistrationInfo><Description>AutoJev local tray and API. Loads model only on demand.</Description></RegistrationInfo>
                      <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT15S</Delay><UserId>{E(sid)}</UserId></LogonTrigger></Triggers>
                      <Principals><Principal id="User"><UserId>{E(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                      <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>
                      <Actions Context="User"><Exec><Command>{E(Path.Combine(root, "app", "AutoJev.exe"))}</Command><Arguments>tray --root &quot;{E(root)}&quot;</Arguments><WorkingDirectory>{E(root)}</WorkingDirectory></Exec></Actions>
                    </Task>
                    """;
                object task = folder.RegisterTask(TaskName(root), xml, 6, sid, null, 3, null);
                Release(task);
            }
            else
            {
                try { folder.DeleteTask(TaskName(root), 0); }
                catch (Exception ex) when ((uint)ex.HResult == 0x80070002) { }
            }
            return true;
        });
        // Remove the legacy entry only after task registration/removal succeeds.
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.DeleteValue(Name(root), throwOnMissingValue: false);
    }
}

internal sealed class TrayHost : Forms.ApplicationContext
{
    private readonly string root;
    private Settings cfg;
    private readonly Forms.Control dispatcher = new();
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem startup = new("Start when I sign in");
    private readonly Forms.ToolStripMenuItem statusItem = new("Model unloaded") { Enabled = false };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Forms.Timer timer = new() { Interval = 5000 };
    private CancellationTokenSource? serverStop;
    private Task<int>? server;
    private SettingsForm? settings;
    private bool restarting, quitting, refreshing, disposed;
    private Drawing.Icon? icon;
    private int exitCode;

    public static int Run(string root, Settings cfg)
    {
        using var check = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        if (Program.Status(check, $"http://127.0.0.1:{cfg.Port}", root).GetAwaiter().GetResult() is not null) return 0;
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var host = new TrayHost(root, cfg);
        Forms.Application.Run(host);
        return host.exitCode;
    }

    private TrayHost(string root, Settings cfg)
    {
        this.root = root; this.cfg = cfg;
        _ = dispatcher.Handle;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open web interface", null, (_, _) => OpenWeb());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        startup.Checked = StartupRegistration.Enabled(root);
        startup.Click += (_, _) => Guard(() => { StartupRegistration.Set(root, !StartupRegistration.Enabled(root)); startup.Checked = StartupRegistration.Enabled(root); });
        menu.Items.Add(startup);
        menu.Items.Add("Unload model now", null, async (_, _) => await Unload());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit AutoJev", null, async (_, _) => await Quit());
        icon = CreateIcon();
        tray = new Forms.NotifyIcon { Icon = icon, Text = "AutoJev - model unloaded", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenWeb();
        menu.Opening += async (_, _) => await Refresh();
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        StartServer();
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);
        using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(35, 75, 61));
        g.FillEllipse(background, 1, 1, 30, 30);
        using var font = new Drawing.Font("Segoe UI", 18, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var foreground = new Drawing.SolidBrush(Drawing.Color.FromArgb(175, 240, 204));
        g.DrawString("A", font, foreground, 7, 3);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void StartServer()
    {
        serverStop?.Dispose();
        serverStop = new CancellationTokenSource();
        var token = serverStop.Token;
        var activeConfig = cfg;
        server = Task.Run(() => Program.Serve(root, activeConfig, token, () => Post(OpenSettings)));
        var running = server;
        _ = running.ContinueWith(t => Post(() =>
        {
            if (restarting || quitting || server != running) return;
            if (t.IsFaulted || t.Result != 0)
            {
                exitCode = 1;
                Program.StartupLog(root, t.Exception?.GetBaseException().ToString() ?? "Server exited with failure. See launcher.log.");
            }
            ExitThread();
        }), TaskScheduler.Default);
    }
    private void Post(Action action) { if (!dispatcher.IsDisposed) { try { dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { } } }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Forms.MessageBox.Show(ex.Message, "AutoJev", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error); } }
    private string Url => $"http://127.0.0.1:{cfg.Port}";
    private void OpenWeb() => Guard(() => Process.Start(new ProcessStartInfo($"http://localhost:{cfg.Port}/") { UseShellExecute = true }));
    private void OpenSettings()
    {
        if (quitting || restarting) return;
        if (settings is { IsDisposed: false }) { settings.Show(); settings.Activate(); return; }
        settings = new SettingsForm(cfg, StartupRegistration.Enabled(root), Apply);
        settings.Show();
        settings.Activate();
    }
    private async Task Refresh()
    {
        if (refreshing || quitting || restarting) return;
        refreshing = true;
        try
        {
            var s = await Program.Status(http, Url, root);
            var state = s?["status"]?.GetValue<string>() ?? "starting";
            var text = state switch { "unloaded" => "Model unloaded - ready on demand", "loading" => "Loading model...", "ready" => "Model loaded - " + cfg.Mode.ToUpperInvariant(), _ => state };
            var waiting = s?["waitingRequests"]?.GetValue<int>() ?? 0;
            if (waiting > 0) text += $"; {waiting} waiting";
            statusItem.Text = text;
            tray.Text = ("AutoJev - " + text)[..Math.Min(63, ("AutoJev - " + text).Length)];
            startup.Checked = StartupRegistration.Enabled(root);
        }
        catch { statusItem.Text = "Server unavailable"; }
        finally { refreshing = false; }
    }
    private async Task Unload()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url + "/local/unload");
            request.Headers.Add("X-AutoJev-Control", File.ReadAllText(Path.Combine(root, "state", "control.token")));
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new Exception(response.StatusCode == HttpStatusCode.Conflict ? "Requests are active. Try again after they finish." : "Could not unload the model.");
            await Refresh();
        }
        catch (Exception ex) { Forms.MessageBox.Show(ex.Message, "AutoJev"); }
    }
    private static void CheckPort(int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        try { probe.Start(); } catch (SocketException) { throw new Exception($"Port {port} is already in use. Choose another port."); } finally { probe.Stop(); }
    }
    private async Task Apply(Settings next, bool runAtLogin)
    {
        if (restarting || quitting) throw new Exception("AutoJev is busy restarting or quitting.");
        next.Validate();
        var current = await Program.Status(http, Url, root);
        if ((current?["queuedRequests"]?.GetValue<int>() ?? 0) != 0) throw new Exception("Wait for active requests to finish before changing settings.");
        if (next.Port != cfg.Port) CheckPort(next.Port);
        if (next.BackendPort != cfg.BackendPort) CheckPort(next.BackendPort);
        var previous = cfg;
        var previousStartup = StartupRegistration.Enabled(root);
        restarting = true;
        try
        {
            // Stop acceptance before replacing configuration. New requests during apply may need retrying.
            serverStop!.Cancel();
            if (server is not null) await server;
            Save(next);
            StartupRegistration.Set(root, runAtLogin);
            cfg = next;
            StartServer();
            await WaitForServer();
        }
        catch
        {
            serverStop?.Cancel();
            if (server is not null) { try { await server; } catch { } }
            Save(previous);
            StartupRegistration.Set(root, previousStartup);
            cfg = previous;
            StartServer();
            throw;
        }
        finally { restarting = false; startup.Checked = StartupRegistration.Enabled(root); }
        await Refresh();
    }
    private void Save(Settings value)
    {
        var path = Path.Combine(root, "config", "server.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Program.Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
    private async Task WaitForServer()
    {
        for (var i = 0; i < 50; i++)
        {
            if (server!.IsCompleted) { await server; throw new Exception("Could not start the server with these settings."); }
            if (await Program.Status(http, Url, root) is not null) return;
            await Task.Delay(100);
        }
        throw new Exception("The server did not respond after applying settings.");
    }
    private async Task Quit()
    {
        if (quitting || restarting) return;
        quitting = true; timer.Stop(); tray.Visible = false; settings?.Close();
        serverStop?.Cancel();
        if (server is not null) { try { await server; } catch { } }
        ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            timer.Stop(); timer.Dispose();
            tray.Visible = false; tray.Dispose(); icon?.Dispose();
            serverStop?.Cancel(); serverStop?.Dispose();
            http.Dispose(); settings?.Dispose(); dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SettingsForm : Forms.Form
{
    public SettingsForm(Settings source, bool startup, Func<Settings, bool, Task> apply)
    {
        Text = "AutoJev settings"; Width = 560; Height = 760;
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        Font = new Drawing.Font("Segoe UI", 10);
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(20), ColumnCount = 2, RowCount = 14, AutoSize = true };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 48));
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 52));
        Controls.Add(layout);
        Forms.NumericUpDown Number(int value, int min, int max) => new() { Minimum = min, Maximum = max, Value = value, Dock = Forms.DockStyle.Top, ThousandsSeparator = true };
        void Row(int row, string label, Forms.Control input) { layout.Controls.Add(new Forms.Label { Text = label, AutoSize = true, Padding = new Forms.Padding(0, 6, 0, 0) }, 0, row); layout.Controls.Add(input, 1, row); }
        var port = Number(source.Port, 1024, 65535); port.ThousandsSeparator = false; port.AccessibleName = "API port";
        var backend = Number(source.BackendPort, 1024, 65535); backend.ThousandsSeparator = false; backend.AccessibleName = "Internal model port";
        var idle = Number(source.IdleUnloadSeconds, 5, 86400); idle.AccessibleName = "Unload after idle seconds";
        var mode = new Forms.ComboBox { DropDownStyle = Forms.ComboBoxStyle.DropDownList, Dock = Forms.DockStyle.Top, AccessibleName = "Compute mode" };
        mode.Items.AddRange(["CUDA", "CPU"]); mode.SelectedIndex = source.Mode == "cuda" ? 0 : 1;
        Row(0, "API port", port); Row(1, "Internal model port", backend); Row(2, "Unload after idle seconds", idle); Row(3, "Compute mode", mode);
        var queue = Number(source.MaxQueuedRequests, 0, 10000); queue.AccessibleName = "Maximum waiting requests";
        var wait = Number(source.QueueTimeoutSeconds, 0, 86400); wait.AccessibleName = "Queue timeout seconds, zero for unlimited";
        var processing = Number(source.RequestTimeoutSeconds, 1, 86400); processing.AccessibleName = "Processing timeout seconds";
        Row(4, "Maximum waiting requests", queue); Row(5, "Queue timeout seconds", wait); Row(6, "Processing timeout seconds", processing);
        var vision = new Forms.CheckBox { Text = "Enable image input", Checked = source.VisionEnabled, AutoSize = true };
        Row(7, "Images", vision);
        var cacheMode = new Forms.ComboBox { DropDownStyle = Forms.ComboBoxStyle.DropDownList, Dock = Forms.DockStyle.Top, AccessibleName = "Default cache mode" };
        cacheMode.Items.AddRange(["None, original order", "Options first, experimental", "Context, original order"]);
        cacheMode.SelectedIndex = Array.IndexOf(new[] { "none", "options", "context" }, source.EffectiveCacheMode);
        var cacheSeconds = Number(source.PrefixCacheSeconds, 1, 3600); cacheSeconds.AccessibleName = "Prefix cache lifetime seconds";
        Row(8, "Default cache mode", cacheMode); Row(9, "Cache idle lifetime, seconds", cacheSeconds);
        var login = new Forms.CheckBox { Text = "Start when I sign in to Windows", Checked = startup, AutoSize = true, Margin = new Forms.Padding(0, 14, 0, 12) };
        layout.Controls.Add(login, 0, 10); layout.SetColumnSpan(login, 2);
        var help = new Forms.Label { Text = "Options changes prompt order; check accuracy. Context reuses shared state/question text in original order. Images bypass both caches.\n\nQueue timeout 0 means wait while the app stays connected. Processing timeout starts at the front of the queue. Apps must allow enough time to wait.\n\n300 idle seconds = 5 minutes. Only inference loads the model. Saving restarts the API after requests finish; changing the port changes the URL.", AutoSize = true, MaximumSize = new Drawing.Size(490, 0) };
        layout.Controls.Add(help, 0, 11); layout.SetColumnSpan(help, 2);
        var error = new Forms.Label { ForeColor = Drawing.Color.Firebrick, AutoSize = true, MaximumSize = new Drawing.Size(450, 0) };
        layout.Controls.Add(error, 0, 12); layout.SetColumnSpan(error, 2);
        var buttons = new Forms.FlowLayoutPanel { AutoSize = true, FlowDirection = Forms.FlowDirection.RightToLeft, Dock = Forms.DockStyle.Bottom };
        var save = new Forms.Button { Text = "Save and apply", AutoSize = true };
        var cancel = new Forms.Button { Text = "Cancel", AutoSize = true, DialogResult = Forms.DialogResult.Cancel };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 13); layout.SetColumnSpan(buttons, 2);
        AcceptButton = save; CancelButton = cancel;
        var saving = false;
        FormClosing += (_, e) => { if (saving) e.Cancel = true; };
        save.Click += async (_, _) =>
        {
            var next = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(source, Program.Json), Program.Json)!;
            next.Port = (int)port.Value; next.BackendPort = (int)backend.Value; next.IdleUnloadSeconds = (int)idle.Value; next.Mode = mode.SelectedIndex == 0 ? "cuda" : "cpu";
            next.MaxQueuedRequests = (int)queue.Value; next.QueueTimeoutSeconds = (int)wait.Value; next.RequestTimeoutSeconds = (int)processing.Value;
            next.VisionEnabled = vision.Checked;
            next.CacheMode = new[] { "none", "options", "context" }[cacheMode.SelectedIndex];
            next.PrefixCacheEnabled = next.CacheMode == "options"; next.PrefixCacheSeconds = (int)cacheSeconds.Value;
            saving = true; layout.Enabled = false; error.Text = "";
            try { await apply(next, login.Checked); saving = false; Close(); }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { saving = false; layout.Enabled = true; }
        };
    }
}
