namespace NetPulse.Design.Tokens;

/// <summary>간격, 반경, 크기 (M2 설계 4.2, 4.4). 화면 코드는 이 값만 쓴다.</summary>
public static class Space
{
    /// <summary>S0은 반 단계 (작은 알약·탐색 항목의 위아래 여백 조정용)</summary>
    public const double S0 = 2, S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24, S7 = 32, S8 = 48;
}

public static class Radius
{
    /// <summary>바깥 유리 (카드, 패널)</summary>
    public const double Outer = 18;
    /// <summary>안쪽 요소, 떠 있는 유리</summary>
    public const double Inner = 12;
    /// <summary>칩, 알약 모양</summary>
    public const double Pill = 999;

    /// <summary>알약 모양: 높이의 절반 (반경이 높이보다 크면 WinUI가 타원으로 줄인다).</summary>
    public static double Capsule(double height) => height / 2;

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
    public const double IconStroke = 1.7;
    public const double ToggleTrackWidth = 40, ToggleTrackHeight = 22, ToggleKnob = 16;
    public const double ToastWidth = 340, DialogWidth = 420;
    /// <summary>등급 고리 (M5 홈): 지름, 고리 굵기</summary>
    public const double GradeRing = 132, GradeRingStroke = 10;
    /// <summary>경로 맵 노드 지름, 선 굵기, 흐르는 빛 점</summary>
    public const double MapNode = 60, MapLine = 2, MapGlow = 6;
    /// <summary>진행 막대 높이</summary>
    public const double MeterHeight = 6;
    /// <summary>최근 측정 줄의 등급 배지</summary>
    public const double RowBadge = 40;
    /// <summary>타임라인 높이: 측정 화면, 홈 (간단 모드)</summary>
    public const double TimelineTall = 300, TimelineCompact = 236;
    /// <summary>타임라인 위쪽 표시 줄 (증상 점, 링크 막대)의 높이, 증상 점 지름</summary>
    public const double TimelineLane = 10, MarkerDot = 8;
    /// <summary>입력 칸 너비: 짧은 숫자, 보통, 긴 글</summary>
    public const double FieldS = 96, FieldM = 180, FieldL = 260;
    /// <summary>글 상자의 지우기 단추 너비 (WinUI 기본 템플릿, 초점이 있고 글이 있으면 나온다)</summary>
    public const double ClearButton = 32;

    /// <summary>
    /// 숫자 칸 너비: 본문 글자 <paramref name="digits"/>자리 + 지우기 단추 + 안쪽 여백(좌우 S3) + 초점 테두리 + 여유(S2).
    /// 포트 칸(5자리)이 FieldS면 초점이 있을 때 "70000"이 잘렸다 (M11 C3).
    /// </summary>
    public static double NumberField(int digits) =>
        Math.Ceiling(digits * Typography.Body.Size * Typography.DigitWidth + ClearButton + Space.S3 * 2 + FocusOuter * 2 + Space.S2);
    /// <summary>메모 입력 칸의 최소 높이</summary>
    public const double MemoMinHeight = 64;
    /// <summary>대상 카드 최소 너비 (한 줄에 셋)</summary>
    public const double TargetCardMin = 220;
    /// <summary>첫 실행 안내 카드 최대 너비</summary>
    public const double GuideWidth = 640;
    /// <summary>홈을 두 열로 놓는 최소 너비 (그보다 좁으면 한 열, 배율 150% 상당)</summary>
    public const double HomeTwoColumns = 760;
    /// <summary>기록 목록이 한 번에 더 보여 주는 줄 수 (M7 설계 3.1)</summary>
    public const int RecordsPage = 30;
    /// <summary>기록 목록에서 첫 프레임에 만드는 줄 수 (나머지는 다음 차례, M7 bench records-enter).</summary>
    public const int RecordsFirstRows = 8;
    /// <summary>창의 기본 클라이언트 크기 (물리 픽셀). UI 테스트와 캡처 매트릭스도 이 크기를 쓴다.</summary>
    public const int WindowWidth = 1280, WindowHeight = 800;
}

/// <summary>강조·흐림 정도 (불투명도).</summary>
public static class Emphasis
{
    /// <summary>완전히 보임</summary>
    public const double Full = 1;
    /// <summary>비활성 컨트롤</summary>
    public const double Disabled = 0.45;
    /// <summary>그래프의 응답 없음 구간 띠</summary>
    public const double LossBand = 0.18;
    /// <summary>불러오는 중 윤곽의 가장 옅은 / 가장 진한 불투명도</summary>
    public const double SkeletonLow = 0.35, SkeletonHigh = 0.8;
    /// <summary>포인터를 따라가는 유리 하이라이트의 세기</summary>
    public const double PointerGlow = 0.08;
    /// <summary>격자선</summary>
    public const double Grid = 1;
    /// <summary>일부 응답 없음 줄, 관측 공백 빗금, 응답이 확인되지 않은 선</summary>
    public const double PartialBand = 0.85, GapBand = 0.55, Unconfirmed = 0.5;
}

/// <summary>그래프 스타일 (M2 설계 5.6).</summary>
public static class ChartStyle
{
    public const double Line = 2, Line2 = 1.5, DashOn = 4, DashOff = 3, Height = 180, AxisGap = 6, MaxMs = 120;
    /// <summary>증상 표시 세로선, 공백 빗금 간격</summary>
    public const double Marker = 1.5, Hatch = 6;
    /// <summary>
    /// Wi‑Fi 신호 품질 선 (M12 설계 C): 지연 선과 모양이 다른 점선 (둥근 끝, 대시 길이 0 → 점). 대시 단위는 선 두께의 배수다.
    /// 축 여백 = 오른쪽 축 글("100%") 폭 + 이 간격. 어댑터 바뀜 표시 막대 두께.
    /// </summary>
    public const double SignalLine = 2, SignalDotOn = 0, SignalDotOff = 2, SignalAxisGap = 6, SignalChange = 3;
    public const int GridLines = 4;
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

    /// <summary>숫자 한 자리 칸의 너비 (글자 크기에 대한 비율, Pretendard 숫자 너비 근사)</summary>
    public const double DigitWidth = 0.62;
}
