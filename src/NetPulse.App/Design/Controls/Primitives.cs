using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// UI Automation에 "그룹"으로 나타나는 패널. 기본 패널은 자동화 트리에 없어서 카드·영역에 이름과 ID를 붙이려면 이것을 쓴다.
/// </summary>
public class GroupPanel : Grid
{
    public GroupPanel(string automationId, string? name = null)
    {
        AutomationProperties.SetAutomationId(this, automationId);
        if (name is not null) AutomationProperties.SetName(this, name);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);

    private sealed class GroupPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => "GroupPanel";
    }
}

/// <summary>알약 모양 테두리: 실제 높이에 맞춰 반경을 정한다.</summary>
public static class Pill
{
    public static Border Apply(Border border)
    {
        border.SizeChanged += (_, e) => border.CornerRadius = new CornerRadius(Radius.Capsule(e.NewSize.Height));
        return border;
    }
}

/// <summary>기본 컨트롤의 모양을 없앤 템플릿 (내용만 그린다). 시각 상태 애니메이션이 없다 (M2 설계 5.3).</summary>
internal static class Templates
{
    private static readonly Dictionary<string, ControlTemplate> Cache = [];

    public static ControlTemplate Plain(string targetType)
    {
        if (Cache.TryGetValue(targetType, out var t)) return t;
        t = (ControlTemplate)XamlReader.Load(
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + targetType + "\">" +
            "<ContentPresenter Content=\"{TemplateBinding Content}\" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"/>" +
            "</ControlTemplate>");
        Cache[targetType] = t;
        return t;
    }
}

/// <summary>선 아이콘 (24 단위 좌표의 경로). 모양은 Lucide 계열 단순 선.</summary>
public static class Icons
{
    public const string Home = "M3 10.5 12 3l9 7.5V21a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1z";
    public const string Pulse = "M3 12h4l3-8 4 16 3-8h4";
    public const string Layers = "M12 3 2 8l10 5 10-5zM2 13l10 5 10-5M2 18l10 5 10-5";
    public const string Square = "M4 4h16v16H4z";
    public const string Card = "M3 5h18v14H3zM3 10h18";
    public const string Menu = "M4 6h16M4 12h16M4 18h16";
    public const string Bell = "M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10 21h4";
    public const string Window = "M3 4h18v16H3zM3 9h18";
    public const string Chart = "M3 3v18h18M7 15l4-4 3 3 5-6";
    public const string Sparkle = "M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M6 18l2.5-2.5M15.5 8.5 18 6";
    public const string Settings = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15l1.6 1-2 3.4-1.8-.7a7 7 0 0 1-2.2 1.3L14.7 22h-4l-.3-2a7 7 0 0 1-2.2-1.3l-1.8.7-2-3.4 1.6-1a7 7 0 0 1 0-2.6L4.4 11l2-3.4 1.8.7A7 7 0 0 1 10.4 7l.3-2h4l.3 2a7 7 0 0 1 2.2 1.3l1.8-.7 2 3.4-1.6 1a7 7 0 0 1 0 2.6z";
    public const string Contrast = "M12 3a9 9 0 1 0 0 18zM12 3a9 9 0 0 1 0 18";
    public const string Check = "M5 12l5 5L20 7";
    public const string Alert = "M12 3 2 20h20zM12 9v5M12 17h.01";
    public const string Cross = "M18 6 6 18M6 6l12 12";
    public const string Info = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 11v6M12 7h.01";
    public const string Question = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14M12 17h.01";
    public const string Play = "M7 4v16l13-8z";
    public const string Refresh = "M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7";
    // M5
    public const string Stop = "M7 7h10v10H7z";
    public const string Pencil = "M4 20h4L19 9l-4-4L4 16zM14 6l4 4";
    public const string Plus = "M12 5v14M5 12h14";
    public const string Minus = "M5 12h14";
    public const string Up = "M12 19V5M5 12l7-7 7 7";
    public const string Down = "M12 5v14M5 12l7 7 7-7";
    public const string Trash = "M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3";
    public const string Back = "M19 12H5M12 5l-7 7 7 7";
    public const string Flag = "M5 21V4M5 4h11l-2 4 2 4H5";
    public const string Pc = "M3 5h18v11H3zM8 20h8M12 16v4";
    public const string Router = "M3 14h18v6H3zM7 17h.01M11 17h.01M8 10a6 6 0 0 1 8 0M5 7a10 10 0 0 1 14 0";
    public const string Globe = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18";
    public const string Clock = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7v5l3 2";
    public const string Report = "M6 3h9l4 4v14H6zM14 3v5h5M9 13h6M9 17h6";
    public const string Diagnose = "M12 3l2.6 5.6 6.1.7-4.5 4.2 1.2 6L12 16.8l-5.4 2.7 1.2-6-4.5-4.2 6.1-.7z";
    public const string Chevron = "M9 6l6 6-6 6";
    public const string ZoomAll = "M3 9V3h6M21 9V3h-6M3 15v6h6M21 15v6h-6";
    public const string Now = "M13 5l7 7-7 7M4 5l7 7-7 7";

    /// <summary>아이콘 하나 (테마 브러시로 그리는 선). 장식이면 자동화 트리에서 뺀다.</summary>
    public static FrameworkElement Make(string data, Brush stroke, double size, bool decorative = true)
    {
        var path = new Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Stroke = stroke,
            StrokeThickness = Size.IconStroke,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        var box = new Viewbox { Width = size, Height = size, Child = new Canvas { Width = IconCanvas, Height = IconCanvas, Children = { path } } };
        if (decorative) AutomationProperties.SetAccessibilityView(box, AccessibilityView.Raw);
        return box;
    }

    /// <summary>아이콘 경로의 좌표 단위 (24×24 격자)</summary>
    private static double IconCanvas => Space.S6;
}
