using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StreamMirror;

/// <summary>
/// Where a game's own desktop window lies in its eye image: the box the ring-only picture takes, so that the ring lands
/// on a capture of that window. <see cref="Section"/> is the game's program file, or <see cref="DesktopRing.DefaultSection"/>.
/// Fractions of the eye image, like the crop box: the centre, the height; <see cref="Aspect"/> is the window's width / height.
/// <see cref="RingOnly"/> (a game's own section): the ring alone whenever that game runs, whatever the main switch says.
/// </summary>
public sealed record DesktopAlignment(string Section, int Eye, double CenterX, double CenterY, double Height, double Aspect, bool RingOnly = false);

/// <summary>
/// desktop-ring.ini: one section per game and a [default] one; the mirror reads it (settings.cpp, applyDesktopRing) and
/// takes the running game's own section, else the default.
/// </summary>
public static class DesktopRing
{
    public const string DefaultSection = "default";

    /// <summary>The environment variable is for tests (the mirror honours the same one).</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("STREAMMIRROR_DESKTOP_RING_FILE") is { Length: > 0 } overridden
        ? overridden : Path.Combine(AppSettings.Folder, "desktop-ring.ini");

    public static bool IsDefault(string section) => string.Equals(section, DefaultSection, StringComparison.OrdinalIgnoreCase);

    public static List<DesktopAlignment> Load() => File.Exists(FilePath) ? Parse(File.ReadAllLines(FilePath)) : [];

    public static List<DesktopAlignment> Parse(IEnumerable<string> lines)
    {
        var found = new List<DesktopAlignment>();
        string? section = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Finish()
        {
            if (section == null) return;
            static double Number(Dictionary<string, string> v, string key, double fallback) =>
                v.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
            var alignment = new DesktopAlignment(section, values.GetValueOrDefault("eye") == "left" ? 0 : 1,
                Number(values, "center_x", 0.5), Number(values, "center_y", 0.5), Number(values, "height", 0), Number(values, "aspect", 0),
                values.GetValueOrDefault("ring_only") == "1");
            if (alignment.Height >= 0.02 && alignment.Aspect >= 0.1) found.Add(alignment); // the mirror's own test
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                Finish();
                section = line[1..^1].Trim();
                values.Clear();
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals > 0) values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        Finish();
        return found;
    }

    public static string Format(IEnumerable<DesktopAlignment> alignments)
    {
        static string N(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
        var text = new StringBuilder("# Desktop ring of StreamMirror VR: where each game's desktop window lies in its eye image. The mirror takes the\n# running game's section, else [default]. Made on the app's Desktop ring page.\n");
        foreach (var a in alignments.OrderBy(a => IsDefault(a.Section) ? 0 : 1).ThenBy(a => a.Section, StringComparer.OrdinalIgnoreCase))
        {
            text.Append('\n').Append('[').Append(a.Section).Append("]\n")
                .Append("eye=").Append(a.Eye == 0 ? "left" : "right").Append('\n')
                .Append("center_x=").Append(N(a.CenterX)).Append('\n')
                .Append("center_y=").Append(N(a.CenterY)).Append('\n')
                .Append("height=").Append(N(a.Height)).Append('\n')
                .Append("aspect=").Append(N(a.Aspect)).Append('\n');
            if (a.RingOnly && !IsDefault(a.Section)) text.Append("ring_only=1\n");
        }
        return text.ToString();
    }

    public static void Save(IEnumerable<DesktopAlignment> alignments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, Format(alignments), new UTF8Encoding(false));
        ConfigFile.SwapIn(temporary, FilePath);
    }

    /// <summary>The one the mirror takes for this game: its own section, else the default (the mirror's rule).</summary>
    public static DesktopAlignment? For(IEnumerable<DesktopAlignment> alignments, string? game) =>
        alignments.FirstOrDefault(a => !string.IsNullOrEmpty(game) && string.Equals(a.Section, game, StringComparison.OrdinalIgnoreCase))
        ?? alignments.FirstOrDefault(a => IsDefault(a.Section));

