using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EmulatorAutoUpdater.Models;

namespace EmulatorAutoUpdater.Services;

public static class AssetPatternHelper
{
    public static string ConvertFilenameToAssetPattern(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return AppSettings.DefaultAssetPatternValue;
        }

        var trimmed = input.Trim();

        // If input is already a valid Regex pattern (starts with (?i) or contains operators like \. or .*), return as-is
        if (trimmed.StartsWith("(?i)") || trimmed.Contains(".*") || trimmed.Contains(@"\."))
        {
            return trimmed;
        }

        // Detect exact extension
        var extMatch = Regex.Match(trimmed, @"\.(zip|7z|rar|tar\.gz|tar\.xz|exe|msi)$", RegexOptions.IgnoreCase);
        string extensionRegex = @"\.(zip|7z)$";
        string baseName = trimmed;

        if (extMatch.Success)
        {
            var ext = extMatch.Value.TrimStart('.').ToLowerInvariant();
            extensionRegex = @"\." + Regex.Escape(ext) + "$";
            baseName = trimmed[..^extMatch.Value.Length];
        }

        // Pre-normalize compound architecture words (e.g. x86_64, x86-64)
        baseName = Regex.Replace(baseName, @"(?i)\bx86[-_]64\b", "x86_64");
        baseName = Regex.Replace(baseName, @"(?i)\bx86[-_]32\b", "x86_32");

        // Pre-normalize version strings like v1.18.0 or 5.0 into a unified token
        baseName = Regex.Replace(baseName, @"(?i)\b(v?\d+(\.\d+)+)\b", "_VER_");

        // Tokenize baseName into words (including _) and non-word delimiters
        var matches = Regex.Matches(baseName, @"[a-zA-Z0-9_]+|[^a-zA-Z0-9_]+");

        var patternBuilder = new StringBuilder();
        patternBuilder.Append("(?i)");

        bool hasAppTitle = false;
        bool addedOs = false;
        bool addedArch = false;

