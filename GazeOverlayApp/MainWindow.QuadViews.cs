using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GazeOverlay;

/// <summary>
/// The Quad Views tab: the settings TallyMouse's QuadViews Companion edits, plus how many pixels they make the game render.
/// They are the settings of VR Gaze Mirror's quad views layer (built on Quad-Views-Foveated): saved as they are made, and a
/// running game takes them at once. The switch on top turns the layer on or off.
/// </summary>
public partial class MainWindow
{
    private string _quadViewsPath => QuadViewsFile.DefaultPath;
    /// <summary>The page edits VR Gaze Mirror's own layer's settings (QuadViewsFile.Plan writes the official layer's too).</summary>
    private const bool _quadViewsOwn = true;
    private QuadViewsFile _quadViewsFile = QuadViewsFile.Load(QuadViewsFile.DefaultPath);
    private QuadViewsSession? _quadViewsSession;
    private readonly Dictionary<string, double> _quadViewsValues = [];
    private readonly HashSet<string> _quadViewsDirty = [];
    private readonly Dictionary<string, Action<double>> _quadViewsSetters = [];
    private bool _quadViewsLoading;
    private CheckBox? _quadViewsTurboBox;
    private readonly Dictionary<string, TextBlock> _quadViewsReadouts = [];

    private void BuildQuadViewsUi()
    {
        QuadViewsNotice.Visibility = Visibility.Collapsed;
        ShowToolTipsPatiently(QuadViewsTitle);

        // Two cards: what a running game takes at once, and what waits for its next start. Side by side when there is room.
        var focus = new StackPanel();
        var resolution = new StackPanel();
        var cards = new Grid { Margin = new Thickness(0, 0, -10, 0) };
        var focusCard = NewCard("Live: a running game shows changes at once", focus, 0);
        var resolutionCard = NewCard("Next start: changes wait for the game to start again", resolution, 0);
        focusCard.Margin = resolutionCard.Margin = new Thickness(0, 0, 10, 10);
        cards.Children.Add(focusCard);
        cards.Children.Add(resolutionCard);
        LayOutCards(cards, sideBySide: true);
        // One under the other in a narrower window, and then no wider than a row of settings reads well.
        PanelQuadViews.SizeChanged += (_, _) =>
        {
            // The drawing beside the switch while the card has room for both, above it otherwise.
            var wide = PanelQuadViews.ActualWidth >= 760;
            DockPanel.SetDock(QuadViewsSceneFrame, wide ? Dock.Left : Dock.Top);
            QuadViewsSceneFrame.Margin = wide ? new Thickness(0, 0, 18, 0) : new Thickness(0, 0, 0, 14);
            QuadViewsSceneFrame.HorizontalAlignment = HorizontalAlignment.Left;
            var sideBySide = PanelQuadViews.ActualWidth >= 900;
            LayOutCards(cards, sideBySide);
            cards.HorizontalAlignment = sideBySide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            cards.Width = sideBySide ? double.NaN : Math.Min(PanelQuadViews.ActualWidth, 720) + 10;
        };
        PanelQuadViews.Children.Add(cards);

        foreach (var setting in QuadViewsSetting.All)
        {
            var description = setting.Description + (setting.Live
                ? "\n\n**Live:** a running game shows the change at once."
                : "\n\n**Next start:** a running game keeps the old value until it starts again.");
            var row = NewRow(description);
            if (setting.IsToggle)
            {
                var box = new CheckBox { Content = setting.Label, FontWeight = FontWeights.SemiBold, ToolTip = WrappedToolTip(description) };
                ShowToolTipsPatiently(box);
                box.Checked += (_, _) => OnQuadViewsChanged(setting.Id, 1);
                box.Unchecked += (_, _) => OnQuadViewsChanged(setting.Id, 0);
                _quadViewsSetters[setting.Id] = v => box.IsChecked = v != 0;
                if (setting.Id == "turbo") _quadViewsTurboBox = box;
                AddRowEditor(row, box, column: 0, span: 3);
            }
            else
            {
                AddRowLabel(row, setting.Label, description);
                var slider = new Slider
                {
                    Minimum = setting.Min, Maximum = setting.Max, SmallChange = setting.Step, LargeChange = setting.Step * 5,
                    TickFrequency = setting.Step, IsSnapToTickEnabled = true,
                };
                var valueText = AddRowValue(row, text =>
                {
                    var digits = new string(text.Trim().TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray()).Replace(',', '.');
                    if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) slider.Value = Math.Clamp(Math.Round(number / setting.Step) * setting.Step, setting.Min, setting.Max);
                });
                // The default is Quad-Views-Foveated's own, read from the file it ships for this headset's runtime.
                slider.MouseDoubleClick += (_, _) => { if (QuadViewsLayerDefaults().TryGetValue(setting.Id, out var standard)) slider.Value = Math.Clamp(standard, setting.Min, setting.Max); };
                Describe(slider, setting.Label, description + "\n\nDouble-click the slider for Quad-Views-Foveated's own default. Click the value to type one.");
                slider.ValueChanged += (_, e) =>
                {
                    valueText.Text = $"{setting.Show(e.NewValue)} {setting.Suffix}";
                    OnQuadViewsChanged(setting.Id, e.NewValue);
                };
                _quadViewsSetters[setting.Id] = v =>
                {
                    slider.Value = Math.Clamp(Math.Round(v / setting.Step) * setting.Step, setting.Min, setting.Max);
                    valueText.Text = $"{setting.Show(slider.Value)} {setting.Suffix}";
                };
                AddRowEditor(row, slider);
            }
            var card = setting.Live ? focus : resolution;
            card.Children.Add(row);
            // The official layer's own debug switch has no place on this page (the file model knows it, for the import).
            row.Visibility = setting.AppliesTo(_quadViewsOwn) ? Visibility.Visible : Visibility.Collapsed;
            // Under the sliders that set the pixel count: what they make (RefreshQuadViewsReadout fills them in).
            var readouts = setting.Id switch
            {
                "focus_v" => ["area"],
                "focus_res" => ["focus_res"],
                "peripheral_res" => new[] { "peripheral_res", "total" },
                _ => [],
            };
            foreach (var id in readouts)
            {
                var readout = new TextBlock
                {
                    Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(RowLabelWidth, 0, 22, 4),
                };
                _quadViewsReadouts[id] = readout;
                card.Children.Add(readout);
            }
        }

