using System.IO;
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

        // Detect extension
        var extMatch = Regex.Match(trimmed, @"\.(zip|7z|rar|tar\.gz|tar\.xz|exe|msi)$", RegexOptions.IgnoreCase);
        string extensionRegex = @"\.(zip|7z)$";
        string baseName = trimmed;

        if (extMatch.Success)
        {
            var ext = extMatch.Value.TrimStart('.').ToLowerInvariant();
            if (ext is not ("zip" or "7z"))
            {
                extensionRegex = @"\." + Regex.Escape(ext) + "$";
            }
            baseName = trimmed[..^extMatch.Value.Length];
        }

        // Tokenize baseName into words and non-word delimiters
        var matches = Regex.Matches(baseName, @"[a-zA-Z0-9]+|[^a-zA-Z0-9]+");

        var patternBuilder = new StringBuilder();
        patternBuilder.Append("(?i)");

        bool hasAppTitle = false;
        bool addedOs = false;
        bool addedArch = false;

        foreach (Match match in matches)
        {
            var token = match.Value;

            // Skip delimiters (hyphens, underscores, dots, spaces)
            if (!Regex.IsMatch(token, @"[a-zA-Z0-9]"))
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
            if (Regex.IsMatch(token, @"^(x64|amd64|x86_64|64bit|64)$", RegexOptions.IgnoreCase))
            {
                if (!addedArch)
                {
                    patternBuilder.Append(".*(x64|amd64|64)");
                    addedArch = true;
                }
                continue;
            }

            if (Regex.IsMatch(token, @"^(arm64|aarch64)$", RegexOptions.IgnoreCase))
            {
                if (!addedArch)
                {
                    patternBuilder.Append(".*(arm64|aarch64)");
                    addedArch = true;
                }
                continue;
            }

            // Detect Version/Build/Hash/Date or build tags
            var isVersionOrHashOrBuildTag =
                Regex.IsMatch(token, @"^\d+$") || // numeric build numbers or dates
                Regex.IsMatch(token, @"^(v?\d+(\.\d+)*)$", RegexOptions.IgnoreCase) || // v1.17.1, 5.0
                Regex.IsMatch(token, @"^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase) || // commit hash like 0133caf702
                Regex.IsMatch(token, @"^(master|main|nightly|canary|dev|release|stable|msvc|clang|pgo|qt|sdl|build)$", RegexOptions.IgnoreCase);

            if (isVersionOrHashOrBuildTag)
            {
                patternBuilder.Append(".*");
                continue;
            }

            // Structural application name token
            if (!hasAppTitle)
            {
                patternBuilder.Append(Regex.Escape(token));
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
}
