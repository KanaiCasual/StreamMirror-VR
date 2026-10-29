using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamMirror;

/// <summary>What Quad-Views-Foveated wrote to its log the last time a game started. Read once, on demand - never watched.</summary>
public sealed class QuadViewsSession
{
    public DateTime? When { get; private set; }
    public string RuntimeName { get; private set; } = "";
    public string SystemName { get; private set; } = "";
    public string AppName { get; private set; } = "";
    public string ExeName { get; private set; } = "";
    /// <summary>The settings files the layer looked for, in the order it read them (the shipped one, then the user's).</summary>
    public List<string> ConfigPaths { get; } = [];
    public bool? EyeTracking { get; private set; }
    /// <summary>The headset's full per-eye resolution, which every pixel count is derived from.</summary>
    public int StereoWidth { get; private set; }
    public int StereoHeight { get; private set; }
    public long? QuadPixels { get; private set; }
    public string? FocusResolution { get; private set; }
    public string? PeripheralResolution { get; private set; }

    public bool HasResolution => StereoWidth > 0 && StereoHeight > 0;
    public long StereoPixels => 2L * StereoWidth * StereoHeight;

    /// <summary>True while StreamMirror VR's own quad views layer is the one the Quad Views page edits (its own folder, log
    /// and settings file); false for the official Quad-Views-Foveated.</summary>
    public static bool OwnLayer { get; set; } = true;

    public static string OfficialFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quad-Views-Foveated");
    public static string OwnFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamMirror", "QuadViews");
    public static string Folder => OwnLayer ? OwnFolder : OfficialFolder;
    public static string LogPath => OwnLayer ? Path.Combine(OwnFolder, "QuadViews.log") : Path.Combine(OfficialFolder, "Quad-Views-Foveated.log");

    private static readonly Regex Prefix = new(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) [+-]\d{4}: ", RegexOptions.Compiled);

    public static QuadViewsSession? Read(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path)) return null;
            // The layer may have the log open for writing while a game runs.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch
        {
            return null;
        }

        var session = new QuadViewsSession();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var prefix = Prefix.Match(line);
            if (!prefix.Success) continue;
            var text = line[prefix.Length..];

            if (text.Contains(" layer (") && text.EndsWith("is active"))
            {
                // A new game start: forget what an earlier one in the same file said.
                session = new QuadViewsSession();
                if (DateTime.TryParseExact(prefix.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) session.When = when;
            }
            else if (After(text, "Using OpenXR runtime: ") is { } runtime) session.RuntimeName = runtime;
            else if (After(text, "Using OpenXR system: ") is { } system) session.SystemName = system;
            else if (After(text, "Application: ") is { } application)
            {
                var open = application.LastIndexOf(" (", StringComparison.Ordinal);
                session.AppName = open > 0 ? application[..open] : application;
                session.ExeName = open > 0 ? application[(open + 2)..].TrimEnd(')') : "";
            }
            else if (After(text, "Trying to locate configuration file at '") is { } configPath)
            {
                var close = configPath.LastIndexOf('\'');
                if (close > 0) session.ConfigPaths.Add(configPath[..close]);
            }
            else if (After(text, "Eye tracking is ") is { } tracking) session.EyeTracking = tracking == "supported";
            else if (After(text, "Recommended focus resolution: ") is { } focus) session.FocusResolution = focus.Split(' ')[0];
            else if (After(text, "Recommended peripheral resolution: ") is { } peripheral) session.PeripheralResolution = peripheral.Split(' ')[0];
            else if (text.TrimStart().StartsWith("Stereo pixel count was:"))
            {
                var size = Regex.Match(text, @"\((\d+)x(\d+)\)");
                if (size.Success)
                {
                    session.StereoWidth = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                    session.StereoHeight = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }
            else if (After(text.TrimStart(), "Quad views pixel count is: ") is { } quad)
            {
                // The layer prints it with the PC's thousands separators.
                var digits = new string(quad.Where(char.IsDigit).ToArray());
                if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) session.QuadPixels = count;
            }
        }
        return session.When == null && session.RuntimeName.Length == 0 ? null : session;
    }

    private static string? After(string text, string start) => text.StartsWith(start, StringComparison.Ordinal) ? text[start.Length..] : null;
}