        // Presets sit in the banner at the top, next to Apply, so they are reachable without scrolling.
        foreach (var preset in QuadViewsSetting.Presets)
        {
            var button = new Button { Content = preset.Name, ToolTip = WrappedToolTip(preset.Description), Margin = new Thickness(0, 2, 6, 2) };
            button.Click += (_, _) =>
            {
                foreach (var (id, value) in preset.Values.Count > 0 ? preset.Values : QuadViewsLayerDefaults())
                {
                    _quadViewsValues[id] = value;
                    _quadViewsDirty.Add(id);
                    _quadViewsLoading = true;
                    _quadViewsSetters[id](value);
                    _quadViewsLoading = false;
                }
                RefreshQuadViewsReadout();
            };
            QuadViewsPresets.Children.Add(button);
        }

        QuadViewsSlots.Children.Add(BuildSlotBar(_appSettings.QuadViewsSlots,
            capture: () => QuadViewsSetting.All.Where(s => !s.IsToggle).ToDictionary(s => s.Id, s => _quadViewsValues.GetValueOrDefault(s.Id).ToString("R", CultureInfo.InvariantCulture)),
            apply: values =>
            {
                foreach (var setting in QuadViewsSetting.All.Where(s => !s.IsToggle))
                {
                    if (!values.TryGetValue(setting.Id, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                    _quadViewsValues[setting.Id] = value;
                    _quadViewsDirty.Add(setting.Id);
                    _quadViewsLoading = true;
                    _quadViewsSetters[setting.Id](value);
                    _quadViewsLoading = false;
                }
                RefreshQuadViewsReadout();
            },
            describe: values => string.Join("\n", QuadViewsSetting.All.Where(s => !s.IsToggle && values.ContainsKey(s.Id)).Select(s =>
                $"{s.Label}: {(double.TryParse(values[s.Id], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? s.Show(v) : values[s.Id])} {s.Suffix}"))
                + "\n\nLike a preset, a slot sets the sliders; click Apply afterwards."));

        // Pick up what another tool (the Companion, a text editor) saved - when the tab is opened, not on a timer.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs) && ReferenceEquals(Tabs.SelectedItem, QuadViewsTab) && _quadViewsDirty.Count == 0) LoadQuadViews();
        };
        // Coming back from the game (or the Companion) with this tab open: show the new game start, again without a timer.
        // Under Gaze from = Fixed centre the new game start matters on every page (the sharp area follows it).
        Activated += (_, _) =>
        {
            var fixedCentre = _values.GetValueOrDefault("gaze_source") == "fixed";
            if ((ReferenceEquals(Tabs.SelectedItem, QuadViewsTab) || fixedCentre) && _quadViewsDirty.Count == 0) LoadQuadViews(keepStatus: true);
        };
    }

