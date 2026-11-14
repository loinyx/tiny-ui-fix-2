using System.Globalization;
using System.Text.Json;

namespace TinyUIFixApp;

/// The app's text in The Sims 3's languages; English fills any key a translation lacks.
public static class L
{
    /// The game's languages, each with its name as its speakers write it.
    public static readonly (string Code, string Name)[] Languages =
    [
        ("en-US", "English"), ("pt-BR", "Português (Brasil)"), ("pt-PT", "Português (Portugal)"), ("es-ES", "Español (España)"),
        ("es-MX", "Español (Latinoamérica)"), ("fr-FR", "Français"), ("de-DE", "Deutsch"), ("it-IT", "Italiano"), ("nl-NL", "Nederlands"),
        ("pl-PL", "Polski"), ("cs-CZ", "Čeština"), ("hu-HU", "Magyar"), ("ru-RU", "Русский"), ("el-GR", "Ελληνικά"), ("da-DK", "Dansk"),
        ("sv-SE", "Svenska"), ("no-NO", "Norsk"), ("fi-FI", "Suomi"), ("ja-JP", "日本語"), ("ko-KR", "한국어"), ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"), ("th-TH", "ไทย"),
    ];

    static Dictionary<string, string> _english = Load("en-US") ?? [];
    static Dictionary<string, string> _current = _english;

    public static string Language { get; private set; } = "en-US";

    static string SettingFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tiny UI Fix 2", "language.txt");

    /// The saved choice, otherwise Windows' display language, otherwise English.
    public static void Init() => Use(Saved() ?? Match(CultureInfo.CurrentUICulture.Name));

    public static void Choose(string code)
    {
        Use(code);
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingFile)!); File.WriteAllText(SettingFile, code); }
        catch (IOException) { }
    }

    static string? Saved()
    {
        try { var code = File.ReadAllText(SettingFile).Trim(); return Languages.Any(l => l.Code == code) ? code : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Windows' language to the game's: the exact one, else the same language (Norwegian Nynorsk and Bokmål are "no-NO").
    public static string Match(string culture)
    {
        if (Languages.FirstOrDefault(l => string.Equals(l.Code, culture, StringComparison.OrdinalIgnoreCase)) is { Code: not null } exact) return exact.Code;
        var language = culture.Split('-')[0].ToLowerInvariant();
        if (language is "nb" or "nn") language = "no";
        if (language == "zh") return culture.Contains("Hant", StringComparison.OrdinalIgnoreCase) || culture.EndsWith("TW") || culture.EndsWith("HK") || culture.EndsWith("MO") ? "zh-TW" : "zh-CN";
        if (language == "es" && culture.Length > 3 && !culture.EndsWith("ES", StringComparison.OrdinalIgnoreCase)) return "es-MX";
        return Languages.FirstOrDefault(l => l.Code.StartsWith(language + "-", StringComparison.Ordinal)).Code ?? "en-US";
    }

    static void Use(string code)
    {
        Language = code;
        _current = Load(code) ?? _english;
        // numbers such as "1,4×" follow the language
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(code);
    }

    static Dictionary<string, string>? Load(string code)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Strings.{code}.json");
        return stream is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
    }

    public static string T(string key) => _current.TryGetValue(key, out var text) || _english.TryGetValue(key, out text) ? text : key;

    public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}
