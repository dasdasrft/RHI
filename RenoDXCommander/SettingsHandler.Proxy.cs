// SettingsHandler.Proxy.cs — fork-only "Network Proxy" card in Settings › Accounts & Data.
// Dynamically injected into Section:Accounts so MainWindow.xaml stays untouched.

using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RenoDXCommander.Services;

namespace RenoDXCommander;

public partial class SettingsHandler
{
    private ComboBox? _proxyModeCombo;
    private TextBox? _proxyHostBox;
    private TextBox? _proxyPortBox;
    private Button? _proxySaveBtn;
    private Button? _proxyTestBtn;
    private TextBlock? _proxyStatusText;
    private bool _proxyCardFailed;
    private bool _proxyUiSyncing;

    private void EnsureProxyCard()
    {
        if (_proxyModeCombo != null)
        {
            RefreshProxyCard();
            return;
        }
        if (_proxyCardFailed) return;

        try
        {
            StackPanel? section = null;
            foreach (var child in _window.SettingsCardsPanel.Children)
            {
                if (child is StackPanel sp && sp.Tag is string t && t == "Section:Accounts")
                {
                    section = sp;
                    break;
                }
            }

            if (section == null)
            {
                _proxyCardFailed = true;
                CrashReporter.Log("[SettingsHandler.EnsureProxyCard] Accounts section not found — card not added");
                return;
            }

            var card = new Border { Style = FindStyle("SettingsCardStyle") };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;

            var head = new StackPanel { Spacing = 3 };
            head.Children.Add(new TextBlock
            {
                Text = "Network Proxy",
                Style = FindStyle("SettingsCardTitleStyle"),
            });
            head.Children.Add(new TextBlock
            {
                Text = "Configure how RHI connects to GitHub and mod servers. Changes apply immediately without restarting.",
                Style = FindStyle("SettingsCardDescStyle"),
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(head);

            // Mode row
            var modeRow = new Grid { ColumnSpacing = 10 };
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            modeRow.Children.Add(new TextBlock { Text = "Proxy mode", Style = FindStyle("SettingsLabelStyle") });

            _proxyModeCombo = new ComboBox
            {
                Style = FindStyle("SettingsComboStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _proxyModeCombo.Items.Add(new ComboBoxItem { Content = "System (Follow OS)", Tag = ProxyMode.System });
            _proxyModeCombo.Items.Add(new ComboBoxItem { Content = "Direct (No proxy)", Tag = ProxyMode.Direct });
            _proxyModeCombo.Items.Add(new ComboBoxItem { Content = "HTTP", Tag = ProxyMode.Http });
            _proxyModeCombo.Items.Add(new ComboBoxItem { Content = "SOCKS5", Tag = ProxyMode.Socks5 });
            _proxyModeCombo.SelectionChanged += ProxyModeCombo_SelectionChanged;
            Grid.SetColumn(_proxyModeCombo, 1);
            modeRow.Children.Add(_proxyModeCombo);
            body.Children.Add(modeRow);

            // Host & Port row
            var endpointRow = new Grid { ColumnSpacing = 10 };
            endpointRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            endpointRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            endpointRow.Children.Add(new TextBlock { Text = "Proxy address", Style = FindStyle("SettingsLabelStyle") });

            var endpointInputs = new Grid { ColumnSpacing = 6 };
            endpointInputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            endpointInputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

            _proxyHostBox = new TextBox
            {
                Style = FindStyle("SettingsTextBoxStyle"),
                PlaceholderText = ProxyService.DefaultHost,
                IsSpellCheckEnabled = false,
            };
            _proxyPortBox = new TextBox
            {
                Style = FindStyle("SettingsTextBoxStyle"),
                PlaceholderText = ProxyService.DefaultPort.ToString(),
                IsSpellCheckEnabled = false,
            };
            Grid.SetColumn(_proxyHostBox, 0);
            Grid.SetColumn(_proxyPortBox, 1);
            endpointInputs.Children.Add(_proxyHostBox);
            endpointInputs.Children.Add(_proxyPortBox);

            Grid.SetColumn(endpointInputs, 1);
            endpointRow.Children.Add(endpointInputs);
            body.Children.Add(endpointRow);

            // Actions row
            var actionsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            _proxySaveBtn = new Button
            {
                Content = "Save & Apply",
                Style = FindStyle("SettingsPrimaryButtonStyle"),
            };
            _proxySaveBtn.Click += ProxySaveBtn_Click;

            _proxyTestBtn = new Button
            {
                Content = "Test Connection",
                Style = FindStyle("SettingsButtonStyle"),
            };
            _proxyTestBtn.Click += ProxyTestBtn_Click;

            actionsRow.Children.Add(_proxySaveBtn);
            actionsRow.Children.Add(_proxyTestBtn);
            body.Children.Add(actionsRow);

            // Status line
            _proxyStatusText = new TextBlock
            {
                Style = FindStyle("SettingsStatusStyle"),
                Foreground = UIFactory.Brush(ResourceKeys.TextTertiaryBrush),
                TextWrapping = TextWrapping.Wrap,
            };
            body.Children.Add(_proxyStatusText);

            section.Children.Add(card);
            RefreshProxyCard();
        }
        catch (Exception ex)
        {
            _proxyCardFailed = true;
            CrashReporter.Log($"[SettingsHandler.EnsureProxyCard] Failed to build proxy card — {ex.Message}");
        }
    }

    private void RefreshProxyCard()
    {
        if (_proxyModeCombo == null || _proxyHostBox == null || _proxyPortBox == null) return;

        _proxyUiSyncing = true;
        try
        {
            var cfg = ProxyService.Instance.CurrentConfig;
            for (int i = 0; i < _proxyModeCombo.Items.Count; i++)
            {
                if (_proxyModeCombo.Items[i] is ComboBoxItem item && item.Tag is ProxyMode m && m == cfg.Mode)
                {
                    _proxyModeCombo.SelectedIndex = i;
                    break;
                }
            }
            _proxyHostBox.Text = cfg.Host;
            _proxyPortBox.Text = cfg.Port.ToString();
            UpdateProxyEndpointEditability(cfg.Mode);
        }
        finally
        {
            _proxyUiSyncing = false;
        }
    }

    private ProxyMode SelectedProxyMode =>
        _proxyModeCombo?.SelectedItem is ComboBoxItem item && item.Tag is ProxyMode mode
            ? mode
            : ProxyMode.System;

    private void UpdateProxyEndpointEditability(ProxyMode mode)
    {
        bool editable = mode is ProxyMode.Http or ProxyMode.Socks5;
        if (_proxyHostBox != null) _proxyHostBox.IsEnabled = editable;
        if (_proxyPortBox != null) _proxyPortBox.IsEnabled = editable;
    }

    private void ProxyModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_proxyUiSyncing) return;
        UpdateProxyEndpointEditability(SelectedProxyMode);
    }

    private void ProxySaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_proxyStatusText == null) return;

        var mode = SelectedProxyMode;
        var (ok, error) = ProxyService.Instance.SaveAndApply(mode, _proxyHostBox?.Text, _proxyPortBox?.Text);
        if (ok)
        {
            RefreshProxyCard();
            var cfg = ProxyService.Instance.CurrentConfig;
            var detail = cfg.Mode is ProxyMode.Http or ProxyMode.Socks5
                ? $"{cfg.Mode} ({cfg.Host}:{cfg.Port})"
                : cfg.Mode.ToString();
            _proxyStatusText.Text = $"Saved & applied: {detail}";
            _proxyStatusText.Foreground = UIFactory.Brush(ResourceKeys.AccentGreenBrush);
        }
        else
        {
            _proxyStatusText.Text = error ?? "Failed to save proxy settings.";
            _proxyStatusText.Foreground = UIFactory.Brush(ResourceKeys.AccentRedBrush);
        }
    }

