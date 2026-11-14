using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TinyUIFixApp;

/// A copy of The Sims 3. "ea" and "steam" are found the way the script finds them; "folder" is any other
/// copy laid out like Steam's (expansions inside it), such as Hydra's, passed to the script by path.
public sealed partial record GameInstall(string Kind, string Path)
{
    public string Title => Kind switch
    {
        "ea" => L.T("game.ea"),
        "steam" => L.T("game.steam"),
        _ => Path.Contains("Hydra", StringComparison.OrdinalIgnoreCase) ? L.T("game.hydra") : L.T("game.folder"),
    };

    public static bool IsGame(string dir) => File.Exists(System.IO.Path.Combine(dir, "Game", "Bin", "TS3W.exe"));
}

public static class Games
{
    const string SettingsName = "TinyUIFixApp.json";

    public static List<GameInstall> Find(string? extraFolder)
    {
        var found = new List<GameInstall>();
        void Add(string kind, string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            dir = System.IO.Path.GetFullPath(dir.Trim().TrimEnd('\\', '/'));
            if (GameInstall.IsGame(dir) && !found.Any(g => string.Equals(g.Path, dir, StringComparison.OrdinalIgnoreCase)))
                found.Add(new GameInstall(kind, dir));
        }

        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Sims\The Sims 3"))
            Add("ea", key?.GetValue("Install Dir") as string);

        using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (steam?.GetValue("SteamPath") is string steamPath)
            {
                var libraries = new List<string> { steamPath };
                var vdf = System.IO.Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
                foreach (var library in libraries) Add("steam", System.IO.Path.Combine(library, "steamapps", "common", "The Sims 3"));
            }
        }

        Add("folder", extraFolder);
        return found;
    }

    public static GameInstall? LoadChoice(string scriptFolder)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(System.IO.Path.Combine(scriptFolder, SettingsName)), AppJson.Default.GameInstall); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void SaveChoice(string scriptFolder, GameInstall game) =>
        File.WriteAllText(System.IO.Path.Combine(scriptFolder, SettingsName), JsonSerializer.Serialize(game, AppJson.Default.GameInstall), new UTF8Encoding(false));

    /// The size the game draws at: "resolution = 3840 2160 0" in its Options.ini (the window's size when windowed),
    /// or the primary monitor's when the game hasn't written one yet.
    public static Screen Resolution(string userDataPath)
    {
        try
        {
            if (ParseResolution(File.ReadAllText(System.IO.Path.Combine(userDataPath, "Options.ini"))) is { } fromGame)
                return new Screen(fromGame.Width, fromGame.Height, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(null, -1, ref mode) && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0
            ? new Screen(mode.dmPelsWidth, mode.dmPelsHeight, false)
            : new Screen(1920, 1080, false);
    }

    public static (int Width, int Height)? ParseResolution(string optionsIni)
    {
        var m = Regex.Match(optionsIni, @"^\s*resolution\s*=\s*(\d+)\s+(\d+)", RegexOptions.Multiline);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var w) || !int.TryParse(m.Groups[2].Value, out var h)) return null;
        return w >= 640 && h >= 480 && w <= 16384 && h <= 16384 ? (w, h) : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    // ENUM_CURRENT_SETTINGS (-1) of the primary display
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode mode);
}

public readonly record struct Screen(int Width, int Height, bool FromGame)
{
    public double Aspect => (double)Width / Height;

    // 21:9 is 2.33 and 32:9 is 3.56; 16:10 and 16:9 stay below 1.8
    public bool IsUltrawide => Aspect >= 2.1;

    public string AspectName => Math.Abs(Aspect - 16.0 / 9) < 0.02 ? "16:9" : Math.Abs(Aspect - 16.0 / 10) < 0.02 ? "16:10"
        : Math.Abs(Aspect - 4.0 / 3) < 0.02 ? "4:3" : Math.Abs(Aspect - 5.0 / 4) < 0.02 ? "5:4"
        : Math.Abs(Aspect - 32.0 / 9) < 0.03 ? "32:9" : Aspect >= 2.3 && Aspect <= 2.42 ? "21:9" : $"{Aspect:0.00}:1";
}

/// The preview is the player's whole screen: the game draws its UI in screen pixels and doesn't resize it with the resolution,
/// so the HUD's share of the screen is its pixel width times the UI scale over the screen's width.
public static class Preview
{
    // the HUD picture was taken in game at 3840x2160 with the UI at 1.4x, one picture pixel per screen pixel
    public const double HudPictureWidth = 1005, HudPictureScale = 1.4;

    public static readonly double[] Presets = [1, 1.25, 1.4, 1.6, 1.75, 2, 2.5];

    // a first install starts from the preset nearest to the screen's height over 1350 px: 1x up to 1440p, 1.6x at 4K
    public static double SuggestedScale(Screen screen) =>
        Presets.Where(p => p <= 2).MinBy(p => Math.Abs(p - Math.Max(1, screen.Height / 1350.0)));

    public static (double FrameHeight, double HudWidth) Layout(double frameWidth, Screen screen, double scale) =>
        (frameWidth / screen.Aspect, frameWidth * HudPictureWidth / HudPictureScale * scale / screen.Width);
}

