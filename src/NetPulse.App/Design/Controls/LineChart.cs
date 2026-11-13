using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 그래프 스타일 (M2 설계 5.6): 1차 계열(강조색 실선), 2차 계열(보조색 점선), 응답 없음 구간 띠, 격자, 축 글자.
/// 색과 선 모양을 함께 달리해 색만으로 구분하지 않는다. 화면 읽기에는 요약 문장을 이름으로 준다.
/// XAML 도형으로 그린다 (실시간 성능은 M5에서 다시 본다).
/// </summary>
public sealed class LineChart : GroupPanel
{
    private readonly Ui ui;
    private readonly Canvas canvas = new();
    private readonly List<TextBlock> axis = [];
    private double[] primary = [], secondary = [];
    private bool[] lost = [];
    private readonly double max;

    public LineChart(Ui ui, string automationId, double maxValue) : base(automationId)
    {
        this.ui = ui;
        max = maxValue;
        Height = ChartStyle.Height;
        Children.Add(canvas);
        SizeChanged += (_, _) => Draw();
    }

    public void SetData(double[] primaryValues, double[] secondaryValues, bool[] lostRounds, string summary)
    {
        primary = primaryValues;
        secondary = secondaryValues;
        lost = lostRounds;
        AutomationProperties.SetName(this, summary);
        Draw();
    }

    private void Draw()
    {
        double w = ActualWidth, h = ActualHeight;
        canvas.Children.Clear();
        if (w <= 0 || h <= 0 || primary.Length < 2) return;
        double plotH = h - Typography.Caption.Size - ChartStyle.AxisGap;
        double X(int i) => w * i / (primary.Length - 1);
        double Y(double v) => plotH - Math.Min(v, max) / max * plotH;

        for (int g = 0; g <= ChartStyle.GridLines; g++)
        {
            double y = plotH * g / ChartStyle.GridLines;
            canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = ui.Brushes.Faint, StrokeThickness = Emphasis.Grid });
        }
        for (int i = 0; i < lost.Length && i < primary.Length; i++)
        {
            if (!lost[i]) continue;
            var band = new Rectangle { Width = Math.Max(Size.Hairline, w / (primary.Length - 1)), Height = plotH, Fill = ui.Brushes.Bad, Opacity = Emphasis.LossBand };
            Canvas.SetLeft(band, X(i) - band.Width / 2);
            canvas.Children.Add(band);
        }
        canvas.Children.Add(Series(secondary, ui.Brushes.Series2, ChartStyle.Line2, dashed: true, X, Y));
        canvas.Children.Add(Series(primary, ui.Brushes.Accent, ChartStyle.Line, dashed: false, X, Y));
        var labels = new[] { ("-2분", 0.0), ("-1분", 0.5), ("지금", 1.0) };
        foreach (var (text, at) in labels)
        {
            var t = Fonts.Text(text, Typography.Caption, ui.Brushes.Muted);
            t.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, Math.Clamp(w * at - t.DesiredSize.Width * at, 0, w - t.DesiredSize.Width));
            Canvas.SetTop(t, plotH + ChartStyle.AxisGap);
            canvas.Children.Add(t);
        }
        var unit = Fonts.Text(max.ToString(CultureInfo.InvariantCulture) + " ms", Typography.Caption, ui.Brushes.Muted);
        canvas.Children.Add(unit);
    }

    private static Polyline Series(double[] values, Brush brush, double thickness, bool dashed, Func<int, double> x, Func<double, double> y)
    {
        var line = new Polyline { Stroke = brush, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round };
        if (dashed) line.StrokeDashArray = [ChartStyle.DashOn, ChartStyle.DashOff];
        for (int i = 0; i < values.Length; i++)
            if (!double.IsNaN(values[i])) line.Points.Add(new Windows.Foundation.Point(x(i), y(values[i])));
        AutomationProperties.SetAccessibilityView(line, AccessibilityView.Raw);
        return line;
    }
}
