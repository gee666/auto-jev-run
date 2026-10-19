namespace AutoJev.Local;

internal sealed class Settings
{
    // Literal IP address or localhost. LAN dashboard access separately requires a configured password.
    [System.Text.Json.Serialization.JsonIgnore]
    public string ListenHost => Environment.GetEnvironmentVariable("AUTOJEV_HOST")?.Trim() is { Length: > 0 } host ? host : "localhost";
    public int Port { get; set; } = 8765;
    public int BackendPort { get; set; } = 8766;
    public string Mode { get; set; } = "cuda";
    public int ContextTokens { get; set; } = 12288;
    public int Threads { get; set; } = 8;
    public int GpuReserveMiB { get; set; } = 1536;
    public int StartupTimeoutSeconds { get; set; } = 900;
    public int RequestTimeoutSeconds { get; set; } = 1200;
    public int MaxQueuedRequests { get; set; } = 1024;
    public int QueueTimeoutSeconds { get; set; } = 0;
    public int MaxBodyBytes { get; set; } = 50331648;
    public int IdleUnloadSeconds { get; set; } = 300;
    public string ModelPath { get; set; } = "models/autojev-27b/autojev-Q4_K_M.gguf";
    public string? CacheMode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveCacheMode => CacheMode is null ? (PrefixCacheEnabled ? "options" : "none") : CacheModePolicy.Parse(CacheMode);
    public bool PrefixCacheEnabled { get; set; } = false;
    public int PrefixCacheSeconds { get; set; } = 300;
    public bool VisionEnabled { get; set; } = false;
    public string VisionProjectorPath { get; set; } = "models/autojev-27b/mmproj-autojev-bf16.gguf";
    public int ImageMaxTokens { get; set; } = 256;
    public string[]? AllowedOrigins { get; set; }
    // Read older configs without requiring migration; allowedOrigins takes precedence.
    public string[] AllowedExtensionOrigins { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] EffectiveAllowedOrigins => AllowedOrigins ?? AllowedExtensionOrigins;
    public void Validate()
    {
        if (!ListenHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !System.Net.IPAddress.TryParse(ListenHost, out _))
            throw new Exception("AUTOJEV_HOST must be localhost or a literal IP address. Prefer loopback and an SSH tunnel for remote access.");
        BrowserOriginPolicy.Validate(EffectiveAllowedOrigins);
        if (CacheMode is not null) CacheMode = CacheModePolicy.Parse(CacheMode);
        if (Port < 1024 || Port > 65535 || BackendPort < 1024 || BackendPort > 65535 || Port == BackendPort) throw new Exception("Choose two different ports between 1024 and 65535.");
        if (Mode is not ("cuda" or "cpu")) throw new Exception("mode must be cuda or cpu.");
        if (IdleUnloadSeconds < 5 || IdleUnloadSeconds > 86400) throw new Exception("Idle timeout must be between 5 and 86400 seconds.");
        if (PrefixCacheSeconds < 1 || PrefixCacheSeconds > 3600) throw new Exception("Prefix cache lifetime must be between 1 and 3600 seconds.");
        if (ImageMaxTokens < 64 || ImageMaxTokens > 8192) throw new Exception("Image token limit must be between 64 and 8192.");
        if (ContextTokens < 512 || Threads < 1 || StartupTimeoutSeconds < 1 || RequestTimeoutSeconds < 1 || RequestTimeoutSeconds > 86400 || QueueTimeoutSeconds < 0 || QueueTimeoutSeconds > 86400 || MaxQueuedRequests < 0 || MaxQueuedRequests > 10000 || MaxBodyBytes < 1024) throw new Exception("Invalid numeric server configuration. Queue size must be 0 to 10000; timeouts cannot exceed 86400 seconds.");
    }
}
