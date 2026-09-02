using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EmulatorAutoUpdater.ViewModels;
using Forms = System.Windows.Forms;

namespace EmulatorAutoUpdater;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _closeAfterSaving;
    private bool _savingBeforeClose;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/EmulatorAutoUpdater.ico", UriKind.Absolute);
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
        }
        catch
        {
            // Fallback if pack URI fails
        }

        _viewModel = new MainWindowViewModel(OpenFolderDialog);
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        LocationChanged += MainWindow_LocationChanged;
        SizeChanged += MainWindow_SizeChanged;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        await _viewModel.LoadSettingsAsync();
        ApplySavedWindowPlacement();
        ApplySavedGridColumnWidths();
        CaptureWindowPlacement();
        CaptureGridColumnWidths();

        _ = _viewModel.CheckAppSelfUpdateAsync();

        if (_viewModel.CheckAllUpdatesOnStartup)
        {
            await _viewModel.CheckAllUpdatesAsync();
        }
    }

    private void ApplySavedWindowPlacement()
    {
        var placement = _viewModel.WindowPlacement;
        if (placement == null)
        {
            return;
        }

        var width = Math.Max(MinWidth, placement.Width);
        var height = Math.Max(MinHeight, placement.Height);
        var requestedBounds = new Rect(placement.Left, placement.Top, width, height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var visibleBounds = Rect.Intersect(requestedBounds, virtualScreen);

        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        if (visibleBounds.Width >= 100 && visibleBounds.Height >= 100)
        {
            Left = placement.Left;
            Top = placement.Top;
        }
        else
        {
            Left = virtualScreen.Left + Math.Max(0, (virtualScreen.Width - width) / 2);
            Top = virtualScreen.Top + Math.Max(0, (virtualScreen.Height - height) / 2);
        }

        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void CaptureWindowPlacement()
    {
        if (!IsLoaded)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        _viewModel.UpdateWindowPlacement(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            WindowState == WindowState.Maximized);
    }

    private void ApplySavedGridColumnWidths()
    {
        var savedWidths = _viewModel.EmulatorGridColumnWidths;
        if (savedWidths.Count == 0)
        {
            return;
        }

        var resizableColumns = EmulatorDataGrid.Columns
            .Where(column => column.Visibility == Visibility.Visible && column.CanUserResize)
            .ToList();
        var count = Math.Min(savedWidths.Count, resizableColumns.Count);
        var totalSaved = savedWidths.Take(count).Sum();

        for (var index = 0; index < count; index++)
        {
            var weight = totalSaved > 0 ? (savedWidths[index] / totalSaved) : 1.0;
            resizableColumns[index].Width = new DataGridLength(weight, DataGridLengthUnitType.Star);
        }
    }

    private void CaptureGridColumnWidths()
    {
        if (!IsLoaded)
        {
            return;
        }

        _viewModel.UpdateEmulatorGridColumnWidths(
            EmulatorDataGrid.Columns
                .Where(column => column.Visibility == Visibility.Visible && column.CanUserResize)
                .Select(column => column.ActualWidth));
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e) => CaptureWindowPlacement();

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => CaptureWindowPlacement();

    private void MainWindow_StateChanged(object? sender, EventArgs e) => CaptureWindowPlacement();

    private void EmulatorDataGrid_ColumnHeaderDragCompleted(object sender, DragCompletedEventArgs e) =>
        CaptureGridColumnWidths();

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterSaving)
        {
            return;
        }

        e.Cancel = true;
        if (_savingBeforeClose)
        {
            return;
        }

        _savingBeforeClose = true;
        CaptureWindowPlacement();
        CaptureGridColumnWidths();
        await _viewModel.SaveSettingsForShutdownAsync();
        _closeAfterSaving = true;
        Close();
    }

    private string? OpenFolderDialog()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "에뮬레이터가 설치된 폴더를 선택하세요.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void ActivityLogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }

    private void ManageSubfolders_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedEmulator != null)
        {
            try
            {
                var window = new SubfolderManagerWindow(vm.SelectedEmulator)
                {
                    Owner = this
                };
                window.ShowDialog();
                vm.RefreshSelectedEmulatorStatus();
            }
            catch (Exception ex)
            {
                var friendlyMsg = EmulatorAutoUpdater.Services.FriendlyExceptionHelper.FormatUserFriendlyErrorMessage(ex, "하위 폴더 관리 창 열기");
                System.Windows.MessageBox.Show(friendlyMsg, "안내", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            System.Windows.MessageBox.Show("하위 폴더를 관리할 에뮬레이터를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