/// <summary>The values the layer ends up with, and which section of which file had the last word on each.</summary>
public sealed record QuadViewsEffective(Dictionary<string, double> Values, Dictionary<string, string> DecidedBy)
{
    public double this[string key] => Values[key];
}

/// <summary>
/// Quad-Views-Foveated's settings.cfg, edited line by line so comments, sections and keys this app does not know survive.
/// The parsing rules copy the layer's own (ParseConfigurationStatement in its layer.cpp) - including that nothing is trimmed.
/// </summary>
public sealed class QuadViewsFile
{
    public static string DefaultPath => Path.Combine(QuadViewsSession.Folder, "settings.cfg");

    /// <summary>The first lines of every file the app writes.</summary>
    public const string OwnFileHeader = "# Quad-Views-Foveated settings, written by StreamMirror VR.";
    private const string OwnFileSecondLine = "# Settings in this first part apply to every headset and OpenXR runtime.";

    /// <summary>The settings in groups, in the order a file lists them: the sharp region with eye tracking, the same
    /// without it (the fixed fallback), and the rest. Each group gets a heading in the app's own files.</summary>
    private static readonly (string Heading, string[] Keys)[] Groups =
    [
        ("# With eye tracking: the sharp region follows your eyes.", ["horizontal_focus_section", "vertical_focus_section", "vertical_focus_offset", "gaze_smoothing_ms", "gaze_deadzone_degrees"]),
        ("# Without eye tracking: the sharp region stays in place.", ["horizontal_fixed_section", "vertical_fixed_section", "horizontal_fixed_offset", "vertical_fixed_offset"]),
        ("# Resolution, sharpening, Turbo and the debug views.", []),
    ];

    /// <summary>The keys the Quad Views page's sliders and switches set - Turbo aside, which guards runtimes and games it breaks.</summary>
    private static readonly HashSet<string> PageKeys = [.. QuadViewsSetting.All.SelectMany(s => s.Keys).Where(k => k != "turbo_mode")];

    private static int GroupOf(string key)
    {
        for (var g = 0; g < Groups.Length - 1; g++) if (Groups[g].Keys.Contains(key)) return g;
        return Groups.Length - 1;
    }
    public List<string> Lines { get; private init; } = [];
    public bool Existed { get; private init; }
    /// <summary>The layer's built-in values (the members' initialisers in its layer.cpp).</summary>
    public static readonly Dictionary<string, double> BuiltIn = new()
    {
        ["peripheral_multiplier"] = 0.5, ["focus_multiplier"] = 1.0,
        ["horizontal_fixed_section"] = 0.5, ["vertical_fixed_section"] = 0.45,
        ["horizontal_focus_section"] = 0.35, ["vertical_focus_section"] = 0.35,
        ["horizontal_fixed_offset"] = 0, ["vertical_fixed_offset"] = 0, ["vertical_focus_offset"] = 0,
        ["smoothen_focus_view_edges"] = 0.2, ["sharpen_focus_view"] = 0.7,
        ["turbo_mode"] = 1, ["debug_eye_gaze"] = 0, ["debug_focus_view"] = 0,
        // StreamMirror VR's own layer only (gaze_filter.h and the diagnostics in its layer.cpp).
        ["gaze_smoothing_ms"] = 8, ["gaze_deadzone_degrees"] = 0.5, ["show_diagnostics"] = 0,
    };

    public static QuadViewsFile Load(string path)
    {
        if (File.Exists(path))
        {
            return new QuadViewsFile { Lines = [.. File.ReadAllText(path).Split('\n').Select(l => l.TrimEnd('\r'))], Existed = true };
        }
        // The file shipped with the layer is read first and switches Turbo off where it is unsafe; a user file that sets
        // turbo_mode for everyone would undo that, so a new file repeats those two guards.
        return new QuadViewsFile
        {
            Lines =
            [
                OwnFileHeader,
                OwnFileSecondLine,
                "",
                "[Varjo]",
                "# Turbo mode is incompatible with Varjo's deferred swapchain release. Use OpenXR Toolkit Turbo instead.",
                "turbo_mode=0",
                "",
                "[SteamVR]",
                "# Turbo Mode causes unexplained errors with SteamVR.",
                "turbo_mode=0",
                "",
            ],
        };
    }

