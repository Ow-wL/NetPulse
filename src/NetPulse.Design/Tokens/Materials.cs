namespace NetPulse.Design.Tokens;

/// <summary>
/// 유리 단계. 떠 있는 유리(<see cref="Float"/>)는 글자·그래프 위에도 뜨므로 글자는 본문색(ink)만 쓴다 (보조색 금지).
/// 그래서 얇다는 느낌은 틴트가 아니라 그림자 깊이와 테두리로 낸다 (M2 구현 판정, DECISIONS).
/// </summary>
public enum GlassLevel { Content, Control, Float, Dialog }

/// <summary>유리 한 단계의 값. 틴트를 이 불투명도로 배경에 합성한 것이 표면색 모델이다 (M2 설계 4.2, 4.3).</summary>
public sealed record GlassMaterial(GlassLevel Level, Rgba Tint, double TintOpacity, double ShadowDepth)
{
    public Rgba TintWithAlpha => Tint.WithAlpha(TintOpacity);
}

/// <summary>배경 빛 하나: 색과 불투명도 상한.</summary>
public sealed record LightSource(string Name, Rgba Color, double MaxOpacity)
{
    public Rgba Layer => Color.WithAlpha(MaxOpacity);
}

/// <summary>
/// 재질과 배경 층 (M2 설계 4.2, 4.3).
/// 유리 뒤에 올 수 있는 것은 바탕 → 배경 빛 4개 → (켜면) 오로라 띠 3개 순서로 그린 것뿐이다.
/// </summary>
public static class Materials
{
    public static GlassMaterial Glass(ThemeKind theme, GlassLevel level) => (theme, level) switch
    {
        (ThemeKind.Dark, GlassLevel.Content) => new(level, Rgba.Hex("#0e0e12"), 0.70, 32),
        (ThemeKind.Dark, GlassLevel.Control) => new(level, Rgba.Hex("#0e0e12"), 0.52, 20),
        (ThemeKind.Dark, GlassLevel.Float) => new(level, Rgba.Hex("#0e0e12"), 0.62, 40),
        (ThemeKind.Dark, GlassLevel.Dialog) => new(level, Rgba.Hex("#0e0e12"), 0.52, 40),
        (ThemeKind.Light, GlassLevel.Content) => new(level, Rgba.Hex("#ffffff"), 0.72, 32),
        (ThemeKind.Light, GlassLevel.Control) => new(level, Rgba.Hex("#ffffff"), 0.55, 20),
        (ThemeKind.Light, GlassLevel.Float) => new(level, Rgba.Hex("#ffffff"), 0.60, 40),
        (ThemeKind.Light, GlassLevel.Dialog) => new(level, Rgba.Hex("#ffffff"), 0.55, 40),
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>배경 빛 원 4개. 움직이므로 모두 한 점에서 겹칠 수 있다고 본다.</summary>
    public static IReadOnlyList<LightSource> Lights(ThemeKind theme, bool problem) => (theme, problem) switch
    {
        (ThemeKind.Dark, false) =>
        [
            new("a", Rgba.Hex("#1e293b"), 0.6), new("b", Rgba.Hex("#0c4a6e"), 0.6),
            new("c", Rgba.Hex("#18181b"), 0.6), new("d", Rgba.Hex("#27272a"), 0.3),
        ],
        (ThemeKind.Dark, true) =>
        [
            new("a", Rgba.Hex("#4c0519"), 0.6), new("b", Rgba.Hex("#7c2d12"), 0.6),
            new("c", Rgba.Hex("#18181b"), 0.6), new("d", Rgba.Hex("#27272a"), 0.3),
        ],
        (ThemeKind.Light, false) =>
        [
            new("a", Rgba.Hex("#e7e5e4"), 0.9), new("b", Rgba.Hex("#dbeafe"), 0.9),
            new("c", Rgba.Hex("#fafaf9"), 0.9), new("d", Rgba.Hex("#e4e4e7"), 0.45),
        ],
        (ThemeKind.Light, true) =>
        [
            new("a", Rgba.Hex("#fecdd3"), 0.85), new("b", Rgba.Hex("#fed7aa"), 0.85),
            new("c", Rgba.Hex("#fafaf9"), 0.85), new("d", Rgba.Hex("#e4e4e7"), 0.425),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(theme)),
    };

    /// <summary>
    /// 효과 줄이기·저성능 대체·전환 중에 쓰는 불투명 표면색 (M2 설계 4.2).
    /// 틴트를 배경 범위의 두 경계 중 본문색과 대비가 더 낮아지는 쪽에 합성한 색이다. 그래서 계산 대비가 그대로 성립한다.
    /// </summary>
    public static Rgba OpaqueSurface(ThemeKind theme, GlassLevel level)
    {
        var p = Palette.For(theme);
        var range = BackgroundRange.For(theme, false, false).Union(BackgroundRange.For(theme, true, false));
        var tint = Glass(theme, level).TintWithAlpha;
        var low = tint.Over(range.Low);
        var high = tint.Over(range.High);
        return Contrast.Ratio(p.Ink, low) <= Contrast.Ratio(p.Ink, high) ? low : high;
    }

    /// <summary>연한 오로라 띠 3개 (켰을 때만). 불투명도는 띠 자체 × 층 불투명도를 합친 상한.</summary>
    public static IReadOnlyList<LightSource> Aurora(ThemeKind theme) => theme == ThemeKind.Dark
        ? [new("teal", Rgba.Hex("#5eead4"), 0.10), new("violet", Rgba.Hex("#a78bfa"), 0.08), new("sky", Rgba.Hex("#7dd3fc"), 0.10)]
        : [new("teal", Rgba.Hex("#99f6e4"), 0.30), new("violet", Rgba.Hex("#c4b5fd"), 0.25), new("sky", Rgba.Hex("#bae6fd"), 0.30)];
}
