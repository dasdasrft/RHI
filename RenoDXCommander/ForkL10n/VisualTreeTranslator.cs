using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using RenoDXCommander.Services;

namespace RenoDXCommander.ForkL10n;

/// <summary>
/// Attaches to a WinUI 3 Window and walks its visual tree (plus open popups via
/// VisualTreeHelper.GetOpenPopupsForXamlRoot) to translate UI strings in-place without
/// modifying upstream XAML or C# string literals.
/// </summary>
public static class VisualTreeTranslator
{
    private static readonly ConditionalWeakTable<DependencyObject, ElementSnapshot> Snapshots = new();
    private static readonly ConditionalWeakTable<Window, WindowAttachment> Attachments = new();
    private static readonly List<WeakReference<WindowAttachment>> LiveAttachments = new();
    private static bool _languageHooked;

    public static void Attach(Window window)
    {
        if (window == null) return;

        L10nService.EnsureLoaded();

        if (!_languageHooked)
        {
            _languageHooked = true;
            L10nService.LanguageChanged += OnLanguageChanged;
        }

        if (Attachments.TryGetValue(window, out _)) return;

        var attachment = new WindowAttachment(window);
        Attachments.Add(window, attachment);
        lock (LiveAttachments)
        {
            LiveAttachments.Add(new WeakReference<WindowAttachment>(attachment));
        }
    }

    private static void OnLanguageChanged()
    {
        List<WindowAttachment> targets;
        lock (LiveAttachments)
        {
            targets = new List<WindowAttachment>(LiveAttachments.Count);
            for (int i = LiveAttachments.Count - 1; i >= 0; i--)
            {
                if (LiveAttachments[i].TryGetTarget(out var target))
                    targets.Add(target);
                else
                    LiveAttachments.RemoveAt(i);
            }
        }

        foreach (var target in targets)
            target.DispatchImmediatePass();
    }

    private sealed class WindowAttachment
    {
        private readonly WeakReference<Window> _windowRef;
        private readonly DispatcherQueue _dispatcher;
        private readonly DispatcherQueueTimer _throttleTimer;
        private bool _isTranslating;
        private bool _rootHooked;

        public WindowAttachment(Window window)
        {
            _windowRef = new WeakReference<Window>(window);
            _dispatcher = window.DispatcherQueue;

            _throttleTimer = _dispatcher.CreateTimer();
            _throttleTimer.Interval = TimeSpan.FromMilliseconds(90);
            _throttleTimer.IsRepeating = false;
            _throttleTimer.Tick += (_, _) => RunTranslationPass();

            if (window.Content is FrameworkElement root)
            {
                HookRoot(root);
            }
            else
            {
                window.Activated += OnFirstActivated;
            }
        }