    public static QuadViewsFile Parse(string text) => new() { Lines = [.. text.Split('\n').Select(l => l.TrimEnd('\r'))], Existed = true };

    public string Text => string.Join("\r\n", Lines);

    private static bool IsComment(string line) => line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal);
    private static bool IsSection(string line) => line.Length > 1 && line[0] == '[' && line[^1] == ']';
    private static bool IsAppSection(string? section) => section != null && (section.StartsWith("[app:", StringComparison.Ordinal) || section.StartsWith("[exe:", StringComparison.Ordinal));

    /// <summary>Every name=value line with the section header it sits under (null = the common part at the top).</summary>
    public IEnumerable<(int Index, string? Section, string Key, string Value)> Entries()
    {
        string? section = null;
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (line.Length == 0 || IsComment(line)) continue;
            if (IsSection(line)) { section = line; continue; }
            var separator = line.IndexOf('=');
            if (separator > 0) yield return (i, section, line[..separator], line[(separator + 1)..]);
        }
    }

    /// <summary>What the file means a setting to be: the first place it is set outside per-game sections.</summary>
    public string? Intended(string key) =>
        Entries().Where(e => e.Key == key && !IsAppSection(e.Section)).Select(e => e.Value).FirstOrDefault();

    /// <summary>The value set in the common part at the top of the file, which every runtime reads.</summary>
    public string? InCommon(string key) => Entries().Where(e => e.Section == null && e.Key == key).Select(e => e.Value).LastOrDefault();

    /// <summary>
    /// The file as Apply writes it: whole and fresh, in the app's layout - not the old file patched. The common part holds
    /// every setting the page has, in its three groups: a changed one as the page sets it, an untouched one as the old file
    /// meant it (so a value is never rounded through a slider), the page's value where the file had none; then the layer's
    /// other keys the old common part set. Below it only what a runtime or game needs apart: Turbo off for Varjo and SteamVR
    /// (the layer reads this file after its own, so the Turbo set here would undo its guards), Turbo wherever the old file
    /// set it for a runtime or game, and lines the page has no setting for (un-advertise). Headset and per-game values of
    /// the page's settings, the old comments and the old layout go: the page's values count everywhere.
    /// </summary>
    /// <param name="ownLayer">The file is StreamMirror VR's own layer's: its own settings are written, the official's debug
    /// switch is not (and the other way round).</param>
    public static QuadViewsFile Plan(QuadViewsFile current, IReadOnlyDictionary<string, double> values, IReadOnlySet<string> changed, bool ownLayer = false)
    {
        var common = new OrderedDictionary<string, string>();
        foreach (var setting in QuadViewsSetting.All.Where(s => s.AppliesTo(ownLayer)))
        {
            var value = values.GetValueOrDefault(setting.Id);
            var fromPage = setting.IsToggle ? (value != 0 ? "1" : "0") : setting.ToFile(value);
            foreach (var key in setting.Keys)
            {
                var meant = setting.CommonOnly ? current.InCommon(key) : current.Intended(key);
                common[key] = changed.Contains(setting.Id) ? fromPage : meant ?? fromPage;
            }
        }
        var unknown = new OrderedDictionary<string, string>();
        foreach (var entry in current.Entries().Where(e => e.Section == null))
        {
            if (PageKeys.Contains(entry.Key) || entry.Key == "turbo_mode") continue; // The page's own, written above.
            if (BuiltIn.ContainsKey(entry.Key)) common[entry.Key] = entry.Value;
            else unknown[entry.Key] = entry.Value;
        }

        var lines = new List<string> { OwnFileHeader, OwnFileSecondLine };
        for (var g = 0; g < Groups.Length; g++)
        {
            var keys = g < Groups.Length - 1 ? Groups[g].Keys.Where(common.ContainsKey) : common.Keys.Where(k => GroupOf(k) == g);
            var group = keys.Select(k => $"{k}={common[k]}").Concat(g == Groups.Length - 1 ? unknown.Select(u => $"{u.Key}={u.Value}") : []).ToList();
            if (group.Count == 0) continue;
            lines.Add("");
            lines.Add(Groups[g].Heading);
            lines.AddRange(group);
        }

        // What a runtime or game needs apart: the Turbo guards first, then what the old file kept apart, in its order.
        var sections = new OrderedDictionary<string, OrderedDictionary<string, string>>
        {
            ["[Varjo]"] = new() { ["turbo_mode"] = "0" },
            ["[SteamVR]"] = new() { ["turbo_mode"] = "0" },
        };
        foreach (var entry in current.Entries().Where(e => e.Section != null && !PageKeys.Contains(e.Key)))
        {
            if (!sections.TryGetValue(entry.Section!, out var kept)) sections[entry.Section!] = kept = new();
            kept[entry.Key] = entry.Value;
        }
        var comments = new Dictionary<string, string>
        {
            ["[Varjo]"] = "# Turbo mode is incompatible with Varjo's deferred swapchain release. Use OpenXR Toolkit Turbo instead.",
            ["[SteamVR]"] = "# Turbo Mode causes unexplained errors with SteamVR.",
        };
        var keptNoted = false;
        foreach (var (header, kept) in sections)
        {
            if (kept.Count == 0) continue;
            lines.Add("");
            if (!comments.ContainsKey(header) && !keptNoted)
            {
                lines.Add("# Kept from the earlier file: settings this app has no control for.");
                keptNoted = true;
            }
            lines.Add(header);
            if (comments.TryGetValue(header, out var comment) && kept.ContainsKey("turbo_mode")) lines.Add(comment);
            lines.AddRange(kept.Select(k => $"{k.Key}={k.Value}"));
        }
        lines.Add("");
        return new QuadViewsFile { Lines = lines, Existed = current.Existed };
    }

    /// <summary>
    /// Where the file sets a value apart from the common part: (section as written, key, value), headset and per-game
    /// sections alike, in file order. The Quad Views page names these, since its sliders show the common value.
    /// </summary>
    public IEnumerable<(string Section, string Key, string Value)> Overrides()
    {
        var common = Entries().Where(e => e.Section == null).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        return Entries()
            .Where(e => e.Section != null && BuiltIn.ContainsKey(e.Key) && (!common.TryGetValue(e.Key, out var value) || value != e.Value))
            .Select(e => (e.Section!, e.Key, e.Value));
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Text, new UTF8Encoding(false));
        ConfigFile.SwapIn(temp, path);
    }

    /// <summary>
    /// Works out what the layer ends up with, the way it does: built-in values, then each file top to bottom, where a
    /// [section] only counts if its name is part of the runtime's or system's name ([app:]/[exe:]: of the game's).
    /// Without a session (no game has run yet) only the common parts count.
    /// </summary>
    public static QuadViewsEffective Evaluate(IEnumerable<(string Name, QuadViewsFile File)> files, QuadViewsSession? session)
    {
        var values = new Dictionary<string, double>(BuiltIn);
        var decidedBy = new Dictionary<string, string>();
        foreach (var (name, file) in files)
        {
            foreach (var entry in file.Entries())
            {
                if (!BuiltIn.ContainsKey(entry.Key) || !SectionApplies(entry.Section, session)) continue;
                if (!double.TryParse(entry.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) continue;
                values[entry.Key] = Clamp(entry.Key, number);
                decidedBy[entry.Key] = $"{entry.Section ?? "the common part"} of {name}";
            }
        }
        return new QuadViewsEffective(values, decidedBy);
    }

    private static bool SectionApplies(string? section, QuadViewsSession? session)
    {
        if (section == null) return true;
        if (session == null) return false;
        if (section.StartsWith("[app:", StringComparison.Ordinal)) return session.AppName.Contains(section[5..^1], StringComparison.Ordinal);
        if (section.StartsWith("[exe:", StringComparison.Ordinal)) return session.ExeName.Contains(section[5..^1], StringComparison.Ordinal);
        var name = section[1..^1];
        return session.RuntimeName.Contains(name, StringComparison.Ordinal) || session.SystemName.Contains(name, StringComparison.Ordinal);
    }

    /// <summary>The same limits the layer applies when it reads a value.</summary>
    public static double Clamp(string key, double value) => key switch
    {
        "peripheral_multiplier" or "focus_multiplier" => Math.Max(0.1, value),
        "horizontal_fixed_section" or "vertical_fixed_section" or "horizontal_focus_section" or "vertical_focus_section" => Math.Clamp(value, 0.1, 0.9),
        "horizontal_fixed_offset" or "vertical_fixed_offset" or "vertical_focus_offset" => Math.Clamp(value, -0.5, 0.5),
        "smoothen_focus_view_edges" => Math.Clamp(value, 0, 0.5),
        "sharpen_focus_view" => Math.Clamp(value, 0, 1),
        "gaze_smoothing_ms" => Math.Clamp(value, 0, 200),
        "gaze_deadzone_degrees" => Math.Clamp(value, 0, 5),
        _ => value != 0 ? 1 : 0,
    };
}

