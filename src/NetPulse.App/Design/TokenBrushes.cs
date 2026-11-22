using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetPulse.Design;
using NetPulse.Design.Tokens;
using Windows.UI;

namespace NetPulse.App.Design;

/// <summary>
/// 토큰 → WinUI 브러시의 유일한 변환 지점 (M2 설계 3절, 정적 검사의 color-literal 허용 파일).
/// 화면 전체가 같은 브러시 객체를 공유한다. 테마를 바꾸면 이 객체들의 색 값만 바꾼다 (요소를 다시 만들지 않음).
/// </summary>
public sealed class TokenBrushes
{
    public SolidColorBrush Bg { get; } = new();
    public SolidColorBrush Ink { get; } = new();
    public SolidColorBrush Muted { get; } = new();
    /// <summary>비활성 글 (고대비에서 GrayText, 그 밖에는 Muted와 같은 색)</summary>
    public SolidColorBrush Disabled { get; } = new();
    public SolidColorBrush Faint { get; } = new();
    public SolidColorBrush Accent { get; } = new();
    public SolidColorBrush AccentInk { get; } = new();
    public SolidColorBrush Series2 { get; } = new();
    /// <summary>Wi‑Fi 신호 품질 선 (M12 설계 C)</summary>
    public SolidColorBrush Signal { get; } = new();
    public SolidColorBrush Ok { get; } = new();
    public SolidColorBrush Warn { get; } = new();
    public SolidColorBrush Bad { get; } = new();
    public SolidColorBrush FocusOuter { get; } = new();
    public SolidColorBrush FocusInner { get; } = new();
    public SolidColorBrush Edge { get; } = new();
    public SolidColorBrush Highlight { get; } = new();
    public SolidColorBrush Scrim { get; } = new();
    public SolidColorBrush Transparent { get; } = new(ToColor(Rgba.Black(0)));

    public Palette Current { get; private set; } = Palette.Dark;

    public TokenBrushes(Palette palette) => Apply(palette);

    /// <summary>모든 공유 브러시의 색을 이 팔레트로 바꾼다.</summary>
    public void Apply(Palette p)
    {
        Current = p;
        Bg.Color = ToColor(p.Bg);
        Ink.Color = ToColor(p.Ink);
        Muted.Color = ToColor(p.Muted);
        Disabled.Color = ToColor(p.Disabled);
        Faint.Color = ToColor(p.Faint);
        Accent.Color = ToColor(p.Accent);
        AccentInk.Color = ToColor(p.AccentInk);
        Series2.Color = ToColor(p.Series2);
        Signal.Color = ToColor(p.Signal);
        Ok.Color = ToColor(p.Ok);
        Warn.Color = ToColor(p.Warn);
        Bad.Color = ToColor(p.Bad);
        FocusOuter.Color = ToColor(p.FocusOuter);
        FocusInner.Color = ToColor(p.FocusInner);
        Edge.Color = ToColor(p.Edge);
        Highlight.Color = ToColor(p.Highlight);
        Scrim.Color = ToColor(p.Scrim);
    }

    public static Color ToColor(Rgba c) => Color.FromArgb((byte)Math.Round(c.A * 255), c.R, c.G, c.B);
}

/// <summary>
/// 리포트 문서 팔레트 (M8 설계 4.1 [1-6]): 미리 보기의 종이 면. 내보내는 파일과 같은 색 (<see cref="NetPulse.Core.Report.ReportPalette"/>).
/// 고대비에서는 쓰지 않는다 (미리 보기가 시스템 색을 따른다).
/// </summary>
public sealed class DocumentBrushes
{
    public DocumentBrushes(NetPulse.Core.Report.ReportPalette p)
    {
        SolidColorBrush B(string hex) => new(TokenBrushes.ToColor(Rgba.Hex(hex)));
        Paper = B(p.Surface);
        Bg = B(p.Bg);
        Ink = B(p.Ink);
        Muted = B(p.Muted);
        Accent = B(p.Accent);
        Bad = B(p.Bad);
        Warn = B(p.Warn);
        Edge = B(p.Edge);
        BadSoft = B(p.BadSoft);
        WarnSoft = B(p.WarnSoft);
        Link = B(p.Link);
        Mark = B(p.Mark);
        Signal = B(p.Signal);
    }

    public SolidColorBrush Paper { get; }
    public SolidColorBrush Bg { get; }
    public SolidColorBrush Ink { get; }
    public SolidColorBrush Muted { get; }
    public SolidColorBrush Accent { get; }
    public SolidColorBrush Bad { get; }
    public SolidColorBrush Warn { get; }
    public SolidColorBrush Edge { get; }
    public SolidColorBrush BadSoft { get; }
    public SolidColorBrush WarnSoft { get; }
    public SolidColorBrush Link { get; }
    public SolidColorBrush Mark { get; }
    public SolidColorBrush Signal { get; }
}

/// <summary>Pretendard 글꼴과 글자 단계 적용.</summary>
public static class Fonts
{
    private static readonly FontFamily Regular = new("ms-appx:///Fonts/Pretendard-Regular.otf#Pretendard");
    private static readonly FontFamily SemiBold = new("ms-appx:///Fonts/Pretendard-SemiBold.otf#Pretendard");
    private static readonly FontFamily Bold = new("ms-appx:///Fonts/Pretendard-Bold.otf#Pretendard");

    public static TextBlock Text(string text, TypeStyle style, Brush foreground)
    {
        var t = new TextBlock { Text = text, Foreground = foreground };
        Apply(t, style);
        return t;
    }

    /// <summary>
    /// 줄바꿈하는 한국어 글: 단어 안에서 줄이 끊기지 않게 글자 사이에 단어 결합자(U+2060)를 넣는다 (M0 방식).
    /// 화면 읽기에는 원래 문장을 이름으로 준다.
    /// </summary>
    public static TextBlock Wrapped(string text, TypeStyle style, Brush foreground)
    {
        var t = Text(KeepAll(text), style, foreground);
        t.TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(t, text);
        return t;
    }

    public static string KeepAll(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i]) && !char.IsWhiteSpace(text[i + 1])) sb.Append('⁠');
        }
        return sb.ToString();
    }

    /// <summary>입력 칸 같은 컨트롤에 글자 단계를 적용한다 (M5).</summary>
    public static void Apply(Microsoft.UI.Xaml.Controls.Control c, TypeStyle style)
    {
        c.FontSize = style.Size;
        (c.FontFamily, c.FontWeight) = style.Weight switch
        {
            FontWeightToken.Bold => (Bold, FontWeights.Bold),
            FontWeightToken.SemiBold => (SemiBold, FontWeights.SemiBold),
            _ => (Regular, FontWeights.Normal),
        };
    }

    public static void Apply(TextBlock t, TypeStyle style)
    {
        t.FontSize = style.Size;
        (t.FontFamily, t.FontWeight) = style.Weight switch
        {
            FontWeightToken.Bold => (Bold, FontWeights.Bold),
            FontWeightToken.SemiBold => (SemiBold, FontWeights.SemiBold),
            _ => (Regular, FontWeights.Normal),
        };
        t.LineHeight = style.Size * style.LineHeight;
        t.LineStackingStrategy = Microsoft.UI.Xaml.LineStackingStrategy.BlockLineHeight;
    }
}
