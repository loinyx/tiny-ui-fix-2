using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TinyUIFixApp;

/// The Tiny UI Fix's script, driven without its web configurator.
public static class Tuf
{
    const string Delimiter = "";
    const string BatName = "tiny-ui-fix-for-ts3.bat";
    const string FolderName = "tiny-ui-fix-for-ts3";
    const string ScriptName = "Use-TinyUIFixForTS3.ps1";
    public const string Repository = "loinyx/tiny-ui-fix-2";
    static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public static Version AppVersion => typeof(Tuf).Assembly.GetName().Version ?? new Version(0, 0);

    /// The folder holding the tool: next to the exe, an existing Tiny UI Fix folder in Documents (keeping its saved choices),
    /// or the app's own folder, filled from the copy of the tool inside the exe.
    public static string FindHome()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) ?? "",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts", "Tiny UI Fix") })
        {
            if (File.Exists(Path.Combine(dir, BatName)) || File.Exists(Path.Combine(dir, FolderName, ScriptName))) return dir;
        }
        return Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tiny UI Fix 2")).FullName;
    }

    /// The exe carries the tool's .bat, so a download is one file; the home's copy follows the exe's when they differ.
    public static void SyncEmbeddedTool(string home)
    {
        using var stream = typeof(Tuf).Assembly.GetManifestResourceStream(BatName);
        if (stream is null) return;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var bat = Path.Combine(home, BatName);
        if (!File.Exists(bat) || !File.ReadAllBytes(bat).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(bat, bytes);
    }

    public static string ScriptFolder(string home) => Path.Combine(home, FolderName);

    /// The .bat carries every file of the tool; unpack it when the folder is missing or older, as the .bat itself would.
    public static void ExtractIfNeeded(string home)
    {
        var bat = Path.Combine(home, BatName);
        var script = Path.Combine(ScriptFolder(home), ScriptName);
        if (!File.Exists(bat) || (File.Exists(script) && File.GetLastWriteTimeUtc(script) >= File.GetLastWriteTimeUtc(bat))) return;

        var data = File.ReadAllText(bat, new UTF8Encoding(false));
        // the first two delimiters wrap the bootstrap script; files follow as destination, type, contents
        var index = data.IndexOf(Delimiter, StringComparison.Ordinal);
        index = data.IndexOf(Delimiter, index + Delimiter.Length, StringComparison.Ordinal);
        if (index < 0) throw new InvalidDataException("The .bat holds no files.");
        while (true)
        {
            var destStart = index + Delimiter.Length;
            var destEnd = data.IndexOf(Delimiter, destStart, StringComparison.Ordinal);
            var typeStart = destEnd + Delimiter.Length;
            var typeEnd = data.IndexOf(Delimiter, typeStart, StringComparison.Ordinal);
            var contentStart = typeEnd + Delimiter.Length;
            var contentEnd = data.IndexOf(Delimiter, contentStart, StringComparison.Ordinal);
            if (destEnd < 0 || typeEnd < 0) throw new InvalidDataException("The .bat is truncated.");

            var dest = data[destStart..destEnd];
            var full = Path.GetFullPath(Path.Combine(ScriptFolder(home), dest));
            if (!full.StartsWith(Path.GetFullPath(ScriptFolder(home)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected path in the .bat: {dest}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var contents = data[contentStart..(contentEnd < 0 ? data.Length : contentEnd)];
            if (data[typeStart..typeEnd] == "BINARY") File.WriteAllBytes(full, Convert.FromBase64String(contents));
            else File.WriteAllText(full, contents, new UTF8Encoding(false));

            if (contentEnd < 0) break;
            index = contentEnd;
        }
    }

    /// Runs the script in a hidden PowerShell, reporting each output line.
    static async Task<(int Exit, string Output)> RunAsync(string home, GameInstall? game, string arguments, Action<string>? onLine, CancellationToken token = default)
    {
        var script = Path.Combine(ScriptFolder(home), ScriptName).Replace("'", "''");
        // "ea" is the script's registry install; Steam and other copies go through its Steam path
        var prefer = game is null || game.Kind != "ea" ? "-PreferSteamVersion" : "";
        var command = $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '{script}' -NonInteractive {prefer} {arguments}";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = ScriptFolder(home),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (game?.Kind == "folder") psi.Environment["TINY_UI_FIX_GAME_PATH"] = game.Path;
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = new StringBuilder();
        void Read(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line)) onLine?.Invoke(line.Trim());
        }
        process.OutputDataReceived += (_, e) => Read(e.Data);
        process.ErrorDataReceived += (_, e) => Read(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } }))
            await process.WaitForExitAsync();
        lock (output) return (process.ExitCode, output.ToString());
    }

    public static async Task<State> ReadStateAsync(string home, GameInstall? game, Action<string>? onLine)
    {
        var file = Path.Combine(Path.GetTempPath(), $"tiny-ui-fix-state-{Environment.ProcessId}.json");
        File.Delete(file);
        var (exit, output) = await RunAsync(home, game, $"-WriteConfiguratorStateTo '{file.Replace("'", "''")}'", onLine);
        if (!File.Exists(file)) throw new ScriptException(L.T("error.readState"), output);
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(file))!; }
        finally { File.Delete(file); }
    }

    public static async Task<string> GenerateAsync(string home, GameInstall? game, Settings settings, IEnumerable<string> loadOrder, IEnumerable<string> disabledRecommended, Action<string> onLine, CancellationToken token)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var order = string.Join(",", loadOrder.Select(Q));
        var config = "@{Nucleus = @{" +
            $"UIScale = {Q(settings.UIScale)}; TextScale = {Q(settings.TextScale)}; CursorScale = {Q(settings.CursorScale)}; " +
            $"CursorScalingAlgorithm = {Q(settings.CursorScalingAlgorithm)}; DisableRuntimeModMismatchCheck = ${(settings.DisableRuntimeModMismatchCheck ? "true" : "false")}" +
            "}}";
        // a first install's Resource.cfg doesn't load Mods\TinyUIFix yet; the script adds the line only when it's needed
        var (exit, output) = await RunAsync(home, game, $"-ChangeResourceCFGToLoadTinyUIFixLast -PatchsetLoadOrder {order} -PatchsetConfiguration {config}", onLine, token);
        token.ThrowIfCancellationRequested();
        if (exit != 0 || !output.Contains("A UI-scaled package file was generated", StringComparison.Ordinal))
            throw new ScriptException(L.T("error.notGenerated"), output);

        // the web configurator kept this list; without it a switched-off recommendation would come back next time
        File.WriteAllText(Path.Combine(ScriptFolder(home), "LastDisabledRecommendedPatchsets.txt"),
            string.Join(Environment.NewLine, disabledRecommended) + Environment.NewLine);
        return output;
    }

    public static async Task UninstallAsync(string home, GameInstall? game)
    {
        var (exit, output) = await RunAsync(home, game, "-Uninstall", null);
        if (exit != 0) throw new ScriptException(L.T("error.uninstall"), output);
    }

    /// The newest release of this app on GitHub ("v2.0.0" tags).
    public static async Task<Version?> LatestVersionAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TinyUIFix2");
        using var json = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseUrl));
        return ParseTag(json.RootElement.GetProperty("tag_name").GetString());
    }

    public static Version? ParseTag(string? tag) => Version.TryParse(tag?.TrimStart('v', 'V'), out var v) ? v : null;

    public static bool GameIsRunning() => Process.GetProcessesByName("TS3W").Length + Process.GetProcessesByName("TS3").Length > 0;

    /// Patchset descriptions are HTML written for the web page; this keeps their text and list structure.
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<summary>.*?</summary>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<hr\s*/?>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*<li[^>]*>\s*", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*<(br|/p|p|/ul|ul|/li|details|/details)[^>]*>\s*", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t\r]+", " ");
        s = Regex.Replace(s, @" ?\n ?", "\n");
        s = Regex.Replace(s, @"\n{2,}", "\n");
        return s.Trim();
    }

    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static double Parse(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}

public sealed class ScriptException(string message, string output) : Exception(message)
{
    public string Output { get; } = output;
}

public sealed class State
{
    public string ScriptVersion { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string UserDataPath { get; set; } = "";
    public string UIScale { get; set; } = "";
    public string TextScale { get; set; } = "";
    public string CursorScale { get; set; } = "";
    public string CursorScalingAlgorithm { get; set; } = "";
    public bool DisableRuntimeModMismatchCheck { get; set; }
    public List<string> LoadOrder { get; set; } = [];
    public bool LoadOrderNotActivelySetByUser { get; set; }
    public List<string> DisabledRecommendedPatchsets { get; set; } = [];
    public List<PatchsetState> Patchsets { get; set; } = [];
}

public sealed class PatchsetState
{
    public string ID { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; }
    public bool Fixed { get; set; }
    public string? Recommendation { get; set; }
}

public sealed class Settings
{
    public string UIScale { get; set; } = "1";
    public string TextScale { get; set; } = "";
    public string CursorScale { get; set; } = "1";
    public string CursorScalingAlgorithm { get; set; } = "";
    public bool DisableRuntimeModMismatchCheck { get; set; }
}