/// <summary>The layer's own arithmetic for the image sizes it asks the game to render (xrEnumerateViewConfigurationViews).</summary>
public static class QuadViewsPixels
{
    public sealed record Result(int PeripheralWidth, int PeripheralHeight, int FocusWidth, int FocusHeight)
    {
        public long Total => 2L * ((long)PeripheralWidth * PeripheralHeight + (long)FocusWidth * FocusHeight);
    }

    public static Result Compute(int stereoWidth, int stereoHeight, double peripheralMultiplier, double focusMultiplier, double horizontalSection, double verticalSection)
    {
        // Single precision and truncation on purpose: that is what the layer does, and it decides the last pixel.
        float peripheral = (float)peripheralMultiplier, focus = (float)focusMultiplier;
        return new Result(
            Align2((uint)(float)(peripheral * stereoWidth)), Align2((uint)(float)(peripheral * stereoHeight)),
            Align2((uint)(float)((float)(focus * (float)horizontalSection) * stereoWidth)),
            Align2((uint)(float)((float)(focus * (float)verticalSection) * stereoHeight)));
    }

    /// <summary>Share of the full-resolution pixel count, for when the headset's resolution is not known yet.</summary>
    public static double Fraction(double peripheralMultiplier, double focusMultiplier, double horizontalSection, double verticalSection) =>
        peripheralMultiplier * peripheralMultiplier + focusMultiplier * focusMultiplier * horizontalSection * verticalSection;

