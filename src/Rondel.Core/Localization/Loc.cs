using System.Globalization;

namespace Rondel.Core.Localization;

/// <summary>
/// UI strings. The language follows the system display language unless the user picks one in
/// settings; switching takes effect immediately (the UI listens to <see cref="Changed"/>).
/// </summary>
public static class Loc
{
    static IReadOnlyDictionary<string, string> s_current = Strings.En;

    /// <summary>"en" or "tr".</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Culture matching <see cref="Language"/>, for dates and numbers shown in the UI.</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "tr" ? "tr-TR" : "en-US");

    public static event Action? Changed;

    /// <param name="setting">"auto", "en" or "tr".</param>
    public static void SetLanguage(string? setting)
    {
        string language = setting?.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "tr" => "tr",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en",
        };
        if (language == Language && ReferenceEquals(s_current, Pick(language)))
            return;
        Language = language;
        s_current = Pick(language);
        Changed?.Invoke();
    }

    public static string Get(string key) =>
        s_current.TryGetValue(key, out string? value) ? value
        : Strings.En.TryGetValue(key, out string? fallback) ? fallback
        : key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Resolves an action label: "@Key" is a string-table key, anything else is literal text.</summary>
    public static string Resolve(string label) =>
        label.StartsWith('@') ? Get(label[1..]) : label;

    static IReadOnlyDictionary<string, string> Pick(string language) =>
        language == "tr" ? Strings.Tr : Strings.En;
}
