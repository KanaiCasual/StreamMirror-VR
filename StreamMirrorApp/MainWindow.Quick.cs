using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace StreamMirror;

/// <summary>
/// The rail's quick switches: the most used on/off settings at a glance. Each one works its page's own control - flipping
/// the switch flips that control and runs what the control runs - and follows that control however it changes (its page,
/// a hotkey, a preset, a refused Windows prompt), so the two never disagree.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, CheckBox> _toggleBoxes = [];
    private readonly Dictionary<string, CheckBox> _quadViewsBoxes = [];
    private CheckBox? _quickMirror;
    private (bool Running, bool Available) _mirrorShown;
    private bool _quickSyncing;

    private void BuildQuickSwitches()
    {
        QuickSwitches.Children.Add(new TextBlock { Text = "Quick switches", Style = (Style)FindResource("Hint"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });

        _quickMirror = Quick("Mirror window", "Opens or closes the mirror window, like Open and Close on the Mirror page.");
        _quickMirror.Click += (_, e) => { if (_quickMirror.IsChecked == true) OnMirrorOpen(_quickMirror, e); else OnMirrorClose(_quickMirror, e); };
        ShowQuickMirror(_mirrorShown.Running, _mirrorShown.Available);

        Follow(Quick("Gaze ring", "Shows or hides the ring in the mirror, like the switch on the Ring page.\n\n**Live:** a running game shows the change at once."),
               _toggleBoxes["enabled"], click: false);
        Follow(Quick("Desktop cover", "Covers the VR game's own window while you play, like the switch on the Desktop cover page."), CoverEnabled, click: true);

        var quadViews = Quick("Quad views", "Switches StreamMirror VR's quad views layer on or off, like the switch on the Quad Views page.\n\nWindows asks for permission, and a game takes the change at its **next start**.");
        Follow(quadViews, QuadViewsLayerOn, click: true);
        QuickSwitches.Children.Add(new TextBlock { Text = "Next game start", Style = (Style)FindResource("Hint"), FontSize = 10.5, Margin = new Thickness(0, -3, 0, 1) });

        var diagnostics = Quick("Diagnostics", "Tints the view in the headset to check quad views while you play, like the switch on the Quad Views page.\n\nThe mirror shows it too: switch it off before you stream.\n\n**Live:** a running game shows the change at once.");
        Follow(diagnostics, _quadViewsBoxes["diagnostics"], click: false);
        Follow(Quick("Debug view", "Tints the sharp region in the headset to show its size and place, like Debug: show focus view on the Quad Views page.\n\n**Live:** a running game shows the change at once."),
               _quadViewsBoxes["debug_focus"], click: false);
    }

    private CheckBox Quick(string label, string tip)
    {
        var box = new CheckBox { Content = label, FontSize = 12, MinWidth = 0, MinHeight = 0, Height = 22, Margin = new Thickness(0), ToolTip = WrappedToolTip(tip) };
        ShowToolTipsPatiently(box);
        QuickSwitches.Children.Add(box);
        return box;
    }

    /// <summary>The quick switch shows the page's control and works it. click: the page's control acts on Click (it reads its
    /// own state then); otherwise on Checked/Unchecked, which setting IsChecked raises by itself.</summary>
    private void Follow(CheckBox quick, CheckBox page, bool click)
    {
        void Show()
        {
            _quickSyncing = true;
            quick.IsChecked = page.IsChecked;
            quick.IsEnabled = page.IsEnabled;
            _quickSyncing = false;
        }
        page.Checked += (_, _) => Show();
        page.Unchecked += (_, _) => Show();
        page.IsEnabledChanged += (_, _) => Show();
        quick.Click += (_, _) =>
        {
            if (_quickSyncing) return;
            page.IsChecked = quick.IsChecked;
            if (click) page.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, page));
            Show(); // a page that refuses (a declined Windows prompt) puts its control back, and this with it
        };
        Show();
    }

    /// <summary>Development aid for --screenshots: a quick switch works its page's control, and follows it back. Only the
    /// switches that act at once without a prompt or a program to start (ring, diagnostics, debug view).</summary>
    public string ExerciseQuickSwitches()
    {
        var report = new System.Text.StringBuilder();
        void Check(bool ok, string what) => report.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}");
        CheckBox Switch(string label) => QuickSwitches.Children.OfType<CheckBox>().Single(b => Equals(b.Content, label));
        void Flip(CheckBox quick)
        {
            quick.IsChecked = quick.IsChecked != true; // what a click does first, then its Click handlers
            quick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, quick));
        }

        var ring = Switch("Gaze ring");
        var was = _values.GetValueOrDefault("enabled");
        Flip(ring);
        Check(_values.GetValueOrDefault("enabled") != was && _toggleBoxes["enabled"].IsChecked == ring.IsChecked, $"Gaze ring switch sets the ring: enabled {was} -> {_values.GetValueOrDefault("enabled")}");
        _toggleBoxes["enabled"].IsChecked = was == "1";
        Check(ring.IsChecked == (was == "1"), "the Ring page's switch moves the quick switch back");

        foreach (var (label, id) in new[] { ("Diagnostics", "diagnostics"), ("Debug view", "debug_focus") })
        {
            var quick = Switch(label);
            var before = _quadViewsValues.GetValueOrDefault(id);
            Flip(quick);
            Check(_quadViewsValues.GetValueOrDefault(id) != before && _quadViewsBoxes[id].IsChecked == quick.IsChecked, $"{label} switch sets the Quad Views page: {before} -> {_quadViewsValues.GetValueOrDefault(id)}");
            _quadViewsBoxes[id].IsChecked = before != 0;
            Check(quick.IsChecked == (before != 0), $"the Quad Views page's {label} moves the quick switch back");
        }
        Check(Switch("Quad views").IsChecked == QuadViewsLayerOn.IsChecked && Switch("Desktop cover").IsChecked == CoverEnabled.IsChecked,
              "Quad views and Desktop cover show their pages' switches");
        return report.ToString();
    }

    /// <summary>Called whenever the Mirror page looks at the mirror window (opened, closed, or closed by its own button).</summary>
    private void ShowQuickMirror(bool running, bool available)
    {
        _mirrorShown = (running, available);
        if (_quickMirror == null) return;
        _quickMirror.IsChecked = running;
        _quickMirror.IsEnabled = available;
    }
}
