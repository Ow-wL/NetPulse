using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

public sealed class HighContrastTests
{
    public static TheoryData<string> Sets => new() { "aquatic", "desert" };

    private static SystemColorSet Get(string name) => name == "aquatic" ? SystemColorSet.Aquatic : SystemColorSet.Desert;

    [Theory, MemberData(nameof(Sets))]
    public void MappedPaletteKeepsSystemTextReadable(string name)
    {
        var p = Get(name).ToPalette();
        Assert.True(Contrast.Ratio(p.Ink, p.Bg) >= 7, "본문");
        Assert.True(Contrast.Ratio(p.Muted, p.Bg) >= 4.5, "보조");
        Assert.True(Contrast.Ratio(p.AccentInk, p.Accent) >= 4.5, "선택 글자");
        Assert.True(Math.Max(Contrast.Ratio(p.FocusOuter, p.Bg), Contrast.Ratio(p.FocusInner, p.FocusOuter)) >= 3, "초점");
    }

    [Theory, MemberData(nameof(Sets))]
    public void StatusColorsCollapseToTextColor(string name)
    {
        // 고대비에서는 색으로 상태를 구분하지 않는다 (아이콘·글자로)
        var p = Get(name).ToPalette();
        Assert.Equal(p.Ink, p.Ok);
        Assert.Equal(p.Ink, p.Bad);
    }

    [Theory, MemberData(nameof(Sets))]
    public void SecondaryTextIsWindowTextAndOnlyDisabledIsGrayText(string name)
    {
        // GrayText는 Windows에서 비활성 글의 색: 보조 글·사이드바 메뉴에 쓰면 켜진 것이 꺼진 것처럼 보인다 (M11 C3)
        var set = Get(name);
        var p = set.ToPalette();
        Assert.Equal(set.WindowText, p.Muted);
        Assert.Equal(set.GrayText, p.Disabled);
        Assert.NotEqual(p.Muted, p.Disabled);
        Assert.True(Contrast.Ratio(p.Disabled, p.Bg) >= 4.5, "비활성 글");
    }

    [Fact]
    public void DisabledTextMatchesMutedOutsideHighContrast()
    {
        Assert.Equal(Palette.Dark.Muted, Palette.Dark.Disabled);
        Assert.Equal(Palette.Light.Muted, Palette.Light.Disabled);
    }

    [Fact]
    public void OpaqueSurfacesPassTextContrast()
    {
        foreach (var theme in new[] { ThemeKind.Dark, ThemeKind.Light })
            foreach (var level in Enum.GetValues<GlassLevel>())
            {
                var p = Palette.For(theme);
                var s = Materials.OpaqueSurface(theme, level);
                Assert.True(s.A >= 1);
                Assert.True(Contrast.Ratio(p.Ink, s) >= 4.5, $"{theme} {level}");
            }
    }
}
