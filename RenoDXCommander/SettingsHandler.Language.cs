// SettingsHandler.Language.cs — fork-only "Interface Language" card in Settings › General.
// Dynamically injected into Section:General so MainWindow.xaml stays untouched.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RenoDXCommander.ForkL10n;
using RenoDXCommander.Services;

namespace RenoDXCommander;

public partial class SettingsHandler
{
    private ComboBox? _languageCombo;
    private CheckBox? _logMissingCheck;
    private bool _languageCardFailed;
    private bool _languageUiSyncing;

    private void EnsureLanguageCard()
    {
        if (_languageCombo != null)
        {
            RefreshLanguageCard();
            return;
        }
        if (_languageCardFailed) return;

        try
        {
            StackPanel? section = null;
            foreach (var child in _window.SettingsCardsPanel.Children)
            {
                if (child is StackPanel sp && sp.Tag is string t && t == "Section:General")
                {
                    section = sp;
                    break;
                }
            }

            if (section == null)
            {
                _languageCardFailed = true;
                CrashReporter.Log("[SettingsHandler.EnsureLanguageCard] General section not found — card not added");
                return;
            }

            var card = new Border { Style = FindStyle("SettingsCardStyle") };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;

            var head = new StackPanel { Spacing = 3 };
            head.Children.Add(new TextBlock
            {
                Text = "Interface Language",
                Style = FindStyle("SettingsCardTitleStyle"),
            });
            head.Children.Add(new TextBlock
            {
                Text = "Switch the application display language immediately without restarting.",
                Style = FindStyle("SettingsCardDescStyle"),
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(head);

            // Language selector row
            var langRow = new Grid { ColumnSpacing = 10 };
            langRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            langRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            langRow.Children.Add(new TextBlock { Text = "Language", Style = FindStyle("SettingsLabelStyle") });

            _languageCombo = new ComboBox
            {
                Style = FindStyle("SettingsComboStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _languageCombo.Items.Add(new ComboBoxItem { Content = "Simplified Chinese (简体中文)", Tag = L10nService.LangZhCn });
            _languageCombo.Items.Add(new ComboBoxItem { Content = "English (Original)", Tag = L10nService.LangEnUs });
            _languageCombo.SelectionChanged += LanguageCombo_SelectionChanged;
            Grid.SetColumn(_languageCombo, 1);
            langRow.Children.Add(_languageCombo);
            body.Children.Add(langRow);

            // Diagnostic missing-translation logger toggle
            _logMissingCheck = new CheckBox
            {
                Content = "Log missing translations",
                IsChecked = L10nService.LogMissingEnabled,
            };
            ToolTipService.SetToolTip(_logMissingCheck,
                "Write untranslated English UI strings to %LocalAppData%\\RHI\\missing_l10n.txt for dictionary updates.");
            _logMissingCheck.Checked += LogMissingCheck_Changed;
            _logMissingCheck.Unchecked += LogMissingCheck_Changed;
            body.Children.Add(_logMissingCheck);

            section.Children.Add(card);
            RefreshLanguageCard();
        }
        catch (Exception ex)
        {
            _languageCardFailed = true;
            CrashReporter.Log($"[SettingsHandler.EnsureLanguageCard] Failed to build language card — {ex.Message}");
        }
    }

    private void RefreshLanguageCard()
    {
        if (_languageCombo == null) return;

        _languageUiSyncing = true;
        try
        {
            var current = L10nService.CurrentLanguage;
            for (int i = 0; i < _languageCombo.Items.Count; i++)
            {
                if (_languageCombo.Items[i] is ComboBoxItem item &&
                    item.Tag is string tag &&
                    string.Equals(tag, current, StringComparison.OrdinalIgnoreCase))
                {
                    _languageCombo.SelectedIndex = i;
                    break;
                }
            }
            if (_logMissingCheck != null)
                _logMissingCheck.IsChecked = L10nService.LogMissingEnabled;
        }
        finally
        {
            _languageUiSyncing = false;
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageUiSyncing) return;
        if (_languageCombo?.SelectedItem is ComboBoxItem item && item.Tag is string lang)
            L10nService.SetLanguage(lang);
    }

    private void LogMissingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_languageUiSyncing || _logMissingCheck == null) return;
        L10nService.SetLogMissing(_logMissingCheck.IsChecked == true);
    }
}
