using Microsoft.AspNetCore.Http;

namespace AutoJev.Local;

internal static class CacheModePolicy
{
    public const string Header = "X-AutoJev-Cache-Mode";
    public const string LegacyHeader = "X-AutoJev-Prefix-Cache";

    public static bool TryParse(string? value, out string mode)
    {
        mode = value?.Trim().ToLowerInvariant() ?? "";
        return mode is "none" or "options" or "context";
    }

    public static string Parse(string value)
    {
        if (!TryParse(value, out var mode)) throw new ArgumentException("cacheMode must be none, options or context.");
        return mode;
    }

    public static bool TryResolve(IHeaderDictionary headers, string defaultMode, out string mode, out string? error)
    {
        mode = defaultMode;
        error = null;
        // Presence, not validity, gives the new header precedence over legacy.
        if (headers.TryGetValue(Header, out var requested))
        {
            if (requested.Count == 1 && TryParse(requested[0], out mode)) return true;
            error = Header + " must be none, options or context.";
            return false;
        }
        if (headers.TryGetValue(LegacyHeader, out var legacy))
        {
            if (legacy.Count != 1 || !bool.TryParse(legacy[0], out var enabled))
            {
                error = LegacyHeader + " must be true or false.";
                return false;
            }
            mode = enabled ? "options" : "none";
        }
        return true;
    }

    // Image checkpoint identity and multimodal offsets are not proven safe.
    public static string ForRequest(string mode, bool hasImages) => hasImages ? "none" : mode;
}
