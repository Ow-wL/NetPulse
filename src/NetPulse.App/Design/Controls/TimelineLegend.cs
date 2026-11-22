using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Core.Presentation;
using NetPulse.Design.Tokens;
using FoundationSize = Windows.Foundation.Size;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 겹침 타임라인의 범례 (측정·기록 화면이 같이 쓴다). 항목이 한 줄에 다 들어가지 않으면 다음 줄로 넘긴다:
/// 150%에서 마지막 항목 "증상 표시"가 잘렸다 (M11 시각 검토 C3). Wi‑Fi 신호가 관측된 측정만 신호 항목을 더한다 (M12 설계 C):
/// 선이면 견본 + 글, 권한 없음·지원 안 함처럼 값이 없으면 견본 없이 상태 글 (긴 글은 항목 안에서 줄을 바꾼다).
/// </summary>
public sealed class TimelineLegend : UserControl
{
    private readonly Ui ui;
    private readonly WrapRow row = new() { HorizontalSpacing = Space.S5, VerticalSpacing = Space.S1 };
    private FrameworkElement? signalItem;
    private string? signalKey;

    public TimelineLegend(Ui ui, string automationId)
    {
        this.ui = ui;
        AutomationProperties.SetAutomationId(this, automationId);
        Content = row;
        Add(ui.Brushes.Accent, false, "인터넷 지연");
        Add(ui.Brushes.Series2, true, "로컬 대상 지연");
        Add(ui.Brushes.Bad, false, "응답 없음이 관측된 회차");
        Add(ui.Brushes.Muted, true, "관측 공백");
        Add(ui.Brushes.Warn, true, "증상 표시");
    }

    /// <summary>신호 항목을 장면에 맞춘다 (같으면 그대로). 신호가 관측되지 않은 측정은 항목이 없다.</summary>
    public void SetSignal(SignalFrame s)
    {
        string? key = s.Display switch { SignalDisplay.Line => "line|" + s.Legend, SignalDisplay.State => "state|" + s.StateText, _ => null };
        if (key == signalKey) return;
        signalKey = key;
        if (signalItem is not null) row.Children.Remove(signalItem);
        signalItem = null;
        if (s.Display == SignalDisplay.Line && s.Legend is { } legend)
        {
            signalItem = Item(Swatch(ui.Brushes.Signal, dotted: true), legend);
            AutomationProperties.SetAutomationId(signalItem, "timeline.legend.signal");
        }
        else if (s.Display == SignalDisplay.State && s.StateText is { } state)
        {
            var text = Fonts.Wrapped(state, Typography.Caption, ui.Brushes.Muted);
            AutomationProperties.SetAutomationId(text, "timeline.legend.signalstate");
            signalItem = text;
        }
        if (signalItem is not null) row.Children.Add(signalItem);
    }

    /// <summary>진단 값: 신호 항목 (없으면 null).</summary>
    public string? SignalKey => signalKey;

    private void Add(Brush brush, bool dashed, string text) => row.Children.Add(Item(Swatch(brush, dashed ? Dash.Dashed : Dash.Solid), text));

    private enum Dash { Solid, Dashed }

    private Line Swatch(Brush brush, Dash dash)
    {
        var line = new Line { X1 = 0, X2 = Space.S4, Y1 = Space.S2, Y2 = Space.S2, Stroke = brush, StrokeThickness = ChartStyle.Line, VerticalAlignment = VerticalAlignment.Center };
        if (dash == Dash.Dashed) line.StrokeDashArray = [ChartStyle.DashOn, ChartStyle.DashOff];
        AutomationProperties.SetAccessibilityView(line, AccessibilityView.Raw);
        return line;
    }

    private Line Swatch(Brush brush, bool dotted)
    {
        var line = Swatch(brush, Dash.Solid);
        if (dotted)
        {
            line.StrokeThickness = ChartStyle.SignalLine;
            line.StrokeDashArray = [ChartStyle.SignalDotOn, ChartStyle.SignalDotOff];
            line.StrokeDashCap = PenLineCap.Round;
            line.StrokeStartLineCap = PenLineCap.Round;
            line.StrokeEndLineCap = PenLineCap.Round;
        }
        return line;
    }

    /// <summary>견본 + 글. 글이 줄 폭보다 길면 항목 안에서 줄을 바꾼다 (견본 열은 고정, 글 열은 남은 폭).</summary>
    private FrameworkElement Item(Line swatch, string text)
    {
        var item = new Grid { ColumnSpacing = Space.S2 };
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        item.Children.Add(swatch);
        var label = Fonts.Wrapped(text, Typography.Caption, ui.Brushes.Muted);
        Grid.SetColumn(label, 1);
        item.Children.Add(label);
        return item;
    }
}

/// <summary>
/// 자식을 왼쪽부터 가로로 놓고, 줄 폭을 넘으면 다음 줄로 넘기는 패널. 줄 폭보다 넓은 자식은 줄 폭으로 재서(글이 줄을 바꾸게) 혼자 한 줄을 쓴다.
/// </summary>
internal sealed class WrapRow : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override FoundationSize MeasureOverride(FoundationSize available) => Layout(available.Width, arrange: false);

    protected override FoundationSize ArrangeOverride(FoundationSize finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private FoundationSize Layout(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, used = 0;
        bool any = false;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) { if (arrange) child.Arrange(new Windows.Foundation.Rect(0, 0, 0, 0)); continue; }
            if (!arrange) child.Measure(new FoundationSize(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            double w = double.IsInfinity(width) ? size.Width : Math.Min(size.Width, width);
            if (any && x + w > width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            if (arrange) child.Arrange(new Windows.Foundation.Rect(x, y, w, size.Height));
            x += w + HorizontalSpacing;
            used = Math.Max(used, x - HorizontalSpacing);
            lineHeight = Math.Max(lineHeight, size.Height);
            any = true;
        }
        return new FoundationSize(double.IsInfinity(width) ? used : Math.Min(used, width), y + lineHeight);
    }
}
