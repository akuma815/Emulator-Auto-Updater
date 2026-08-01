namespace EmulatorAutoUpdater.Models;

public sealed class BuildAsset
{
    public string Version { get; init; } = "";
    public DateTimeOffset PublishedAt { get; init; }
    public string AssetName { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
}