    /// <summary>Slider values for "QV defaults": the shipped settings file alone, as the layer reads it for this runtime.</summary>
    private Dictionary<string, double> QuadViewsLayerDefaults()
    {
        var files = new List<(string, QuadViewsFile)>();
        var shipped = _quadViewsSession?.ConfigPaths.FirstOrDefault(p => !string.Equals(p, _quadViewsPath, StringComparison.OrdinalIgnoreCase));
        if (shipped != null && File.Exists(shipped))
        {
            try { files.Add(("the file shipped with the layer", QuadViewsFile.Load(shipped))); } catch { /* unreadable: the built-in values remain */ }
        }
        var defaults = QuadViewsFile.Evaluate(files, _quadViewsSession);
        return QuadViewsSetting.All.Where(s => !s.IsToggle).ToDictionary(s => s.Id, s => s.ToSlider(defaults[s.Keys[0]]));
    }

    private void OnQuadViewsChanged(string id, double value)
    {
        if (_quadViewsLoading) return;
        _quadViewsValues[id] = value;
        _quadViewsDirty.Add(id);
        RefreshQuadViewsReadout();
    }

    /// <summary>Shows whether the layer is on. The official Quad-Views-Foveated still counts: switching ours on turns it off.</summary>
    private void ShowQuadViewsLayer(SystemStatus status)
    {
        QuadViewsLayerOn.IsChecked = status.OwnQuadViewsOn;
        QuadViewsLayerOn.IsEnabled = status.OwnQuadViewsInstalled;
        QuadViewsLayerNote.Text =
            !status.OwnQuadViewsInstalled ? "The quad views layer is not installed. Run the VR Gaze Mirror installer's Repair."
            : status.OwnQuadViewsOn ? "On: Direct3D 11, Direct3D 12 and Vulkan games that offer quad views get them, at their next start."
            : status.QuadViews.Health != LayerHealth.NotActive ? "Off. The official Quad-Views-Foveated is on and does quad views now; switching this on turns it off."
            : "Off: no game gets quad views. The settings below wait for it.";
    }

    private async void OnQuadViewsLayerToggle(object sender, RoutedEventArgs e)
    {
        var on = QuadViewsLayerOn.IsChecked == true;
        // On: ours, and the official layer off (one quad views layer at a time), above the mirror - in one prompt.
        var layers = LayerStatus.ReadLayers();
        var switches = new Dictionary<string, bool>();
        foreach (var layer in layers)
        {
            if (layer.LayerName == LayerStatus.OwnQuadViewsLayer) switches[layer.JsonPath] = on;
            else if (on && layer.LayerName == LayerStatus.QuadViewsLayer) switches[layer.JsonPath] = false;
        }
        var after = layers.Select(l => switches.TryGetValue(l.JsonPath, out var enabled) ? l with { Enabled = enabled } : l).ToList();
        var order = LayerOrder.Suggest(after) ?? after;
        try
        {
            if (!await LayerOrder.ApplyAsync(false, order, switches)) SetQuadViewsStatus("Nothing changed: Windows' permission prompt was declined.");
            else AppLog.Write($"Quad views layer switched {(on ? "on" : "off")}.");
        }
        catch (Exception error)
        {
            SetQuadViewsStatus("Could not switch: " + error.Message);
            AppLog.Write("The quad views layer could not be switched", error);
        }
        RefreshStatus();
    }