    private static int Align2(uint n) => (int)((n + 1) & ~1u);
}

/// <summary>The lines under the Quad Views page's sliders: what the values make, in pixels and in sharpness.
/// <paramref name="pixels"/> and the headset's size are null until a game has started once.</summary>
public static class QuadViewsReadout
{
    /// <summary>The multiplier in plain words: how much the game draws (its square), and what that does.</summary>
    public static string Sharpness(double multiplier) => Math.Abs(multiplier - 1) < 0.005
        ? "As sharp as the headset."
        : multiplier > 1
            ? $"Draws {100 * multiplier * multiplier:0} % of the headset's pixels: sharper than the headset, but slower."
            : $"Draws {100 * multiplier * multiplier:0} % of the headset's pixels: faster, but softer.";

    public static string Focus(double multiplier, QuadViewsPixels.Result? pixels) => pixels != null
        ? $"Sharp area: {pixels.FocusWidth} x {pixels.FocusHeight} per eye.\n{Sharpness(multiplier)}"
        : $"{Sharpness(multiplier)}\nPixel sizes show here once a game has started with quad views.";

    public static string Peripheral(double multiplier, QuadViewsPixels.Result? pixels, int stereoWidth, int stereoHeight) => pixels != null
        ? $"Surround: {pixels.PeripheralWidth} x {pixels.PeripheralHeight} per eye (the headset: {stereoWidth} x {stereoHeight}).\n{Sharpness(multiplier)}"
        : Sharpness(multiplier);

