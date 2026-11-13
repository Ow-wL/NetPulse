using NetPulse.Design.Effects;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

public sealed class EffectsPolicyTests
{
    private static readonly OsState Normal = new(true, true, false, false, true, SystemColorSet.Aquatic);

    [Fact]
    public void NormalIsAcrylicWithMovingLights()
    {
        var m = EffectsPolicy.Compute(Normal, AppSettings.Default, false);
        Assert.Equal(MaterialMode.Acrylic, m.Material);
        Assert.Equal(ThemeKind.Dark, m.Theme);
        Assert.True(m.LightsVisible);
        Assert.True(m.LightsMoving);
        Assert.False(m.AuroraVisible);   // 오로라 기본 끔
        Assert.False(m.ReduceMotion);
    }

    [Theory]
    [InlineData(false, false, false, "os-transparency-off")]
    [InlineData(true, true, false, "energy-saver")]
    [InlineData(true, false, true, "low-performance")]
    public void ReducedEffectsUseOpaqueSurfacesAndStillLights(bool transparency, bool energySaver, bool lowPerf, string reason)
    {
        var os = Normal with { TransparencyEnabled = transparency, EnergySaver = energySaver };
        var m = EffectsPolicy.Compute(os, AppSettings.Default with { Aurora = true }, lowPerf);
        Assert.Equal(MaterialMode.Opaque, m.Material);
        Assert.False(m.LightsMoving);
        Assert.False(m.AuroraVisible);
        Assert.Contains(reason, m.Reasons);
    }

    [Fact]
    public void AppReduceEffectsSettingAlsoReduces()
    {
        var m = EffectsPolicy.Compute(Normal, AppSettings.Default with { ReduceEffects = true }, false);
        Assert.Equal(MaterialMode.Opaque, m.Material);
    }

    [Fact]
    public void HighContrastWinsOverEverything()
    {
        var os = Normal with { HighContrast = true, TransparencyEnabled = false };
        var m = EffectsPolicy.Compute(os, AppSettings.Default with { Aurora = true }, true);
        Assert.Equal(MaterialMode.HighContrast, m.Material);
        Assert.False(m.LightsVisible);
        Assert.False(m.AuroraVisible);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ReduceMotionIsIndependent(bool osAnimations, bool appReduce, bool expected)
    {
        var m = EffectsPolicy.Compute(Normal with { AnimationsEnabled = osAnimations }, AppSettings.Default with { ReduceMotion = appReduce }, false);
        Assert.Equal(expected, m.ReduceMotion);
        Assert.Equal(MaterialMode.Acrylic, m.Material);   // 움직임 줄이기는 재질을 바꾸지 않는다
        if (expected) Assert.False(m.LightsMoving);
    }

    [Theory]
    [InlineData(ThemePreference.Dark, false, ThemeKind.Dark)]
    [InlineData(ThemePreference.Light, true, ThemeKind.Light)]
    [InlineData(ThemePreference.System, true, ThemeKind.Dark)]
    [InlineData(ThemePreference.System, false, ThemeKind.Light)]
    public void ThemePreferenceResolves(ThemePreference pref, bool systemDark, ThemeKind expected) =>
        Assert.Equal(expected, EffectsPolicy.Compute(Normal with { SystemPrefersDark = systemDark }, AppSettings.Default with { Theme = pref }, false).Theme);
}

public sealed class ReapplyTests
{
    private static readonly OsState Hc = new(true, true, true, false, true, SystemColorSet.Aquatic);

    [Fact]
    public void SystemColorChangeWhileHighContrastIsReapplied()
    {
        var m = EffectsPolicy.Compute(Hc, AppSettings.Default, false);
        var os2 = Hc with { SystemColors = SystemColorSet.Desert };
        var m2 = EffectsPolicy.Compute(os2, AppSettings.Default, false);
        Assert.True(EffectsPolicy.NeedsReapply(m, Hc, m2, os2));
    }

    [Fact]
    public void SystemColorChangeOutsideHighContrastIsIgnored()
    {
        var os = Hc with { HighContrast = false };
        var m = EffectsPolicy.Compute(os, AppSettings.Default, false);
        var os2 = os with { SystemColors = SystemColorSet.Desert };
        Assert.False(EffectsPolicy.NeedsReapply(m, os, EffectsPolicy.Compute(os2, AppSettings.Default, false), os2));
    }

    [Fact]
    public void SameInputsNeedNoReapply()
    {
        var m = EffectsPolicy.Compute(Hc, AppSettings.Default, false);
        Assert.False(EffectsPolicy.NeedsReapply(m, Hc, m, Hc));
    }
}