    private void LoadQuadViews(bool keepStatus = false)
    {
        _quadViewsSession = QuadViewsSession.Read(QuadViewsSession.LogPath);
        // The own layer's first settings are the official layer's, where there are any (the layer copies them too).
        var official = Path.Combine(QuadViewsSession.OfficialFolder, "settings.cfg");
        var imported = !File.Exists(_quadViewsPath) && File.Exists(official);
        _quadViewsFile = QuadViewsFile.Load(imported ? official : _quadViewsPath);
        _quadViewsDirty.Clear();

        var effective = EvaluateQuadViews(_quadViewsFile);
        var common = EvaluateQuadViews(_quadViewsFile, commonOnly: true);
        _quadViewsLoading = true;
        foreach (var setting in QuadViewsSetting.All)
        {
            // What the file says it wants; where it says nothing, what the layer falls back to.
            var key = setting.Keys[0];
            var fileValue = ParseQuadViewsValue(key, setting.CommonOnly ? _quadViewsFile.InCommon(key) : _quadViewsFile.Intended(key))
                            ?? (setting.CommonOnly ? common[key] : effective[key]);
            var shown = setting.IsToggle ? fileValue : setting.ToSlider(fileValue);
            _quadViewsValues[setting.Id] = shown;
            _quadViewsSetters[setting.Id](shown);
        }
        _quadViewsLoading = false;

        if (!keepStatus)
        {
            SetQuadViewsStatus((imported ? "These are your Quad-Views-Foveated settings: VR Gaze Mirror's layer starts from them.\n" : "") +
                               "Changes are saved as you make them. The card titles below say when a running game takes them.");
        }
        RefreshQuadViewsReadout();
        SyncFixedFocusKeys(); // The fixed centre's sharp area follows the file.
    }

    private static double? ParseQuadViewsValue(string key, string? text) =>
        text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? QuadViewsFile.Clamp(key, number) : null;

    private QuadViewsFile PlannedQuadViewsFile() => QuadViewsFile.Plan(_quadViewsFile, _quadViewsValues, _quadViewsDirty, _quadViewsOwn);

    /// <summary>The own layer's file follows the page: written whenever the page differs from it (the layer picks it up).
    /// Not before the user has changed something when there is no file yet (the layer's first start makes it, from the
    /// official layer's), and never in a self-test or screenshot run.</summary>
    private void SaveOwnQuadViews()
    {
        if (App.Headless || (_quadViewsDirty.Count == 0 && !File.Exists(_quadViewsPath))) return;
        var planned = PlannedQuadViewsFile();
        if (planned.Text == _quadViewsFile.Text) return;
        try
        {
            planned.Save(_quadViewsPath);
            _quadViewsFile = QuadViewsFile.Parse(planned.Text);
            _quadViewsDirty.Clear();
        }
        catch (Exception error)
        {
            SetQuadViewsStatus("Could not save: " + error.Message);
            AppLog.Write("Quad Views settings could not be saved", error);
        }
    }

