using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RenoDXCommander.Services;

namespace RenoDXCommander.ForkL10n;

/// <summary>
/// Fork-only localization service. Loads an embedded zh-CN.json dictionary (plus an optional
/// user override at %LocalAppData%\RHI\zh-CN.override.json), persists language selection in
/// SettingsFile ("ForkLanguage"), and optionally logs missing English UI strings.
/// </summary>
public static class L10nService
{
    public const string LangZhCn = "zh-CN";
    public const string LangEnUs = "en-US";

    private const string SettingKeyLanguage = "ForkLanguage";
    private const string SettingKeyLogMissing = "ForkL10nLogMissing";

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RHI");
    private static readonly string OverridePath = Path.Combine(AppDataDir, "zh-CN.override.json");
    private static readonly string MissingLogPath = Path.Combine(AppDataDir, "missing_l10n.txt");

    private static readonly object DictionaryLock = new();
    private static readonly object MissingFileLock = new();
    private static readonly ConcurrentDictionary<string, byte> LoggedMissing = new(StringComparer.Ordinal);

    private static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private static List<(Regex Regex, string Replace)> _patterns = new();
    private static bool _loaded;

    private static readonly HashSet<string> ProperNounAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "RHI", "ReShade", "RenoDX", "Luma", "OptiScaler", "ReLimiter", "Display Commander",
        "DLSS", "Streamline", "Neural Rendering", "MFG", "HDR", "SDR", "VRR", "G-Sync",
        "ReBAR", "DLDSR", "DXVK", "RTX HDR", "INI", "DLL", "Shader", "GitHub",
        "Steam", "GOG", "Epic", "EA", "Xbox", "Ubisoft", "Battle.net", "Rockstar",
        "Discord", "Ko-fi", "PCGW", "Nexus", "Ultra+", "NVIDIA", "AMD", "Intel",
        "Vulkan", "DirectX", "DX9", "DX10", "DX11", "DX12", "OpenGL",
        "Insert", "Home", "End", "F12", "PrintScreen", "32-bit", "64-bit",
        "HTTP", "SOCKS5", "FPS", "OSD", "OLED", "DOF", "CRT", "BFI"
    };

    private static readonly Regex AsciiLetterRegex = new("[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex CjkRegex = new("[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex FileNameOrPathRegex = new(
        @"^(?:[A-Za-z]:\\|\\\\|https?://|v?\d+(?:\.\d+)+|0x[0-9A-Fa-f]+|[\w.\-]+\.(?:dll|ini|exe|json|fx|addon\d*|conf|txt|zip|7z))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string CurrentLanguage { get; private set; }
    public static bool IsChinese => string.Equals(CurrentLanguage, LangZhCn, StringComparison.Ordinal);
    public static bool LogMissingEnabled { get; private set; }

    public static event Action? LanguageChanged;

    static L10nService()
    {
        var snap = SettingsFile.Snapshot();
        CurrentLanguage = snap.TryGetValue(SettingKeyLanguage, out var lang) &&
                          string.Equals(lang, LangEnUs, StringComparison.OrdinalIgnoreCase)
            ? LangEnUs
            : LangZhCn;

        LogMissingEnabled = (snap.TryGetValue(SettingKeyLogMissing, out var logVal) &&
                             (string.Equals(logVal, "true", StringComparison.OrdinalIgnoreCase) || logVal == "1"))
                            || string.Equals(Environment.GetEnvironmentVariable("RHI_L10N_LOG_MISSING"), "1", StringComparison.Ordinal);
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (DictionaryLock)
        {
            if (_loaded) return;
            ReloadCore();
            _loaded = true;
        }
    }

    public static void Reload()
    {
        lock (DictionaryLock)
        {
            ReloadCore();
            _loaded = true;
        }
    }

    public static void SetLanguage(string language)
    {
        var normalized = string.Equals(language, LangEnUs, StringComparison.OrdinalIgnoreCase)
            ? LangEnUs
            : LangZhCn;

        SettingsFile.Update(s => s[SettingKeyLanguage] = normalized, "ForkL10n");

        if (string.Equals(CurrentLanguage, normalized, StringComparison.Ordinal))
            return;

        CurrentLanguage = normalized;
        LanguageChanged?.Invoke();
    }

    public static void SetLogMissing(bool enabled)
    {
        LogMissingEnabled = enabled;
        SettingsFile.Update(s => s[SettingKeyLogMissing] = enabled ? "true" : "false", "ForkL10n");
    }

    public static bool TryTranslate(string? source, out string translated)
    {
        if (string.IsNullOrEmpty(source) || !IsChinese)
        {
            translated = source ?? string.Empty;
            return false;
        }

        EnsureLoaded();

        if (_exact.TryGetValue(source, out var exactHit))
        {
            translated = exactHit;
            return true;
        }

        var trimmed = source.Trim();
        if (trimmed.Length == 0)
        {
            translated = source;
            return false;
        }

        if (trimmed.Length != source.Length && _exact.TryGetValue(trimmed, out exactHit))
        {
            translated = PreserveOuterWhitespace(source, trimmed, exactHit);
            return true;
        }

        foreach (var (regex, replace) in _patterns)
        {
            if (regex.IsMatch(source))
            {
                translated = regex.Replace(source, replace);
                return !string.Equals(translated, source, StringComparison.Ordinal);
            }
            if (trimmed.Length != source.Length && regex.IsMatch(trimmed))
            {
                var replaced = regex.Replace(trimmed, replace);
                translated = PreserveOuterWhitespace(source, trimmed, replaced);
                return !string.Equals(translated, source, StringComparison.Ordinal);
            }
        }

        if (LogMissingEnabled)
            TryLogMissing(trimmed);

        translated = source;
        return false;
    }

    public static string Translate(string source) =>
        TryTranslate(source, out var translated) ? translated : source;

    private static string PreserveOuterWhitespace(string original, string trimmed, string replacement)
    {
        int start = original.IndexOf(trimmed, StringComparison.Ordinal);
        if (start < 0) return replacement;
        int end = start + trimmed.Length;
        return string.Concat(original.AsSpan(0, start), replacement, original.AsSpan(end));
    }

    private static void ReloadCore()
    {
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<(Regex Regex, string Replace)>();

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("zh-CN.json", StringComparison.OrdinalIgnoreCase));

            if (resName != null)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                    MergePayload(stream, exact, patterns, prependPatterns: false);
            }
            else
            {
                CrashReporter.Log("[ForkL10n] Embedded zh-CN.json resource not found.");
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[ForkL10n] Failed to load embedded zh-CN.json — {ex.Message}");
        }

        try
        {
            if (File.Exists(OverridePath))
            {
                using var fs = File.OpenRead(OverridePath);
                MergePayload(fs, exact, patterns, prependPatterns: true);
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[ForkL10n] Failed to load override zh-CN.override.json — {ex.Message}");
        }

        _exact = exact;
        _patterns = patterns;
    }

    private static void MergePayload(
        Stream stream,
        Dictionary<string, string> exact,
        List<(Regex Regex, string Replace)> patterns,
        bool prependPatterns)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        var payload = JsonSerializer.Deserialize<DictionaryPayload>(stream, opts);
        if (payload == null) return;

        if (payload.Exact != null)
        {
            foreach (var kv in payload.Exact)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    exact[kv.Key] = kv.Value;
            }
        }

        if (payload.Patterns != null && payload.Patterns.Count > 0)
        {
            var compiled = new List<(Regex, string)>(payload.Patterns.Count);
            foreach (var rule in payload.Patterns)
            {
                if (string.IsNullOrEmpty(rule.Pattern) || rule.Replace == null) continue;
                try
                {
                    var rx = new Regex(rule.Pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
                    compiled.Add((rx, rule.Replace));
                }
                catch (Exception ex)
                {
                    CrashReporter.Log($"[ForkL10n] Invalid regex '{rule.Pattern}' — {ex.Message}");
                }
            }

            if (prependPatterns)
                patterns.InsertRange(0, compiled);
            else
                patterns.AddRange(compiled);
        }
    }

    private static void TryLogMissing(string text)
    {
        if (text.Length < 2 || text.Length > 400) return;
        if (!AsciiLetterRegex.IsMatch(text)) return;
        if (CjkRegex.IsMatch(text)) return;
        if (ProperNounAllowlist.Contains(text)) return;
        if (FileNameOrPathRegex.IsMatch(text)) return;
        if (!LoggedMissing.TryAdd(text, 0)) return;

        try
        {
            lock (MissingFileLock)
            {
                Directory.CreateDirectory(AppDataDir);
                var singleLine = text.Replace("\r\n", "\\n").Replace("\n", "\\n");
                File.AppendAllText(MissingLogPath, singleLine + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostic logging must never break UI rendering.
        }
    }

    private sealed class DictionaryPayload
    {
        public Dictionary<string, string>? Exact { get; set; }
        public List<PatternRule>? Patterns { get; set; }
    }

    private sealed class PatternRule
    {
        public string? Pattern { get; set; }
        public string? Replace { get; set; }
    }
}