    /// <summary>The running game's own alignment asks for the ring alone whenever it runs (the default never does).</summary>
    public static bool AlwaysRingOnly(IEnumerable<DesktopAlignment> alignments, string? game) =>
        For(alignments, game) is { RingOnly: true } own && !IsDefault(own.Section);

    // ------------------------------------------------------------------ the overlay and the box

    /// <summary>
    /// The window's picture is <paramref name="windowWidth"/> x <paramref name="windowHeight"/>; the eye picture lies over it
    /// at <paramref name="eye"/> (same units). The window then shows this box of the eye image. The box has the window's
    /// shape whatever the eye image's: the eye picture keeps its own shape when it is resized.
    /// </summary>
    public static (double CenterX, double CenterY, double Height, double Aspect) BoxFromOverlay(double windowWidth, double windowHeight, Rect eye) =>
        ((windowWidth / 2 - eye.X) / eye.Width, (windowHeight / 2 - eye.Y) / eye.Height, windowHeight / eye.Height, windowWidth / windowHeight);

    /// <summary>The way back: where the eye picture (of this width / height) lies over the window's picture for this box.</summary>
    public static Rect OverlayFromBox(double windowWidth, double windowHeight, double eyeAspect, double centerX, double centerY, double height)
    {
        var eyeHeight = windowHeight / height;
        var eyeWidth = eyeHeight * eyeAspect;
        return new Rect(windowWidth / 2 - centerX * eyeWidth, windowHeight / 2 - centerY * eyeHeight, eyeWidth, eyeHeight);
    }

    // ------------------------------------------------------------------ the game's window

    /// <summary>The running VR game's biggest window and the game's program file (empty when the producer names none).</summary>
    public static (IntPtr Window, string Game)? FindGameWindow()
    {
        if (DesktopCover.TargetFromProducer() is not { } target) return null;
        var windows = DesktopCover.WindowsOf(target.ProcessIds);
        if (windows.Count == 0) return null;
        var biggest = windows.MaxBy(w => (long)w.Bounds.Width * w.Bounds.Height);
        return (biggest.Window, MirrorLive.ReadProducer()?.Program ?? "");
    }

    /// <summary>
    /// The window's inside (what OBS Window Capture gets with "Client area"): asked of the window itself, with DWM's full
    /// content, which also has DirectX pictures; if that comes back black, its place on the screen. Null, and why, when
    /// neither has a picture - a game in exclusive full screen has none to give.
    /// </summary>
    public static BitmapSource? CaptureWindow(IntPtr window, out string problem)
    {
        problem = "";
        if (IsIconic(window)) { problem = "The game's window is minimised."; return null; }
        if (!GetClientRect(window, out var client) || client.Right < 16 || client.Bottom < 16) { problem = "The game's window has no inside to capture."; return null; }
        int width = client.Right, height = client.Bottom;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var before = SelectObject(memory, bitmap);
        var pixels = new byte[width * height * 4];
        try
        {
            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            bool Read()
            {
                SelectObject(memory, before);
                GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0);
                SelectObject(memory, bitmap);
                return !AllBlack(pixels);
            }
            if (PrintWindow(window, memory, PW_CLIENTONLY | PW_RENDERFULLCONTENT) && Read()) return Picture(pixels, width, height);
            var corner = new POINT();
            ClientToScreen(window, ref corner);
            if (BitBlt(memory, 0, 0, width, height, screen, corner.X, corner.Y, SRCCOPY | CAPTUREBLT) && Read()) return Picture(pixels, width, height);
            problem = "The game's window came out black. Run the game in a window or borderless, not exclusive full screen.";
            return null;
        }
        finally
        {
            SelectObject(memory, before);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool AllBlack(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4 * 97) // a spread of samples is plenty
        {
            if (bgra[i] > 8 || bgra[i + 1] > 8 || bgra[i + 2] > 8) return false;
        }
        return true;
    }

    private static BitmapSource Picture(byte[] bgra, int width, int height)
    {
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        picture.Freeze();
        return picture;
    }

    private const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2, SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
