namespace EmulatorAutoUpdater.Models;

public sealed class AppSettings
{
    public const string CurrentAppVersion = "1.2.0";
    public const string AppUpdateRepository = "akuma815/Emulator-Auto-Updater";
    public const string DefaultAssetPatternValue = @"(?i)(win|windows).*(x64|amd64).*\.(zip|7z)$";

    public bool CheckAllUpdatesOnStartup { get; set; } = true;
    public bool UseVersionSubfolders { get; set; } = false;
    public string LanguageCode { get; set; } = "ko-KR";
    public string DefaultAssetPattern { get; set; } = DefaultAssetPatternValue;
    public WindowPlacementSettings? WindowPlacement { get; set; }
    public List<double> EmulatorGridColumnWidths { get; set; } = [];
    public List<EmulatorConfig> Emulators { get; set; } = [];
}
