using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace EmulatorAutoUpdater.Models;

public sealed class EmulatorConfig : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string _folder = string.Empty;
    private string _repository = string.Empty;
    private string _assetPattern = string.Empty;
    private string _installedVersion = string.Empty;
    private DateTimeOffset? _lastDownloadedAt;
    private string _statusText = "미확인";
    private string _statusType = "Unknown";
    private string _latestVersion = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Folder
    {
        get => _folder;
        set => SetProperty(ref _folder, value);
    }

    public string Repository
    {
        get => _repository;
        set => SetProperty(ref _repository, value);
    }

    public string AssetPattern
    {
        get => _assetPattern;
        set => SetProperty(ref _assetPattern, value);
    }

    public string InstalledVersion
    {
        get => _installedVersion;
        set => SetProperty(ref _installedVersion, value);
    }

    public DateTimeOffset? LastDownloadedAt
    {
        get => _lastDownloadedAt;
        set => SetProperty(ref _lastDownloadedAt, value);
    }

    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    [JsonIgnore]
    public string StatusType
    {
        get => _statusType;
        set => SetProperty(ref _statusType, value);
    }

    [JsonIgnore]
    public string LatestVersion
    {
        get => _latestVersion;
        set => SetProperty(ref _latestVersion, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
