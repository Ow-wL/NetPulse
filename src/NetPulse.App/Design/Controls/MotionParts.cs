using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 자릿수가 굴러가는 숫자 (기획서 7.4 "숫자 변화"). 자리마다 0~9를 세로로 쌓고 보이는 칸만 잘라 보여 주며, 값이 바뀌면 그 자리로 굴린다.
/// 움직임 줄이기면 바로 바뀐다. 화면 읽기에는 값을 이름으로 준다.
/// </summary>
public sealed class DigitRoller : GroupPanel
{
    private readonly Ui ui;
    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    private readonly List<StackPanel> columns = [];
    private readonly TypeStyle style;
    private readonly string unit;

    public DigitRoller(Ui ui, string automationId, int digits, TypeStyle style, string unit) : base(automationId)
    {
        this.ui = ui;
        this.style = style;
        this.unit = unit;
        double line = style.Size * style.LineHeight;
        for (int d = 0; d < digits; d++)
        {
            var column = new StackPanel();
            for (int n = 0; n <= 9; n++)
            {
                var t = Fonts.Text(n.ToString(CultureInfo.InvariantCulture), style, ui.Brushes.Ink);
                t.Height = line;
                column.Children.Add(t);
            }
            var window = new Grid { Height = line, Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, style.Size, line) }, Width = style.Size * Typography.DigitWidth };
            window.Children.Add(column);
            columns.Add(column);
            row.Children.Add(window);
        }
        var u = Fonts.Text(unit, Typography.Body, ui.Brushes.Muted);
        u.VerticalAlignment = VerticalAlignment.Bottom;
        u.Margin = new Thickness(Space.S1, 0, 0, 0);
        row.Children.Add(u);
        AutomationProperties.SetAccessibilityView(row, AccessibilityView.Raw);
        Children.Add(row);
    }

    public void Set(int value, bool animate = true)
    {
        var text = Math.Clamp(value, 0, (int)Math.Pow(10, columns.Count) - 1).ToString(CultureInfo.InvariantCulture).PadLeft(columns.Count, '0');
        double line = style.Size * style.LineHeight;
        for (int i = 0; i < columns.Count; i++)
        {
            var to = new Vector3(0, (float)(-(text[i] - '0') * line), 0);
            if (animate) ui.Motion.MoveTo(columns[i], to, MotionTokens.Slow);
            else ui.Motion.Place(columns[i], to);
        }
        AutomationProperties.SetName(this, value.ToString(CultureInfo.InvariantCulture) + " " + unit);
    }
}

/// <summary>
/// 펄스 (기획서 7.4 대표 연출): 심박선 위로 빛이 가로지른다. 장식이라 화면 읽기에서 뺀다. 움직임 줄이기면 빛이 멈추고 숨는다.
/// </summary>
public sealed class PulseLine : Grid
{
    public PulseLine(Ui ui, string automationId)
    {
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Height = Size.ControlL;
        var line = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), Icons.Pulse),
            Stroke = ui.Brushes.Accent,
            StrokeThickness = Size.IconStroke,
            Stretch = Stretch.Fill,
            StrokeLineJoin = PenLineJoin.Round,
        };
        Children.Add(line);
        var glow = new Rectangle { Width = Size.ControlL, HorizontalAlignment = HorizontalAlignment.Left, Fill = ui.Brushes.Faint };
        Children.Add(glow);
        SizeChanged += (_, e) => { if (e.PreviousSize.Width <= 0 && e.NewSize.Width > 0) ui.Motion.Sweep(glow, (float)(e.NewSize.Width - Size.ControlL), MotionTokens.Slow * 6); };
    }
}
