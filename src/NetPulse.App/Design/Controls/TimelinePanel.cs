using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Core.Presentation;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>타임라인 조작 (키보드·휠·버튼이 같은 명령).</summary>
public enum TimelineCommand { ZoomIn, ZoomOut, PanLeft, PanRight, ToStart, ToNow, ShowAll }

/// <summary>
/// 겹침 타임라인 (M5 설계 4.7, N1). 값은 Core <see cref="TimelineModel"/>이 정하고 이 부품은 그리기만 한다.
/// <list type="bullet">
/// <item>공유기 지연 = 보조 계열(점선), 인터넷 지연 = 강조색 실선. 색과 선 모양을 함께 달리한다.</item>
/// <item>응답 없음이 관측된 회차 = 끊김 색 띠, 일부만 = 위쪽 주의 색 줄, 관측 공백 = 빗금 회색.</item>
/// <item>위쪽 표시 줄: 증상(주의 색 점 + 세로 점선), 링크 끊김(끊김 색 막대, 이어지는 중이면 마지막 표본까지).</item>
/// </list>
/// 키보드(초점이 있을 때): ←/→ 이동, +/− 확대, Home/End 처음/지금. Ctrl+휠 확대, 휠 이동. 화면 읽기 이름은 요약 문장.
/// XAML 도형으로 그리되 점 수는 열 수의 두 배로 제한된다 (간추리기).
/// </summary>
public sealed class TimelinePanel : UserControl
{
    private readonly Ui ui;
    private readonly Canvas canvas = new();
    private readonly bool compact;
    private TimelineFrame? frame;
    private Func<long, string> timeLabel = _ => "";
    private bool follow;

    public TimelinePanel(Ui ui, string automationId, bool compact)
    {
        this.ui = ui;
        this.compact = compact;
        AutomationProperties.SetAutomationId(this, automationId);
        Height = compact ? Size.TimelineCompact : Size.TimelineTall;
        IsTabStop = !compact;
        UseSystemFocusVisuals = true;
        FocusVisualPrimaryBrush = ui.Brushes.FocusOuter;
        FocusVisualSecondaryBrush = ui.Brushes.FocusInner;
        FocusVisualPrimaryThickness = new Thickness(Size.FocusOuter);
        FocusVisualSecondaryThickness = new Thickness(Size.FocusInner);
        Content = canvas;
        canvas.Background = ui.Brushes.Transparent;   // 휠·포인터를 받기 위해
        // 폭이 바뀌어 장면을 만든 열 수와 달라지면 화면에 다시 계산을 맡긴다 (M12 설계 A: 배치 전 폭 0에서 만든 열 1개 장면이
        // 끝난 측정에서 새 스냅숏 없이 남았다). 다시 계산할 화면이 없으면 있는 장면을 새 폭으로 다시 그리기만 한다
        SizeChanged += (_, _) =>
        {
            if (ColumnsChanged is { } changed && ActualWidth > 0 && FrameColumns != Columns) changed();
            else Draw();
        };
        if (!compact)
        {
            KeyDown += OnKey;
            PointerWheelChanged += OnWheel;
            Tapped += (_, e) => { if (PlotWidth > 0) Picked?.Invoke(Math.Min(1, e.GetPosition(this).X / PlotWidth)); };
        }
    }

    /// <summary>
    /// Wi‑Fi 신호 오른쪽 축 여백을 뺀다 (M12 설계 C 표시). 화면은 장면을 만들기 전에 정한다: <see cref="Columns"/>가 여백을 뺀 그래프 폭이 된다.
    /// 신호가 없는 측정은 거짓 (지금과 같음).
    /// </summary>
    public bool SignalAxis { get; set; }

    private double? axisMargin;

