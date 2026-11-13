namespace NetPulse.Design.Tokens;

/// <summary>시스템 색 (고대비에서 쓰는 이름들). M2 설계 5.2.</summary>
public sealed record SystemColorSet(
    string Name,
    Rgba Window,
    Rgba WindowText,
    Rgba GrayText,
    Rgba Highlight,
    Rgba HighlightText,
    Rgba Hotlight,
    Rgba ButtonFace,
    Rgba ButtonText)
{
    /// <summary>
    /// 시뮬레이션용 고정 팔레트. Windows 11 고대비 테마 "Aquatic"(어두운)과 "Desert"(밝은)에 해당하는 값으로 둔다.
    /// 이 값으로 시험하는 것은 앱의 매핑·분기뿐이다. 실제 OS 고대비 동작은 사용자 인수 항목이다.
    /// </summary>
    public static readonly SystemColorSet Aquatic = new("aquatic",
        Window: Rgba.Hex("#202020"), WindowText: Rgba.Hex("#ffffff"), GrayText: Rgba.Hex("#a6a6a6"),
        Highlight: Rgba.Hex("#8ee3f0"), HighlightText: Rgba.Hex("#263b50"), Hotlight: Rgba.Hex("#75e9fc"),
        ButtonFace: Rgba.Hex("#202020"), ButtonText: Rgba.Hex("#ffffff"));

    public static readonly SystemColorSet Desert = new("desert",
        Window: Rgba.Hex("#fffaef"), WindowText: Rgba.Hex("#3d3d3d"), GrayText: Rgba.Hex("#676767"),
        Highlight: Rgba.Hex("#903909"), HighlightText: Rgba.Hex("#fff5e3"), Hotlight: Rgba.Hex("#1c5e75"),
        ButtonFace: Rgba.Hex("#fffaef"), ButtonText: Rgba.Hex("#202020"));

    /// <summary>
    /// 고대비 팔레트로 옮긴다. 상태색은 쓰지 않고 본문색으로 (아이콘·글자로 구분). 초점은 강조(Highlight) 바깥 + 바탕 안쪽.
    /// </summary>
    public Palette ToPalette() => new(
        Name: "high-contrast-" + Name,
        Bg: Window,
        Ink: WindowText,
        Muted: GrayText,
        Faint: Window,
        Accent: Highlight,
        AccentInk: HighlightText,
        Series2: WindowText,
        Ok: WindowText,
        Warn: WindowText,
        Bad: WindowText,
        FocusOuter: Highlight,
        FocusInner: Window,
        Edge: WindowText,
        Highlight: Window.WithAlpha(0),
        Scrim: Window.WithAlpha(0.85),
        Shadow: Window.WithAlpha(0));
}
