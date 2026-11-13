using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

/// <summary>
/// 대비 매트릭스 (M2 설계 4.3, 7.2). 글자·선·초점 표시 × 유리 단계 × 테마를, 배경 범위의 가장 불리한 조건으로 판정한다.
/// 배경 범위는 정상·끊김 × 오로라 켬·끔을 모두 감싼다. 컨트롤·떠 있는 유리 뒤에는 콘텐츠 유리, 그래프·상태색, 글자도 올 수 있다.
/// </summary>
public sealed class ContrastMatrixTests
{
    private const double Text = 4.5, Graphic = 3.0;

    public static TheoryData<ThemeKind> Themes => new() { ThemeKind.Dark, ThemeKind.Light };

    private static BackgroundRange Backdrop(ThemeKind theme) =>
        BackgroundRange.For(theme, false, false)
            .Union(BackgroundRange.For(theme, true, false))
            .Union(BackgroundRange.For(theme, false, true))
            .Union(BackgroundRange.For(theme, true, true));

    /// <summary>단계별 유리 표면의 범위 (모델: 틴트를 배경 범위 위에 합성).</summary>
    internal static BackgroundRange Surface(ThemeKind theme, GlassLevel level)
    {
        var p = Palette.For(theme);
        var behind = Backdrop(theme);
        if (level is GlassLevel.Control or GlassLevel.Float)
        {
            // 컨트롤 유리는 콘텐츠 유리 위에 놓일 수 있다 (카드 안 버튼).
            // 스크롤 콘텐츠는 컨트롤 유리 밑으로 지나가지 않는다 (배치 규칙, M2 설계 4.3의 5).
            var content = behind.Under(Materials.Glass(theme, GlassLevel.Content).TintWithAlpha);
            behind = behind.Union(content);
        }
        if (level == GlassLevel.Float)
        {
            // 떠 있는 유리(펼침, 도움말)는 글자·그래프 선 위에도 뜬다
            foreach (var c in new[] { p.Ink, p.Muted, p.Accent, p.Series2, p.Ok, p.Warn, p.Bad })
                behind = behind.Union(new BackgroundRange(c, c, 1));
        }
        if (level == GlassLevel.Dialog)
            behind = Surface(theme, GlassLevel.Control).Union(behind).Under(p.Scrim);
        var surface = behind.Under(Materials.Glass(theme, level).TintWithAlpha);
        // 두꺼운 유리에는 포인터 하이라이트가 바탕 위(글자 아래)에 올 수 있다 (보통 유리에는 넣지 않는다)
        if (level is GlassLevel.Content)
            surface = surface.Union(surface.Under(Rgba.White(Emphasis.PointerGlow)));
        return surface;
    }

    private static void AtLeast(double minimum, double actual, string what) =>
        Assert.True(actual >= minimum, $"{what}: 대비 {actual:0.00} < {minimum}");

    [Theory, MemberData(nameof(Themes))]
    public void TextIsReadableOnEveryGlassLevel(ThemeKind theme)
    {
        var p = Palette.For(theme);
        foreach (var level in Enum.GetValues<GlassLevel>())
        {
            var surface = Surface(theme, level);
            AtLeast(Text, surface.WorstRatio(p.Ink), $"{theme} {level} ink");
            if (level != GlassLevel.Float)   // 떠 있는 유리 위 글자는 ink만 쓴다 (GlassLevel 설명)
                AtLeast(Text, surface.WorstRatio(p.Muted), $"{theme} {level} muted");
        }
    }

    [Theory, MemberData(nameof(Themes))]
    public void LinesStatusAndAccentAreVisibleOnContentAndControls(ThemeKind theme)
    {
        var p = Palette.For(theme);
        foreach (var level in new[] { GlassLevel.Content, GlassLevel.Control })
        {
            var surface = Surface(theme, level);
            AtLeast(Graphic, surface.WorstRatio(p.Accent), $"{theme} {level} accent");
            AtLeast(Graphic, surface.WorstRatio(p.Series2), $"{theme} {level} series2");
            AtLeast(Graphic, surface.WorstRatio(p.Ok), $"{theme} {level} ok");
            AtLeast(Graphic, surface.WorstRatio(p.Warn), $"{theme} {level} warn");
            AtLeast(Graphic, surface.WorstRatio(p.Bad), $"{theme} {level} bad");
        }
    }

    [Theory, MemberData(nameof(Themes))]
    public void TextOnAccentFillIsReadable(ThemeKind theme)
    {
        var p = Palette.For(theme);
        AtLeast(Text, Contrast.Ratio(p.AccentInk, p.Accent), $"{theme} accentInk on accent");
    }

    [Theory, MemberData(nameof(Themes))]
    public void FocusRingIsVisibleOnEverySurface(ThemeKind theme)
    {
        // 두 겹 초점 표시: 표면 휘도 구간의 어느 값에서든 두 색 중 하나는 3:1 이상이어야 한다
        var p = Palette.For(theme);
        foreach (var level in Enum.GetValues<GlassLevel>())
        {
            var s = Surface(theme, level);
            double lo = Contrast.Luminance(s.Low), hi = Contrast.Luminance(s.High);
            double lOuter = Contrast.Luminance(p.FocusOuter), lInner = Contrast.Luminance(p.FocusInner);
            for (int i = 0; i <= 200; i++)
            {
                double l = lo + (hi - lo) * i / 200;
                double best = Math.Max(Ratio(lOuter, l), Ratio(lInner, l));
                AtLeast(Graphic, best, $"{theme} {level} focus ring at luminance {l:0.000}");
            }
        }

        static double Ratio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Fact]
    public void BackgroundRangeCoversEveryVertexAndOverlap()
    {
        // 경계는 모든 부분집합 합성색을 감싼다 (4.3의 보수성)
        var lights = Materials.Lights(ThemeKind.Dark, false);
        var range = BackgroundRange.From(Palette.Dark.Bg, lights);
        Assert.Equal(16, range.VertexCount);
        var all = lights.Aggregate(Palette.Dark.Bg, (c, l) => l.Layer.Over(c));
        Assert.InRange(all.R, range.Low.R, range.High.R);
        Assert.InRange(all.G, range.Low.G, range.High.G);
        Assert.InRange(all.B, range.Low.B, range.High.B);
    }

    [Fact]
    public void ForegroundInsideTheSurfaceRangeFails()
    {
        var range = new BackgroundRange(Rgba.Hex("#000000"), Rgba.Hex("#ffffff"), 2);
        Assert.Equal(1, range.WorstRatio(Rgba.Hex("#808080")));
    }
}
