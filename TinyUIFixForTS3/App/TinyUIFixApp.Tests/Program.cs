using System.Globalization;
using System.Security.Cryptography;
using TinyUIFixApp;

// Checks the app's logic without opening the game. Pass --integration to also run the real script (read-only).
var failures = 0;
var total = 0;
void Check(string name, bool ok, string detail = "")
{
    total++;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
}
bool Near(double a, double b, double tolerance = 0.01) => Math.Abs(a - b) <= tolerance;
var temp = Directory.CreateTempSubdirectory("tuf-tests-").FullName;

// --- resolution from Options.ini
Check("parses resolution", Games.ParseResolution("a = 1\r\nresolution = 3840 2160 0\r\nfullscreen = 0") == (3840, 2160));
Check("parses extra spaces", Games.ParseResolution("resolution=   2560    1440") == (2560, 1440));
Check("ignores pseudoresolution", Games.ParseResolution("pseudoresolution = 1280 720") is null);
Check("pseudoresolution before the real one", Games.ParseResolution("pseudoresolution = 0,0\nresolution = 3440 1440 0") == (3440, 1440));
Check("missing resolution", Games.ParseResolution("fullscreen = 1") is null);
Check("rejects nonsense size", Games.ParseResolution("resolution = 0 0 0") is null && Games.ParseResolution("resolution = 99999 1 0") is null);
Check("rejects garbage", Games.ParseResolution("resolution = abc def") is null);

var withIni = Directory.CreateDirectory(Path.Combine(temp, "withini")).FullName;
File.WriteAllText(Path.Combine(withIni, "Options.ini"), "resolution = 5120 1440 0\r\n");
var fromIni = Games.Resolution(withIni);
Check("reads Options.ini", fromIni == new Screen(5120, 1440, true), fromIni.ToString());
var monitor = Games.Resolution(Path.Combine(temp, "nothing-here"));
Check("falls back to the monitor", !monitor.FromGame && monitor.Width >= 640 && monitor.Height >= 480, monitor.ToString());

// --- aspect names and ultrawide detection
foreach (var (w, h, name, wide) in new[]
{
    (1920, 1080, "16:9", false), (3840, 2160, "16:9", false), (1366, 768, "16:9", false), (2560, 1600, "16:10", false),
    (1024, 768, "4:3", false), (1280, 1024, "5:4", false), (2560, 1080, "21:9", true), (3440, 1440, "21:9", true),
    (5120, 1440, "32:9", true), (3840, 1080, "32:9", true),
})
{
    var screen = new Screen(w, h, true);
    Check($"{w}x{h} is {name}{(wide ? ", ultrawide" : "")}", screen.AspectName == name && screen.IsUltrawide == wide, $"{screen.AspectName} wide={screen.IsUltrawide}");
}

// --- preview geometry
var fourK = new Screen(3840, 2160, true);
var (h4k, hud4k) = Preview.Layout(2000, fourK, 1.4);
Check("16:9 frame keeps 16:9", Near(h4k, 1125));
// measured in game: the HUD picture spans 523.4 px of a 2000 px wide 4K screenshot at 1.4x (444 px of visible art)
Check("4K 1.4x HUD matches the in-game screenshot", Near(hud4k, 2000 * 1005 / 3840.0, 0.01), hud4k.ToString("0.00"));
Check("HUD grows with the UI scale", Near(Preview.Layout(2000, fourK, 2).HudWidth / hud4k, 2 / 1.4, 1e-9));
Check("HUD is the same share at any window width", Near(Preview.Layout(900, fourK, 1.4).HudWidth / 900, hud4k / 2000, 1e-9));
var uw = new Screen(3440, 1440, true);
var (huw, hudUw) = Preview.Layout(1200, uw, 1.4);
Check("21:9 frame keeps 21:9", Near(1200 / huw, 3440 / 1440.0, 1e-9));
Check("21:9 HUD share uses the screen width", Near(hudUw / 1200, 1005 / 3440.0, 1e-9));
var fhd = new Screen(1920, 1080, true);
Check("1080p 1.4x HUD is half the screen", Near(Preview.Layout(1000, fhd, 1.4).HudWidth / 1000, 1005 / 1920.0, 1e-9));
Check("1080p 1x HUD is a third of the screen", Near(Preview.Layout(1000, fhd, 1).HudWidth / 1000, 1005 / 1.4 / 1920, 1e-9));

