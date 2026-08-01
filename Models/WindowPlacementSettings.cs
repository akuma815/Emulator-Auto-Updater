namespace EmulatorAutoUpdater.Models;

public sealed class WindowPlacementSettings
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1500;
    public double Height { get; set; } = 960;
    public bool IsMaximized { get; set; }
}
