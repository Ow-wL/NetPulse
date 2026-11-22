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
        (ThemeKind.Dark, GlassLevel.Dialog) => new(level, Rgba.Hex("#0e0e12"), 0.62, 40),   // M2 이월 2: 뒤에 글자·그래프가 와도 보조 글자 4.5 (0.52 → 0.62)
        // 라이트의 그림자 깊이는 다크보다 얕게 (밝은 바탕에서 그림자가 시안보다 진하게 보였다, G4 M2 1라운드 시각 검토).
        // M2 이월 3: 세 두께가 비슷해 보여 단계 사이 깊이 차이를 벌린다 (16·10·20 → 18·6·28). 틴트·불투명도는 대비 때문에 그대로
        (ThemeKind.Light, GlassLevel.Content) => new(level, Rgba.Hex("#ffffff"), 0.72, 18),
        (ThemeKind.Light, GlassLevel.Control) => new(level, Rgba.Hex("#ffffff"), 0.55, 6),
        (ThemeKind.Light, GlassLevel.Float) => new(level, Rgba.Hex("#ffffff"), 0.60, 28),
        (ThemeKind.Light, GlassLevel.Dialog) => new(level, Rgba.Hex("#ffffff"), 0.86, 20),   // M2 이월 2 (0.55 → 0.86)
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
    /// 효과 줄이기·저성능 대체·전환 중에 쓰는 불투명 표면색 (M2 설계 4.2, M12 설계 E).
    /// 틴트를 배경 범위의 두 경계 중 본문색과 대비가 더 낮아지는 쪽에 합성한 색을 기준으로 한다 (계산 대비가 그대로 성립).
    /// M12: 경계색은 채널마다 최솟값·최댓값을 모은 색이라 다크는 자줏빛, 라이트는 베이지로 물들었다 (M11 시각 검토 C3).
    /// 그래서 같은 휘도(본문색과의 대비가 같거나 높은 쪽)의 무채색으로 바꾼다. 대비는 휘도로만 정해지므로 보장은 그대로다.
    /// </summary>
    public static Rgba OpaqueSurface(ThemeKind theme, GlassLevel level)
    {
        var p = Palette.For(theme);
        var range = BackgroundRange.For(theme, false, false).Union(BackgroundRange.For(theme, true, false));
        var tint = Glass(theme, level).TintWithAlpha;
        var low = tint.Over(range.Low);
        var high = tint.Over(range.High);
        var worst = Contrast.Ratio(p.Ink, low) <= Contrast.Ratio(p.Ink, high) ? low : high;
        return NeutralLike(worst, p.Ink);
    }

    /// <summary>색과 휘도가 가장 가까운 회색 가운데 <paramref name="ink"/>와의 대비가 원래 색보다 낮지 않은 것.</summary>
    public static Rgba NeutralLike(Rgba color, Rgba ink)
    {
        double target = Contrast.Luminance(color), floor = Contrast.Ratio(ink, color);
        Rgba best = color;
        double bestGap = double.MaxValue;
        for (int v = 0; v <= 255; v++)
        {
            var g = new Rgba((byte)v, (byte)v, (byte)v);
            if (Contrast.Ratio(ink, g) < floor) continue;
            double gap = Math.Abs(Contrast.Luminance(g) - target);
            if (gap < bestGap) { bestGap = gap; best = g; }
        }
        return best;
    }

    /// <summary>
    /// 흑요석 미세 결 (M2 설계 5.5 "M11 재검토", 사용자 결정 "M12에서 넣기", M12 설계 E). 시안 CSS 그대로:
    /// 3 DIP 격자, 점 반지름 0.6 DIP, 다크는 흰 점 0.8·라이트는 검은 점 0.7, 층 불투명도 0.06. 투명 효과를 쓸 때만 보인다(불투명·고대비에서는 끈다).
    /// </summary>
    public static class Grain
    {
        public const double Spacing = 3, Radius = 0.6, LayerOpacity = 0.06;
        public static Rgba Dot(ThemeKind theme) => theme == ThemeKind.Dark ? new Rgba(255, 255, 255, 0.8) : new Rgba(0, 0, 0, 0.7);
    }

    /// <summary>결 층 (배경 범위 계산용): 점 색, 불투명도 상한 = 점 불투명도 × 층 불투명도 (점이 다 덮은 화소).</summary>
    public static LightSource GrainLayer(ThemeKind theme)
    {
        var dot = Grain.Dot(theme);
        return new LightSource("grain", dot.WithAlpha(1), dot.A * Grain.LayerOpacity);
    }

    /// <summary>연한 오로라 띠 3개 (켰을 때만). 불투명도는 띠 자체 × 층 불투명도를 합친 상한.</summary>
    public static IReadOnlyList<LightSource> Aurora(ThemeKind theme) => theme == ThemeKind.Dark
        ? [new("teal", Rgba.Hex("#5eead4"), 0.10), new("violet", Rgba.Hex("#a78bfa"), 0.08), new("sky", Rgba.Hex("#7dd3fc"), 0.10)]
        : [new("teal", Rgba.Hex("#99f6e4"), 0.30), new("violet", Rgba.Hex("#c4b5fd"), 0.25), new("sky", Rgba.Hex("#bae6fd"), 0.30)];
}