// --- first install: suggested scale from the screen's height
foreach (var (w, h, want) in new[] { (1920, 1080, 1.0), (2560, 1440, 1.0), (3440, 1440, 1.0), (2560, 1600, 1.25), (3840, 2160, 1.6), (5120, 2880, 2.0), (1366, 768, 1.0), (7680, 4320, 2.0) })
    Check($"suggests {want}x for {w}x{h}", Preview.SuggestedScale(new Screen(w, h, true)) == want, Preview.SuggestedScale(new Screen(w, h, true)).ToString());

// --- languages: Windows' language picks the game's, and every translation has every key with the same placeholders
foreach (var (windows, want) in new[] { ("pt-BR", "pt-BR"), ("pt-PT", "pt-PT"), ("pt", "pt-BR"), ("en-GB", "en-US"), ("de-AT", "de-DE"), ("nb-NO", "no-NO"),
             ("nn-NO", "no-NO"), ("zh-Hant-TW", "zh-TW"), ("zh-HK", "zh-TW"), ("zh-CN", "zh-CN"), ("es-AR", "es-MX"), ("es-ES", "es-ES"), ("tr-TR", "en-US"), ("ja-JP", "ja-JP") })
    Check($"Windows {windows} shows {want}", L.Match(windows) == want, L.Match(windows));
var english = Json("en-US");
foreach (var (code, _) in L.Languages)
{
    var strings = Json(code);
    if (strings is null) { Check($"{code} translation exists", false, "missing"); continue; }
    var missing = english!.Keys.Except(strings.Keys).ToList();
    var extra = strings.Keys.Except(english.Keys).ToList();
    var placeholders = english.Keys.Intersect(strings.Keys).Where(k => Holes(english[k]) != Holes(strings[k])).ToList();
    Check($"{code} has every text, same placeholders", missing.Count == 0 && extra.Count == 0 && placeholders.Count == 0,
        $"missing {string.Join(",", missing)} extra {string.Join(",", extra)} placeholders {string.Join(",", placeholders)}");
}
static Dictionary<string, string>? Json(string code)
{
    using var stream = typeof(L).Assembly.GetManifestResourceStream($"Strings.{code}.json");
    return stream is null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
}
static string Holes(string text) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{\d\}").Select(m => m.Value).Order());

// --- numbers go to the script in invariant form, whatever the Windows language
var culture = CultureInfo.CurrentCulture;
CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
Check("Format uses a dot", Tuf.Format(1.4) == "1.4" && Tuf.Format(2) == "2" && Tuf.Format(1.25) == "1.25");
Check("Parse reads a dot", Near(Tuf.Parse("1.4", 1), 1.4) && Near(Tuf.Parse("", 1), 1) && Near(Tuf.Parse("-2", 1), 1));
CultureInfo.CurrentCulture = culture;

// --- patchset descriptions written as HTML
var html = "<p>Hello &amp; welcome.</p><details><summary>Details</summary><ul><li>Press <kbd>A</kbd> to go left.</li><li>Press <kbd>D</kbd>.</li></ul></details>";
var text = Tuf.HtmlToText(html);
Check("HTML becomes text", !text.Contains('<') && text.Contains("Hello & welcome.") && text.Contains("• Press A to go left.") && !text.Contains("Details"), text);

