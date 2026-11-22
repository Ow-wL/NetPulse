using NetPulse.Core.Report;
using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

/// <summary>
/// Wi‑Fi 신호 품질 선 색 (M12 설계 C 표시): 지연 선과 다른 색, 그래프 바탕과 3:1 이상(기획서 그래프 기준), 고대비에서는 시스템 색,
/// 리포트 문서 색은 라이트 팔레트와 같다. 수치는 <c>docs/loop/m12/signal-display.md</c>에 적었다.
/// </summary>
public sealed class M12SignalColorTests
{
    private const double Graphic = 3.0;

    public static TheoryData<ThemeKind> Themes => new() { ThemeKind.Dark, ThemeKind.Light };

    private static void AtLeast(double minimum, double actual, string what) =>
        Assert.True(actual >= minimum, $"{what}: 대비 {actual:0.00} < {minimum}");

    [Theory, MemberData(nameof(Themes))]
    public void SignalLineIsVisibleOnTheGraphBackground(ThemeKind theme)
    {
        var p = Palette.For(theme);
        AtLeast(Graphic, Contrast.Ratio(p.Signal, p.Bg), $"{theme} signal on bg");
        // 타임라인은 콘텐츠 유리 카드 위에 그린다 (배경 범위의 가장 불리한 경우). 응답 없음 띠 위를 지나갈 때도
        var content = ContrastMatrixTests.Surface(theme, GlassLevel.Content);
        AtLeast(Graphic, content.WorstRatio(p.Signal), $"{theme} signal on content glass");
        AtLeast(Graphic, ContrastMatrixTests.Surface(theme, GlassLevel.Control).WorstRatio(p.Signal), $"{theme} signal on control glass");
        AtLeast(Graphic, content.Under(p.Bad.WithAlpha(Emphasis.LossBand)).WorstRatio(p.Signal), $"{theme} signal on loss band");
    }

    [Theory, MemberData(nameof(Themes))]
    public void SignalColorDiffersFromTheLatencyLinesAndMarkers(ThemeKind theme)
    {
        var p = Palette.For(theme);
        Assert.DoesNotContain(p.Signal, new[] { p.Accent, p.Series2, p.Warn, p.Bad, p.Ok, p.Muted });
        Assert.Equal(1, p.Signal.A);
    }

    [Fact]
    public void HighContrastUsesTheSystemHotlightColor()
    {
        foreach (var hc in new[] { SystemColorSet.Aquatic, SystemColorSet.Desert })
        {
            var p = hc.ToPalette();
            Assert.Equal(hc.Hotlight, p.Signal);
            AtLeast(Graphic, Contrast.Ratio(p.Signal, p.Bg), $"{hc.Name} hotlight on window");
        }
    }

    [Fact]
    public void ReportSignalColorIsTheLightPaletteColorAndVisibleOnPaper()
    {
        var doc = ReportPalette.Document;
        Assert.Equal(Palette.Light.Signal, Rgba.Hex(doc.Signal));
        AtLeast(Graphic, Contrast.Ratio(Rgba.Hex(doc.Signal), Rgba.Hex(doc.Surface)), "report signal on paper");
    }
}
