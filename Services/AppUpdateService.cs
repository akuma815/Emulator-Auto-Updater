using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmulatorAutoUpdater.Models;

namespace EmulatorAutoUpdater.Services;

public sealed class AppUpdateService
{
    private readonly GitHubReleaseService _releaseService;

    public AppUpdateService(GitHubReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public sealed record AppUpdateCheckResult(
        bool IsUpdateAvailable,
        string CurrentVersion,
        string LatestVersion,
        string DownloadUrl,
        string ReleaseNotes);

    public async Task<AppUpdateCheckResult> CheckForAppUpdateAsync(CancellationToken cancellationToken = default)
    {
        var release = await _releaseService.GetLatestReleaseAsync(
            AppSettings.AppUpdateRepository,
            @"(?i)EmulatorAutoUpdater-.*\.zip$",
            cancellationToken);

        if (release == null || release.Assets.Count == 0)
        {
            return new AppUpdateCheckResult(false, AppSettings.CurrentAppVersion, AppSettings.CurrentAppVersion, string.Empty, string.Empty);
        }

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ?? release.Assets.First();
        var rawVersion = release.TagName?.Trim().TrimStart('v', 'V') ?? string.Empty;

        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            rawVersion = GitHubReleaseService.ResolveVersion(release.TagName, asset.Name, release.PublishedAt);
        }

        var isNewer = IsNewerVersion(AppSettings.CurrentAppVersion, rawVersion);

        return new AppUpdateCheckResult(
            IsUpdateAvailable: isNewer,
            CurrentVersion: AppSettings.CurrentAppVersion,
            LatestVersion: rawVersion,
            DownloadUrl: asset.BrowserDownloadUrl,
            ReleaseNotes: release.Body);
    }

    public static bool IsNewerVersion(string currentVersion, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(targetVersion) || string.IsNullOrWhiteSpace(currentVersion))
        {
            return false;
        }

        var curClean = currentVersion.Trim().TrimStart('v', 'V');
        var tgtClean = targetVersion.Trim().TrimStart('v', 'V');

        if (string.Equals(curClean, tgtClean, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Version.TryParse(curClean, out var curVer) && Version.TryParse(tgtClean, out var tgtVer))
        {
            return curVer < tgtVer;
        }

        return false;
    }

    public static string GetUpdateTempDirectory()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EmulatorAutoUpdater_AppUpdate");
        Directory.CreateDirectory(temp);
        return temp;
    }

    public static void CleanupTempUpdateFiles()
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "EmulatorAutoUpdater_AppUpdate");
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
        catch { }
    }

    public static string CreateUpdaterBatchScript(int processId, string zipFilePath, string appDirectory, string currentExePath)
    {
        var tempDir = GetUpdateTempDirectory();
        var extractStageDir = Path.Combine(tempDir, "extracted");
        var batPath = Path.Combine(tempDir, "update_app.bat");
        var logPath = Path.Combine(tempDir, "updater.log");

        var script = $@"@echo off
chcp 65001 > NUL
title Emulator Auto Updater Self-Updater
set LOGFILE=""{logPath.Replace("'", "''")}""

echo [%date% %time%] [Self-Updater] Starting update process... > %LOGFILE%
echo [%date% %time%] Target directory: '{appDirectory}' >> %LOGFILE%
echo [%date% %time%] Zip file path: '{zipFilePath}' >> %LOGFILE%
echo [%date% %time%] Waiting for PID {processId} to exit... >> %LOGFILE%

timeout /t 2 /nobreak > NUL

:wait_loop
tasklist /FI ""PID eq {processId}"" 2>NUL | find /I ""{processId}"" >NUL
if %ERRORLEVEL%==0 (
    timeout /t 1 /nobreak > NUL
    goto wait_loop
)

timeout /t 2 /nobreak > NUL
echo [%date% %time%] PID {processId} exited. >> %LOGFILE%

echo [%date% %time%] Extracting update zip... >> %LOGFILE%
if exist ""{extractStageDir.Replace("'", "''")}"" rmdir /s /q ""{extractStageDir.Replace("'", "''")}""
powershell -NoProfile -ExecutionPolicy Bypass -Command ""Expand-Archive -Path '{zipFilePath.Replace("'", "''")}' -DestinationPath '{extractStageDir.Replace("'", "''")}' -Force"" >> %LOGFILE% 2>&1

if exist ""{appDirectory.Replace("'", "''")}\config.json"" (
    if exist ""{extractStageDir.Replace("'", "''")}\config.json"" (
        echo [%date% %time%] Preserving user config.json >> %LOGFILE%
        del /f /q ""{extractStageDir.Replace("'", "''")}\config.json"" 2>NUL
    )
)

echo [%date% %time%] Renaming old executable... >> %LOGFILE%
if exist ""{appDirectory.Replace("'", "''")}\EmulatorAutoUpdater.exe.old"" del /f /q ""{appDirectory.Replace("'", "''")}\EmulatorAutoUpdater.exe.old"" 2>NUL
if exist ""{appDirectory.Replace("'", "''")}\EmulatorAutoUpdater.exe"" move /y ""{appDirectory.Replace("'", "''")}\EmulatorAutoUpdater.exe"" ""{appDirectory.Replace("'", "''")}\EmulatorAutoUpdater.exe.old"" >> %LOGFILE% 2>&1

echo [%date% %time%] Copying new binary files... >> %LOGFILE%
xcopy /e /y /i ""{extractStageDir.Replace("'", "''")}\*"" ""{appDirectory.Replace("'", "''")}\"" >> %LOGFILE% 2>&1

echo [%date% %time%] Restarting application... >> %LOGFILE%
start """" ""{currentExePath.Replace("'", "''")}""

timeout /t 2 /nobreak > NUL
if exist ""{appDirectory.Replace("'", "''")}\EmulatorAutoUpdater.exe.old"" del /f /q ""{appDirectory.Replace("'", "''")}\EmulatorAutoUpdater.exe.old"" 2>NUL
echo [%date% %time%] Update complete! >> %LOGFILE%
exit
";

        File.WriteAllText(batPath, script, System.Text.Encoding.UTF8);
        return batPath;
    }
}
