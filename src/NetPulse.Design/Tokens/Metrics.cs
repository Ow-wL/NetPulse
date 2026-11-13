namespace NetPulse.Design.Tokens;

/// <summary>간격, 반경, 크기 (M2 설계 4.2, 4.4). 화면 코드는 이 값만 쓴다.</summary>
public static class Space
{
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24, S7 = 32, S8 = 48;
}

public static class Radius
{
    /// <summary>바깥 유리 (카드, 패널)</summary>
    public const double Outer = 18;
    /// <summary>안쪽 요소, 떠 있는 유리</summary>
    public const double Inner = 12;
    /// <summary>칩, 알약 모양</summary>
    public const double Pill = 999;

    /// <summary>동심 반경: 안쪽 반경 = 바깥 반경 − 여백 (0 아래로 내려가지 않음).</summary>
    public static double Concentric(double outer, double inset) => Math.Max(0, outer - inset);
}

public static class Size
{
    public const double IconS = 16, IconM = 18, IconL = 20;
    public const double ControlS = 32, ControlM = 40, ControlL = 48;
    public const double NavRailWidth = 232;
    public const double FocusOuter = 2, FocusInner = 1;
    public const double Hairline = 1;
    /// <summary>창의 기본 클라이언트 크기 (물리 픽셀). UI 테스트와 캡처 매트릭스도 이 크기를 쓴다.</summary>
    public const int WindowWidth = 1280, WindowHeight = 800;
}

public enum FontWeightToken { Regular, SemiBold, Bold }

/// <summary>글자 단계 하나.</summary>
public sealed record TypeStyle(string Name, double Size, FontWeightToken Weight, double LineHeight)
{
    /// <summary>WCAG의 "큰 글자": 24 px 이상, 또는 18.66 px 이상 굵게.</summary>
    public bool IsLarge => Size >= 24 || (Size >= 18.66 && Weight != FontWeightToken.Regular);
}

public static class Typography
{
    public static readonly TypeStyle Display = new("display", 52, FontWeightToken.Bold, 1.0);
    public static readonly TypeStyle Title = new("title", 30, FontWeightToken.Bold, 1.25);
    public static readonly TypeStyle Headline = new("headline", 22, FontWeightToken.SemiBold, 1.25);
    public static readonly TypeStyle Body = new("body", 14, FontWeightToken.Regular, 1.5);
    public static readonly TypeStyle Label = new("label", 13, FontWeightToken.SemiBold, 1.4);
    public static readonly TypeStyle Caption = new("caption", 12.5, FontWeightToken.Regular, 1.4);

    public static IReadOnlyList<TypeStyle> All { get; } = [Display, Title, Headline, Body, Label, Caption];
}
