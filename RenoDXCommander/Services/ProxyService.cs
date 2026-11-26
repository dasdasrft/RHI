// ProxyService.cs — fork-only network proxy manager wired via HttpClient.DefaultProxy.
// Supports System (OS default), Direct (bypass), Http, and Socks5 modes with
// atomic snapshot swaps so changes take effect immediately without restart.

using System.Net;

namespace RenoDXCommander.Services;

public enum ProxyMode
{
    System,
    Direct,
    Http,
    Socks5,
}

public sealed record ProxyConfig(ProxyMode Mode, string Host, int Port);

public sealed class ProxyService : IWebProxy
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 7890;

    private const string KeyMode = "ForkProxyMode";
    private const string KeyHost = "ForkProxyHost";
    private const string KeyPort = "ForkProxyPort";

    private sealed record ProxyState(ProxyConfig Config, WebProxy? CustomProxy);

    private static IWebProxy? _systemProxy;
    private static int _initialized;

    private volatile ProxyState _state = new(
        new ProxyConfig(ProxyMode.System, DefaultHost, DefaultPort),
        null);

    private ProxyService() { }

    public static ProxyService Instance { get; } = new();

    public ProxyConfig CurrentConfig => _state.Config;

    /// <summary>
    /// Captures the original <see cref="HttpClient.DefaultProxy"/> as the System fallback,
    /// loads persisted settings, and installs <see cref="Instance"/> as <see cref="HttpClient.DefaultProxy"/>.
    /// </summary>
    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            _systemProxy = HttpClient.DefaultProxy;

        Instance.ReloadFromSettings();
        HttpClient.DefaultProxy = Instance;
    }

    public void ReloadFromSettings()
    {
        var s = SettingsFile.Snapshot();
        var mode = s.TryGetValue(KeyMode, out var rawMode) && Enum.TryParse<ProxyMode>(rawMode, true, out var parsedMode)
            ? parsedMode
            : ProxyMode.System;

        var host = s.TryGetValue(KeyHost, out var rawHost) && !string.IsNullOrWhiteSpace(rawHost)
            ? rawHost.Trim()
            : DefaultHost;

        var port = s.TryGetValue(KeyPort, out var rawPort) && int.TryParse(rawPort, out var parsedPort) && parsedPort is >= 1 and <= 65535
            ? parsedPort
            : DefaultPort;

        var (state, error) = TryBuildState(new ProxyConfig(mode, host, port));
        if (state != null)
        {
            _state = state;
        }
        else
        {
            CrashReporter.Log($"[ProxyService.ReloadFromSettings] Invalid saved proxy config ({error}); falling back to System");
            _state = new ProxyState(new ProxyConfig(ProxyMode.System, DefaultHost, DefaultPort), null);
        }
    }

    public (bool Ok, string? Error) SaveAndApply(ProxyMode mode, string? host, string? portText)
    {
        var cleanHost = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();
        int port;

        if (mode is ProxyMode.Http or ProxyMode.Socks5)
        {
            if (string.IsNullOrWhiteSpace(host))
                return (false, "Proxy host cannot be empty.");

            if (!int.TryParse(portText?.Trim(), out port) || port is < 1 or > 65535)
                return (false, "Port must be between 1 and 65535.");
        }
        else
        {
            port = int.TryParse(portText?.Trim(), out var parsed) && parsed is >= 1 and <= 65535
                ? parsed
                : DefaultPort;
        }

        var config = new ProxyConfig(mode, cleanHost, port);
        var (state, error) = TryBuildState(config);
        if (state == null)
            return (false, error ?? "Invalid proxy address.");

        _state = state;

        var saved = SettingsFile.Update(d =>
        {
            d[KeyMode] = config.Mode.ToString();
            d[KeyHost] = config.Host;
            d[KeyPort] = config.Port.ToString();
        }, "ProxyService.SaveAndApply");

        if (!saved)
            return (false, "Applied in memory, but failed to write settings.json.");

        CrashReporter.Log($"[ProxyService.SaveAndApply] Applied mode={config.Mode}, endpoint={config.Host}:{config.Port}");
        return (true, null);
    }

    private static (ProxyState? State, string? Error) TryBuildState(ProxyConfig config)
    {
        if (config.Mode is ProxyMode.System or ProxyMode.Direct)
            return (new ProxyState(config, null), null);

        var scheme = config.Mode == ProxyMode.Socks5 ? "socks5" : "http";
        var uriString = $"{scheme}://{config.Host}:{config.Port}";
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var proxyUri))
            return (null, $"Invalid proxy URI: {uriString}");

        var webProxy = new WebProxy(proxyUri)
        {
            BypassProxyOnLocal = false,
        };
        return (new ProxyState(config, webProxy), null);
    }

    public ICredentials? Credentials
    {
        get
        {
            var state = _state;
            return state.Config.Mode switch
            {
                ProxyMode.Direct => null,
                ProxyMode.Http or ProxyMode.Socks5 => state.CustomProxy?.Credentials,
                _ => _systemProxy?.Credentials,
            };
        }
        set
        {
            var state = _state;
            if (state.CustomProxy != null)
                state.CustomProxy.Credentials = value;
            else if (state.Config.Mode == ProxyMode.System && _systemProxy != null)
                _systemProxy.Credentials = value;
        }
    }

    public Uri? GetProxy(Uri destination)
    {
        var state = _state;
        return state.Config.Mode switch
        {
            ProxyMode.Direct => destination,
            ProxyMode.Http or ProxyMode.Socks5 => state.CustomProxy?.GetProxy(destination) ?? destination,
            _ => _systemProxy?.GetProxy(destination) ?? destination,
        };
    }

    public bool IsBypassed(Uri host)
    {
        var state = _state;
        return state.Config.Mode switch
        {
            ProxyMode.Direct => true,
            ProxyMode.Http or ProxyMode.Socks5 => state.CustomProxy?.IsBypassed(host) ?? true,
            _ => _systemProxy?.IsBypassed(host) ?? true,
        };
    }
}
