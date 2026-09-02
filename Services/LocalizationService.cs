using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace EmulatorAutoUpdater.Services;

public record LanguageOption(string Code, string NativeName, string EnglishName);

public static class LocalizationService
{
    private static ResourceDictionary? _currentLanguageDictionary;

    public static readonly IReadOnlyList<LanguageOption> SupportedLanguages = new List<LanguageOption>
    {
        new("ko-KR", "한국어", "Korean"),
        new("en-US", "English", "English"),
        new("ja-JP", "日本語", "Japanese"),
        new("zh-CN", "简体中文", "Chinese (Simplified)")
    };

    public static string CurrentLanguageCode { get; private set; } = "ko-KR";

    public static void Initialize(string? preferredLanguageCode = null)
    {
        string targetCode;
        if (string.IsNullOrWhiteSpace(preferredLanguageCode))
        {
            var osCulture = CultureInfo.CurrentUICulture.Name;
            var osMatch = SupportedLanguages.FirstOrDefault(l =>
                l.Code.Equals(osCulture, StringComparison.OrdinalIgnoreCase) ||
                l.Code.StartsWith(osCulture.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            targetCode = osMatch?.Code ?? "ko-KR";
        }
        else
        {
            targetCode = NormalizeLanguageCode(preferredLanguageCode);
        }

        SetLanguage(targetCode);
    }

    public static void SetLanguage(string languageCode)
    {
        var normalized = NormalizeLanguageCode(languageCode);
        CurrentLanguageCode = normalized;

        try
        {
            var dictUri = new Uri($"/Languages/Strings.{normalized}.xaml", UriKind.Relative);
            var newDict = (ResourceDictionary)System.Windows.Application.LoadComponent(dictUri);

            if (System.Windows.Application.Current != null)
            {
                var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
                if (_currentLanguageDictionary != null)
                {
                    merged.Remove(_currentLanguageDictionary);
                }

                merged.Add(newDict);
                _currentLanguageDictionary = newDict;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load language dictionary for {normalized}: {ex.Message}");
        }
    }

    public static string GetString(string key, params object[] args)
    {
        if (System.Windows.Application.Current != null && System.Windows.Application.Current.Resources.Contains(key))
        {
            var val = System.Windows.Application.Current.Resources[key]?.ToString();
            if (!string.IsNullOrEmpty(val))
            {
                if (args.Length > 0)
                {
                    try { return string.Format(val, args); } catch { return val; }
                }
                return val;
            }
        }

        return key;
    }

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            var match = SupportedLanguages.FirstOrDefault(l => 
                l.Code.Equals(languageCode, StringComparison.OrdinalIgnoreCase) ||
                l.Code.StartsWith(languageCode, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Code;
        }

        return "ko-KR";
    }
}
