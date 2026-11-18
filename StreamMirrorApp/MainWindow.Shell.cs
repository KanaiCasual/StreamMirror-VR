using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StreamMirror;

/// <summary>
/// The window's frame follows its width, in three forms:
/// <list type="bullet">
/// <item>Expanded: the rail with the pages' names, the quick switches as labelled switches at its foot.</item>
/// <item>Compact: the rail as icons; a top bar carries the quick switches (with their names while they fit) and the save mark.</item>
/// <item>Minimal: no rail; the top bar has a menu that opens the rail as a drawer, the page's name, the quick switches as icons.</item>
/// </list>
/// The same controls move between the forms (the rail, the quick switches), so their state and wiring never split.
/// </summary>
public partial class MainWindow
{
    private enum ShellForm { None, Expanded, Compact, Minimal }

    /// <summary>Window widths (client area) from which the rail shows names, and from which it shows at all.</summary>
    private const double ExpandedFromWidth = 1180, CompactFromWidth = 760;

    private ShellForm _shellForm;

    private void UpdateShell()
    {
        var width = Root.ActualWidth;
        if (width <= 0) return;
        var form = width >= ExpandedFromWidth ? ShellForm.Expanded : width >= CompactFromWidth ? ShellForm.Compact : ShellForm.Minimal;
        if (form != _shellForm)
        {
            _shellForm = form;
            ApplyShellForm(form);
        }
        TopTitle.Text = (Tabs.SelectedItem as TabItem)?.Header as string ?? "";
        if (form != ShellForm.Expanded) FitQuickBar();
    }

    private void ApplyShellForm(ShellForm form)
    {
        CloseDrawer();
        var drawer = form == ShellForm.Minimal;
        if (drawer && ReferenceEquals(Rail.Parent, Root))
        {
            Root.Children.Remove(Rail);
            DrawerHost.Child = Rail;
        }
        else if (!drawer && ReferenceEquals(Rail.Parent, DrawerHost))
        {
            DrawerHost.Child = null;
            Root.Children.Insert(0, Rail);
            Grid.SetColumn(Rail, 0);
        }

        var icons = form == ShellForm.Compact;
        Nav.ItemTemplate = (DataTemplate)FindResource(icons ? "RailShort" : "RailFull");
        Nav.MinWidth = icons ? 0 : 158;
        RailBrand.Visibility = RailVersion.Visibility = icons ? Visibility.Collapsed : Visibility.Visible;
        Rail.Padding = icons ? new Thickness(6, 14, 6, 10) : new Thickness(10, 14, 10, 10);
        // Only the expanded rail carries the save mark; the other forms have it in the top bar.
        SaveMark.Visibility = form != ShellForm.Expanded || SaveMarkText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        TopBar.Visibility = form == ShellForm.Expanded ? Visibility.Collapsed : Visibility.Visible;
        MenuButton.Visibility = TopTitle.Visibility = drawer ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageTitles(shown: !drawer);
        PlaceQuickSwitches(inRail: form == ShellForm.Expanded);
    }

    private List<TextBlock>? _pageTitles;