    private QuadViewsEffective EvaluateQuadViews(QuadViewsFile userFile, bool commonOnly = false)
    {
        var files = new List<(string, QuadViewsFile)>();
        // The file shipped with the layer is read first; the log says where it is.
        var shipped = _quadViewsSession?.ConfigPaths.FirstOrDefault(p => !string.Equals(p, _quadViewsPath, StringComparison.OrdinalIgnoreCase));
        if (shipped != null && File.Exists(shipped))
        {
            try { files.Add(("the file shipped with the layer", QuadViewsFile.Load(shipped))); } catch { /* unreadable: evaluate without it */ }
        }
        files.Add(("your settings file", userFile));
        return QuadViewsFile.Evaluate(files, commonOnly ? null : _quadViewsSession);
    }

    private (System.Windows.Media.Imaging.BitmapSource Source, double Scale, System.Windows.Media.Imaging.BitmapSource Low)? _quadViewsLow;

    /// <summary>
    /// The drawing beside the switch: the Mirror page's picture with the sharp area at its share of the view (the focus
    /// sections), sharp inside and blocky outside, where the surround draws at its lower resolution. Without a picture, the
    /// box and the numbers alone.
    /// </summary>
    private void UpdateQuadViewsScene(double horizontal, double vertical, double focus, double peripheral, double share)
    {
        var width = QuadViewsSceneFrame.Width;
        var height = QuadViewsSceneFrame.Height;
        var source = _cropImage.Source as System.Windows.Media.Imaging.BitmapSource ?? _pictureFirst;
        if (source != null)
        {
            // Blockier the lower the surround's resolution; not to scale, enough to see the difference.
            var scale = Math.Clamp(peripheral * 0.18, 0.02, 1.0) * Math.Min(1, 640.0 / source.PixelWidth);
            if (_quadViewsLow is not { } cached || !ReferenceEquals(cached.Source, source) || Math.Abs(cached.Scale - scale) > 1e-6)
            {
                var low = new System.Windows.Media.Imaging.TransformedBitmap(source, new ScaleTransform(scale, scale));
                low.Freeze();
                _quadViewsLow = (source, scale, low);
            }
            QuadViewsLow.Source = _quadViewsLow!.Value.Low;
            QuadViewsSharp.Source = source;
        }
        else
        {
            QuadViewsLow.Source = QuadViewsSharp.Source = null;
        }
        var box = new Rect((1 - horizontal) / 2 * width, (1 - vertical) / 2 * height, horizontal * width, vertical * height);
        QuadViewsSharp.Clip = new RectangleGeometry(box);
        QuadViewsBox.Width = box.Width;
        QuadViewsBox.Height = box.Height;
        Canvas.SetLeft(QuadViewsBox, box.X);
        Canvas.SetTop(QuadViewsBox, box.Y);
        QuadViewsBoxText.Text = $"Sharp, {100 * focus * focus:0} % of native"; // the sliders give resolution as pixel count: the multiplier squared
        QuadViewsBoxLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(QuadViewsBoxLabel, Math.Clamp(box.X, 4, width - QuadViewsBoxLabel.DesiredSize.Width - 4));
        Canvas.SetTop(QuadViewsBoxLabel, Math.Max(4, box.Y - QuadViewsBoxLabel.DesiredSize.Height - 4));
        QuadViewsSurroundText.Text = $"Surround, {100 * peripheral * peripheral:0} % of native";
        QuadViewsShareText.Text = $"{100 * share:0} % of the pixels";
        QuadViewsSceneFrame.ToolTip = WrappedToolTip("The sharp area at its share of your view, on your last picture from the Mirror page.\n\n" +
            "Outside it the game draws at the surround's resolution. The blockiness shows the difference, not to scale.\n\n" +
            "The pixels: what the game draws with these settings, against the headset's full resolution without quad views.");
    }

    private QuadViewsPixels.Result? QuadViewsPixelsFor(QuadViewsEffective effective)
    {
        if (_quadViewsSession is not { HasResolution: true } session) return null;
        var (horizontal, vertical) = QuadViewsSections(effective);
        return QuadViewsPixels.Compute(session.StereoWidth, session.StereoHeight, effective["peripheral_multiplier"], effective["focus_multiplier"], horizontal, vertical);
    }

