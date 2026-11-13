using NetPulse.Design.Tokens;

namespace NetPulse.Design;

/// <summary>
/// 유리 뒤 배경이 가질 수 있는 색의 보수적 범위 (M2 설계 4.3).
/// 1. 배경 빛·오로라 층의 모든 부분집합을 그리는 순서대로 바탕 위에 합성해 꼭짓점 색을 만든다.
/// 2. 블러는 이 색들의 가중 평균이다. 평균색의 각 채널은 꼭짓점의 채널 최솟값~최댓값 사이에 있고,
///    휘도는 채널마다 증가 함수의 합이므로 (채널별 최솟값 색의 휘도) ~ (채널별 최댓값 색의 휘도) 사이에 있다.
/// 3. 그래서 두 경계색 Low, High만 있으면 휘도 구간을 보수적으로 감쌀 수 있다.
/// </summary>
public sealed record BackgroundRange(Rgba Low, Rgba High, int VertexCount)
{
    public static BackgroundRange From(Rgba bg, IReadOnlyList<LightSource> layers)
    {
        if (bg.A < 1) throw new ArgumentException("바탕은 불투명해야 합니다.", nameof(bg));
        if (layers.Count > 16) throw new ArgumentException("층이 너무 많습니다.", nameof(layers));
        byte rMin = 255, gMin = 255, bMin = 255, rMax = 0, gMax = 0, bMax = 0;
        int count = 1 << layers.Count;
        for (int mask = 0; mask < count; mask++)
        {
            var c = bg;
            for (int i = 0; i < layers.Count; i++)
                if ((mask & (1 << i)) != 0) c = layers[i].Layer.Over(c);
            rMin = Math.Min(rMin, c.R); gMin = Math.Min(gMin, c.G); bMin = Math.Min(bMin, c.B);
            rMax = Math.Max(rMax, c.R); gMax = Math.Max(gMax, c.G); bMax = Math.Max(bMax, c.B);
        }
        return new BackgroundRange(new Rgba(rMin, gMin, bMin), new Rgba(rMax, gMax, bMax), count);
    }

    /// <summary>테마·상태·오로라에 따른 배경 범위.</summary>
    public static BackgroundRange For(ThemeKind theme, bool problem, bool aurora)
    {
        var layers = new List<LightSource>(Materials.Lights(theme, problem));
        if (aurora) layers.AddRange(Materials.Aurora(theme));
        return From(Palette.For(theme).Bg, layers);
    }

    /// <summary>두 범위를 모두 감싸는 범위 (예: 정상·끊김·오로라 켜고 끔을 한꺼번에).</summary>
    public BackgroundRange Union(BackgroundRange other) => new(
        new Rgba(Math.Min(Low.R, other.Low.R), Math.Min(Low.G, other.Low.G), Math.Min(Low.B, other.Low.B)),
        new Rgba(Math.Max(High.R, other.High.R), Math.Max(High.G, other.High.G), Math.Max(High.B, other.High.B)),
        VertexCount + other.VertexCount);

    /// <summary>이 범위 위에 반투명 표면(틴트)을 얹었을 때의 표면 범위. 합성은 채널마다 증가 함수라 경계가 그대로 옮겨 간다.</summary>
    public BackgroundRange Under(Rgba surface) =>
        surface.A >= 1 ? new BackgroundRange(surface, surface, VertexCount) : new BackgroundRange(surface.Over(Low), surface.Over(High), VertexCount);

    /// <summary>
    /// 이 범위(표면) 위의 불투명 전경색과의 가장 낮은 대비비.
    /// 표면 휘도 구간 [L(Low), L(High)] 안에 전경 휘도가 있으면 1 (구분 불가로 실패),
    /// 아니면 가까운 끝과의 대비비.
    /// </summary>
    public double WorstRatio(Rgba foreground)
    {
        if (foreground.A < 1) throw new ArgumentException("전경은 불투명해야 합니다.", nameof(foreground));
        double lf = Contrast.Luminance(foreground), lo = Contrast.Luminance(Low), hi = Contrast.Luminance(High);
        if (lf >= lo && lf <= hi) return 1;
        double nearest = lf < lo ? lo : hi;
        return (Math.Max(lf, nearest) + 0.05) / (Math.Min(lf, nearest) + 0.05);
    }
}