        foreach (Match match in matches)
        {
            var token = match.Value;

            // Skip delimiters (hyphens, dots, spaces, etc.)
            if (!Regex.IsMatch(token, @"[a-zA-Z0-9_]"))
            {
                continue;
            }

            // Detect OS keywords
            if (Regex.IsMatch(token, @"^(win|windows|win32|win64)$", RegexOptions.IgnoreCase))
            {
                if (!addedOs)
                {
                    patternBuilder.Append(".*(win|windows)");
                    addedOs = true;
                }
                continue;
            }

            if (Regex.IsMatch(token, @"^(linux|ubuntu)$", RegexOptions.IgnoreCase))
            {
                if (!addedOs)
                {
                    patternBuilder.Append(".*(linux|ubuntu)");
                    addedOs = true;
                }
                continue;
            }

            if (Regex.IsMatch(token, @"^(mac|macos|osx)$", RegexOptions.IgnoreCase))
            {
                if (!addedOs)
                {
                    patternBuilder.Append(".*(mac|macos|osx)");
                    addedOs = true;
                }
                continue;
            }

            // Detect Arch keywords
            if (Regex.IsMatch(token, @"^(x64|amd64|x86_64|x86-64|64bit)$", RegexOptions.IgnoreCase))
            {
                if (!addedArch)
                {
                    patternBuilder.Append(".*" + Regex.Escape(token));
                    addedArch = true;
                }
                continue;
            }

            if (Regex.IsMatch(token, @"^(x86|x86_32|x86-32|32bit)$", RegexOptions.IgnoreCase))
            {
                if (!addedArch)
                {
                    patternBuilder.Append(".*" + Regex.Escape(token));
                    addedArch = true;
                }
                continue;
            }

            if (Regex.IsMatch(token, @"^(arm64|aarch64)$", RegexOptions.IgnoreCase))
            {
                if (!addedArch)
                {
                    patternBuilder.Append(".*" + Regex.Escape(token));
                    addedArch = true;
                }
                continue;
            }

            // Detect transient versions, dates, build numbers, or commit hashes to replace with .*
            var isTransientVersionOrHash =
                token.Equals("_VER_", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(token, @"^\d{3,}$") || // numeric build numbers or dates (e.g. 21430, 20260728)
                Regex.IsMatch(token, @"^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase); // commit hash like 5ec94b1971, 0133caf702

            if (isTransientVersionOrHash)
            {
                patternBuilder.Append(".*");
                continue;
            }

            // Variant / Title token (e.g. Eden, clang, pgo, release, symbols, qt, sdl, msvc)
            if (!hasAppTitle)
            {
                if (addedOs || addedArch)
                {
                    patternBuilder.Append(".*" + Regex.Escape(token));
                }
                else
                {
                    patternBuilder.Append(Regex.Escape(token));
                }
                hasAppTitle = true;
            }
            else
            {
                patternBuilder.Append(".*" + Regex.Escape(token));
            }
        }

        patternBuilder.Append(".*");
        patternBuilder.Append(extensionRegex);

        // Collapse multiple consecutive .* into a single .*
        var finalPattern = Regex.Replace(patternBuilder.ToString(), @"(\.\*)+", ".*");

        return finalPattern;
    }

    public static string BuildExclusionAssetPattern(string currentPattern, string excludedFilename)
    {
        if (string.IsNullOrWhiteSpace(excludedFilename))
        {
            return currentPattern ?? string.Empty;
        }

        var basePattern = currentPattern?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(basePattern) || basePattern == AppSettings.DefaultAssetPatternValue)
        {
            basePattern = "(?i).*\\.(zip|7z)$";
        }
        else if (!basePattern.StartsWith("(?i)"))
        {
            basePattern = "(?i)" + basePattern;
        }

        // Detect extension from excludedFilename
        var extMatch = Regex.Match(excludedFilename, @"\.(zip|7z|rar|tar\.gz|tar\.xz|exe|msi)$", RegexOptions.IgnoreCase);
        string baseName = extMatch.Success ? excludedFilename[..^extMatch.Value.Length] : excludedFilename;

        // Pre-normalize compound words and versions
        baseName = Regex.Replace(baseName, @"(?i)\bx86[-_]64\b", "x86_64");
        baseName = Regex.Replace(baseName, @"(?i)\bx86[-_]32\b", "x86_32");
        baseName = Regex.Replace(baseName, @"(?i)\b(v?\d+(\.\d+)+)\b", "");

        var tokens = Regex.Matches(baseName, @"[a-zA-Z0-9_]+")
            .Select(m => m.Value)
            .Where(t => t.Length >= 2)
            .ToList();

        // Identify candidate exclude token
        string? excludeToken = null;

        // Known common exclusion words
        var commonExclusionWords = new[] { "symbols", "symbol", "ssl", "pdb", "debug", "sdk", "source", "dev", "portable", "installer", "setup" };
        var foundCommon = tokens.FirstOrDefault(t => commonExclusionWords.Any(w => string.Equals(w, t, StringComparison.OrdinalIgnoreCase)));

        if (foundCommon != null)
        {
            excludeToken = foundCommon;
        }
        else
        {
            // Find token in excludedFilename that does NOT appear in current basePattern
            foreach (var token in tokens.AsEnumerable().Reverse())
            {
                if (Regex.IsMatch(token, @"^(win|windows|win32|win64|linux|ubuntu|mac|macos|osx|x64|amd64|x86_64|x86|arm64|aarch64|64|32)$", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                if (!basePattern.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    excludeToken = token;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(excludeToken))
        {
            excludeToken = tokens.LastOrDefault() ?? "symbols";
        }

        string lookahead = $"(?!.*{excludeToken})";

        // Check if pattern already has this exact lookahead
        if (basePattern.Contains(lookahead, StringComparison.OrdinalIgnoreCase))
        {
            return basePattern;
        }

        // Inject lookahead right after (?i)
        if (basePattern.StartsWith("(?i)"))
        {
            return "(?i)" + lookahead + basePattern.Substring(4);
        }

        return "(?i)" + lookahead + basePattern;
    }
}