    /// <summary>With eye tracking the layer sizes the sharp region from the focus keys, without it from the fixed ones.</summary>
    private (double Horizontal, double Vertical) QuadViewsSections(QuadViewsEffective effective) =>
        _quadViewsSession?.EyeTracking == false
            ? (effective["horizontal_fixed_section"], effective["vertical_fixed_section"])
            : (effective["horizontal_focus_section"], effective["vertical_focus_section"]);

    private void RefreshQuadViewsReadout()
    {
        SaveOwnQuadViews();
        var session = _quadViewsSession;
        var saved = EvaluateQuadViews(_quadViewsFile);
        var planned = PlannedQuadViewsFile();
        var next = EvaluateQuadViews(planned);
        var culture = CultureInfo.CurrentCulture;

        // ---- render load
        if (session is { HasResolution: true })
        {
            var savedPixels = QuadViewsPixelsFor(saved)!;
            var lastDetail = session.QuadPixels is { } last
                ? $"Last game start {session.When:g}: {last.ToString("N0", culture)} px/frame ({100.0 * last / session.StereoPixels:0.0} % of the full {session.StereoWidth}x{session.StereoHeight} per eye), " +
                  $"sharp {session.FocusResolution}, surround {session.PeripheralResolution}. Runtime: {session.RuntimeName}."
                : $"Last game start {session.When:g}: the game did not use quad views. Full resolution is {session.StereoWidth}x{session.StereoHeight} per eye.";
            var gameIsBehind = session.QuadPixels is { } ran && ran != savedPixels.Total;
            QuadViewsLoadLast.Text = gameIsBehind ? "The game last ran with another resolution. Restart it for the saved one; the other settings it takes at once." : "";
            QuadViewsLoadLast.Visibility = gameIsBehind ? Visibility.Visible : Visibility.Collapsed;
            var savedShare = (double)savedPixels.Total / session.StereoPixels;
            static string Fewer(double share) => share > 0 && share < 0.95 ? $", about {1 / share:0.#} times fewer pixels" : "";
            QuadViewsLoadSaved.ToolTip = WrappedToolTip($"Saved: {savedPixels.Total.ToString("N0", culture)} pixels a frame.\n\n" + lastDetail +
                                                         "\n\nPixels are a guide to GPU load. A game that waits on the CPU gains no frame rate from fewer pixels.");
            QuadViewsLoadSaved.Text = $"Saved settings: the game draws {100 * savedShare:0} % of the full picture{Fewer(savedShare)}.";
        }
        else
        {
            double Share(QuadViewsEffective e) { var (h, v) = QuadViewsSections(e); return 100 * QuadViewsPixels.Fraction(e["peripheral_multiplier"], e["focus_multiplier"], h, v); }
            QuadViewsLoadLast.Text = "The quad views layer has logged no game start yet, so the app does not know your headset's resolution. Run a game with quad views once to see real pixel counts here.";
            QuadViewsLoadLast.Visibility = Visibility.Visible;
            QuadViewsLoadSaved.Text = $"Saved settings: about {Share(saved):0.0} % of the full-resolution pixel count.";
        }

        // ---- under the sliders: what the values on them make
        {
            var (horizontal, vertical) = QuadViewsSections(next);
            var pixels = QuadViewsPixelsFor(next);
            _quadViewsReadouts["area"].Text = QuadViewsReadout.Area(horizontal, vertical);
            _quadViewsReadouts["focus_res"].Text = QuadViewsReadout.Focus(next["focus_multiplier"], pixels);
            _quadViewsReadouts["peripheral_res"].Text = QuadViewsReadout.Peripheral(next["peripheral_multiplier"], pixels, session?.StereoWidth ?? 0, session?.StereoHeight ?? 0);
            _quadViewsReadouts["total"].Text = QuadViewsReadout.Total(next["peripheral_multiplier"], next["focus_multiplier"], horizontal, vertical, pixels, session?.StereoPixels ?? 0);
            UpdateQuadViewsScene(horizontal, vertical, next["focus_multiplier"], next["peripheral_multiplier"],
                pixels is { } counted && session is { StereoPixels: > 0 } ? (double)counted.Total / session.StereoPixels : QuadViewsPixels.Fraction(next["peripheral_multiplier"], next["focus_multiplier"], horizontal, vertical));
        }

        // ---- what the file sets apart from the sliders
        var overrides = planned.Overrides().Select(o =>
        {
            var section = o.Section[1..^1];
            section = section.StartsWith("app:", StringComparison.Ordinal) || section.StartsWith("exe:", StringComparison.Ordinal) ? section[4..] : section;
            var setting = QuadViewsSetting.All.FirstOrDefault(s => s.Keys.Contains(o.Key));
            if (setting == null || !double.TryParse(o.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
            var shown = setting.IsToggle ? (number != 0 ? "on" : "off") : $"{setting.Show(setting.ToSlider(number))} {setting.Suffix}";
            return $"{section}: {setting.Label.ToLowerInvariant()} {shown}";
        }).Where(text => text != null).Distinct().ToList();
        QuadViewsOverrides.Text = overrides.Count > 0 ? "Set apart in the file: " + string.Join("; ", overrides) + "." : "";
        QuadViewsOverrides.Visibility = overrides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- settings the file sets, but not for this runtime
        var problems = new List<string>();
        if (session != null)
        {
            foreach (var setting in QuadViewsSetting.All.Where(s => !s.IsToggle && s.AppliesTo(_quadViewsOwn)))
            {
                var key = session.EyeTracking == false && setting.Keys.Length > 1 ? setting.Keys[1] : setting.Keys[0];
                if (ParseQuadViewsValue(key, _quadViewsFile.Intended(key)) is not { } wanted || Math.Abs(wanted - saved[key]) < 0.0005) continue;
                var source = saved.DecidedBy.TryGetValue(key, out var decidedBy) ? decidedBy : "the layer's built-in value";
                problems.Add($"{setting.Label}: the file says {setting.Show(setting.ToSlider(wanted))} {setting.Suffix}, the game gets {setting.Show(setting.ToSlider(saved[key]))} {setting.Suffix} (from {source}).");
            }
        }
        var turboNote = session != null && _quadViewsValues.GetValueOrDefault("turbo") != 0 && next["turbo_mode"] == 0 && next.DecidedBy.TryGetValue("turbo_mode", out var turboBy)
            ? $"Turbo mode stays off with your runtime: {turboBy} switches it off on purpose." : null;

        if (_quadViewsTurboBox != null)
        {
            _quadViewsTurboBox.Content = turboNote != null ? "Turbo mode (stays off with your runtime)" : "Turbo mode";
            _quadViewsTurboBox.ToolTip = WrappedToolTip((turboNote != null ? turboNote + "\n\n" : "") + QuadViewsSetting.All.First(s => s.Id == "turbo").Description);
        }
        if (problems.Count > 0)
        {
            QuadViewsNoticeText.Text =
                $"Some settings in the file do not reach your game. The file holds them under headset sections, and none of those match your OpenXR runtime (\"{session!.RuntimeName}\"):\n  - " +
                string.Join("\n  - ", problems) + "\nA change on this page writes them to the common part of the file, which every runtime reads." +
                (turboNote != null ? "\n" + turboNote : "");
            QuadViewsNotice.Visibility = Visibility.Visible;
        }
        else
        {
            QuadViewsNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void SetQuadViewsStatus(string text)
    {
        QuadViewsStatus.Text = text;
        QuadViewsStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQuadViewsOpenFile(object sender, RoutedEventArgs e) => OpenQuadViewsFile(_quadViewsPath);

    private void OnQuadViewsOpenLog(object sender, RoutedEventArgs e) => OpenQuadViewsFile(QuadViewsSession.LogPath);

    private void OpenQuadViewsFile(string path)
    {
        if (!File.Exists(path))
        {
            SetQuadViewsStatus("That file does not exist yet: " + path);
            return;
        }
        OpenFileOrShow(path);
    }
}