        private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_windowRef.TryGetTarget(out var win))
            {
                win.Activated -= OnFirstActivated;
                if (win.Content is FrameworkElement root)
                    HookRoot(root);
            }
        }

        private void HookRoot(FrameworkElement root)
        {
            if (_rootHooked) return;
            _rootHooked = true;

            root.Loaded += (_, _) => SchedulePass();
            root.LayoutUpdated += (_, _) =>
            {
                if (!_isTranslating)
                    SchedulePass();
            };

            if (root.IsLoaded)
                SchedulePass();
        }

        public void DispatchImmediatePass()
        {
            _dispatcher.TryEnqueue(RunTranslationPass);
        }

        private void SchedulePass()
        {
            if (_isTranslating) return;
            if (!_throttleTimer.IsRunning)
                _throttleTimer.Start();
        }

        private void RunTranslationPass()
        {
            if (_isTranslating) return;
            if (!_windowRef.TryGetTarget(out var window)) return;
            if (window.Content is not FrameworkElement root) return;

            _isTranslating = true;
            try
            {
                bool toChinese = L10nService.IsChinese;
                WalkSubtree(root, toChinese);

                var xamlRoot = root.XamlRoot;
                if (xamlRoot != null)
                {
                    var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
                    foreach (var popup in popups)
                    {
                        if (popup.Child != null)
                            WalkSubtree(popup.Child, toChinese);
                    }
                }
            }
            catch (Exception ex)
            {
                CrashReporter.Log($"[ForkL10n] VisualTreeTranslator pass failed — {ex.Message}");
            }
            finally
            {
                _isTranslating = false;
            }
        }
    }

    private static void WalkSubtree(DependencyObject root, bool toChinese)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            TranslateNode(node, toChinese);

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = count - 1; i >= 0; i--)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child != null)
                    stack.Push(child);
            }
        }
    }

    private static void TranslateNode(DependencyObject node, bool toChinese)
    {
        // ToolTipService.ToolTip on any DependencyObject
        var tip = ToolTipService.GetToolTip(node);
        if (tip is string tipText)
        {
            ApplySlot(node, "ToolTip", tipText, v => ToolTipService.SetToolTip(node, v), toChinese);
        }
        else if (tip is ToolTip tipControl && tipControl.Content is string tipContent)
        {
            ApplySlot(tipControl, "Content", tipContent, v => tipControl.Content = v, toChinese);
        }

        switch (node)
        {
            case ListViewBase lvb:
                EnsureListViewHooks(lvb);
                break;

            case ItemsRepeater ir:
                EnsureItemsRepeaterHooks(ir);
                break;

            case TextBlock tb:
                EnsureTextCallback(tb);
                if (tb.Inlines.Count > 1 ||
                    (tb.Inlines.Count == 1 && tb.ReadLocalValue(TextBlock.TextProperty) == DependencyProperty.UnsetValue))
                {
                    foreach (var inline in tb.Inlines)
                    {
                        if (inline is Run run)
                            ApplySlot(run, "Text", run.Text, v => run.Text = v, toChinese);
                    }
                }
                else
                {
                    ApplySlot(tb, "Text", tb.Text, v => tb.Text = v, toChinese);
                }
                break;

            case ToggleSwitch ts:
                if (ts.Header is string tsHeader)
                    ApplySlot(ts, "Header", tsHeader, v => ts.Header = v, toChinese);
                if (ts.OnContent is string tsOn)
                    ApplySlot(ts, "OnContent", tsOn, v => ts.OnContent = v, toChinese);
                if (ts.OffContent is string tsOff)
                    ApplySlot(ts, "OffContent", tsOff, v => ts.OffContent = v, toChinese);
                break;

            case TextBox tbox:
                if (tbox.Header is string tboxHeader)
                    ApplySlot(tbox, "Header", tboxHeader, v => tbox.Header = v, toChinese);
                ApplySlot(tbox, "PlaceholderText", tbox.PlaceholderText, v => tbox.PlaceholderText = v, toChinese);
                break;

            case AutoSuggestBox asb:
                if (asb.Header is string asbHeader)
                    ApplySlot(asb, "Header", asbHeader, v => asb.Header = v, toChinese);
                ApplySlot(asb, "PlaceholderText", asb.PlaceholderText, v => asb.PlaceholderText = v, toChinese);
                break;

            case NumberBox nb:
                if (nb.Header is string nbHeader)
                    ApplySlot(nb, "Header", nbHeader, v => nb.Header = v, toChinese);
                ApplySlot(nb, "PlaceholderText", nb.PlaceholderText, v => nb.PlaceholderText = v, toChinese);
                break;

            case ComboBox cb:
                if (cb.Header is string cbHeader)
                    ApplySlot(cb, "Header", cbHeader, v => cb.Header = v, toChinese);
                ApplySlot(cb, "PlaceholderText", cb.PlaceholderText, v => cb.PlaceholderText = v, toChinese);
                foreach (var item in cb.Items)
                {
                    if (item is ComboBoxItem cbi)
                    {
                        EnsureContentCallback(cbi);
                        if (cbi.Content is string cbiStr)
                            ApplySlot(cbi, "Content", cbiStr, v => cbi.Content = v, toChinese);
                        else if (cbi.Content is DependencyObject cbiDep)
                            WalkSubtree(cbiDep, toChinese);
                    }
                }
                break;

            case Expander exp:
                if (exp.Header is string expHeader)
                    ApplySlot(exp, "Header", expHeader, v => exp.Header = v, toChinese);
                break;

            case InfoBar ib:
                ApplySlot(ib, "Title", ib.Title, v => ib.Title = v, toChinese);
                ApplySlot(ib, "Message", ib.Message, v => ib.Message = v, toChinese);
                break;

            case ContentDialog cd:
                if (cd.Title is string cdTitle)
                    ApplySlot(cd, "Title", cdTitle, v => cd.Title = v, toChinese);
                ApplySlot(cd, "PrimaryButtonText", cd.PrimaryButtonText, v => cd.PrimaryButtonText = v, toChinese);
                ApplySlot(cd, "SecondaryButtonText", cd.SecondaryButtonText, v => cd.SecondaryButtonText = v, toChinese);
                ApplySlot(cd, "CloseButtonText", cd.CloseButtonText, v => cd.CloseButtonText = v, toChinese);
                if (cd.Content is string cdContent)
                    ApplySlot(cd, "Content", cdContent, v => cd.Content = v, toChinese);
                break;

            case MenuFlyoutSubItem mfsi:
                ApplySlot(mfsi, "Text", mfsi.Text, v => mfsi.Text = v, toChinese);
                foreach (var sub in mfsi.Items)
                {
                    if (sub != null)
                        TranslateNode(sub, toChinese);
                }
                break;

            case MenuFlyoutItem mfi:
                ApplySlot(mfi, "Text", mfi.Text, v => mfi.Text = v, toChinese);
                break;

            case ContentControl cc:
                EnsureContentCallback(cc);
                if (cc.Content is string ccContent)
                    ApplySlot(cc, "Content", ccContent, v => cc.Content = v, toChinese);
                break;
        }
    }

    private static void EnsureTextCallback(TextBlock tb)
    {
        var snapshot = Snapshots.GetOrCreateValue(tb);
        if (snapshot.TextCallbackRegistered) return;
        snapshot.TextCallbackRegistered = true;

        tb.RegisterPropertyChangedCallback(TextBlock.TextProperty, static (sender, _) =>
        {
            if (sender is not TextBlock block) return;
            if (block.Inlines.Count > 1 ||
                (block.Inlines.Count == 1 && block.ReadLocalValue(TextBlock.TextProperty) == DependencyProperty.UnsetValue))
                return;

            ApplySlot(block, "Text", block.Text, v => block.Text = v, L10nService.IsChinese);
        });
    }

    private static void EnsureContentCallback(ContentControl cc)
    {
        var snapshot = Snapshots.GetOrCreateValue(cc);
        if (snapshot.ContentCallbackRegistered) return;
        snapshot.ContentCallbackRegistered = true;

        cc.RegisterPropertyChangedCallback(ContentControl.ContentProperty, static (sender, _) =>
        {
            if (sender is not ContentControl ctrl) return;
            if (ctrl.Content is string text)
                ApplySlot(ctrl, "Content", text, v => ctrl.Content = v, L10nService.IsChinese);
        });
    }

    private static void EnsureListViewHooks(ListViewBase lvb)
    {
        var snapshot = Snapshots.GetOrCreateValue(lvb);
        if (snapshot.ListHooksRegistered) return;
        snapshot.ListHooksRegistered = true;

        lvb.ContainerContentChanging += static (_, args) =>
        {
            if (args.InRecycleQueue || args.ItemContainer == null) return;
            WalkSubtree(args.ItemContainer, L10nService.IsChinese);
        };
    }

    private static void EnsureItemsRepeaterHooks(ItemsRepeater ir)
    {
        var snapshot = Snapshots.GetOrCreateValue(ir);
        if (snapshot.ListHooksRegistered) return;
        snapshot.ListHooksRegistered = true;

        ir.ElementPrepared += static (_, args) =>
        {
            if (args.Element != null)
                WalkSubtree(args.Element, L10nService.IsChinese);
        };
    }

    private static void ApplySlot(
        DependencyObject target,
        string slotKey,
        string? currentValue,
        Action<string> setter,
        bool toChinese)
    {
        if (string.IsNullOrEmpty(currentValue)) return;

        var snapshot = Snapshots.GetOrCreateValue(target);
        if (snapshot.Slots.TryGetValue(slotKey, out var slot))
        {
            if (slot.IsUpdating) return;

            if (string.Equals(currentValue, slot.LastApplied, StringComparison.Ordinal))
            {
                if (toChinese)
                {
                    // Already translated and unchanged: O(1) skip.
                    if (!string.Equals(slot.LastApplied, slot.Original, StringComparison.Ordinal))
                        return;

                    // Previously restored to English; re-translate using cached Original.
                    if (L10nService.TryTranslate(slot.Original, out var translated))
                    {
                        WriteSlot(slot, translated, setter);
                    }
                }
                else
                {
                    if (!string.Equals(currentValue, slot.Original, StringComparison.Ordinal))
                    {
                        WriteSlot(slot, slot.Original, setter);
                    }
                }
                return;
            }

            // Code-behind or container recycling updated the text dynamically.
            slot.Original = currentValue;
            if (toChinese && L10nService.TryTranslate(currentValue, out var nextTranslated))
            {
                WriteSlot(slot, nextTranslated, setter);
            }
            else
            {
                slot.LastApplied = currentValue;
            }
            return;
        }

        // First time seeing this control+slot.
        var newSlot = new TextSlot(currentValue, currentValue);
        snapshot.Slots[slotKey] = newSlot;

        if (toChinese && L10nService.TryTranslate(currentValue, out var initialTranslated))
        {
            WriteSlot(newSlot, initialTranslated, setter);
        }
    }

    private static void WriteSlot(TextSlot slot, string value, Action<string> setter)
    {
        slot.IsUpdating = true;
        try
        {
            slot.LastApplied = value;
            setter(value);
        }
        finally
        {
            slot.IsUpdating = false;
        }
    }

    private sealed class ElementSnapshot
    {
        public Dictionary<string, TextSlot> Slots { get; } = new(StringComparer.Ordinal);
        public bool TextCallbackRegistered { get; set; }
        public bool ContentCallbackRegistered { get; set; }
        public bool ListHooksRegistered { get; set; }
    }

    private sealed class TextSlot(string original, string lastApplied)
    {
        public string Original { get; set; } = original;
        public string LastApplied { get; set; } = lastApplied;
        public bool IsUpdating { get; set; }
    }
}
