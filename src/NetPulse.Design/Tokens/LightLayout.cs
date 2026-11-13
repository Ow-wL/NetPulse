namespace NetPulse.Design.Tokens;

/// <summary>
/// 배경 빛 하나의 자리 (M2 설계 5.5). 왼쪽·위는 창 너비·높이에 대한 비율, 크기와 흐름 거리는 창의 긴 변에 대한 비율.
/// 값은 M0 흑요석 시안의 배치(1440×900 기준)를 옮긴 것이다.
/// </summary>
public sealed record LightPlacement(double Left, double Top, double Size, double DriftX, double DriftY, double DriftScale);

/// <summary>오로라 띠 하나의 자리. 각도는 도.</summary>
public sealed record RibbonPlacement(double Left, double Top, double Width, double Height, double Angle, double DriftX);

public static class LightLayout
{
    public static IReadOnlyList<LightPlacement> Lights { get; } =
    [
        new(-0.12, -0.18, 0.58, 0.18, 0.14, 1.18),
        new(0.58, 0.08, 0.58, -0.22, 0.12, 0.88),
        new(0.22, 0.41, 0.58, 0.14, -0.18, 1.10),
        new(0.60, 0.62, 0.30, -0.22, 0.12, 0.88),
    ];

    public static IReadOnlyList<RibbonPlacement> Ribbons { get; } =
    [
        new(-0.20, 0.05, 1.40, 0.16, -18, 0.10),
        new(-0.20, 0.38, 1.40, 0.12, -14, -0.08),
        new(-0.20, 0.70, 1.40, 0.10, -20, 0.06),
    ];

    /// <summary>원형 그라디언트 가장자리: 중심 불투명 → 이 비율부터 옅어져 → 끝에서 투명.</summary>
    public const double FalloffStart = 0.15;
}

/// <summary>
/// 대비 시험 장면 (M2 설계 5.7, 기획서 7.8). 유리 뒤에 일부러 까는 가장 불리한 배경이다. 제품 화면에서는 쓰지 않는다.
/// 무늬 색은 허용된 배경 범위(설계 4.3)에서 만든다: 범위의 가장 어두운·밝은 경계를 번갈아 까는 바둑판(강한 경계, 흑백 대응),
/// 빛 하나씩을 최대 불투명도로 깐 띠(고채도 대응), 떠 있는 유리 뒤를 지나는 그래프 선.
/// 범위 밖의 색(순백, 원색)은 이 앱에서 유리 뒤에 오지 않으므로 넣지 않는다 (구현 판정, DECISIONS).
/// </summary>
public static class ContrastScene
{
    public const int CheckerCells = 12;

    /// <summary>테마의 무늬 색: 경계 두 색과 빛 하나씩 합성한 색들 (정상·끊김, 오로라 포함).</summary>
    public static IReadOnlyList<Rgba> Swatches(ThemeKind theme)
    {
        var bg = Palette.For(theme).Bg;
        var all = BackgroundRange.For(theme, false, true).Union(BackgroundRange.For(theme, true, true));
        var list = new List<Rgba> { all.Low, all.High };
        foreach (var l in Materials.Lights(theme, false).Concat(Materials.Lights(theme, true)).Concat(Materials.Aurora(theme)))
            list.Add(l.Layer.Over(bg));
        return list;
    }
}