    /// <summary>오른쪽 축 여백: "100%" 글 폭 + 간격 (처음 쓸 때 한 번 잰다).</summary>
    private double AxisMargin
    {
        get
        {
            if (axisMargin is double m) return m;
            var probe = Fonts.Text(TimelineGeometry.QualityMax.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%", Typography.Caption, ui.Brushes.Muted);
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            axisMargin = m = Math.Ceiling(probe.DesiredSize.Width) + ChartStyle.SignalAxisGap;
            return m;
        }
    }

    /// <summary>그래프 영역 폭 (시간축·선·띠·끝 시각을 모두 이 안에 둔다).</summary>
    private double PlotWidth => TimelineGeometry.PlotWidth(ActualWidth, SignalAxis, SignalAxis ? AxisMargin : 0);

    /// <summary>조작 요청 (화면이 보기 창을 바꾸고 다시 그린다).</summary>
    public event Action<TimelineCommand>? Command;

    /// <summary>폭이 바뀌어 지금 열 수가 장면을 만든 열 수와 다르다 (아직 장면이 없을 때 포함). 화면이 장면을 다시 만들어 <see cref="Show"/>한다.</summary>
    public event Action? ColumnsChanged;

    /// <summary>그래프를 누름: 폭에 대한 가로 위치 비율 (0~1). 기록 화면이 그 자리의 끊김 줄로 간다 (M7 설계 4.1).</summary>
    public event Action<double>? Picked;

    /// <summary>열 수 = 그래프 폭 (픽셀, 신호 축 여백을 뺀 것). 화면이 <see cref="TimelineModel.Frame"/>에 넘긴다.</summary>
    public int Columns => Math.Max(1, (int)Math.Round(PlotWidth));

    /// <summary>마지막으로 받은 장면을 계산할 때 쓴 열 수 (화면은 <see cref="Columns"/>로 장면을 만들고 바로 <see cref="Show"/>한다). 진단·시험용 (M12 설계 A)</summary>
    public int? FrameColumns { get; private set; }
    /// <summary>처음 받은 장면의 열 수 (고치기 전 실행의 증거: 폭 0에서 만든 장면이면 1)</summary>
    public int? FirstFrameColumns { get; private set; }
    public bool HasFrame => frame is not null;

    /// <summary>진단 값: 장면 유무, 지금 폭, 장면에 쓴 열 수. 장면이 있으면 열 수가 지금 폭과 같아야 한다 (M12 설계 A)</summary>
    public object Diag() => new
    {
        hasFrame = HasFrame, width = ActualWidth, columns = Columns, frameColumns = FrameColumns, firstFrameColumns = FirstFrameColumns,
        // M12 설계 C: 신호 축이 있으면 열 수 = 여백을 뺀 그래프 폭 (plotWidth)
        signalAxis = SignalAxis, plotWidth = PlotWidth, axisMargin = SignalAxis ? AxisMargin : 0,
        signal = frame?.Signal is { } s ? new { display = s.Display.ToString(), segments = s.Segments.Count, points = s.Segments.Sum(x => x.Count), changes = s.AdapterChanges.Count, s.MinQuality, s.OtherAdapters, legend = s.Legend, state = s.StateText } : null,
    };

    public void Show(TimelineFrame next, Func<long, string> label, bool followingNow, string? range)
    {
        frame = next;
        FrameColumns = Columns;
        FirstFrameColumns ??= FrameColumns;
        timeLabel = label;
        follow = followingNow;
        AutomationProperties.SetName(this, next.Summary);
        AutomationProperties.SetHelpText(this, range ?? "");
        Draw();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => "Timeline";
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        TimelineCommand? c = e.Key switch
        {
            Windows.System.VirtualKey.Left => TimelineCommand.PanLeft,
            Windows.System.VirtualKey.Right => TimelineCommand.PanRight,
            Windows.System.VirtualKey.Add or (Windows.System.VirtualKey)187 => TimelineCommand.ZoomIn,
            Windows.System.VirtualKey.Subtract or (Windows.System.VirtualKey)189 => TimelineCommand.ZoomOut,
            Windows.System.VirtualKey.Home => TimelineCommand.ToStart,
            Windows.System.VirtualKey.End => TimelineCommand.ToNow,
            _ => null,
        };
        if (c is { } cmd) { Command?.Invoke(cmd); e.Handled = true; }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        bool ctrl = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        Command?.Invoke(ctrl ? (delta > 0 ? TimelineCommand.ZoomIn : TimelineCommand.ZoomOut) : (delta > 0 ? TimelineCommand.PanLeft : TimelineCommand.PanRight));
        e.Handled = true;
    }

    private void Draw()
    {
        canvas.Children.Clear();
        // w = 그래프 영역 (신호 축이 있으면 오른쪽 여백을 뺌, M12 설계 C). 시간축·선·띠·끝 시각은 모두 이 안에
        double w = PlotWidth, h = ActualHeight;
        if (frame is not { } f || w <= 0 || h <= 0) return;
        double laneH = Size.TimelineLane;
        double top = laneH + Space.S2;                                  // 위쪽 표시 줄 아래부터 그래프
        double axisH = Typography.Caption.Size + ChartStyle.AxisGap;
        double plotH = Math.Max(1, h - top - axisH);
        double span = Math.Max(1, f.ToMs - f.FromMs);
        double X(double ms) => (ms - f.FromMs) / span * w;
        double Y(double v) => top + plotH - Math.Min(v, f.AxisMaxMs) / f.AxisMaxMs * plotH;

        // 격자와 세로축 값
        for (int g = 0; g <= ChartStyle.GridLines; g++)
        {
            double y = top + plotH * g / ChartStyle.GridLines;
            canvas.Children.Add(Decor(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = ui.Brushes.Faint, StrokeThickness = Emphasis.Grid }));
        }
        var axisMax = Fonts.Text($"{f.AxisMaxMs:0} ms", Typography.Caption, ui.Brushes.Muted);
        Canvas.SetTop(axisMax, top);
        canvas.Children.Add(axisMax);

        // 띠 (응답 없음이 관측된 회차 칸, 공백)
        foreach (var b in f.Bands)
        {
            double x1 = Math.Max(0, X(b.FromMs)), x2 = Math.Min(w, X(b.ToMs));
            double bw = Math.Max(Size.Hairline, x2 - x1);
            Rectangle rect = b.Kind switch
            {
                BandKind.ExternalNone => new Rectangle { Width = bw, Height = plotH, Fill = ui.Brushes.Bad, Opacity = Emphasis.LossBand },
                BandKind.PartialNone => new Rectangle { Width = bw, Height = Size.Hairline * 3, Fill = ui.Brushes.Warn, Opacity = Emphasis.PartialBand },
                _ => new Rectangle { Width = bw, Height = plotH, Fill = ui.Brushes.Faint, Stroke = ui.Brushes.Edge, StrokeThickness = Size.Hairline, StrokeDashArray = [ChartStyle.Hatch, ChartStyle.Hatch], Opacity = Emphasis.GapBand },
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, b.Kind == BandKind.PartialNone ? top : top);
            canvas.Children.Add(Decor(rect));
        }

        // 선 (구간마다 따로: 누락·공백·값 없음을 건너 잇지 않음)
        foreach (var seg in f.Router) canvas.Children.Add(Series(seg, ui.Brushes.Series2, ChartStyle.Line2, dashed: true, X, Y));
        foreach (var seg in f.Internet) canvas.Children.Add(Series(seg, ui.Brushes.Accent, ChartStyle.Line, dashed: false, X, Y));

        // Wi‑Fi 신호 품질 (오른쪽 축 0~100%, 점선): 기본 경로 어댑터 하나의 선, 상태가 정상이 아니거나 끊기거나 어댑터가 바뀌면 끊는다
        if (SignalAxis && f.Signal.Axis)
        {
            double SignalY(double q) => TimelineGeometry.SignalY(q, top, plotH);
            foreach (var seg in f.Signal.Segments)
            {
                var line = Series(seg, ui.Brushes.Signal, ChartStyle.SignalLine, dashed: false, X, SignalY);
                line.StrokeDashArray = [ChartStyle.SignalDotOn, ChartStyle.SignalDotOff];
                line.StrokeDashCap = PenLineCap.Round;
                line.StrokeStartLineCap = PenLineCap.Round;
                line.StrokeEndLineCap = PenLineCap.Round;
                canvas.Children.Add(line);
            }
            foreach (var (text, q) in new[] { ("100%", TimelineGeometry.QualityMax), ("0%", 0.0) })
            {
                var t = Fonts.Text(text, Typography.Caption, ui.Brushes.Muted);
                t.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(t, w + ChartStyle.SignalAxisGap);
                Canvas.SetTop(t, Math.Clamp(SignalY(q) - t.DesiredSize.Height / 2, top, top + plotH - t.DesiredSize.Height));
                canvas.Children.Add(t);   // 축 글은 시각 눈금처럼 배치 검사에 들어간다 (겹침·잘림)
            }
            // 무선 어댑터 바뀜: 위쪽 표시 줄의 세로 막대 (증상 점·링크 끊김 막대와 모양이 다름)
            foreach (var at in f.Signal.AdapterChanges)
            {
                var tick = new Rectangle { Width = ChartStyle.SignalChange, Height = laneH, Fill = ui.Brushes.Signal };
                Canvas.SetLeft(tick, Math.Clamp(X(at) - ChartStyle.SignalChange / 2, 0, Math.Max(0, w - ChartStyle.SignalChange)));
                Canvas.SetTop(tick, 0);
                canvas.Children.Add(Decor(tick));
            }
        }

        // 위쪽 표시 줄: 링크 끊김 막대, 증상 점과 세로 점선
        foreach (var d in f.LinkDowns)
        {
            double x1 = Math.Max(0, X(d.FromMs)), x2 = Math.Min(w, X(d.ToMs));
            var bar = new Rectangle { Width = Math.Max(Size.MarkerDot / 2, x2 - x1), Height = laneH / 2, Fill = ui.Brushes.Bad, RadiusX = laneH / 4, RadiusY = laneH / 4 };
            Canvas.SetLeft(bar, x1);
            Canvas.SetTop(bar, laneH / 4);
            canvas.Children.Add(Decor(bar));
        }
        foreach (var m in f.Marks)
        {
            double x = X(m.AtMs);
            canvas.Children.Add(Decor(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + plotH, Stroke = ui.Brushes.Warn, StrokeThickness = ChartStyle.Marker, StrokeDashArray = [ChartStyle.DashOn, ChartStyle.DashOff] }));
            var dot = new Ellipse { Width = Size.MarkerDot, Height = Size.MarkerDot, Fill = ui.Brushes.Warn };
            Canvas.SetLeft(dot, x - Size.MarkerDot / 2);
            Canvas.SetTop(dot, (laneH - Size.MarkerDot) / 2);
            canvas.Children.Add(Decor(dot));
        }