    /// <summary>The sharp area's share of the view: the two size sliders together.</summary>
    public static string Area(double horizontalSection, double verticalSection) =>
        $"Together: the sharp area covers {100 * horizontalSection * verticalSection:0} % of the view.";

    /// <summary>What both images cost together, against the headset rendering everything at full resolution.</summary>
    public static string Total(double peripheralMultiplier, double focusMultiplier, double horizontalSection, double verticalSection, QuadViewsPixels.Result? pixels, long stereoPixels)
    {
        double sharp, surround;
        if (pixels != null && stereoPixels > 0)
        {
            sharp = 2.0 * pixels.FocusWidth * pixels.FocusHeight / stereoPixels;
            surround = 2.0 * pixels.PeripheralWidth * pixels.PeripheralHeight / stereoPixels;
        }
        else
        {
            sharp = focusMultiplier * focusMultiplier * horizontalSection * verticalSection;
            surround = peripheralMultiplier * peripheralMultiplier;
        }
        return $"In all: {100 * (sharp + surround):0} % of the pixels the headset renders without quad views.\nSharp area {100 * sharp:0} %, surround {100 * surround:0} %.";
    }
}

/// <summary>Fraction: the file holds 0..1, the slider percent. PixelShare: a multiplier, the slider the share of pixels.
/// Direct: the slider shows the file's number as it is (milliseconds, degrees).</summary>
public enum QuadViewsScale { Fraction, PixelShare, Direct }

/// <summary>Which layer a setting belongs to: both, only StreamMirror VR's own, or only the official Quad-Views-Foveated.</summary>
public enum QuadViewsLayers { Both, OwnOnly, OfficialOnly }

