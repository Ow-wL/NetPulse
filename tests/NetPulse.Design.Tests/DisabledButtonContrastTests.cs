using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

/// <summary>
/// 비활성 단추 대비 (M12 설계 F, DECISIONS "M12 비활성 단추 대비"). 사용자 결정: 비활성 글은 본문 기준 4.5:1에서 빼되 3:1 이상.
/// 단추 전체 불투명도 0.45(약 1.4~1.7:1)를 비활성 글·테두리 토큰으로 바꿨다. 비활성 단추의 바탕:
/// 주요·보조 단추는 보통(컨트롤) 유리, 고스트·아이콘 단추는 놓인 카드(콘텐츠) 유리나 대화상자 유리. 범위는 <see cref="ContrastMatrixTests.Surface"/>와 같은 모델.
/// 화면 실측은 UI 시험 M12ContrastTests.
/// </summary>
public sealed class DisabledButtonContrastTests
{
    private const double DisabledText = 3.0, Edge = 2.0;

    public static TheoryData<ThemeKind> Themes => new() { ThemeKind.Dark, ThemeKind.Light };
    public static TheoryData<string> Sets => new() { "aquatic", "desert" };

    private static readonly GlassLevel[] Under = [GlassLevel.Content, GlassLevel.Control, GlassLevel.Dialog];

    [Theory, MemberData(nameof(Themes))]
    public void DisabledInkReachesThreeToOneOnEveryButtonSurface(ThemeKind theme)
    {
        var p = Palette.For(theme);
        foreach (var level in Under)
        {
            double r = ContrastMatrixTests.Surface(theme, level).WorstRatio(p.DisabledInk);
            Assert.True(r >= DisabledText, $"{theme} {level} disabled ink: {r:0.00} < {DisabledText}");
            // 효과 줄이기·저성능 대체의 불투명 표면도
            double o = Contrast.Ratio(p.DisabledInk, Materials.OpaqueSurface(theme, level));
            Assert.True(o >= DisabledText, $"{theme} {level} opaque disabled ink: {o:0.00}");
        }
    }

    [Theory, MemberData(nameof(Themes))]
    public void DisabledEdgeIsVisibleOnButtonSurfaces(ThemeKind theme)
    {
        // 비활성 요소의 테두리는 WCAG 1.4.11 예외지만, 점선 모양이 누를 수 없음을 알리므로 보이게 둔다 (설계 판단 2:1)
        var p = Palette.For(theme);
        foreach (var level in Under)
        {
            double r = ContrastMatrixTests.Surface(theme, level).WorstRatio(p.DisabledEdge);
            Assert.True(r >= Edge, $"{theme} {level} disabled edge: {r:0.00} < {Edge}");
        }
    }

    [Theory, MemberData(nameof(Themes))]
    public void DisabledInkIsClearlyDifferentFromEnabledInk(ThemeKind theme)
    {
        // 활성 글(본문색)과 휘도가 확실히 달라야 한다 (색상만의 차이가 아님). 보조 글과도 다른 값이다
        var p = Palette.For(theme);
        Assert.True(Contrast.Ratio(p.DisabledInk, p.Ink) >= 2.5, $"{theme}: 비활성 글과 본문색 {Contrast.Ratio(p.DisabledInk, p.Ink):0.00}");
        Assert.NotEqual(p.Muted, p.DisabledInk);
        // 비활성 글 토큰은 단추 전용이다: 입력 칸의 비활성 글(Disabled)은 그대로 보조 글
        Assert.Equal(p.Muted, p.Disabled);
    }

    [Theory, MemberData(nameof(Sets))]
    public void HighContrastDisabledButtonUsesGrayText(string name)
    {
        var set = name == "aquatic" ? SystemColorSet.Aquatic : SystemColorSet.Desert;
        var p = set.ToPalette();
        Assert.Equal(set.GrayText, p.DisabledInk);
        Assert.Equal(set.GrayText, p.DisabledEdge);
        // 고대비 유리는 Window 색으로 그린다
        Assert.True(Contrast.Ratio(p.DisabledInk, p.Bg) >= DisabledText, $"{name}: GrayText/Window {Contrast.Ratio(p.DisabledInk, p.Bg):0.00}");
    }
}
