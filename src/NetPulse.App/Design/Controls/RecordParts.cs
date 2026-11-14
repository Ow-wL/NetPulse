using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Core.Presentation;
using NetPulse.Core.Targets;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 지연 추세 (M7 설계 3.2 [1-7]): 대상별 5분 창 중앙값. 외부 대상은 실선, 로컬 대상은 점선. 끊긴 곳은 잇지 않는다.
/// 값은 화면 읽기 이름으로도 준다 (대상마다 처음·마지막·최댓값).
/// </summary>
public sealed class TrendChart : GroupPanel
{
    private readonly Ui ui;
    private readonly Canvas canvas = new();
    private IReadOnlyList<(TrendLine Line, TargetRole Role)> lines = [];
    private long fromMs, toMs;
    private Func<long, string> label = _ => "";

    public TrendChart(Ui ui, string automationId) : base(automationId, "지연 추세")
    {
        this.ui = ui;
        Height = ChartStyle.Height;
        Children.Add(canvas);
        SizeChanged += (_, _) => Draw();
    }

    public void SetData(IReadOnlyList<(TrendLine Line, TargetRole Role)> data, long from, long to, Func<long, string> timeLabel, string summary)
    {
        lines = data;
        fromMs = from;
        toMs = Math.Max(from + 1, to);
        label = timeLabel;
        AutomationProperties.SetName(this, summary);
        Draw();
    }

    /// <summary>축 최댓값: 가장 큰 중앙값을 넘는 깔끔한 값.</summary>
    public static double AxisMax(double max)
    {
        foreach (var step in new[] { 20.0, 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0 })
            if (max <= step) return step;
        return Math.Ceiling(max / 1000) * 1000;
    }

    private void Draw()
    {
        double w = ActualWidth, h = ActualHeight;
        canvas.Children.Clear();
        if (w <= 0 || h <= 0) return;
        double plotH = h - Typography.Caption.Size - ChartStyle.AxisGap;
        double max = AxisMax(lines.SelectMany(l => l.Line.Segments).SelectMany(s => s).Select(p => p.MedianMs).DefaultIfEmpty(0).Max());
        double X(long ms) => w * (ms - fromMs) / (toMs - fromMs);
        double Y(double v) => plotH - Math.Min(v, max) / max * plotH;
        for (int g = 0; g <= ChartStyle.GridLines; g++)
        {
            double y = plotH * g / ChartStyle.GridLines;
            canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = ui.Brushes.Faint, StrokeThickness = Emphasis.Grid });
        }
        foreach (var (line, role) in lines)
            foreach (var seg in line.Segments)
            {
                bool local = role == TargetRole.Local;
                var poly = new Polyline
                {
                    Stroke = local ? ui.Brushes.Series2 : ui.Brushes.Accent,
                    StrokeThickness = local ? ChartStyle.Line2 : ChartStyle.Line,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                if (local) poly.StrokeDashArray = [ChartStyle.DashOn, ChartStyle.DashOff];
                // 창의 가운데에 점 (한 창뿐이면 짧은 가로선으로 보이게 창 폭만큼)
                foreach (var (at, median) in seg)
                    poly.Points.Add(new Windows.Foundation.Point(X(at + RecordView.TrendWindowMs / 2), Y(median)));
                if (seg.Count == 1)
                {
                    var (at, median) = seg[0];
                    poly.Points.Clear();
                    poly.Points.Add(new Windows.Foundation.Point(X(at), Y(median)));
                    poly.Points.Add(new Windows.Foundation.Point(X(at + RecordView.TrendWindowMs), Y(median)));
                }
                AutomationProperties.SetAccessibilityView(poly, AccessibilityView.Raw);
                canvas.Children.Add(poly);
            }
        foreach (var (ms, at) in new[] { (fromMs, 0.0), (toMs, 1.0) })
        {
            var t = Fonts.Text(label(ms), Typography.Caption, ui.Brushes.Muted);
            t.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, Math.Clamp(w * at - t.DesiredSize.Width * at, 0, Math.Max(0, w - t.DesiredSize.Width)));
            Canvas.SetTop(t, plotH + ChartStyle.AxisGap);
            canvas.Children.Add(t);
        }
        canvas.Children.Add(Fonts.Text(max.ToString(CultureInfo.InvariantCulture) + " ms", Typography.Caption, ui.Brushes.Muted));
    }
}
