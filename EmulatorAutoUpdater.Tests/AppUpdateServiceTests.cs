using System.IO;
using Xunit;
using EmulatorAutoUpdater.Services;

namespace EmulatorAutoUpdater.Tests;

public class AppUpdateServiceTests
{
    [Theory]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.1.0", "v1.1.0", false)]
    [InlineData("1.1.0", "1.2.0", true)]
    [InlineData("1.1.0", "v1.2.0", true)]
    [InlineData("1.1.0", "2.0.0", true)]
    [InlineData("1.2.0", "1.1.0", false)]
    public void IsNewerVersion_ShouldCorrectlyCompareVersions(string current, string target, bool expectedResult)
    {
        var result = AppUpdateService.IsNewerVersion(current, target);
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void CreateUpdaterBatchScript_ShouldGenerateValidBatchFile()
    {
        var scriptPath = AppUpdateService.CreateUpdaterBatchScript(1234, @"C:\Temp\update.zip", @"C:\AppDir", @"C:\AppDir\EmulatorAutoUpdater.exe");
        Assert.True(File.Exists(scriptPath));

        var content = File.ReadAllText(scriptPath);
        Assert.Contains("1234", content);
        Assert.Contains(@"C:\AppDir", content);
        Assert.Contains("Expand-Archive", content);
    }
}
