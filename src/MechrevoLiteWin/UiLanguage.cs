namespace MechrevoLite;

/// <summary>
/// UI language is zh-CN or English only. English lives in the main <c>Strings.resx</c>;
/// Chinese is the zh-CN satellite. Stored value is <c>AppConfig["language"]</c>.
/// </summary>
internal static class UiLanguage
{
    public const string Chinese = "zh-CN";
    public const string English = "en";
    public const string ConfigKey = "language";

    public static string Normalize(string? stored, string? uiCultureName)
    {
        if (!string.IsNullOrWhiteSpace(stored))
        {
            if (stored.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Chinese;
            if (stored.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;
        }

        if (!string.IsNullOrWhiteSpace(uiCultureName) &&
            uiCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return Chinese;
        return English;
    }

    public static int IndexOf(string code) =>
        string.Equals(code, English, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    public static string CodeFromIndex(int index) => index == 1 ? English : Chinese;
}
