namespace NetPulse.Design;

/// <summary>
/// WCAG 2.x 대비 계산 (M2 설계 4.3).
/// 유리 위 글자는 "허용된 배경 범위 안에서 가장 불리한 경우"로 판정한다:
/// 배경 범위의 색마다 유리 틴트를 알파 합성해 표면색을 만들고, 글자색과의 대비비 중 최솟값을 쓴다.
/// 가정: 아크릴의 블러는 배경을 평균 내므로 범위 안에 머문다. 잡음 질감과 광도 층은 넣지 않는다 (G5 캡처 실측으로 보완).
/// </summary>
public static class Contrast
{
    /// <summary>상대 휘도 (0~1). 불투명한 색만.</summary>
    public static double Luminance(Rgba c)
    {
        if (c.A < 1) throw new ArgumentException("불투명한 색이어야 합니다. 먼저 배경에 합성하세요.", nameof(c));
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>대비비 (1~21).</summary>
    public static double Ratio(Rgba a, Rgba b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// 배경 범위의 각 색 위에 표면(틴트, 반투명 가능)을 합성하고, 그 위의 전경(반투명 가능)과의 대비비 중 가장 낮은 값.
    /// </summary>
    public static WorstCase Worst(Rgba foreground, Rgba surface, IReadOnlyList<Rgba> backgrounds)
    {
        if (backgrounds.Count == 0) throw new ArgumentException("배경 범위가 비었습니다.", nameof(backgrounds));
        WorstCase? worst = null;
        foreach (var bg in backgrounds)
        {
            var under = surface.A >= 1 ? surface : surface.Over(bg);
            var fg = foreground.A >= 1 ? foreground : foreground.Over(under);
            double ratio = Ratio(fg, under);
            if (worst is null || ratio < worst.Value.Ratio) worst = new WorstCase(ratio, bg, under);
        }
        return worst!.Value;
    }
}

/// <summary>가장 불리한 조합: 대비비, 그때의 배경 빛 색, 합성된 표면색.</summary>
public readonly record struct WorstCase(double Ratio, Rgba Background, Rgba Surface);