// --- installs: a picked folder is listed only when it holds the game
var fakeGame = Path.Combine(temp, "Fake Sims 3");
Directory.CreateDirectory(Path.Combine(fakeGame, "Game", "Bin"));
File.WriteAllText(Path.Combine(fakeGame, "Game", "Bin", "TS3W.exe"), "");
var found = Games.Find(fakeGame);
Check("picked game folder is listed", found.Any(g => g.Kind == "folder" && g.Path == fakeGame && g.Title == L.T("game.folder")));
Check("folder without the game is ignored", !Games.Find(temp).Any(g => g.Path == temp));
Check("Hydra folder gets its name", new GameInstall("folder", @"C:\Games\Hydra\The Sims 3").Title == L.T("game.hydra"));
Games.SaveChoice(temp, new GameInstall("folder", fakeGame));
Check("choice round-trips", Games.LoadChoice(temp) == new GameInstall("folder", fakeGame));
File.WriteAllText(Path.Combine(temp, "TinyUIFixApp.json"), "{broken");
Check("broken choice file is ignored", Games.LoadChoice(temp) is null);

// --- the .bat unpacks to exactly the files the tool runs from
var home = Tuf.FindHome();
Check("finds the Tiny UI Fix folder", home is not null, "none");
if (home is not null)
{
    var copy = Directory.CreateDirectory(Path.Combine(temp, "home")).FullName;
    File.Copy(Path.Combine(home, "tiny-ui-fix-for-ts3.bat"), Path.Combine(copy, "tiny-ui-fix-for-ts3.bat"));
    Tuf.ExtractIfNeeded(copy);
    var extracted = Directory.GetFiles(Tuf.ScriptFolder(copy), "*", SearchOption.AllDirectories);
    var differing = extracted.Where(f =>
    {
        var live = Path.Combine(Tuf.ScriptFolder(home), Path.GetRelativePath(Tuf.ScriptFolder(copy), f));
        return !File.Exists(live) || !SHA256.HashData(File.ReadAllBytes(f)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(live)));
    }).Select(f => Path.GetRelativePath(Tuf.ScriptFolder(copy), f)).ToList();
    Check($"the .bat holds the same {extracted.Length} files as the folder", extracted.Length > 30 && differing.Count == 0, string.Join(", ", differing));
    var script = File.ReadAllText(Path.Combine(Tuf.ScriptFolder(copy), "Use-TinyUIFixForTS3.ps1"));
    Check("script has the app's state export", script.Contains("$WriteConfiguratorStateTo"));
    Check("script flushes style-sheets", script.Contains("$CSSWriter.Flush()"));
    Check("script reads the app's game folder", script.Contains("TINY_UI_FIX_GAME_PATH"));
    var patchset = File.ReadAllText(Path.Combine(Tuf.ScriptFolder(copy), "Patchsets", "VanillaCoreCompatibilityPatch.ps1"));
    Check("combo-box row count is not scaled", !patchset.Contains("Floats = 15, 27, 205, 227") && patchset.Contains("Floats = 27, 205, 227"));
    Check("main menu is centred", patchset.Contains("0xda0227dfd918adf7") && patchset.Contains("'0.55'"));
    Check("extraction is skipped when up to date", Time(() => Tuf.ExtractIfNeeded(copy)) < 50);
}

// --- the real script, read-only: it reports the state the app shows
if (args.Contains("--integration") && home is not null)
{
    var game = Games.Find(null).FirstOrDefault(g => g.Kind != "ea") ?? Games.Find(null).FirstOrDefault();
    if (game is null) Check("a game install to read", false, "none found");
    else
    {
        var state = await Tuf.ReadStateAsync(home, game, null);
        Check("script reports the chosen game", string.Equals(state.GamePath.TrimEnd('\\'), game.Path, StringComparison.OrdinalIgnoreCase), state.GamePath);
        Check("script reports the nucleus first", state.LoadOrder.FirstOrDefault() == "Nucleus" && state.Patchsets.Any(p => p.ID == "Nucleus" && p.Fixed));
        Check("script reports a usable UI scale", Tuf.Parse(state.UIScale, 0) > 0, state.UIScale);
        Check("script reports the user's data folder", Directory.Exists(state.UserDataPath), state.UserDataPath);
        Check("resolution comes from the game", Games.Resolution(state.UserDataPath).FromGame);
    }
}

Directory.Delete(temp, true);
Console.WriteLine($"\n{total - failures}/{total} passed");
return failures == 0 ? 0 : 1;

static long Time(Action action)
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    action();
    return watch.ElapsedMilliseconds;
}
