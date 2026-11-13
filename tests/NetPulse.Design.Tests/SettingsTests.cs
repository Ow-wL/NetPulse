using NetPulse.Design.Settings;

namespace NetPulse.Design.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "netpulse-settings-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string FilePath => Path.Combine(folder, AppSettings.FileName);

    [Fact]
    public void FirstRunIsDarkWithEffectsOn()
    {
        var (s, status, _) = AppSettings.Load(folder);
        Assert.Equal(SettingsLoadStatus.Missing, status);
        Assert.Equal(ThemePreference.Dark, s.Theme);
        Assert.False(s.Aurora);
        Assert.False(s.ReduceEffects);
        Assert.False(s.ReduceMotion);
    }

    [Fact]
    public void SavedSettingsComeBack()
    {
        var saved = new AppSettings(ThemePreference.System, true, true, false);
        saved.Save(folder);
        var (s, status, _) = AppSettings.Load(folder);
        Assert.Equal(SettingsLoadStatus.Loaded, status);
        Assert.Equal(saved, s);
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Contains("\"version\": 1", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\": 1, \"theme\": \"purple\"}")]
    [InlineData("")]
    public void CorruptFileFallsBackToDefaultsAndIsKept(string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, content);
        var (s, status, _) = AppSettings.Load(folder);
        Assert.Equal(SettingsLoadStatus.Corrupt, status);
        Assert.Equal(AppSettings.Default, s);
        Assert.Equal(content, File.ReadAllText(FilePath));   // 지우거나 덮어쓰지 않는다
    }

    [Theory]
    [InlineData("{\"version\": 2, \"theme\": \"light\"}")]
    [InlineData("{\"theme\": \"light\"}")]
    public void UnknownVersionFallsBackToDefaults(string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, content);
        var (s, status, _) = AppSettings.Load(folder);
        Assert.Equal(SettingsLoadStatus.UnknownVersion, status);
        Assert.Equal(AppSettings.Default, s);
    }
}
