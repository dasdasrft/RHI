// ProxyService.cs — fork-only network proxy manager wired via HttpClient.DefaultProxy.
// Supports System (OS default), Direct (bypass), Http, and Socks5 modes with
// atomic snapshot swaps so changes take effect immediately without restart.

using System.Net;
using System.Net.Sockets;

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

    public static IWebProxy? SystemProxyFallback => _systemProxy;

    public (bool Ok, string? Error) SaveAndApply(ProxyMode mode, string? host, string? portText)
    {
        ProxyConfig config;

        if (mode is ProxyMode.Http or ProxyMode.Socks5)
        {
            var (valid, validated, validationError) = TryValidateEndpoint(host, portText);
            if (!valid || validated == null)
                return (false, validationError ?? "Invalid proxy address.");

            config = new ProxyConfig(mode, validated.Value.Host, validated.Value.Port);
        }
        else
        {
            // Preserve previously configured host/port when switching to System or Direct mode.
            var current = _state.Config;
            var preservedHost = !string.IsNullOrWhiteSpace(host) && TryNormalizeHost(host, out var normalizedHost, out _)
                ? normalizedHost
                : current.Host;
            var preservedPort = int.TryParse(portText?.Trim(), out var parsed) && parsed is >= 1 and <= 65535
                ? parsed
                : current.Port;
            config = new ProxyConfig(mode, preservedHost, preservedPort);
        }

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

    public static (bool Ok, IWebProxy? Proxy, string? Error) TryCreateTestProxy(
        ProxyMode mode,
        string? host,
        string? portText)
    {
        if (mode == ProxyMode.Direct)
            return (true, null, null);

        if (mode == ProxyMode.System)
            return (true, _systemProxy, null);

        var (valid, endpoint, error) = TryValidateEndpoint(host, portText);
        if (!valid || endpoint == null)
            return (false, null, error ?? "Invalid proxy address.");

        var (state, buildError) = TryBuildState(new ProxyConfig(mode, endpoint.Value.Host, endpoint.Value.Port));
        if (state?.CustomProxy == null)
            return (false, null, buildError ?? "Invalid proxy address.");

        return (true, state.CustomProxy, null);
    }

    private static (bool Ok, (string Host, int Port)? Endpoint, string? Error) TryValidateEndpoint(
        string? host,
        string? portText)
    {
        if (!TryNormalizeHost(host, out var normalizedHost, out var hostError))
            return (false, null, hostError);

        if (!int.TryParse(portText?.Trim(), out int port) || port is < 1 or > 65535)
            return (false, null, "Port must be between 1 and 65535.");

        return (true, (normalizedHost, port), null);
    }

    private static bool TryNormalizeHost(string? rawHost, out string normalizedHost, out string? error)
    {
        normalizedHost = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rawHost))
        {
            error = "Proxy host cannot be empty.";
            return false;
        }

        var candidate = rawHost.Trim();
        if (candidate.Contains("://", StringComparison.Ordinal) ||
            candidate.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0 ||
            candidate.Any(char.IsWhiteSpace))
        {
            error = "Proxy host must be a hostname or IP address without scheme, path, or spaces.";
            return false;
        }

        if (candidate.StartsWith('[') || candidate.EndsWith(']'))
        {
            if (!(candidate.StartsWith('[') && candidate.EndsWith(']') && candidate.Length > 2))
            {
                error = "Invalid IPv6 proxy host.";
                return false;
            }

            var inner = candidate[1..^1];
            if (!IPAddress.TryParse(inner, out var ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Invalid IPv6 proxy host.";
                return false;
            }

            normalizedHost = $"[{ipv6}]";
            return true;
        }

        if (IPAddress.TryParse(candidate, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                normalizedHost = $"[{ip}]";
                return true;
            }

            if (Uri.CheckHostName(candidate) == UriHostNameType.IPv4)
            {
                normalizedHost = candidate;
                return true;
            }

            error = "Invalid IPv4 proxy host.";
            return false;
        }

        if (candidate.Contains(':', StringComparison.Ordinal))
        {
            error = "Enter the port in the Port field, not in the Host field.";
            return false;
        }

        if (Uri.CheckHostName(candidate) != UriHostNameType.Dns)
        {
            error = "Invalid proxy host.";
            return false;
        }

        normalizedHost = candidate;
        return true;
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