    private async void ProxyTestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_proxyTestBtn == null || _proxyStatusText == null) return;

        _proxyTestBtn.IsEnabled = false;
        _proxyStatusText.Text = "Testing connection to https://github.com/robots.txt ...";
        _proxyStatusText.Foreground = UIFactory.Brush(ResourceKeys.TextSecondaryBrush);

        var sw = Stopwatch.StartNew();
        try
        {
            var http = App.Services.GetRequiredService<HttpClient>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://github.com/robots.txt");
            if (!http.DefaultRequestHeaders.Contains("User-Agent"))
                req.Headers.TryAddWithoutValidation("User-Agent", "RHI/2.0");
            using var resp = await http.SendAsync(req, cts.Token);
            sw.Stop();

            int code = (int)resp.StatusCode;
            if (code is >= 200 and < 400)
            {
                _proxyStatusText.Text = $"Connected ({code} {resp.ReasonPhrase}, {sw.ElapsedMilliseconds} ms)";
                _proxyStatusText.Foreground = UIFactory.Brush(ResourceKeys.AccentGreenBrush);
            }
            else
            {
                _proxyStatusText.Text = $"HTTP {code} {resp.ReasonPhrase} ({sw.ElapsedMilliseconds} ms)";
                _proxyStatusText.Foreground = UIFactory.Brush(ResourceKeys.AccentRedBrush);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _proxyStatusText.Text = $"Failed ({sw.ElapsedMilliseconds} ms): {ex.Message}";
            _proxyStatusText.Foreground = UIFactory.Brush(ResourceKeys.AccentRedBrush);
        }
        finally
        {
            _proxyTestBtn.IsEnabled = true;
        }
    }
}
