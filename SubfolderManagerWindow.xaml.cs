using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using EmulatorAutoUpdater.Models;

namespace EmulatorAutoUpdater;

public partial class SubfolderManagerWindow : Window
{
    private readonly EmulatorConfig _emulator;
    public ObservableCollection<SubfolderItem> SubfolderItems { get; } = new();

    public SubfolderManagerWindow(EmulatorConfig emulator)
    {
        InitializeComponent();
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));

        TitleTextBlock.Text = $"📁 [{_emulator.Name}] 다운로드된 버전 폴더 관리";
        PathTextBlock.Text = $"루트 경로: {_emulator.Folder}";

        SubfolderDataGrid.ItemsSource = SubfolderItems;
        LoadSubfolders();
    }

    private void LoadSubfolders()
    {
        SubfolderItems.Clear();

        if (string.IsNullOrWhiteSpace(_emulator.Folder) || !Directory.Exists(_emulator.Folder))
        {
            StatusMessageTextBlock.Text = "에뮬레이터 설치 폴더가 지정되지 않았거나 디스크에 존재하지 않습니다.";
            UpdateSelectionSummary();
            return;
        }

        try
        {
            var directories = Directory.GetDirectories(_emulator.Folder);
            foreach (var dir in directories)
            {
                var folderName = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(folderName)) continue;

                if (!IsValidVersionSubfolder(dir, folderName, _emulator.InstalledVersion, _emulator.Name))
                {
                    continue;
                }

                // Find executables inside top directory
                var exeFiles = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                var exeName = exeFiles.Length > 0 ? string.Join(", ", exeFiles.Select(Path.GetFileName)) : "없음";

                var sizeBytes = CalculateDirectorySize(dir);
                var lastWrite = Directory.GetLastWriteTime(dir);

                var item = new SubfolderItem
                {
                    IsSelected = false,
                    FolderName = folderName,
                    FullPath = dir,
                    SizeBytes = sizeBytes,
                    LastModified = lastWrite,
                    ExecutableName = exeName
                };

                item.PropertyChanged += Item_PropertyChanged;
                SubfolderItems.Add(item);
            }

            StatusMessageTextBlock.Text = SubfolderItems.Count == 0
                ? "다운로드된 버전 하위 폴더를 찾을 수 없습니다."
                : $"총 {SubfolderItems.Count}개의 버전 하위 폴더를 찾았습니다.";
        }
        catch (Exception ex)
        {
            StatusMessageTextBlock.Text = $"폴더 스캔 중 오류 발생: {ex.Message}";
        }

        UpdateSelectionSummary();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubfolderItem.IsSelected))
        {
            UpdateSelectionSummary();
        }
    }

    private void UpdateSelectionSummary()
    {
        var selectedItems = SubfolderItems.Where(i => i.IsSelected).ToList();
        var selectedCount = selectedItems.Count;
        var totalBytes = selectedItems.Sum(i => i.SizeBytes);

        SelectionSummaryTextBlock.Text = $"선택됨: {selectedCount}개 폴더 ({FormatBytes(totalBytes)})";
        DeleteSelectedButton.IsEnabled = selectedCount > 0;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SubfolderItems)
        {
            item.IsSelected = true;
        }
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SubfolderItems)
        {
            item.IsSelected = false;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadSubfolders();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = SubfolderItems.Where(i => i.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            System.Windows.MessageBox.Show("삭제할 하위 폴더를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Process Lock Check for each selected folder
        foreach (var item in selectedItems)
        {
            if (IsFolderProcessRunning(item.FullPath, out var runningExe))
            {
                System.Windows.MessageBox.Show(
                    $"선택한 '{item.FolderName}' 폴더 내의 에뮬레이터 프로세스({runningExe}.exe)가 실행 중입니다.\n에뮬레이터를 종료한 후 다시 시도하세요.",
                    "프로세스 실행 중 경고",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        var totalBytes = selectedItems.Sum(i => i.SizeBytes);
        var confirmResult = System.Windows.MessageBox.Show(
            $"선택한 {selectedItems.Count}개의 버전 폴더를 삭제하시겠습니까?\n\n- 대상 폴더: {string.Join(", ", selectedItems.Select(i => i.FolderName))}\n- 예상 확보 용량: {FormatBytes(totalBytes)}\n\n※ 삭제된 폴더는 복구할 수 없습니다.",
            "하위 폴더 영구 삭제 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        int successCount = 0;
        int failCount = 0;

        foreach (var item in selectedItems)
        {
            try
            {
                Directory.Delete(item.FullPath, recursive: true);
                successCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                StatusMessageTextBlock.Text = $"'{item.FolderName}' 삭제 실패: {ex.Message}";
            }
        }

        System.Windows.MessageBox.Show(
            failCount == 0
                ? $"{successCount}개의 하위 폴더를 성공적으로 삭제했습니다."
                : $"{successCount}개 삭제 성공, {failCount}개 삭제 실패하였습니다.",
            "삭제 완료",
            MessageBoxButton.OK,
            failCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        LoadSubfolders();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static bool IsFolderProcessRunning(string targetFolder, out string runningExeName)
    {
        runningExeName = string.Empty;
        try
        {
            if (!Directory.Exists(targetFolder)) return false;

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
        catch { }

        return false;
    }

    private static readonly HashSet<string> ProtectedUserFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bios", "saves", "savestates", "cheats", "shaders", "textures", "memcards",
        "states", "screenshots", "logs", "cache", "config", "covers", "dump",
        "sstates", "lang", "languages", "themes", "system", "games", "roms", "keys", "portable"
    };

    public static bool IsValidVersionSubfolder(
        string dirPath,
        string folderName,
        string? installedVersion,
        string? emulatorName = null)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;

        // Rule 1: Exclude system/hidden directories like .git, $RECYCLE.BIN
        if (folderName.StartsWith(".") || folderName.StartsWith("$")) return false;

        // Rule 2: Exclude standard non-version user data subfolders
        if (ProtectedUserFolders.Contains(folderName)) return false;

        var nameClean = folderName.Trim();
        var instVersionClean = installedVersion?.Trim();
        var emuNameClean = emulatorName?.Trim();

        // Rule 3: Folder name equals InstalledVersion or contains InstalledVersion (e.g. "1.3.340", "v1.3.340", "Ryujinx-1.3.340")
        if (!string.IsNullOrWhiteSpace(instVersionClean) && instVersionClean.Length >= 2 &&
            nameClean.Contains(instVersionClean, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Rule 4: Folder name matches Release Date pattern (e.g. "2026-07-28_10-55", "2026-07-28")
        if (System.Text.RegularExpressions.Regex.IsMatch(nameClean, @"\d{4}-\d{2}-\d{2}"))
        {
            return true;
        }

        // Rule 5: Folder name matches strict Version Number format (e.g. "v1.18.0", "1.3.340", "2.7.494")
        if (System.Text.RegularExpressions.Regex.IsMatch(nameClean, @"^(v?\d+\.\d+(\.\d+)*([-_].*)?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        // Rule 6: If executable is inside, check if executable name matches emulator identity (e.g. Ryujinx.exe vs Ryujinx)
        try
        {
            if (Directory.Exists(dirPath))
            {
                var exeFiles = Directory.GetFiles(dirPath, "*.exe", SearchOption.TopDirectoryOnly);
                if (exeFiles.Length > 0 && !string.IsNullOrWhiteSpace(emuNameClean))
                {
                    var emuKey = emuNameClean.Replace(" ", "").Replace("-", "").Replace("_", "");
                    foreach (var exeFile in exeFiles)
                    {
                        var exeKey = Path.GetFileNameWithoutExtension(exeFile).Replace(" ", "").Replace("-", "").Replace("_", "");
                        if (exeKey.Contains(emuKey, StringComparison.OrdinalIgnoreCase) ||
                            emuKey.Contains(exeKey, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore I/O errors
        }

        return false;
    }

    private static long CalculateDirectorySize(string directoryPath)
    {
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            return dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch
        {
            return 0L;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public sealed class SubfolderItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string FolderName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string FormattedSize => FormatBytes(SizeBytes);
    public DateTime LastModified { get; init; }
    public string FormattedDate => LastModified.ToString("yyyy-MM-dd HH:mm");
    public string ExecutableName { get; init; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
