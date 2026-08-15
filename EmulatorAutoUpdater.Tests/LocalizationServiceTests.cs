using System.Linq;
using EmulatorAutoUpdater.Services;
using Xunit;

namespace EmulatorAutoUpdater.Tests;

public class LocalizationServiceTests
{
    [Theory]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("KO", "ko-KR")]
    [InlineData("en-US", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("ja", "ja-JP")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("invalid-lang", "ko-KR")]
    [InlineData(null, "ko-KR")]
    public void NormalizeLanguageCode_ReturnsExpectedLanguageCode(string? input, string expected)
    {
        var result = LocalizationService.NormalizeLanguageCode(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    [System.STAThread]
    public void SupportedLanguages_ContainsFourLanguages()
    {
        var languages = LocalizationService.SupportedLanguages;
        Assert.Equal(4, languages.Count);
        Assert.Contains(languages, l => l.Code == "ko-KR");
        Assert.Contains(languages, l => l.Code == "en-US");
        Assert.Contains(languages, l => l.Code == "ja-JP");
        Assert.Contains(languages, l => l.Code == "zh-CN");
    }

    [Fact]
    public void GetString_ReturnsKeyWhenNotInitialized()
    {
        var str = LocalizationService.GetString("NonExistentKey123");
        Assert.Equal("NonExistentKey123", str);
    }
}