        // 가로축 시각
        int ticks = compact ? 3 : 5;
        for (int i = 0; i < ticks; i++)
        {
            double at = (double)i / (ticks - 1);
            long ms = f.FromMs + (long)(span * at);
            string text = follow && i == ticks - 1 ? "지금" : timeLabel(ms);
            var t = Fonts.Text(text, Typography.Caption, ui.Brushes.Muted);
            t.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, Math.Clamp(w * at - t.DesiredSize.Width * at, 0, Math.Max(0, w - t.DesiredSize.Width)));
            Canvas.SetTop(t, top + plotH + ChartStyle.AxisGap);
            canvas.Children.Add(t);
        }
    }

    private static UIElement Decor(UIElement e)
    {
        AutomationProperties.SetAccessibilityView(e, AccessibilityView.Raw);
        return e;
    }

    private static Polyline Series(IReadOnlyList<TimelinePoint> points, Brush brush, double thickness, bool dashed, Func<double, double> x, Func<double, double> y)
    {
        var line = new Polyline { Stroke = brush, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round };
        if (dashed) line.StrokeDashArray = [ChartStyle.DashOn, ChartStyle.DashOff];
        foreach (var p in points)
        {
            line.Points.Add(new Windows.Foundation.Point(x(p.XMs), y(p.MinMs)));
            if (p.MaxMs > p.MinMs) line.Points.Add(new Windows.Foundation.Point(x(p.XMs), y(p.MaxMs)));
        }
        if (points.Count == 1)   // 점 하나뿐인 구간도 보이게 (짧은 가로선)
        {
            var only = line.Points[0];
            line.Points.Add(new Windows.Foundation.Point(only.X + thickness, only.Y));
        }
        AutomationProperties.SetAccessibilityView(line, AccessibilityView.Raw);
        return line;
    }
}
