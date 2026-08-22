using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using EmulatorAutoUpdater.Models;
using EmulatorAutoUpdater.Services;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace EmulatorAutoUpdater.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly GitHubReleaseService _releaseService = new();
    private readonly Func<string?> _openFolderPicker;
    private readonly Dictionary<string, EmulatorUpdateHistory> _updateHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmulatorTransferState> _transferStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeUpdateChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _settingsWriteLock = new(1, 1);
    private string? _customConfigFilePath;
    private string _activityLogText = string.Empty;
    private string _statusMessage = "설정을 불러오거나 에뮬레이터를 추가하세요.";
    private string _checkAllUpdatesStatusMessage = string.Empty;
    private int _progress;
    private bool _isBusy;
    private bool _checkAllUpdatesOnStartup = true;
    private bool _useVersionSubfolders;
    private string _defaultAssetPattern = AppSettings.DefaultAssetPatternValue;
    private bool _isCheckingAllUpdates;
    private CancellationTokenSource? _checkAllUpdatesCancellation;
    private bool _isDownloadingAllUpdates;
    private CancellationTokenSource? _downloadAllUpdatesCancellation;
    private EmulatorConfig? _selectedEmulator;
    private BuildAsset? _selectedAsset;
    private string _selectedReleaseVersion = string.Empty;
    private string _releaseNotes = string.Empty;

    private readonly AppUpdateService _appUpdateService;
    private bool _isAppUpdateAvailable;
    private string _latestAppVersion = string.Empty;
    private string _appUpdateDownloadUrl = string.Empty;
    private string _appUpdateBannerText = string.Empty;

    public MainWindowViewModel(Func<string?> openFolderPicker)
    {
        _openFolderPicker = openFolderPicker ?? throw new ArgumentNullException(nameof(openFolderPicker));
        _appUpdateService = new AppUpdateService(_releaseService);
        AppUpdateService.CleanupTempUpdateFiles();

        AddEmulatorCommand = new RelayCommand(_ => AddEmulator(), _ => !IsBusy);
        RemoveEmulatorCommand = new RelayCommand(_ => RemoveEmulator(), _ => SelectedEmulator != null && !IsBusy && !IsEmulatorDownloading(SelectedEmulator) && !IsEmulatorChecking(SelectedEmulator));
        LoadSettingsCommand = new RelayCommand(async _ => await LoadSettingsAsync(), _ => !IsBusy && _activeDownloads.Count == 0);
        SaveSettingsCommand = new RelayCommand(async _ => await SaveSettingsAsync(), _ => !IsBusy);
        OpenConfigFileCommand = new RelayCommand(async _ => await OpenConfigFileAsync(), _ => !IsBusy && _activeDownloads.Count == 0);
        SaveConfigFileAsCommand = new RelayCommand(async _ => await SaveConfigFileAsAsync(), _ => !IsBusy);
        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder(), _ => SelectedEmulator != null && !IsBusy && !IsEmulatorDownloading(SelectedEmulator) && !IsEmulatorChecking(SelectedEmulator));
        CheckUpdatesCommand = new RelayCommand(async _ => await CheckUpdatesAsync(), _ => SelectedEmulator != null && !IsBusy && !IsEmulatorChecking(SelectedEmulator));
        CheckAllUpdatesCommand = new RelayCommand(
            async _ => await CheckAllUpdatesAsync(),
            _ => IsCheckingAllUpdates || (Emulators.Count > 0 && !IsBusy));
        DownloadAllUpdatesCommand = new RelayCommand(
            async _ => await DownloadAllUpdatesAsync(),
            _ => IsDownloadingAllUpdates || (Emulators.Count > 0 && !IsBusy));
        ConvertDefaultAssetPatternCommand = new RelayCommand(_ => ConvertDefaultAssetPattern());
        ConvertSelectedAssetPatternCommand = new RelayCommand(_ => ConvertSelectedAssetPattern(), _ => SelectedEmulator != null && !IsBusy);
        CopySelectedAssetNameCommand = new RelayCommand(_ => CopySelectedAssetName());
        ApplySelectedAssetToPatternCommand = new RelayCommand(_ => ApplySelectedAssetToPattern(), _ => SelectedEmulator != null && !IsBusy);
        ExcludeSelectedAssetFromPatternCommand = new RelayCommand(_ => ExcludeSelectedAssetFromPattern(), _ => SelectedEmulator != null && !IsBusy);
        DownloadOnlyCommand = new RelayCommand(async _ => await DownloadOnlyConcurrentAsync(), _ => CanDownloadSelectedAsset());
        DownloadUpdateCommand = new RelayCommand(async _ => await DownloadUpdateConcurrentAsync(), _ => CanDownloadSelectedAsset());
        PerformAppSelfUpdateCommand = new RelayCommand(async _ => await PerformAppSelfUpdateAsync(), _ => IsAppUpdateAvailable && !IsBusy);
        ClearLogCommand = new RelayCommand(_ => ActivityLogText = string.Empty);
    }

    public ObservableCollection<EmulatorConfig> Emulators { get; } = new();
    public ObservableCollection<BuildAsset> ReleaseAssets { get; } = new();

    public RelayCommand AddEmulatorCommand { get; }
    public RelayCommand RemoveEmulatorCommand { get; }
    public RelayCommand LoadSettingsCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand OpenConfigFileCommand { get; }
    public RelayCommand SaveConfigFileAsCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand CheckAllUpdatesCommand { get; }
    public RelayCommand DownloadAllUpdatesCommand { get; }
    public RelayCommand ConvertDefaultAssetPatternCommand { get; }
    public RelayCommand ConvertSelectedAssetPatternCommand { get; }
    public RelayCommand CopySelectedAssetNameCommand { get; }
    public RelayCommand ApplySelectedAssetToPatternCommand { get; }
    public RelayCommand ExcludeSelectedAssetFromPatternCommand { get; }
    public RelayCommand DownloadOnlyCommand { get; }
    public RelayCommand DownloadUpdateCommand { get; }
    public RelayCommand PerformAppSelfUpdateCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public string WindowTitle => $"Emulator Auto Updater v{AppSettings.CurrentAppVersion}";

    public bool IsAppUpdateAvailable
    {
        get => _isAppUpdateAvailable;
        set
        {
            if (SetProperty(ref _isAppUpdateAvailable, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string LatestAppVersion
    {
        get => _latestAppVersion;
        set => SetProperty(ref _latestAppVersion, value);
    }

    public string AppUpdateDownloadUrl
    {
        get => _appUpdateDownloadUrl;
        set => SetProperty(ref _appUpdateDownloadUrl, value);
    }

    public string AppUpdateBannerText
    {
        get => _appUpdateBannerText;
        set => SetProperty(ref _appUpdateBannerText, value);
    }

    public string ActivityLogText
    {
        get => _activityLogText;
        set => SetProperty(ref _activityLogText, value);
    }

    public void AppendLog(string message)
    {
        var timeStamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{timeStamp}] {message}";
        if (string.IsNullOrWhiteSpace(ActivityLogText))
        {
            ActivityLogText = line;
        }
        else
        {
            ActivityLogText += Environment.NewLine + line;
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value) && !string.IsNullOrWhiteSpace(value))
            {
                AppendLog(value);
            }
        }
    }

    public string CheckAllUpdatesStatusMessage
    {
        get => _checkAllUpdatesStatusMessage;
        set => SetProperty(ref _checkAllUpdatesStatusMessage, value);
    }

    public int Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public bool CheckAllUpdatesOnStartup
    {
        get => _checkAllUpdatesOnStartup;
        set => SetProperty(ref _checkAllUpdatesOnStartup, value);
    }

    public bool UseVersionSubfolders
    {
        get => _useVersionSubfolders;
        set => SetProperty(ref _useVersionSubfolders, value);
    }

    public IReadOnlyList<LanguageOption> SupportedLanguages => LocalizationService.SupportedLanguages;

    public string SelectedLanguageCode
    {
        get => LocalizationService.CurrentLanguageCode;
        set
        {
            if (LocalizationService.CurrentLanguageCode != value)
            {
                LocalizationService.SetLanguage(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLanguageCode)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckAllUpdatesButtonText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadAllUpdatesButtonText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentConfigFilePath)));
                
                foreach (var emu in Emulators)
                {
                    if (emu.StatusType == "Unknown")
                    {
                        emu.StatusText = LocalizationService.GetString("StatusUnchecked", "미확인");
                    }
                    else if (emu.StatusType == "UpToDate")
                    {
                        emu.StatusText = LocalizationService.GetString("StatusUpToDateRoot", "최신 (루트폴더)");
                    }
                    else if (emu.StatusType == "UpdateAvailable")
                    {
                        emu.StatusText = LocalizationService.GetString("StatusUpdateAvailable", emu.LatestVersion);
                    }
                    else if (emu.StatusType == "CheckFailed")
                    {
                        emu.StatusText = LocalizationService.GetString("StatusCheckFailed", "조회 실패");
                    }
                }

                _ = SaveSettingsToDiskAsync();
            }
        }
    }

    public string DefaultAssetPattern
    {
        get => _defaultAssetPattern;
        set => SetProperty(ref _defaultAssetPattern, value);
    }

    public WindowPlacementSettings? WindowPlacement { get; private set; }

    public IReadOnlyList<double> EmulatorGridColumnWidths { get; private set; } = [];

    public string CurrentConfigFilePath => $"{LocalizationService.GetString("LblCurrentConfigFile", "설정 파일:")} {GetConfigFilePath()}";

    public bool IsCheckingAllUpdates
    {
        get => _isCheckingAllUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingAllUpdates, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckAllUpdatesButtonText)));
                UpdateCommandStates();
            }
        }
    }

    public string CheckAllUpdatesButtonText => IsCheckingAllUpdates
        ? LocalizationService.GetString("BtnCancelCheck", "확인 취소")
        : LocalizationService.GetString("BtnCheckAllUpdates", "전체 업데이트 확인");

    public bool IsDownloadingAllUpdates
    {
        get => _isDownloadingAllUpdates;
        private set
        {
            if (SetProperty(ref _isDownloadingAllUpdates, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadAllUpdatesButtonText)));
                UpdateCommandStates();
            }
        }
    }

    public string DownloadAllUpdatesButtonText => IsDownloadingAllUpdates
        ? LocalizationService.GetString("BtnCancelDownload", "다운로드 취소")
        : LocalizationService.GetString("BtnDownloadAllUpdates", "전체 다운로드");

    public EmulatorConfig? SelectedEmulator
    {
        get => _selectedEmulator;
        set
        {
            var previousEmulator = _selectedEmulator;
            if (SetProperty(ref _selectedEmulator, value))
            {
                if (previousEmulator != null)
                {
                    previousEmulator.PropertyChanged -= SelectedEmulatorOnPropertyChanged;
                }

                if (value != null)
                {
                    value.PropertyChanged += SelectedEmulatorOnPropertyChanged;
                }

                ReleaseAssets.Clear();
                SelectedAsset = null;
                SelectedReleaseVersion = string.Empty;
                ReleaseNotes = string.Empty;
                RestoreUpdateHistory(value);
                RestoreTransferState(value);
                UpdateCommandStates();
            }
        }
    }

    public BuildAsset? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (SetProperty(ref _selectedAsset, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string SelectedReleaseVersion
    {
        get => _selectedReleaseVersion;
        set => SetProperty(ref _selectedReleaseVersion, value);
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        set => SetProperty(ref _releaseNotes, value);
    }

    public async Task LoadSettingsAsync(string? targetFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(targetFilePath))
        {
            _customConfigFilePath = targetFilePath;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentConfigFilePath)));
        }

        await ExecuteBusyOperationAsync(async () =>
        {
            CleanupLegacyAppDataConfig();

            var path = GetConfigFilePath();
            if (!File.Exists(path))
            {
                StatusMessage = $"설정 파일이 없습니다: {path}";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentConfigFilePath)));
                return;
            }

            var json = await File.ReadAllTextAsync(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            CheckAllUpdatesOnStartup = settings?.CheckAllUpdatesOnStartup ?? true;
            UseVersionSubfolders = settings?.UseVersionSubfolders ?? false;
            DefaultAssetPattern = settings?.DefaultAssetPattern ?? AppSettings.DefaultAssetPatternValue;
            WindowPlacement = settings?.WindowPlacement;
            EmulatorGridColumnWidths = settings?.EmulatorGridColumnWidths ?? [];

            LocalizationService.Initialize(settings?.LanguageCode);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLanguageCode)));

            Emulators.Clear();
            _updateHistory.Clear();
            _transferStates.Clear();
            _activeDownloads.Clear();
            foreach (var emulator in settings?.Emulators ?? [])
            {
                if (string.IsNullOrWhiteSpace(emulator.Id))
                {
                    emulator.Id = Guid.NewGuid().ToString("N");
                }

                // Initial status on load is Unchecked (미확인)
                emulator.StatusText = LocalizationService.GetString("StatusUnchecked", "미확인");
                emulator.StatusType = "Unknown";
                emulator.LatestVersion = string.Empty;

                Emulators.Add(emulator);
            }

            StatusMessage = LocalizationService.GetString("LogConfigLoaded", Path.GetFileName(path));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentConfigFilePath)));
        }, LocalizationService.GetString("LogLoadingConfig", "설정을 불러오는 중입니다..."));
    }

    public async Task SaveSettingsAsync(string? targetFilePath = null)
    {
        await ExecuteBusyOperationAsync(async () =>
        {
            await SaveSettingsToDiskAsync(targetFilePath);
            StatusMessage = LocalizationService.GetString("LogConfigSaved", Path.GetFileName(CurrentConfigFilePath));
        }, LocalizationService.GetString("LogSavingConfig", "설정을 저장하는 중입니다..."));
    }

    public async Task OpenConfigFileAsync()
    {
        var currentPath = CurrentConfigFilePath;
        var initialDir = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
        {
            initialDir = AppContext.BaseDirectory;
        }

        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "설정 파일 열기",
            Filter = "JSON 설정 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
            InitialDirectory = initialDir,
            CheckFileExists = true
        };

        if (openFileDialog.ShowDialog() == true)
        {
            await LoadSettingsAsync(openFileDialog.FileName);
        }
    }

    public async Task SaveConfigFileAsAsync()
    {
        var currentPath = CurrentConfigFilePath;
        var initialDir = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
        {
            initialDir = AppContext.BaseDirectory;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "다른 이름으로 설정 저장",
            Filter = "JSON 설정 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
            DefaultExt = "json",
            InitialDirectory = initialDir,
            FileName = Path.GetFileName(currentPath)
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            await SaveSettingsAsync(saveFileDialog.FileName);
        }
    }

    public void ConvertDefaultAssetPattern()
    {
        if (string.IsNullOrWhiteSpace(DefaultAssetPattern))
        {
            StatusMessage = "기본 Asset Pattern 입력란에 예시 파일명(예: ppsspp_win_x64_v1.18.0.zip)을 입력하세요.";
            return;
        }

        var converted = AssetPatternHelper.ConvertFilenameToAssetPattern(DefaultAssetPattern);
        DefaultAssetPattern = converted;
        StatusMessage = $"기본 Asset Pattern이 추천 정규식 패턴('{converted}')으로 변환되었습니다.";
    }

    public void ConvertSelectedAssetPattern()
    {
        if (SelectedEmulator == null)
        {
            StatusMessage = "패턴을 변환할 에뮬레이터를 먼저 선택하세요.";
            return;
        }

        string sourceName = string.Empty;

        // Priority 1: User's explicitly selected asset in the bottom asset list
        if (SelectedAsset != null && !string.IsNullOrWhiteSpace(SelectedAsset.AssetName))
        {
            sourceName = SelectedAsset.AssetName;
        }
        // Priority 2: Text inside AssetPattern if it is a raw filename (not yet converted)
        else if (!string.IsNullOrWhiteSpace(SelectedEmulator.AssetPattern) &&
                 SelectedEmulator.AssetPattern != AppSettings.DefaultAssetPatternValue &&
                 !SelectedEmulator.AssetPattern.StartsWith("(?i)"))
        {
            sourceName = SelectedEmulator.AssetPattern;
        }
        // Priority 3: Fallback to the first asset in ReleaseAssets
        else if (ReleaseAssets.Count > 0 && !string.IsNullOrWhiteSpace(ReleaseAssets.First().AssetName))
        {
            sourceName = ReleaseAssets.First().AssetName;
        }
        // Priority 4: Fallback to AssetPattern
        else if (!string.IsNullOrWhiteSpace(SelectedEmulator.AssetPattern))
        {
            sourceName = SelectedEmulator.AssetPattern;
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            StatusMessage = "Asset Pattern 입력란에 예시 파일명(예: PPSSPPWindows64-v1.17.1.zip)을 적거나, 하단 파일 목록에서 변환할 항목을 먼저 선택하세요.";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(sourceName);
        }
        catch { }

        var converted = AssetPatternHelper.ConvertFilenameToAssetPattern(sourceName);
        SelectedEmulator.AssetPattern = converted;
        StatusMessage = $"선택한 파일명('{sourceName}')을 추천 정규식 패턴('{converted}')으로 변환했습니다.";
    }

    public void CopySelectedAssetName()
    {
        var targetAsset = SelectedAsset ?? ReleaseAssets.FirstOrDefault();
        if (targetAsset == null || string.IsNullOrWhiteSpace(targetAsset.AssetName))
        {
            StatusMessage = "복사할 파일명이 없습니다. 먼저 업데이트 조회를 수행하세요.";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(targetAsset.AssetName);
            StatusMessage = $"[클립보드 복사 완료] 파일명: '{targetAsset.AssetName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"클립보드 복사 중 오류 발생: {ex.Message}";
        }
    }

    public void ApplySelectedAssetToPattern()
    {
        var targetAsset = SelectedAsset ?? ReleaseAssets.FirstOrDefault();
        if (targetAsset == null || string.IsNullOrWhiteSpace(targetAsset.AssetName))
        {
            StatusMessage = "패턴으로 반영할 파일명이 없습니다. 먼저 업데이트 조회를 수행하세요.";
            return;
        }

        if (SelectedEmulator == null)
        {
            StatusMessage = "패턴을 변경할 에뮬레이터를 먼저 선택하세요.";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(targetAsset.AssetName);
        }
        catch { }

        var converted = AssetPatternHelper.ConvertFilenameToAssetPattern(targetAsset.AssetName);
        SelectedEmulator.AssetPattern = converted;
        StatusMessage = $"선택한 파일명('{targetAsset.AssetName}')으로 추천 정규식 패턴('{converted}')이 생성되었습니다.";
    }

    public void ExcludeSelectedAssetFromPattern()
    {
        if (SelectedEmulator == null)
        {
            StatusMessage = "패턴을 변경할 에뮬레이터를 먼저 선택하세요.";
            return;
        }

        var targetAsset = SelectedAsset;
        if (targetAsset == null || string.IsNullOrWhiteSpace(targetAsset.AssetName))
        {
            StatusMessage = "패턴에서 제외할 파일 항목을 하단 목록에서 선택하세요.";
            return;
        }

        var currentPattern = SelectedEmulator.AssetPattern;
        var updatedPattern = AssetPatternHelper.BuildExclusionAssetPattern(currentPattern, targetAsset.AssetName);

        SelectedEmulator.AssetPattern = updatedPattern;
        StatusMessage = $"선택한 파일명('{targetAsset.AssetName}')을 제외하도록 패턴이 갱신되었습니다: '{updatedPattern}'";
    }

    public async Task CheckAppSelfUpdateAsync()
    {
        try
        {
            var updateCheck = await _appUpdateService.CheckForAppUpdateAsync(CancellationToken.None);
            if (updateCheck.IsUpdateAvailable)
            {
                LatestAppVersion = updateCheck.LatestVersion;
                AppUpdateDownloadUrl = updateCheck.DownloadUrl;
                IsAppUpdateAvailable = true;
                AppUpdateBannerText = $"⚡ 프로그램 신규 업데이트가 있습니다 (v{updateCheck.LatestVersion})! [프로그램 업데이트] 버튼을 눌러 자동 갱신하세요.";
                AppendLog($"[프로그램 업데이트] 새 버전(v{updateCheck.LatestVersion})이 출시되었습니다. 상단 버튼을 클릭해 최신 버전으로 갱신하세요.");
            }
            else
            {
                IsAppUpdateAvailable = false;
            }
        }
        catch
        {
            // Ignore failure during background self-update check
        }
    }

    public async Task PerformAppSelfUpdateAsync()
    {
        if (!IsAppUpdateAvailable || string.IsNullOrWhiteSpace(AppUpdateDownloadUrl))
        {
            StatusMessage = "업데이트할 최신 프로그램 아티팩트 URL을 찾을 수 없습니다.";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Emulator Auto Updater 최신 버전(v{LatestAppVersion})으로 업데이트를 진행하시겠습니까?\n\n업데이트 파일 다운로드 후 프로그램이 자동 종료되고 새 버전으로 재기동됩니다.",
            "프로그램 자동 업데이트",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            StatusMessage = $"프로그램 업데이트 다운로드 중 (v{LatestAppVersion})...";
            Progress = 15;

            var currentProcess = Process.GetCurrentProcess();
            var currentExePath = Environment.ProcessPath ?? currentProcess.MainModule?.FileName ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EmulatorAutoUpdater.exe");
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            var tempDir = AppUpdateService.GetUpdateTempDirectory(appDir);
            var zipPath = Path.Combine(tempDir, "update.zip");

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
                var bytes = await httpClient.GetByteArrayAsync(AppUpdateDownloadUrl);
                await File.WriteAllBytesAsync(zipPath, bytes);
            }

            Progress = 85;
            StatusMessage = "업데이트 파일 준비 완료. 프로그램을 자동 재기동합니다...";

            var batPath = AppUpdateService.CreateUpdaterBatchScript(currentProcess.Id, zipPath, appDir, currentExePath);

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = tempDir
            };

            Process.Start(psi);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            var friendlyMsg = FriendlyExceptionHelper.FormatUserFriendlyErrorMessage(ex, "프로그램 자동 업데이트");
            StatusMessage = $"프로그램 업데이트 실패: {ex.Message}";
            AppendLog($"❌ [오류/Exception] 프로그램 자동 업데이트 중 실패: {ex.Message}");
            System.Windows.MessageBox.Show(friendlyMsg, "업데이트 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void UpdateWindowPlacement(
        double left,
        double top,
        double width,
        double height,
        bool isMaximized)
    {
        if (!double.IsFinite(left) ||
            !double.IsFinite(top) ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            return;
        }

        WindowPlacement = new WindowPlacementSettings
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            IsMaximized = isMaximized
        };
    }

    public void UpdateEmulatorGridColumnWidths(IEnumerable<double> widths)
    {
        EmulatorGridColumnWidths = widths
            .Where(width => double.IsFinite(width) && width > 0)
            .ToList();
    }

    public async Task SaveSettingsForShutdownAsync()
    {
        try
        {
            await SaveSettingsToDiskAsync();
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex);
        }
    }

    public async Task CheckUpdatesAsync()
    {
        var emulator = SelectedEmulator;
        if (emulator == null)
        {
            StatusMessage = "확인할 에뮬레이터를 먼저 선택하세요.";
            return;
        }

        lock (_activeUpdateChecks)
        {
            if (!_activeUpdateChecks.Add(emulator.Id))
            {
                return;
            }
        }

        UpdateCommandStates();

        if (ReferenceEquals(SelectedEmulator, emulator))
        {
            StatusMessage = $"'{emulator.Name}'의 업데이트 정보를 확인하는 중입니다...";
            Progress = 0;
        }

        emulator.StatusText = "확인 중...";
        emulator.StatusType = "Checking";

        try
        {
            if (ReferenceEquals(SelectedEmulator, emulator))
            {
                ReleaseAssets.Clear();
                SelectedAsset = null;
                SelectedReleaseVersion = string.Empty;
                ReleaseNotes = string.Empty;
            }

            var release = await _releaseService.GetLatestReleaseAsync(emulator.Repository, emulator.AssetPattern, CancellationToken.None);
            if (release == null)
            {
                emulator.StatusText = "조회 실패";
                emulator.StatusType = "CheckFailed";
                if (ReferenceEquals(SelectedEmulator, emulator))
                {
                    StatusMessage = "릴리즈를 가져오지 못했습니다. Repository 또는 URL을 확인하세요.";
                }
                return;
            }

            var foundAssets = _releaseService.FindAssets(release, emulator.AssetPattern);
            if (foundAssets.Count == 0)
            {
                emulator.StatusText = "조건 맞는 파일 없음";
                emulator.StatusType = "CheckFailed";
                if (ReferenceEquals(SelectedEmulator, emulator))
                {
                    StatusMessage = "지정한 AssetPattern과 맞는 파일을 찾지 못했습니다.";
                }
                return;
            }

            var latestVersion = foundAssets.First().Version;
            emulator.LatestVersion = latestVersion;

            var notesHeader = string.IsNullOrWhiteSpace(release.FetchSource)
                ? string.Empty
                : $"[조회 방식: {release.FetchSource}]\n\n";

            _updateHistory[emulator.Id] = new EmulatorUpdateHistory(
                foundAssets.ToList(),
                latestVersion,
                notesHeader + release.Body);

            AppendLog($"[{emulator.Name}] 최신 릴리즈 확인 완료: v{latestVersion} (방식: {release.FetchSource})");

            var dualStatus = EvaluateDualFolderStatus(emulator, foundAssets.First());
            if (dualStatus.IsUpToDate)
            {
                emulator.StatusText = dualStatus.SummaryStatusText;
                emulator.StatusType = "UpToDate";
            }
            else
            {
                emulator.StatusText = $"업데이트 가능 ({latestVersion})";
                emulator.StatusType = "UpdateAvailable";
            }

            if (ReferenceEquals(SelectedEmulator, emulator))
            {
                RestoreUpdateHistory(emulator);
                StatusMessage = dualStatus.IsUpToDate
                    ? $"최신 버전 상태: {dualStatus.DetailedLogText}"
                    : $"'{latestVersion}' 버전을 찾았습니다. 다운로드 또는 압축 해제가 가능합니다.";
                Progress = 100;
            }
        }
        catch (Exception ex)
        {
            emulator.StatusText = "조회 실패";
            emulator.StatusType = "CheckFailed";
            await LogErrorAsync(ex);
            if (ReferenceEquals(SelectedEmulator, emulator))
            {
                StatusMessage = $"오류: {ex.Message}";
            }
        }
        finally
        {
            lock (_activeUpdateChecks)
            {
                _activeUpdateChecks.Remove(emulator.Id);
            }
            UpdateCommandStates();
        }
    }

    public async Task CheckAllUpdatesAsync()
    {
        if (IsCheckingAllUpdates)
        {
            CheckAllUpdatesStatusMessage = "전체 업데이트 확인을 취소하는 중입니다...";
            StatusMessage = "전체 업데이트 확인을 취소하는 중입니다...";
            _checkAllUpdatesCancellation?.Cancel();
            return;
        }

        if (Emulators.Count == 0)
        {
            CheckAllUpdatesStatusMessage = "등록된 에뮬레이터가 없습니다.";
            StatusMessage = "등록된 에뮬레이터가 없습니다.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _checkAllUpdatesCancellation = cancellation;
        IsCheckingAllUpdates = true;
        IsBusy = true;
        Progress = 0;

        try
        {
            var emulators = Emulators.ToList();
            var results = new (string summaryText, EmulatorUpdateHistory? history)[emulators.Count];
            var completedCount = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 10,
                CancellationToken = cancellation.Token
            };

            await Parallel.ForEachAsync(
                Enumerable.Range(0, emulators.Count),
                parallelOptions,
                async (index, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var emulator = emulators[index];

                    emulator.StatusText = "확인 중...";
                    emulator.StatusType = "Checking";

                    var release = await _releaseService.GetLatestReleaseAsync(
                        emulator.Repository,
                        emulator.AssetPattern,
                        ct);
                    ct.ThrowIfCancellationRequested();

                    if (release == null)
                    {
                        emulator.StatusText = "조회 실패";
                        emulator.StatusType = "CheckFailed";
                        results[index] = ($"{emulator.Name}: 릴리즈 조회 실패", null);
                    }
                    else
                    {
                        var foundAssets = _releaseService.FindAssets(release, emulator.AssetPattern);
                        if (foundAssets.Count == 0)
                        {
                            emulator.StatusText = "조건 맞는 파일 없음";
                            emulator.StatusType = "CheckFailed";
                            results[index] = ($"{emulator.Name}: 조건에 맞는 파일 없음 ({release.FetchSource})", null);
                        }
                        else
                        {
                            var latestVersion = foundAssets.First().Version;
                            emulator.LatestVersion = latestVersion;

                            var notesHeader = string.IsNullOrWhiteSpace(release.FetchSource)
                                ? string.Empty
                                : $"[조회 방식: {release.FetchSource}]\n\n";

                            var history = new EmulatorUpdateHistory(
                                foundAssets.ToList(),
                                latestVersion,
                                notesHeader + release.Body);

                            var dualStatus = EvaluateDualFolderStatus(emulator, foundAssets.First());
                            string summaryMsg;
                            if (dualStatus.IsUpToDate)
                            {
                                emulator.StatusText = dualStatus.SummaryStatusText;
                                emulator.StatusType = "UpToDate";
                                summaryMsg = $"{emulator.Name}: {dualStatus.DetailedLogText} (방식: {release.FetchSource})";
                            }
                            else
                            {
                                emulator.StatusText = $"업데이트 가능 ({latestVersion})";
                                emulator.StatusType = "UpdateAvailable";
                                summaryMsg = $"{emulator.Name}: 업데이트 가능 ({latestVersion}) (방식: {release.FetchSource})";
                            }

                            results[index] = (summaryMsg, history);
                            if (history != null)
                            {
                                lock (_updateHistory)
                                {
                                    _updateHistory[emulator.Id] = history;
                                }

                                if (ReferenceEquals(SelectedEmulator, emulator))
                                {
                                    RestoreUpdateHistory(emulator);
                                }
                            }
                        }
                    }

                    var currentCompleted = Interlocked.Increment(ref completedCount);
                    var (resultText, _) = results[index];
                    AppendLog($"[{emulator.Name}] {resultText}");
                    StatusMessage = $"전체 업데이트 확인 중 ({currentCompleted}/{emulators.Count}): {emulator.Name}";
                    Progress = currentCompleted * 100 / emulators.Count;
                });

            var summary = new List<string>(emulators.Count);
            for (var index = 0; index < emulators.Count; index++)
            {
                var (summaryText, _) = results[index];
                summary.Add(summaryText);
            }

            RestoreUpdateHistory(SelectedEmulator);
            AppendLog(LocalizationService.GetString("LogCheckCompleted", emulators.Count));
            StatusMessage = LocalizationService.GetString("LogCheckCompleted", emulators.Count);
            Progress = 100;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            AppendLog(LocalizationService.GetString("LogCheckCancelled", "전체 업데이트 확인을 취소했습니다."));
            StatusMessage = LocalizationService.GetString("LogCheckCancelled", "전체 업데이트 확인을 취소했습니다.");
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex);
            AppendLog($"전체 업데이트 확인 오류: {ex.Message}");
            StatusMessage = $"오류: {ex.Message}";
        }
        finally
        {
            _checkAllUpdatesCancellation = null;
            IsCheckingAllUpdates = false;
            IsBusy = false;
        }
    }

    public async Task DownloadAllUpdatesAsync()
    {
        if (IsDownloadingAllUpdates)
        {
            CheckAllUpdatesStatusMessage = "전체 다운로드를 취소하는 중입니다...";
            StatusMessage = "전체 다운로드를 취소하는 중입니다...";
            _downloadAllUpdatesCancellation?.Cancel();
            return;
        }

        if (Emulators.Count == 0)
        {
            CheckAllUpdatesStatusMessage = "등록된 에뮬레이터가 없습니다.";
            StatusMessage = "등록된 에뮬레이터가 없습니다.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _downloadAllUpdatesCancellation = cancellation;
        IsDownloadingAllUpdates = true;
        IsBusy = true;
        Progress = 0;

        try
        {
            var emulators = Emulators.ToList();
            var step1Results = new (string summaryText, BuildAsset? asset, EmulatorUpdateHistory? history)[emulators.Count];
            var completedCheckCount = 0;

            CheckAllUpdatesStatusMessage = "전체 업데이트 정보 확인 중...";
            StatusMessage = "전체 업데이트 정보 확인 중...";

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 10,
                CancellationToken = cancellation.Token
            };

            await Parallel.ForEachAsync(
                Enumerable.Range(0, emulators.Count),
                parallelOptions,
                async (index, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var emulator = emulators[index];

                    BuildAsset? asset = null;
                    EmulatorUpdateHistory? history = null;
                    string summaryText;

                    try
                    {
                        var release = await _releaseService.GetLatestReleaseAsync(emulator.Repository, emulator.AssetPattern, ct);
                        ct.ThrowIfCancellationRequested();

                        if (release == null)
                        {
                            summaryText = $"{emulator.Name}: 릴리즈 조회 실패";
                        }
                        else
                        {
                            var foundAssets = _releaseService.FindAssets(release, emulator.AssetPattern);
                            if (foundAssets.Count == 0)
                            {
                                summaryText = $"{emulator.Name}: 조건에 맞는 파일 없음 ({release.FetchSource})";
                            }
                            else
                            {
                                asset = foundAssets.First();
                                var notesHeader = string.IsNullOrWhiteSpace(release.FetchSource)
                                    ? string.Empty
                                    : $"[조회 방식: {release.FetchSource}]\n\n";

                                history = new EmulatorUpdateHistory(
                                    foundAssets.ToList(),
                                    asset.Version,
                                    notesHeader + release.Body);
                                summaryText = $"{emulator.Name}: 다운로드 준비 ({asset.Version}) [{release.FetchSource}]";
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        summaryText = $"{emulator.Name}: 정보 확인 실패 ({ex.Message})";
                    }

                    step1Results[index] = (summaryText, asset, history);
                    if (history != null)
                    {
                        lock (_updateHistory)
                        {
                            _updateHistory[emulator.Id] = history;
                        }

                        if (ReferenceEquals(SelectedEmulator, emulator))
                        {
                            RestoreUpdateHistory(emulator);
                        }
                    }

                    var currentCompleted = Interlocked.Increment(ref completedCheckCount);
                    var statusMsg = $"[1/2단계] 전체 업데이트 정보 확인 중 ({currentCompleted}/{emulators.Count}): {emulator.Name}";
                    CheckAllUpdatesStatusMessage = statusMsg;
                    StatusMessage = statusMsg;
                    Progress = currentCompleted * 50 / emulators.Count;
                });

            RestoreUpdateHistory(SelectedEmulator);

            cancellation.Token.ThrowIfCancellationRequested();

            var downloadSummary = new string[emulators.Count];
            var completedDownloadCount = 0;

            var downloadParallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellation.Token
            };

            await Parallel.ForEachAsync(
                Enumerable.Range(0, emulators.Count),
                downloadParallelOptions,
                async (index, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var emulator = emulators[index];
                    var (checkSummaryText, asset, _) = step1Results[index];

                    if (asset == null)
                    {
                        downloadSummary[index] = checkSummaryText;
                    }
                    else if (EvaluateDualFolderStatus(emulator, asset) is { IsUpToDate: true } dualStatus)
                    {
                        emulator.StatusText = dualStatus.SummaryStatusText;
                        emulator.StatusType = "UpToDate";
                        AppendLog($"[{emulator.Name}] {dualStatus.DetailedLogText} 이 이미 준비되어 있어 다운로드를 건너땁니다.");
                        downloadSummary[index] = $"{emulator.Name}: 이미 최신 ({dualStatus.SummaryStatusText})";
                    }
                    else
                    {
                        try
                        {
                            await ExecuteEmulatorDownloadAsync(emulator, async () =>
                            {
                                ct.ThrowIfCancellationRequested();

                                var targetFolder = GetFinalTargetFolder(emulator, asset);
                                EnsureEnoughDiskSpace(targetFolder);
                                var assetFileName = GetSafeFileName(asset.AssetName, asset.DownloadUrl);
                                var downloadedFile = Path.Combine(targetFolder, assetFileName);

                                ReportTransfer(emulator, $"다운로드 중: {assetFileName}", 0);
                                await DownloadAssetForEmulatorAsync(emulator, asset.DownloadUrl, downloadedFile, ct);

                                emulator.LastDownloadedAt = DateTimeOffset.Now;
                                await SaveSettingsToDiskAsync();

                                var extension = Path.GetExtension(downloadedFile);
                                var extracted = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                                                extension.Equals(".7z", StringComparison.OrdinalIgnoreCase);

                                if (extracted)
                                {
                                    if (IsEmulatorProcessRunning(targetFolder, out var runningExe))
                                    {
                                        throw new InvalidOperationException($"에뮬레이터 프로세스({runningExe}.exe)가 실행 중입니다. 에뮬레이터를 종료한 후 다시 시도하세요.");
                                    }

                                    ReportTransfer(emulator, $"압축 해제 준비 중: {assetFileName}", 95);
                                    await ExtractArchiveForEmulatorAsync(
                                        emulator,
                                        downloadedFile,
                                        targetFolder,
                                        extension.Equals(".zip", StringComparison.OrdinalIgnoreCase));

                                    if (IsPpssppEmulator(emulator))
                                    {
                                        RestorePpssppJapaneseFont(targetFolder);
                                    }

                                    File.Delete(downloadedFile);
                                }

                                emulator.InstalledVersion = asset.Version;
                                var dualStatusBatch = EvaluateDualFolderStatus(emulator, asset);
                                emulator.StatusText = dualStatusBatch.SummaryStatusText;
                                emulator.StatusType = "UpToDate";
                                await SaveSettingsToDiskAsync();

                                ReportTransfer(
                                    emulator,
                                    extracted
                                        ? $"다운로드 및 압축 해제 완료: {targetFolder}"
                                        : $"다운로드 완료: {downloadedFile}",
                                    100);

                                downloadSummary[index] = $"{emulator.Name}: 다운로드 완료 ({asset.Version})";
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            await LogErrorAsync(ex);
                            downloadSummary[index] = $"{emulator.Name}: 다운로드 실패 ({ex.Message})";
                        }
                    }

                    var currentCompleted = Interlocked.Increment(ref completedDownloadCount);
                    AppendLog($"[{emulator.Name}] {downloadSummary[index]}");
                    StatusMessage = $"[2/2단계] 전체 다운로드 중 ({currentCompleted}/{emulators.Count}): {emulator.Name}";
                    Progress = 50 + (currentCompleted * 50 / emulators.Count);
                });

            AppendLog(LocalizationService.GetString("LogDownloadCompleted", "전체 다운로드 및 압축 해제 작업이 완료되었습니다."));
            StatusMessage = LocalizationService.GetString("LogDownloadCompleted", "전체 다운로드 및 압축 해제 작업이 완료되었습니다.");
            Progress = 100;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            AppendLog(LocalizationService.GetString("LogDownloadCancelled", "전체 다운로드를 취소했습니다."));
            StatusMessage = LocalizationService.GetString("LogDownloadCancelled", "전체 다운로드를 취소했습니다.");
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex);
            AppendLog($"전체 다운로드 오류: {ex.Message}");
            StatusMessage = $"오류: {ex.Message}";
        }
        finally
        {
            _downloadAllUpdatesCancellation = null;
            IsDownloadingAllUpdates = false;
            IsBusy = false;
        }
    }

    private async Task CheckAllUpdatesLegacyAsync()
    {
        if (Emulators.Count == 0)
        {
            StatusMessage = "설정된 에뮬레이터가 없습니다.";
            return;
        }

        await ExecuteBusyOperationAsync(async () =>
        {
            var summary = new List<string>();
            foreach (var emulator in Emulators)
            {
                var release = await _releaseService.GetLatestReleaseAsync(emulator.Repository, emulator.AssetPattern, CancellationToken.None);
                if (release == null)
                {
                    summary.Add($"{emulator.Name}: 릴리즈 조회 실패");
                    continue;
                }

                var foundAssets = _releaseService.FindAssets(release, emulator.AssetPattern);
                if (foundAssets.Count == 0)
                {
                    summary.Add($"{emulator.Name}: 패턴에 맞는 파일 없음");
                    continue;
                }

                var latestVersion = foundAssets.First().Version;
                _updateHistory[emulator.Id] = new EmulatorUpdateHistory(
                    foundAssets.ToList(),
                    latestVersion,
                    release.Body);
                summary.Add(IsUpToDate(emulator, foundAssets.First())
                    ? $"{emulator.Name}: 최신"
                    : $"{emulator.Name}: 업데이트 가능({latestVersion})");
            }

            RestoreUpdateHistory(SelectedEmulator);
            StatusMessage = string.Join(" | ", summary);
        }, "모든 에뮬레이터의 업데이트를 확인 중입니다...");
    }

    private async Task DownloadOnlyConcurrentAsync()
    {
        var emulator = SelectedEmulator;
        var selectedAsset = SelectedAsset;
        if (emulator == null)
        {
            return;
        }

        await ExecuteEmulatorDownloadAsync(emulator, async () =>
        {
            var asset = await ResolveAssetForEmulatorAsync(emulator, selectedAsset);
            var targetFolder = GetFinalTargetFolder(emulator, asset);
            var assetFileName = GetSafeFileName(asset.AssetName, asset.DownloadUrl);
            var downloadedFile = Path.Combine(targetFolder, assetFileName);

            ReportTransfer(emulator, $"다운로드 중: {assetFileName}", 0);
            await DownloadAssetForEmulatorAsync(emulator, asset.DownloadUrl, downloadedFile);

            emulator.LastDownloadedAt = DateTimeOffset.Now;
            emulator.InstalledVersion = asset.Version;
            var dualStatusOnly = EvaluateDualFolderStatus(emulator, asset);
            emulator.StatusText = dualStatusOnly.SummaryStatusText;
            emulator.StatusType = "UpToDate";
            await SaveSettingsToDiskAsync();
            ReportTransfer(emulator, $"다운로드 완료: {downloadedFile}", 100);
        });
    }

    private async Task DownloadUpdateConcurrentAsync()
    {
        var emulator = SelectedEmulator;
        var selectedAsset = SelectedAsset;
        if (emulator == null)
        {
            return;
        }

        await ExecuteEmulatorDownloadAsync(emulator, async () =>
        {
            var asset = await ResolveAssetForEmulatorAsync(emulator, selectedAsset);
            var targetFolder = GetFinalTargetFolder(emulator, asset);
            var assetFileName = GetSafeFileName(asset.AssetName, asset.DownloadUrl);
            var downloadedFile = Path.Combine(targetFolder, assetFileName);

            ReportTransfer(emulator, $"다운로드 중: {assetFileName}", 0);
            await DownloadAssetForEmulatorAsync(emulator, asset.DownloadUrl, downloadedFile);

            emulator.LastDownloadedAt = DateTimeOffset.Now;
            await SaveSettingsToDiskAsync();

            var extension = Path.GetExtension(downloadedFile);
            var extracted = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".7z", StringComparison.OrdinalIgnoreCase);

            if (extracted)
            {
                if (IsEmulatorProcessRunning(targetFolder, out var runningExe))
                {
                    throw new InvalidOperationException($"에뮬레이터 프로세스({runningExe}.exe)가 실행 중입니다. 에뮬레이터를 종료한 후 다시 시도하세요.");
                }

                ReportTransfer(emulator, $"압축 해제 준비 중: {assetFileName}", 95);
                await ExtractArchiveForEmulatorAsync(
                    emulator,
                    downloadedFile,
                    targetFolder,
                    extension.Equals(".zip", StringComparison.OrdinalIgnoreCase));

                if (IsPpssppEmulator(emulator))
                {
                    RestorePpssppJapaneseFont(targetFolder);
                }

                File.Delete(downloadedFile);
            }

            emulator.InstalledVersion = asset.Version;
            var dualStatus = EvaluateDualFolderStatus(emulator, asset);
            emulator.StatusText = dualStatus.SummaryStatusText;
            emulator.StatusType = "UpToDate";
            await SaveSettingsToDiskAsync();
            ReportTransfer(
                emulator,
                extracted
                    ? $"다운로드 및 압축 해제 완료: {targetFolder}"
                    : $"다운로드 완료: {downloadedFile}",
                100);
        });
    }

    private async Task ExecuteEmulatorDownloadAsync(EmulatorConfig emulator, Func<Task> operation)
    {
        lock (_activeDownloads)
        {
            if (!_activeDownloads.Add(emulator.Id))
            {
                return;
            }
        }

        emulator.StatusText = "다운로드 중...";
        emulator.StatusType = "Downloading";
        ReportTransfer(emulator, "다운로드를 시작합니다...", 0);
        UpdateCommandStates();

        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex);
            ReportTransfer(emulator, $"오류: {ex.Message}", 100);
        }
        finally
        {
            lock (_activeDownloads)
            {
                _activeDownloads.Remove(emulator.Id);
            }
            UpdateCommandStates();
        }
    }

    public void RefreshSelectedEmulatorStatus()
    {
        RunOnUIThread(() =>
        {
            if (SelectedEmulator != null)
            {
                RestoreUpdateHistory(SelectedEmulator);
                UpdateCommandStates();
            }
        });
    }

    private async Task<BuildAsset> ResolveAssetForEmulatorAsync(
        EmulatorConfig emulator,
        BuildAsset? preferredAsset)
    {
        if (preferredAsset != null)
        {
            return preferredAsset;
        }

        if (!GitHubReleaseService.IsDirectDownloadUrl(emulator.Repository))
        {
            throw new InvalidOperationException("다운로드할 파일을 먼저 선택하세요.");
        }

        var release = await _releaseService.GetLatestReleaseAsync(emulator.Repository, CancellationToken.None);
        var directAsset = release?.Assets.FirstOrDefault()
                          ?? throw new InvalidOperationException("직접 다운로드 링크에서 파일을 찾지 못했습니다.");

        var version = GitHubReleaseService.ResolveVersion(
            release?.TagName ?? release?.Name,
            directAsset.Name,
            release?.PublishedAt ?? DateTimeOffset.UtcNow);

        return new BuildAsset
        {
            Version = version,
            PublishedAt = release!.PublishedAt,
            AssetName = directAsset.Name,
            DownloadUrl = directAsset.BrowserDownloadUrl
        };
    }

    private async Task DownloadAssetForEmulatorAsync(
        EmulatorConfig emulator,
        string downloadUrl,
        string destinationFile,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            EnableMultipleHttp2Connections = true
        };

        using var httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");

        using var initialResponse = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        initialResponse.EnsureSuccessStatusCode();

        var contentType = initialResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("다운로드 실패: 서버에서 바이너리 파일 대신 HTML 웹 페이지를 반환했습니다.");
        }

        var finalUrl = initialResponse.RequestMessage?.RequestUri?.ToString() ?? downloadUrl;
        var totalBytes = initialResponse.Content.Headers.ContentLength ?? -1L;

        const long minBytesForParallel = 5 * 1024 * 1024; // 5 MB
        const int numChunks = 8;

        if (totalBytes >= minBytesForParallel)
        {
            try
            {
                using (var fs = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.SetLength(totalBytes);
                }

                var chunkSize = totalBytes / numChunks;
                long totalDownloaded = 0;
                var chunkTasks = new Task[numChunks];

                for (int i = 0; i < numChunks; i++)
                {
                    int chunkIndex = i;
                    long start = chunkIndex * chunkSize;
                    long end = (chunkIndex == numChunks - 1) ? totalBytes - 1 : (start + chunkSize - 1);

                    chunkTasks[chunkIndex] = Task.Run(async () =>
                    {
                        using var chunkReq = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                        chunkReq.Headers.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");
                        chunkReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                        using var chunkRes = await httpClient.SendAsync(chunkReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        chunkRes.EnsureSuccessStatusCode();

                        await using var stream = await chunkRes.Content.ReadAsStreamAsync(cancellationToken);
                        await using var fs = new FileStream(destinationFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1048576, useAsync: true);
                        fs.Seek(start, SeekOrigin.Begin);

                        var buffer = new byte[1048576];
                        int read;
                        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            var currentRead = Interlocked.Add(ref totalDownloaded, read);
                            ReportTransfer(emulator, null, (int)Math.Min(95, currentRead * 95 / totalBytes));
                        }
                    }, cancellationToken);
                }

                await Task.WhenAll(chunkTasks);
                VerifyDownloadedFileOrThrow(destinationFile);
                return;
            }
            catch
            {
            }
        }

        // Single stream download fallback
        await using var fileStream = new FileStream(
            destinationFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1048576,
            useAsync: true);

        await using var contentStream = await initialResponse.Content.ReadAsStreamAsync(cancellationToken);
        var singleBuffer = new byte[1048576];
        long totalRead = 0;
        int singleRead;

        while ((singleRead = await contentStream.ReadAsync(singleBuffer.AsMemory(0, singleBuffer.Length), cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(singleBuffer.AsMemory(0, singleRead), cancellationToken);
            totalRead += singleRead;

            if (totalBytes > 0)
            {
                ReportTransfer(emulator, null, (int)Math.Min(95, totalRead * 95 / totalBytes));
            }
        }
    }

    private async Task ExtractArchiveForEmulatorAsync(
        EmulatorConfig emulator,
        string archivePath,
        string destinationDirectory,
        bool isZip)
    {
        var fileName = Path.GetFileName(archivePath);
        emulator.StatusText = "압축 해제 중...";
        emulator.StatusType = "Extracting";
        ReportTransfer(emulator, $"압축 해제 시작: {fileName}", 95);

        long lastTicks = 0;
        int lastProgressVal = -1;

        var progress = new Progress<int>(value =>
        {
            var now = Environment.TickCount64;
            if (value != lastProgressVal && (now - lastTicks > 100 || value >= 99))
            {
                lastTicks = now;
                lastProgressVal = value;
                ReportTransferQuiet(emulator, value);
            }
        });

        var dummyCurrentFile = new Progress<string>(_ => { });

        await Task.Run(() =>
        {
            if (isZip)
            {
                ExtractZipArchiveSequential(archivePath, destinationDirectory, progress, dummyCurrentFile);
            }
            else
            {
                ExtractSevenZipArchiveSequential(archivePath, destinationDirectory, progress, dummyCurrentFile);
            }
        });
    }

    private void ReportTransferQuiet(EmulatorConfig emulator, int progress)
    {
        bool isSelected;
        lock (_transferStates)
        {
            if (!_transferStates.TryGetValue(emulator.Id, out var state))
            {
                state = new EmulatorTransferState();
                _transferStates[emulator.Id] = state;
            }

            state.Progress = progress;
            isSelected = ReferenceEquals(SelectedEmulator, emulator);
        }

        if (isSelected)
        {
            RunOnUIThread(() => Progress = progress);
        }
    }

    private static bool IsPpssppEmulator(EmulatorConfig emulator)
    {
        if (emulator.Name.Contains("PPSSPP", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(emulator.Repository.Trim(), UriKind.Absolute, out var repositoryUri))
        {
            return emulator.Repository.Contains("ppsspp", StringComparison.OrdinalIgnoreCase);
        }

        return repositoryUri.Host.Equals("ppsspp.org", StringComparison.OrdinalIgnoreCase) ||
               repositoryUri.Host.EndsWith(".ppsspp.org", StringComparison.OrdinalIgnoreCase) ||
               repositoryUri.AbsolutePath.Contains("ppsspp", StringComparison.OrdinalIgnoreCase);
    }

    private static void RestorePpssppJapaneseFont(string emulatorFolder)
    {
        var destinationFont = Path.Combine(emulatorFolder, "assets", "flash0", "font", "jpn0.pgf");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFont)!);

        RunOnUIThread(() =>
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/jpn0.pgf", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(uri);
                if (streamInfo != null)
                {
                    using var fontStream = streamInfo.Stream;
                    using var destStream = File.Create(destinationFont);
                    fontStream.CopyTo(destStream);
                    return;
                }
            }
            catch
            {
                // ignore
            }

            var sourceFont = Path.Combine(AppContext.BaseDirectory, "Assets", "jpn0.pgf");
            if (File.Exists(sourceFont))
            {
                File.Copy(sourceFont, destinationFont, overwrite: true);
            }
        });
    }

    private void ReportTransfer(EmulatorConfig emulator, string? status, int? progress)
    {
        bool isSelected;
        lock (_transferStates)
        {
            if (!_transferStates.TryGetValue(emulator.Id, out var state))
            {
                state = new EmulatorTransferState();
                _transferStates[emulator.Id] = state;
            }

            if (status != null)
            {
                state.Status = status;
            }

            if (progress.HasValue)
            {
                state.Progress = progress.Value;
            }

            isSelected = ReferenceEquals(SelectedEmulator, emulator);
        }

        if (isSelected)
        {
            RunOnUIThread(() =>
            {
                if (status != null) StatusMessage = status;
                if (progress.HasValue) Progress = progress.Value;
            });
        }
    }

    public async Task DownloadOnlyAsync()
    {
        await ExecuteBusyOperationAsync(async () =>
        {
            var assetToDownload = await ResolveAssetToDownloadAsync();
            if (assetToDownload == null || SelectedEmulator == null)
            {
                return;
            }

            var targetFolder = GetFinalTargetFolder(SelectedEmulator, assetToDownload);
            var assetFileName = GetSafeFileName(assetToDownload.AssetName, assetToDownload.DownloadUrl);
            var downloadedFile = Path.Combine(targetFolder, assetFileName);

            StatusMessage = $"다운로드 중: {assetFileName}";
            await DownloadAssetToFileAsync(assetToDownload.DownloadUrl, downloadedFile);

            SelectedEmulator.LastDownloadedAt = DateTimeOffset.Now;
            SelectedEmulator.InstalledVersion = assetToDownload.Version;
            var dualStatusOnly = EvaluateDualFolderStatus(SelectedEmulator, assetToDownload);
            SelectedEmulator.StatusText = dualStatusOnly.SummaryStatusText;
            SelectedEmulator.StatusType = "UpToDate";
            await SaveSettingsToDiskAsync();

            Progress = 100;
            StatusMessage = $"다운로드 완료: {downloadedFile}";
        }, "다운로드 중입니다...");
    }

    public async Task DownloadUpdateAsync()
    {
        await ExecuteBusyOperationAsync(async () =>
        {
            var assetToDownload = await ResolveAssetToDownloadAsync();
            if (assetToDownload == null || SelectedEmulator == null)
            {
                return;
            }

            var targetFolder = GetFinalTargetFolder(SelectedEmulator, assetToDownload);
            var assetFileName = GetSafeFileName(assetToDownload.AssetName, assetToDownload.DownloadUrl);
            var downloadedFile = Path.Combine(targetFolder, assetFileName);
            var extracted = false;

            StatusMessage = $"다운로드 중: {assetFileName}";
            await DownloadAssetToFileAsync(assetToDownload.DownloadUrl, downloadedFile);

            try
            {
                var extension = Path.GetExtension(downloadedFile);
                if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "압축 해제 중...";
                    StatusMessage = "ZIP 압축 해제 중...";
                    await ExtractArchiveInBackgroundAsync(downloadedFile, targetFolder, isZip: true);
                    extracted = true;
                }
                else if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "압축 해제 중...";
                    StatusMessage = "7z 압축 해제 중...";
                    await ExtractArchiveInBackgroundAsync(downloadedFile, targetFolder, isZip: false);
                    extracted = true;
                }

                if (extracted)
                {
                    File.Delete(downloadedFile);
                    SelectedEmulator.InstalledVersion = assetToDownload.Version;
                    var dualStatusExtracted = EvaluateDualFolderStatus(SelectedEmulator, assetToDownload);
                    SelectedEmulator.StatusText = dualStatusExtracted.SummaryStatusText;
                    SelectedEmulator.StatusType = "UpToDate";
                    await SaveSettingsToDiskAsync();
                    Progress = 100;
                    StatusMessage = $"다운로드 및 압축 해제가 완료되었습니다. 압축 파일을 삭제했습니다. 경로: {targetFolder}";
                    return;
                }

                SelectedEmulator.InstalledVersion = assetToDownload.Version;
                var dualStatusNormal = EvaluateDualFolderStatus(SelectedEmulator, assetToDownload);
                SelectedEmulator.StatusText = dualStatusNormal.SummaryStatusText;
                SelectedEmulator.StatusType = "UpToDate";
                await SaveSettingsToDiskAsync();
                Progress = 100;
                StatusMessage = $"압축 파일이 아니어서 다운로드만 완료했습니다. 경로: {downloadedFile}";
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{ex.Message} 다운로드 파일은 삭제하지 않았습니다: {downloadedFile}", ex);
            }
        }, "다운로드 중입니다...");
    }

    private async Task<BuildAsset?> ResolveAssetToDownloadAsync()
    {
        var assetToDownload = SelectedAsset;
        if (assetToDownload != null)
        {
            return assetToDownload;
        }

        if (SelectedEmulator == null || !GitHubReleaseService.IsDirectDownloadUrl(SelectedEmulator.Repository))
        {
            StatusMessage = "다운로드할 파일을 선택하세요.";
            return null;
        }

        var directRelease = await _releaseService.GetLatestReleaseAsync(SelectedEmulator.Repository, CancellationToken.None);
        if (directRelease == null || directRelease.Assets.Count == 0)
        {
            StatusMessage = "직접 다운로드 링크를 처리할 수 없습니다.";
            return null;
        }

        var directAsset = directRelease.Assets.First();
        var version = GitHubReleaseService.ResolveVersion(
            directRelease.TagName ?? directRelease.Name,
            directAsset.Name,
            directRelease.PublishedAt);

        assetToDownload = new BuildAsset
        {
            Version = version,
            PublishedAt = directRelease.PublishedAt,
            AssetName = directAsset.Name,
            DownloadUrl = directAsset.BrowserDownloadUrl
        };

        SelectedAsset = assetToDownload;
        SelectedReleaseVersion = assetToDownload.Version;
        ReleaseNotes = "직접 다운로드 URL을 사용합니다.";
        return assetToDownload;
    }

    private async Task DownloadAssetToFileAsync(string downloadUrl, string destinationFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EmulatorAutoUpdater/1.0");

        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("다운로드 실패: 서버에서 바이너리 파일 대신 HTML 웹 페이지를 반환했습니다.");
        }

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(destinationFile);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            totalRead += read;

            if (totalBytes > 0)
            {
                Progress = (int)Math.Min(95, totalRead * 95 / totalBytes);
            }
        }

        VerifyDownloadedFileOrThrow(destinationFile);
    }

    private static void VerifyDownloadedFileOrThrow(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("다운로드한 파일을 찾을 수 없습니다.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length < 100 * 1024)
        {
            var headerBytes = new byte[Math.Min(fileInfo.Length, 512)];
            using (var fs = File.OpenRead(filePath))
            {
                fs.Read(headerBytes, 0, headerBytes.Length);
            }
            var headerText = System.Text.Encoding.UTF8.GetString(headerBytes);
            if (headerText.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                headerText.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                headerText.Contains("bot", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(filePath); } catch { }
                throw new InvalidOperationException("다운로드 실패: 서버에서 바이너리 패치 대신 HTML 봇 챌린지 페이지를 반환했습니다.");
            }
        }
    }

    private async Task ExtractArchiveInBackgroundAsync(
        string archivePath,
        string destinationDirectory,
        bool isZip)
    {
        long lastTicks = 0;
        int lastProgressVal = -1;

        var progress = new Progress<int>(value =>
        {
            var now = Environment.TickCount64;
            if (value != lastProgressVal && (now - lastTicks > 100 || value >= 99))
            {
                lastTicks = now;
                lastProgressVal = value;
                Progress = value;
            }
        });

        var dummyCurrentFile = new Progress<string>(_ => { });

        await Task.Run(() =>
        {
            if (isZip)
            {
                ExtractZipArchiveSequential(archivePath, destinationDirectory, progress, dummyCurrentFile);
            }
            else
            {
                ExtractSevenZipArchiveSequential(archivePath, destinationDirectory, progress, dummyCurrentFile);
            }
        });
    }

    private static void ExtractZipArchiveSequential(
        string archivePath,
        string destinationDirectory,
        IProgress<int> progress,
        IProgress<string> currentFile)
    {
        Directory.CreateDirectory(destinationDirectory);
        var normalizedDestination = GetNormalizedDestination(destinationDirectory);

        using var archive = ZipFile.OpenRead(archivePath);
        var fileEntries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var rootFolder = FindSingleRootFolder(fileEntries.Select(entry => entry.FullName));
        var totalFiles = fileEntries.Count;
        var completedFiles = 0;

        foreach (var entry in archive.Entries)
        {
            var relativePath = RemoveRootFolder(entry.FullName, rootFolder);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = GetSafeExtractionPath(destinationDirectory, normalizedDestination, relativePath);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            currentFile.Report(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var entryStream = entry.Open();
            WriteEntryAtomically(entryStream, destinationPath);

            completedFiles++;
            progress.Report(CalculateExtractionProgress(completedFiles, totalFiles));
        }
    }

    private static void ExtractSevenZipArchiveSequential(
        string archivePath,
        string destinationDirectory,
        IProgress<int> progress,
        IProgress<string> currentFile)
    {
        Directory.CreateDirectory(destinationDirectory);
        var normalizedDestination = GetNormalizedDestination(destinationDirectory);
        var normalizedArchivePath = Path.GetFullPath(archivePath);
        var readerOptions = new ReaderOptions { LeaveStreamOpen = false };

        using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archiveStream, readerOptions);
        var rootFolder = FindSingleRootFolder(
            archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Select(entry => entry.Key ?? string.Empty));
        using var reader = archive.ExtractAllEntries();
        var lastProgress = 95;

        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            var entryName = entry.Key ?? string.Empty;
            var relativePath = RemoveRootFolder(entryName, rootFolder);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = GetSafeExtractionPath(destinationDirectory, normalizedDestination, relativePath);

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (destinationPath.Equals(normalizedArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"압축 파일이 자기 자신을 덮어쓰려고 합니다: {entryName}");
            }

            currentFile.Report(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var entryStream = reader.OpenEntryStream();
            WriteEntryAtomically(entryStream, destinationPath, (copiedBytes, totalBytes) =>
            {
                if (totalBytes <= 0)
                {
                    return;
                }

                var entryProgress = 95 + (int)Math.Min(4, copiedBytes * 4 / totalBytes);
                if (entryProgress > lastProgress)
                {
                    lastProgress = entryProgress;
                    progress.Report(lastProgress);
                }
            }, entry.Size);
        }

        progress.Report(99);
    }

    private static string GetNormalizedDestination(string destinationDirectory)
    {
        return Path.GetFullPath(destinationDirectory)
                   .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               + Path.DirectorySeparatorChar;
    }

    private static string? FindSingleRootFolder(IEnumerable<string> filePaths)
    {
        string? commonRoot = null;
        var hasFiles = false;

        foreach (var filePath in filePaths)
        {
            var normalizedPath = filePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var separatorIndex = normalizedPath.IndexOf(Path.DirectorySeparatorChar);

            if (separatorIndex <= 0)
            {
                return null;
            }

            var root = normalizedPath[..separatorIndex];
            if (root is "." or "..")
            {
                return null;
            }

            if (commonRoot == null)
            {
                commonRoot = root;
            }
            else if (!commonRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            hasFiles = true;
        }

        return hasFiles ? commonRoot : null;
    }

    private static string RemoveRootFolder(string entryPath, string? rootFolder)
    {
        var normalizedPath = entryPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return normalizedPath;
        }

        if (normalizedPath.Equals(rootFolder, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = rootFolder + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath[prefix.Length..]
            : normalizedPath;
    }

    private static string GetSafeExtractionPath(
        string destinationDirectory,
        string normalizedDestination,
        string entryName)
    {
        var entryPath = entryName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entryPath));

        if (!destinationPath.StartsWith(normalizedDestination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"압축 파일에 안전하지 않은 경로가 포함되어 있습니다: {entryName}");
        }

        return destinationPath;
    }

    private static void WriteEntryAtomically(
        Stream entryStream,
        string destinationPath,
        Action<long, long>? reportProgress = null,
        long totalBytes = 0)
    {
        var temporaryPath = destinationPath + ".emulator-auto-updater.tmp";

        try
        {
            using (var outputFile = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 1024,
                       FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                long copiedBytes = 0;
                int bytesRead;

                while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    outputFile.Write(buffer, 0, bytesRead);
                    copiedBytes += bytesRead;
                    reportProgress?.Invoke(copiedBytes, totalBytes);
                }

                outputFile.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static int CalculateExtractionProgress(int completedFiles, int totalFiles)
    {
        return totalFiles <= 0
            ? 99
            : 95 + (int)Math.Min(4, completedFiles * 4L / totalFiles);
    }

    private static void ExtractZipArchive(string archivePath, string destinationDirectory)
    {
        var normalizedDestination = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!destinationPath.StartsWith(normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"압축 파일에 안전하지 않은 경로가 포함되어 있습니다: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        var readerOptions = new ReaderOptions
        {
            LeaveStreamOpen = false
        };

        using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archivePath, readerOptions);
        var normalizedDestination = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            var entryPath = (entry.Key ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entryPath));

            if (!destinationPath.StartsWith(normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"압축 파일에 안전하지 않은 경로가 포함되어 있습니다: {entry.Key}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var entryStream = entry.OpenEntryStream();
            using var outputFile = File.Create(destinationPath);
            entryStream.CopyTo(outputFile);
        }
    }

    private static string GetSafeFileName(string preferredName, string fallbackUrl)
    {
        var fileName = preferredName;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            try
            {
                var uri = new Uri(fallbackUrl);
                if (!string.IsNullOrWhiteSpace(uri.LocalPath))
                {
                    fileName = Path.GetFileName(uri.LocalPath);
                }
            }
            catch
            {
                // ignore invalid URI
            }
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download.zip";
        }

        fileName = fileName.Trim('"').Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? "download.zip" : fileName;
    }

    private static string GetTargetFolderOrThrow(EmulatorConfig emulator)
    {
        if (string.IsNullOrWhiteSpace(emulator.Folder))
        {
            throw new InvalidOperationException("먼저 에뮬레이터 설치 폴더를 설정하세요.");
        }

        var targetFolder = Path.GetFullPath(emulator.Folder);
        Directory.CreateDirectory(targetFolder);
        CleanupOrphanedTmpFiles(targetFolder);
        return targetFolder;
    }

    private string GetFinalTargetFolder(EmulatorConfig emulator, BuildAsset asset)
    {
        var baseFolder = GetTargetFolderOrThrow(emulator);
        if (!UseVersionSubfolders)
        {
            return baseFolder;
        }

        var subfolderName = GetVersionSubfolderName(asset.Version, asset.PublishedAt);
        var finalFolder = Path.Combine(baseFolder, subfolderName);
        Directory.CreateDirectory(finalFolder);
        CleanupOrphanedTmpFiles(finalFolder);
        return finalFolder;
    }

    private static string GetVersionSubfolderName(string? version, DateTimeOffset publishedAt)
    {
        var raw = version?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = publishedAt > DateTimeOffset.MinValue
                ? publishedAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm")
                : DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
        }

        var folderName = raw.Replace(':', '-').Replace(' ', '_').Replace('/', '-').Replace('\\', '-');
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            folderName = folderName.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(folderName) ? "version" : folderName;
    }

    private static bool IsEmulatorProcessRunning(string targetFolder, out string runningExeName)
    {
        runningExeName = string.Empty;
        try
        {
            if (!Directory.Exists(targetFolder))
            {
                return false;
            }

            var exeFiles = Directory.GetFiles(targetFolder, "*.exe", SearchOption.TopDirectoryOnly);
            foreach (var exeFile in exeFiles)
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(exeFile);
                var processes = System.Diagnostics.Process.GetProcessesByName(fileNameWithoutExt);
                if (processes.Length > 0)
                {
                    runningExeName = fileNameWithoutExt;
                    return true;
                }
            }
        }
        catch
        {
            // Ignore process lookup failures
        }

        return false;
    }

    private static void CleanupOrphanedTmpFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var tmpFiles = Directory.GetFiles(directory, "*.emulator-auto-updater.tmp", SearchOption.AllDirectories);
            var now = DateTime.UtcNow;
            foreach (var tmpFile in tmpFiles)
            {
                var fi = new FileInfo(tmpFile);
                if (now - fi.LastWriteTimeUtc > TimeSpan.FromMinutes(1))
                {
                    try { fi.Delete(); } catch { }
                }
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    private string GetConfigFilePath()
    {
        if (!string.IsNullOrWhiteSpace(_customConfigFilePath) && File.Exists(_customConfigFilePath))
        {
            return _customConfigFilePath;
        }

        // 1. AppContext.BaseDirectory
        var baseDir = AppContext.BaseDirectory;
        var baseConfig = Path.Combine(baseDir, "config.json");
        if (File.Exists(baseConfig))
        {
            return baseConfig;
        }

        var baseConfigUpper = Path.Combine(baseDir, "Config.json");
        if (File.Exists(baseConfigUpper))
        {
            return baseConfigUpper;
        }

        // 2. Directory.GetCurrentDirectory()
        var currentDir = Directory.GetCurrentDirectory();
        if (!string.Equals(baseDir.TrimEnd('\\', '/'), currentDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            var currentConfig = Path.Combine(currentDir, "config.json");
            if (File.Exists(currentConfig))
            {
                return currentConfig;
            }

            var currentConfigUpper = Path.Combine(currentDir, "Config.json");
            if (File.Exists(currentConfigUpper))
            {
                return currentConfigUpper;
            }
        }

        // 3. Case-insensitive search for config*.json in baseDir or currentDir
        if (Directory.Exists(baseDir))
        {
            var matchedBase = Directory.GetFiles(baseDir, "*.json")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "config.json", StringComparison.OrdinalIgnoreCase));
            if (matchedBase != null)
            {
                return matchedBase;
            }
        }

        if (Directory.Exists(currentDir))
        {
            var matchedCurrent = Directory.GetFiles(currentDir, "*.json")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "config.json", StringComparison.OrdinalIgnoreCase));
            if (matchedCurrent != null)
            {
                return matchedCurrent;
            }
        }

        return baseConfig;
    }

    private static void CleanupLegacyAppDataConfig()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacyFolder = Path.Combine(appData, "EmulatorAutoUpdater");
            if (Directory.Exists(legacyFolder))
            {
                Directory.Delete(legacyFolder, true);
            }
        }
        catch
        {
            // Ignore failure during legacy cleanup
        }
    }

    private async Task SaveSettingsToDiskAsync(string? targetFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(targetFilePath))
        {
            _customConfigFilePath = targetFilePath;
        }

        List<EmulatorConfig> emulatorSnapshot = [];
        List<double> columnWidthsSnapshot = [];
        RunOnUIThread(() =>
        {
            emulatorSnapshot = Emulators.ToList();
            columnWidthsSnapshot = EmulatorGridColumnWidths.ToList();
        });

        await _settingsWriteLock.WaitAsync();
        try
        {
            var path = GetConfigFilePath();
            var temporaryPath = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var settings = new AppSettings
            {
                CheckAllUpdatesOnStartup = CheckAllUpdatesOnStartup,
                UseVersionSubfolders = UseVersionSubfolders,
                LanguageCode = SelectedLanguageCode,
                DefaultAssetPattern = DefaultAssetPattern,
                WindowPlacement = WindowPlacement,
                EmulatorGridColumnWidths = columnWidthsSnapshot,
                Emulators = emulatorSnapshot
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
            RunOnUIThread(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentConfigFilePath))));
        }
        finally
        {
            _settingsWriteLock.Release();
        }
    }

    private void AddEmulator()
    {
        var newEmulator = new EmulatorConfig
        {
            Name = "새 에뮬레이터",
            Repository = "owner/repo",
            Folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AssetPattern = DefaultAssetPattern,
            InstalledVersion = string.Empty
        };

        Emulators.Add(newEmulator);
        SelectedEmulator = newEmulator;
        StatusMessage = "새 에뮬레이터 항목을 추가했습니다.";
    }

    private void RemoveEmulator()
    {
        if (SelectedEmulator == null)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"선택한 에뮬레이터('{SelectedEmulator.Name}') 항목을 목록에서 삭제하시겠습니까?",
            "에뮬레이터 삭제 확인",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var removedName = SelectedEmulator.Name;
        _updateHistory.Remove(SelectedEmulator.Id);
        _transferStates.Remove(SelectedEmulator.Id);
        Emulators.Remove(SelectedEmulator);
        SelectedEmulator = null;
        StatusMessage = $"'{removedName}' 항목을 삭제했습니다.";
    }

    private static void EnsureEnoughDiskSpace(string destinationPath, long requiredBytes = 100 * 1024 * 1024)
    {
        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return;

            var driveInfo = new DriveInfo(root);
            if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < requiredBytes)
            {
                var reqMb = requiredBytes / (1024 * 1024);
                var availMb = driveInfo.AvailableFreeSpace / (1024 * 1024);
                throw new InvalidOperationException($"설치 드라이브({root})의 여유 공간이 부족합니다. (필요: 약 {reqMb}MB, 현재 여유: {availMb}MB)");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // Ignore Inspection error on UNC shares
        }
    }

    private void BrowseFolder()
    {
        if (SelectedEmulator == null)
        {
            return;
        }

        var folder = _openFolderPicker();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SelectedEmulator.Folder = folder;
            StatusMessage = "폴더 경로를 선택했습니다.";
        }
    }

    private static bool IsUpToDate(EmulatorConfig emulator, BuildAsset asset)
    {
        var installed = emulator.InstalledVersion?.Trim();
        var latestVersion = asset.Version?.Trim() ?? string.Empty;

        // 1. Version-based comparison
        if (IsVersionUpToDate(installed, latestVersion))
        {
            return true;
        }

        // 2. Date-based comparison (fallback when no valid version or when installed/latest are dates)
        if (asset.PublishedAt > DateTimeOffset.MinValue && emulator.LastDownloadedAt.HasValue)
        {
            // If the remote published date is <= when it was last downloaded, it is up to date!
            if (asset.PublishedAt.UtcDateTime <= emulator.LastDownloadedAt.Value.UtcDateTime)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVersionUpToDate(string? installed, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latestVersion))
        {
            return false;
        }

        var inst = installed.Trim();
        var latest = latestVersion.Trim();

        // 1. Exact match
        if (string.Equals(inst, latest, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Normalized 'v' prefix comparison
        var instClean = inst.TrimStart('v', 'V').Trim();
        var latestClean = latest.TrimStart('v', 'V').Trim();

        if (string.Equals(instClean, latestClean, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. System.Version comparison (e.g. 2.6.3.0 vs 2.6.3)
        if (System.Version.TryParse(instClean, out var vInst) &&
            System.Version.TryParse(latestClean, out var vLatest))
        {
            return vLatest <= vInst;
        }

        // 4. Integer build number comparison (e.g. 4066 vs 4066)
        if (long.TryParse(instClean, out var lInst) &&
            long.TryParse(latestClean, out var lLatest))
        {
            return lLatest <= lInst;
        }

        // 5. Revision number comparison (e.g., 1.20.4-721-gc42e41c034)
        var revInstMatch = Regex.Match(instClean, @"-(\d+)-g[0-9a-f]+$", RegexOptions.IgnoreCase);
        var revLatestMatch = Regex.Match(latestClean, @"-(\d+)-g[0-9a-f]+$", RegexOptions.IgnoreCase);
        if (revInstMatch.Success && revLatestMatch.Success &&
            long.TryParse(revInstMatch.Groups[1].Value, out var revInst) &&
            long.TryParse(revLatestMatch.Groups[1].Value, out var revLatest))
        {
            return revLatest <= revInst;
        }

        // 6. Build dash number comparison (e.g., 2606-282 vs 2606-282)
        var dashInstMatch = Regex.Match(instClean, @"^(\d+)-(\d+)$");
        var dashLatestMatch = Regex.Match(latestClean, @"^(\d+)-(\d+)$");
        if (dashInstMatch.Success && dashLatestMatch.Success)
        {
            var majorInst = long.Parse(dashInstMatch.Groups[1].Value);
            var minorInst = long.Parse(dashInstMatch.Groups[2].Value);
            var majorLatest = long.Parse(dashLatestMatch.Groups[1].Value);
            var minorLatest = long.Parse(dashLatestMatch.Groups[2].Value);

            if (majorLatest < majorInst) return true;
            if (majorLatest == majorInst) return minorLatest <= minorInst;
            return false;
        }

        // 7. Date / Timestamp comparison (when no explicit version tag exists):
        if (DateTimeOffset.TryParse(inst, out var instDate) &&
            DateTimeOffset.TryParse(latest, out var latestDate))
        {
            return latestDate.UtcDateTime <= instDate.UtcDateTime;
        }

        return false;
    }

    private sealed record DualFolderStatusResult(
        bool IsUpToDate,
        bool HasRootVersion,
        bool HasSubfolderVersion,
        string SubfolderName,
        string SummaryStatusText,
        string DetailedLogText);

    private DualFolderStatusResult EvaluateDualFolderStatus(EmulatorConfig emulator, BuildAsset asset)
    {
        var installed = emulator.InstalledVersion?.Trim();

        // If InstalledVersion is missing, but root folder exists and contains .exe files and UseVersionSubfolders is false:
        if (string.IsNullOrWhiteSpace(installed) && !UseVersionSubfolders)
        {
            if (!string.IsNullOrWhiteSpace(emulator.Folder) && Directory.Exists(emulator.Folder))
            {
                var rootExeFiles = Directory.GetFiles(emulator.Folder, "*.exe", SearchOption.TopDirectoryOnly);
                if (rootExeFiles.Length > 0)
                {
                    emulator.InstalledVersion = asset.Version;
                    installed = asset.Version;
                }
            }
        }

        var isRootUpToDate = IsUpToDate(emulator, asset);

        var targetSubfolderName = GetVersionSubfolderName(asset.Version, asset.PublishedAt);
        var foundSubfolderName = targetSubfolderName;
        var isSubfolderUpToDate = false;

        if (!string.IsNullOrWhiteSpace(emulator.Folder) && Directory.Exists(emulator.Folder))
        {
            try
            {
                // 1. Direct path check
                var exactPath = Path.Combine(emulator.Folder, targetSubfolderName);
                if (Directory.Exists(exactPath))
                {
                    var exeFiles = Directory.GetFiles(exactPath, "*.exe", SearchOption.TopDirectoryOnly);
                    if (exeFiles.Length > 0)
                    {
                        isSubfolderUpToDate = true;
                        foundSubfolderName = targetSubfolderName;
                    }
                }

                // 2. Flexible scanning if direct path did not match
                if (!isSubfolderUpToDate)
                {
                    var subDirs = Directory.GetDirectories(emulator.Folder);
                    foreach (var subDir in subDirs)
                    {
                        var dirName = Path.GetFileName(subDir);
                        if (string.IsNullOrWhiteSpace(dirName)) continue;

                        if (SubfolderManagerWindow.IsValidVersionSubfolder(subDir, dirName, asset.Version, emulator.Name))
                        {
                            if (dirName.Equals(asset.Version, StringComparison.OrdinalIgnoreCase) ||
                                dirName.Equals($"v{asset.Version}", StringComparison.OrdinalIgnoreCase) ||
                                dirName.Contains(asset.Version, StringComparison.OrdinalIgnoreCase) ||
                                dirName.Equals(targetSubfolderName, StringComparison.OrdinalIgnoreCase))
                            {
                                var exeFiles = Directory.GetFiles(subDir, "*.exe", SearchOption.TopDirectoryOnly);
                                if (exeFiles.Length > 0)
                                {
                                    isSubfolderUpToDate = true;
                                    foundSubfolderName = dirName;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                isSubfolderUpToDate = false;
            }
        }

        if (isRootUpToDate && isSubfolderUpToDate)
        {
            return new DualFolderStatusResult(
                IsUpToDate: true,
                HasRootVersion: true,
                HasSubfolderVersion: true,
                SubfolderName: foundSubfolderName,
                SummaryStatusText: LocalizationService.GetString("StatusUpToDateRoot", "최신 (루트폴더)"),
                DetailedLogText: $"루트 폴더[✓ 최신] & 하위 폴더('{foundSubfolderName}')[✓ 최신]");
        }

        if (isSubfolderUpToDate)
        {
            return new DualFolderStatusResult(
                IsUpToDate: true,
                HasRootVersion: false,
                HasSubfolderVersion: true,
                SubfolderName: foundSubfolderName,
                SummaryStatusText: LocalizationService.GetString("StatusUpToDateSubfolder", foundSubfolderName),
                DetailedLogText: $"루트 폴더[미업데이트] & 하위 폴더('{foundSubfolderName}')[✓ 최신 감지]");
        }

        if (isRootUpToDate)
        {
            return new DualFolderStatusResult(
                IsUpToDate: true,
                HasRootVersion: true,
                HasSubfolderVersion: false,
                SubfolderName: targetSubfolderName,
                SummaryStatusText: LocalizationService.GetString("StatusUpToDateRoot", "최신 (루트폴더)"),
                DetailedLogText: $"루트 폴더[✓ 최신] & 하위 폴더('{targetSubfolderName}')[미존재]");
        }

        return new DualFolderStatusResult(
            IsUpToDate: false,
            HasRootVersion: false,
            HasSubfolderVersion: false,
            SubfolderName: targetSubfolderName,
            SummaryStatusText: LocalizationService.GetString("StatusUpdateAvailable", asset.Version),
            DetailedLogText: $"루트 폴더[업데이트 필요] & 하위 폴더('{targetSubfolderName}')[미존재]");
    }

    private async Task ExecuteBusyOperationAsync(Func<Task> operation, string busyMessage)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = busyMessage;
            Progress = 0;
            await operation();
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex);
            StatusMessage = $"오류: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (Progress == 0)
            {
                Progress = 100;
            }
        }
    }

    private async Task LogErrorAsync(Exception ex)
    {
        try
        {
            var logPath = GetLogFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}";
            await File.AppendAllTextAsync(logPath, logEntry);
        }
        catch
        {
            // Logging must never break the updater workflow.
        }

        AppendLog($"❌ [오류/Exception] {ex.GetType().Name}: {ex.Message}");
    }

    private static string GetLogFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Log.txt");
    }

    private void UpdateCommandStates()
    {
        RunOnUIThread(() =>
        {
            if (SelectedEmulator != null &&
                IsEmulatorDownloading(SelectedEmulator) &&
                _transferStates.TryGetValue(SelectedEmulator.Id, out var transferState))
            {
                StatusMessage = transferState.Status;
                Progress = transferState.Progress;
            }

            AddEmulatorCommand.RaiseCanExecuteChanged();
            RemoveEmulatorCommand.RaiseCanExecuteChanged();
            LoadSettingsCommand.RaiseCanExecuteChanged();
            SaveSettingsCommand.RaiseCanExecuteChanged();
            OpenConfigFileCommand.RaiseCanExecuteChanged();
            SaveConfigFileAsCommand.RaiseCanExecuteChanged();
            ConvertSelectedAssetPatternCommand.RaiseCanExecuteChanged();
            ApplySelectedAssetToPatternCommand.RaiseCanExecuteChanged();
            ExcludeSelectedAssetFromPatternCommand.RaiseCanExecuteChanged();
            BrowseFolderCommand.RaiseCanExecuteChanged();
            CheckUpdatesCommand.RaiseCanExecuteChanged();
            CheckAllUpdatesCommand.RaiseCanExecuteChanged();
            DownloadAllUpdatesCommand.RaiseCanExecuteChanged();
            DownloadOnlyCommand.RaiseCanExecuteChanged();
            DownloadUpdateCommand.RaiseCanExecuteChanged();
            PerformAppSelfUpdateCommand.RaiseCanExecuteChanged();
        });
    }

    private bool CanDownloadSelectedAsset()
    {
        return SelectedEmulator != null &&
               !IsBusy &&
               !IsEmulatorDownloading(SelectedEmulator) &&
               !IsEmulatorChecking(SelectedEmulator) &&
               (SelectedAsset != null || CanDownloadDirectly());
    }

    private bool IsEmulatorChecking(EmulatorConfig? emulator)
    {
        if (emulator == null) return false;
        lock (_activeUpdateChecks)
        {
            return _activeUpdateChecks.Contains(emulator.Id);
        }
    }

    private bool IsEmulatorDownloading(EmulatorConfig? emulator)
    {
        if (emulator == null) return false;
        lock (_activeDownloads)
        {
            return _activeDownloads.Contains(emulator.Id);
        }
    }

    private bool CanDownloadDirectly()
    {
        return SelectedEmulator != null && GitHubReleaseService.IsDirectDownloadUrl(SelectedEmulator.Repository);
    }

    private void SelectedEmulatorOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EmulatorConfig.Repository) or nameof(EmulatorConfig.AssetPattern) or nameof(EmulatorConfig.Folder) or nameof(EmulatorConfig.Name))
        {
            UpdateCommandStates();
        }
    }

    private void RestoreUpdateHistory(EmulatorConfig? emulator)
    {
        RunOnUIThread(() =>
        {
            ReleaseAssets.Clear();
            SelectedAsset = null;
            SelectedReleaseVersion = string.Empty;
            ReleaseNotes = string.Empty;

            if (emulator == null || !_updateHistory.TryGetValue(emulator.Id, out var history))
            {
                return;
            }

            foreach (var asset in history.Assets)
            {
                ReleaseAssets.Add(asset);
            }

            SelectedAsset = ReleaseAssets.FirstOrDefault();
            SelectedReleaseVersion = history.ReleaseVersion;
            ReleaseNotes = history.ReleaseNotes;
        });
    }

    private void RestoreTransferState(EmulatorConfig? emulator)
    {
        RunOnUIThread(() =>
        {
            if (emulator != null && _transferStates.TryGetValue(emulator.Id, out var state))
            {
                StatusMessage = string.IsNullOrWhiteSpace(state.Status)
                    ? $"'{emulator.Name}' 항목을 선택했습니다."
                    : state.Status;
                Progress = state.Progress;
            }
            else if (emulator != null)
            {
                StatusMessage = $"'{emulator.Name}' 항목을 선택했습니다.";
                Progress = 0;
            }
            else
            {
                StatusMessage = "에뮬레이터를 선택하세요.";
                Progress = 0;
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed record EmulatorUpdateHistory(
        IReadOnlyList<BuildAsset> Assets,
        string ReleaseVersion,
        string ReleaseNotes);

    private sealed class EmulatorTransferState
    {
        public int Progress { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    private static void RunOnUIThread(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }
        else
        {
            action();
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