    /// <summary>A narrow window names the page in its top bar, so the page's own big title steps aside and leaves the room to what is beside it.</summary>
    private void UpdatePageTitles(bool shown)
    {
        if (_pageTitles == null)
        {
            var titleStyle = (Style)FindResource("PageTitle");
            _pageTitles = [];
            void Collect(object node)
            {
                if (node is TextBlock { Style: var style } title && ReferenceEquals(style, titleStyle)) _pageTitles.Add(title);
                if (node is DependencyObject parent) foreach (var child in LogicalTreeHelper.GetChildren(parent)) Collect(child);
            }
            Collect(Tabs);
        }
        foreach (var title in _pageTitles) title.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The quick switches: labelled switches at the rail's foot, or chips in the top bar.</summary>
    private void PlaceQuickSwitches(bool inRail)
    {
        var host = (Panel)QuickSwitches.Parent;
        if (inRail && !ReferenceEquals(host, Rail.Child))
        {
            host.Children.Remove(QuickSwitches);
            var dock = (DockPanel)Rail.Child;
            dock.Children.Insert(dock.Children.IndexOf(Nav), QuickSwitches); // docked bottom, above the save mark
            DockPanel.SetDock(QuickSwitches, Dock.Bottom);
        }
        else if (!inRail && !ReferenceEquals(host, QuickBar))
        {
            host.Children.Remove(QuickSwitches);
            QuickBar.Children.Add(QuickSwitches);
        }
        QuickSwitches.Orientation = inRail ? Orientation.Vertical : Orientation.Horizontal;
        QuickSwitches.Margin = inRail ? new Thickness(10, 6, 4, 2) : new Thickness(0);
        foreach (var child in QuickSwitches.Children)
        {
            if (child is TextBlock words) words.Visibility = inRail ? Visibility.Visible : Visibility.Collapsed;
        }
        SetQuickForm(inRail ? "rail" : "chip");
    }

    private string _quickForm = "";

    /// <summary>"rail": the app's labelled switches; "chip": icon and name; "icon": the icon alone (the name is the tooltip's first line).</summary>
    private void SetQuickForm(string form)
    {
        if (form == _quickForm) return;
        _quickForm = form;
        foreach (var box in QuickSwitches.Children.OfType<CheckBox>())
        {
            if (form == "rail")
            {
                box.ClearValue(StyleProperty);
                box.FontSize = 12;
                box.Height = 22;
                box.Margin = new Thickness(0);
            }
            else
            {
                box.Style = (Style)FindResource(form == "chip" ? "QuickChip" : "QuickIcon");
                box.ClearValue(FontSizeProperty);
                box.ClearValue(HeightProperty);
                box.ClearValue(MarginProperty);
            }
        }
    }

    /// <summary>What the quick switches and the save mark need with names; forgotten when the save mark's words change.</summary>
    private (double NamesAndSave, double Names)? _barNeeds;

    /// <summary>
    /// The top bar shows as much as fits, in this order of giving way: the save mark's words, then the quick switches'
    /// names (their icons and tooltips stay). The widths are measured once, so a resize only compares numbers.
    /// </summary>
    private void FitQuickBar()
    {
        var room = ((FrameworkElement)TopBar.Child).ActualWidth;
        if (room <= 0) return;
        _barNeeds ??= MeasureBarForms();
        var fixedPart = (TopTitle.Visibility == Visibility.Visible ? 110 : 0) // the page's name keeps a readable start
                      + (MenuButton.Visibility == Visibility.Visible ? MenuButton.Width : 0);
        var (names, saveWords) = _barNeeds.Value.NamesAndSave + fixedPart <= room ? (true, true)
                               : _barNeeds.Value.Names + fixedPart <= room ? (true, false)
                               : (false, false);
        SetQuickForm(names ? "chip" : "icon");
        TopSaveMarkText.Visibility = saveWords ? Visibility.Visible : Visibility.Collapsed;
    }

    private (double NamesAndSave, double Names) MeasureBarForms()
    {
        // Each part measured on its own: a new style or visibility re-measures the part, but its panel may answer from before.
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        double Width(FrameworkElement part) { part.Measure(unbounded); return part.DesiredSize.Width; }
        SetQuickForm("chip");
        var names = QuickSwitches.Children.OfType<CheckBox>().Where(b => b.Visibility != Visibility.Collapsed).Sum(Width)
                  + QuickBar.Margin.Left + QuickBar.Margin.Right;
        var mark = TopSaveMark.Visibility == Visibility.Visible ? Width(TopSaveMarkIcon) + TopSaveMark.Margin.Left + TopSaveMark.Margin.Right : 0;
        TopSaveMarkText.Visibility = Visibility.Visible;
        var words = TopSaveMark.Visibility == Visibility.Visible ? Width(TopSaveMarkText) : 0;
        return (names + mark + words, names + mark);
    }

    // ---- the drawer of a narrow window

    private void OnMenuButton(object sender, RoutedEventArgs e)
    {
        if (DrawerLayer.Visibility == Visibility.Visible) CloseDrawer(); else OpenDrawer();
    }

    private void OpenDrawer()
    {
        DrawerLayer.Visibility = Visibility.Visible;
        // The keyboard goes to the page that is open, so the arrows move through the pages and Enter picks one.
        (Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) as UIElement ?? Nav).Focus();
    }

    private void CloseDrawer()
    {
        if (DrawerLayer.Visibility != Visibility.Visible) return;
        DrawerLayer.Visibility = Visibility.Collapsed;
        if (_shellForm == ShellForm.Minimal) MenuButton.Focus();
    }

    private void OnDrawerShade(object sender, MouseButtonEventArgs e) => CloseDrawer();

    /// <summary>Development aid for --screenshots: a picture of the open drawer, when the window is narrow enough to have one.</summary>
    public void SnapshotDrawer(Action<string> snapshot)
    {
        if (_shellForm != ShellForm.Minimal) return;
        OpenDrawer();
        snapshot("drawer");
        CloseDrawer();
    }

    /// <summary>Esc closes the drawer, and so does choosing a page in it.</summary>
    private void BuildShell()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DrawerLayer.Visibility != Visibility.Visible) return;
            CloseDrawer();
            e.Handled = true;
        };
        Nav.PreviewMouseLeftButtonUp += (_, _) => { if (_shellForm == ShellForm.Minimal) CloseDrawer(); };
        Nav.KeyDown += (_, e) => { if (e.Key == Key.Enter && _shellForm == ShellForm.Minimal) CloseDrawer(); };
    }
}