/// <summary>One control of the Quad Views tab. A slider can drive two keys: the eye-tracked one and its fixed fallback.</summary>
public sealed class QuadViewsSetting
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Suffix { get; init; }
    public required string Description { get; init; }
    public required string[] Keys { get; init; }
    public bool IsToggle { get; init; }
    /// <summary>Only ever written in the common part: headset sections that override it do so on purpose (Turbo).</summary>
    public bool CommonOnly { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    public QuadViewsScale Scale { get; init; }
    public bool NeedsRestartNote { get; init; }
    public QuadViewsLayers Layers { get; init; }
    /// <summary>A running game takes the change at once (the layer reads its settings again); otherwise at its next start.</summary>
    public bool Live { get; init; } = true;
    /// <summary>What the tooltip says about when the change takes effect, where the plain Live or Next start line is not enough.</summary>
    public string? TimingNote { get; init; }

    public bool AppliesTo(bool ownLayer) => Layers == QuadViewsLayers.Both || (Layers == QuadViewsLayers.OwnOnly) == ownLayer;

    /// <summary>The value as the page shows it: tenths where the slider moves in tenths.</summary>
    public string Show(double sliderValue) => sliderValue.ToString(Step < 1 ? "0.0" : "0");

    /// <summary>The value with its unit. A resolution leads with the number the file holds (0.4x), then the share of pixels.</summary>
    public string Format(double sliderValue) => Scale == QuadViewsScale.PixelShare
        ? $"{ToFile(sliderValue)}x · {Show(sliderValue)} %"
        : $"{Show(sliderValue)} {Suffix}";

    /// <summary>File value -> what the slider shows (percent, or the number itself).</summary>
    public double ToSlider(double fileValue) => Scale switch
    {
        QuadViewsScale.PixelShare => fileValue * fileValue * 100,
        QuadViewsScale.Direct => fileValue,
        _ => fileValue * 100,
    };

    /// <summary>Slider percent -> the text written to the file. Resolution is a share of the pixel count, so the multiplier
    /// (which scales width and height alike) is its square root - the same logic as TallyMouse's QuadViews Companion.</summary>
    public string ToFile(double sliderValue) => Scale switch
    {
        QuadViewsScale.PixelShare => Math.Round(Math.Sqrt(sliderValue / 100), 3).ToString("0.###", CultureInfo.InvariantCulture),
        QuadViewsScale.Direct => Math.Round(sliderValue, 1).ToString("0.#", CultureInfo.InvariantCulture),
        _ => Math.Round(sliderValue / 100, 2).ToString("0.##", CultureInfo.InvariantCulture),
    };

    public double ToFileNumber(double sliderValue) => double.Parse(ToFile(sliderValue), CultureInfo.InvariantCulture);

    /// <summary>The focus size reaches a running game (with eye tracking), but the pixel count it sets only comes at the next
    /// start: it sits under Next start, and live it is only for trying sizes.</summary>
    private const string FocusSizeTiming = "**Next start:** set it, then start the game again: that is when the sharp region gets its pixel count. While a game runs the size changes at once, so you can try how small it can go before you notice the edge. The pixels stay as they were until the next start, though: a region made bigger gets blurrier, a smaller one saves nothing yet. Without eye tracking, the whole change waits for the next start.";

    public static readonly QuadViewsSetting[] All =
    [
        new()
        {
            Id = "focus_h", Label = "Horizontal focus size", Suffix = "% of FOV", Min = 10, Max = 90, Live = false, TimingNote = FocusSizeTiming,
            Keys = ["horizontal_focus_section", "horizontal_fixed_section"],
            Description = "Width of the sharp region that follows your eyes, as a share of the headset's field of view.\n\n**Smaller:** faster. Too small and you see the blurry surround.",
        },
        new()
        {
            Id = "focus_v", Label = "Vertical focus size", Suffix = "% of FOV", Min = 10, Max = 90, Live = false, TimingNote = FocusSizeTiming,
            Keys = ["vertical_focus_section", "vertical_fixed_section"],
            Description = "Height of the sharp region.",
        },
        new()
        {
            Id = "offset_v", Label = "Vertical focus offset", Suffix = "% of FOV", Min = -50, Max = 50,
            Keys = ["vertical_focus_offset", "vertical_fixed_offset"],
            Description = "Moves the sharp region up (positive) or down.\n\nUse it if the region keeps sitting above or below your gaze.",
        },
        new()
        {
            Id = "gaze_smoothing", Label = "Gaze smoothing", Suffix = "ms", Min = 0, Max = 50, Scale = QuadViewsScale.Direct, Layers = QuadViewsLayers.OwnOnly,
            Keys = ["gaze_smoothing_ms"],
            Description = "How the sharp region catches up when your eyes move: the time it takes to cover most of the way.\n\n**0:** at once. A few milliseconds hides tracker jitter without a lag you can see. Takes effect while the game runs.",
        },
        new()
        {
            Id = "gaze_deadzone", Label = "Gaze deadzone", Suffix = "°", Min = 0, Max = 3, Step = 0.1, Scale = QuadViewsScale.Direct, Layers = QuadViewsLayers.OwnOnly,
            Keys = ["gaze_deadzone_degrees"],
            Description = "The sharp region stays where it is until your eyes move further than this. Tracker jitter then leaves it alone.\n\n**0:** it follows every movement. Takes effect while the game runs.",
        },
        new()
        {
            Id = "focus_res", Label = "Foveate resolution", Suffix = "%", Min = 50, Max = 400, Scale = QuadViewsScale.PixelShare, Live = false,
            Keys = ["focus_multiplier"],
            Description = "How sharp the sharp region is.\n\n**100 %:** as sharp as the headset. **More** is sharper, but slower.\n\n**1.22x** is the same setting, written the way the settings file and other quad views tools write it. Type 1.2x to set that number, or 150 for 150 %.",
        },
        new()
        {
            Id = "peripheral_res", Label = "Peripheral resolution", Suffix = "%", Min = 1, Max = 100, Scale = QuadViewsScale.PixelShare, Live = false,
            Keys = ["peripheral_multiplier"],
            Description = "How sharp everything outside the sharp region is.\n\n**Lower** runs faster, but looks softer. Most of the speed comes from here.\n\n**0.4x** is the same setting, written the way the settings file and other quad views tools write it. Type 0.4x to set that number, or 16 for 16 %.",
        },
        new()
        {
            Id = "sharpen", Label = "Foveate sharpness", Suffix = "%", Min = 0, Max = 100, Step = 5,
            Keys = ["sharpen_focus_view"],
            Description = "Sharpening filter on the sharp region.\n\n**0:** off, and saves a little GPU time.",
        },
        new()
        {
            Id = "transition", Label = "Transition thickness", Suffix = "%", Min = 0, Max = 50,
            Keys = ["smoothen_focus_view_edges"],
            Description = "Width of the blend between the sharp region and the surround.\n\n**0:** a hard edge.",
        },
        new()
        {
            Id = "turbo", Label = "Turbo mode", Suffix = "", IsToggle = true, CommonOnly = true, Live = false,
            Keys = ["turbo_mode"],
            Description = "Lets the game start its next frame early, which can raise the frame rate.\n\nQuad-Views-Foveated switches it off for SteamVR and Varjo runtimes because it causes errors there, and this app leaves those exceptions in place.",
        },
        new()
        {
            Id = "diagnostics", Label = "Show diagnostics in the headset", Suffix = "", IsToggle = true, Layers = QuadViewsLayers.OwnOnly,
            Keys = ["show_diagnostics"],
            Description = "Tints the view in the headset to check the settings while you play: the surround blue, the blend amber, the sharp region green. A ring marks where the tracker says you look (red while it has lost your eyes), a cross where the sharp region is aimed.\n\nSwitch it off before you stream: the mirror shows it too.",
        },
        new()
        {
            Id = "debug_gaze", Label = "Debug: show eye gaze", Suffix = "", IsToggle = true, Layers = QuadViewsLayers.OfficialOnly,
            Keys = ["debug_eye_gaze"],
            Description = "Draws a small square in the headset where Quad-Views-Foveated thinks you are looking.",
        },
        new()
        {
            Id = "debug_focus", Label = "Debug: show focus view", Suffix = "", IsToggle = true,
            Keys = ["debug_focus_view"],
            Description = "Tints the sharp region in the headset so you can see its size and position.",
        },
    ];

    /// <summary>Slider values by Id. They never touch Turbo or the debug switches.</summary>
    public static readonly (string Name, string Description, Dictionary<string, double> Values)[] Presets =
    [
        // Read off QuadViews Companion 1.0.17. Its resolution presets are round multipliers (0.75, 0.2) that it shows as a
        // share of the pixel count (56 %, 4 %), so they are given here unrounded to write the same multipliers.
        ("Give me FPS", "The Companion's frame-rate preset: a small sharp region, low resolutions, strong sharpening.", new()
        {
            ["focus_h"] = 30, ["focus_v"] = 30, ["offset_v"] = -10, ["focus_res"] = 56.25, ["peripheral_res"] = 4,
            ["sharpen"] = 75, ["transition"] = 20,
        }),
        ("TM's Favorite", "TallyMouse's own pick in the Companion: a supersampled sharp region with a low-resolution surround.", new()
        {
            ["focus_h"] = 33, ["focus_v"] = 32, ["offset_v"] = -10, ["focus_res"] = 200, ["peripheral_res"] = 4.84, // 0.22, as the Companion writes it
            ["sharpen"] = 80, ["transition"] = 30,
        }),
        // No values of its own: the window fills it with what the layer does when there is no user file at all - the file
        // shipped with the layer, read for this PC's runtime. (The Companion's button reads that shipped file too, from
        // mbucchia's original install folder - which is why it fails once this package has replaced that install.)
        ("QV defaults", "Quad-Views-Foveated's own behaviour on this PC with nothing set: its shipped settings file, read for your headset's runtime.", new()),
    ];
}
