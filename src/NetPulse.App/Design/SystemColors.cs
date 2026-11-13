using NetPulse.Design;
using NetPulse.Design.Tokens;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace NetPulse.App.Design;

/// <summary>
/// 시스템 색 공급자 (M2 설계 5.2). OS에서 읽거나, 시뮬레이션이면 고정 팔레트(Aquatic, Desert)를 준다.
/// 정적 검사의 color-literal 허용 파일이다 (OS 색을 토큰 색 형식으로 옮기는 곳).
/// </summary>
public static class SystemColors
{
    public static SystemColorSet FromOs(UISettings ui)
    {
        Rgba Get(UIElementType t) { Color c = ui.UIElementColor(t); return new Rgba(c.R, c.G, c.B); }
        return new SystemColorSet("os",
            Window: Get(UIElementType.Window), WindowText: Get(UIElementType.WindowText), GrayText: Get(UIElementType.GrayText),
            Highlight: Get(UIElementType.Highlight), HighlightText: Get(UIElementType.HighlightText), Hotlight: Get(UIElementType.Hotlight),
            ButtonFace: Get(UIElementType.ButtonFace), ButtonText: Get(UIElementType.ButtonText));
    }

    public static SystemColorSet Simulated(string name) => name == "desert" ? SystemColorSet.Desert : SystemColorSet.Aquatic;

    /// <summary>Windows가 다크 모드를 쓰는지 (앱 배경색이 어두운지로 판단).</summary>
    public static bool PrefersDark(UISettings ui)
    {
        Color bg = ui.GetColorValue(UIColorType.Background);
        return Contrast.Luminance(new Rgba(bg.R, bg.G, bg.B)) < Contrast.Luminance(Rgba.White()) / 2;
    }
}
