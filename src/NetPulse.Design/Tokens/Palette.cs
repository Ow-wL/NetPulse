namespace NetPulse.Design.Tokens;

public enum ThemeKind { Dark, Light }

/// <summary>
/// 한 테마의 색 토큰 (M2 설계 4.1). 출발점은 M0 흑요석 시안 (커밋 b772cdc).
/// 대비 계산(4.3)이나 실측(7.3)이 기준에 못 미치면 값을 고치고 DECISIONS에 적는다.
/// </summary>
public sealed record Palette(
    string Name,
    Rgba Bg,
    Rgba Ink,
    Rgba Muted,
    Rgba Faint,
    Rgba Accent,
    Rgba AccentInk,
    Rgba Series2,
    Rgba Ok,
    Rgba Warn,
    Rgba Bad,
    Rgba FocusOuter,
    Rgba FocusInner,
    Rgba Edge,
    Rgba Highlight,
    Rgba Scrim,
    Rgba Shadow)
{
    public static readonly Palette Dark = new(
        Name: "dark",
        Bg: Rgba.Hex("#050507"),
        Ink: Rgba.Hex("#f4f4f5"),
        Muted: Rgba.Hex("#a3a3ad"),
        Faint: Rgba.White(0.06),
        Accent: Rgba.Hex("#7dd3fc"),
        AccentInk: Rgba.Hex("#04131c"),
        Series2: Rgba.Hex("#a1a1aa"),
        Ok: Rgba.Hex("#34d399"),
        Warn: Rgba.Hex("#fbbf24"),
        Bad: Rgba.Hex("#fb7185"),
        FocusOuter: Rgba.Hex("#7dd3fc"),
        FocusInner: Rgba.Hex("#050507"),
        Edge: Rgba.White(0.10),
        Highlight: Rgba.White(0.35),
        Scrim: Rgba.Black(0.55),
        Shadow: Rgba.Black(0.70)) { Signal = Rgba.Hex("#a78bfa"), DisabledInk = Rgba.Hex("#8a8a94"), DisabledEdge = Rgba.Hex("#6b6b75") };

    public static readonly Palette Light = new(
        Name: "light",
        Bg: Rgba.Hex("#f4f4f2"),
        Ink: Rgba.Hex("#111113"),
        Muted: Rgba.Hex("#52525b"),
        Faint: Rgba.Black(0.05),
        Accent: Rgba.Hex("#0369a1"),
        AccentInk: Rgba.Hex("#ffffff"),
        Series2: Rgba.Hex("#71717a"),
        Ok: Rgba.Hex("#047857"),
        Warn: Rgba.Hex("#a16207"),
        Bad: Rgba.Hex("#be123c"),
        FocusOuter: Rgba.Hex("#0369a1"),
        FocusInner: Rgba.Hex("#111113"),   // 흰색이면 글자 위에 뜬 얇은 유리(중간 밝기)에서 3:1이 안 나온다
        Edge: Rgba.Black(0.08),
        Highlight: Rgba.White(1.0),
        Scrim: Rgba.Black(0.25),
        Shadow: Rgba.Black(0.10)) { Signal = Rgba.Hex("#6d28d9"), DisabledInk = Rgba.Hex("#71717a"), DisabledEdge = Rgba.Hex("#9a9aa3") };

    public static Palette For(ThemeKind theme) => theme == ThemeKind.Dark ? Dark : Light;

    /// <summary>
    /// Wi‑Fi 신호 품질 선 (M12 설계 C 표시): 지연 선(Accent 실선·Series2 파선)과 다른 색, 점선으로 그린다. 그래프 바탕과 3:1 이상
    /// (Design 시험 M12SignalColorTests). 고대비에서는 시스템 색 Hotlight. 리포트 문서 색(<c>ReportPalette.Signal</c>)은 라이트 값과 같다.
    /// </summary>
    public Rgba Signal { get; init; }

    /// <summary>비활성 글을 보조 글과 다르게 둘 때만 (고대비: GrayText). 없으면 <see cref="Muted"/>.</summary>
    public Rgba? DisabledText { get; init; }

    /// <summary>비활성 글 색. 고대비에서는 시스템 GrayText(보조 글은 WindowText), 그 밖에는 보조 글과 같다.</summary>
    public Rgba Disabled => DisabledText ?? Muted;

    /// <summary>
    /// 비활성 단추의 글·아이콘 (M12 설계 F, DECISIONS "M12 비활성 단추 대비"): 단추 전체를 불투명도로 흐리던 것(약 1.4~1.7:1)을 대신한다.
    /// 비활성 단추는 컨트롤 유리 위에 그리므로 그 유리의 가장 불리한 범위에서 3:1 이상, 본문색보다 확실히 어둡게(라이트는 밝게) 고른다
    /// (Design 시험 DisabledButtonContrastTests). 고대비에서는 값을 두지 않아 시스템 GrayText(<see cref="Disabled"/>)가 된다.
    /// </summary>
    public Rgba DisabledInk { get => disabledInk ?? Disabled; init => disabledInk = value; }
    private readonly Rgba? disabledInk;

    /// <summary>비활성 단추의 점선 테두리 (누를 수 없음을 색만이 아니라 모양으로도). 고대비에서는 GrayText.</summary>
    public Rgba DisabledEdge { get => disabledEdge ?? Disabled; init => disabledEdge = value; }
    private readonly Rgba? disabledEdge;
}
