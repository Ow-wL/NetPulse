using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 글 상자 (M5 설계 4.8): 이름표, 입력 칸, 오류 줄. 바탕은 투명(뒤의 두꺼운 유리)이고 테두리와 초점 테두리만 토큰 색으로 바꾼다.
/// 보조색 글자(자리표시)를 옅은 바탕 위에 두지 않는다 (대비 계산 규칙). 시각 상태는 색만 즉시 바뀐다.
/// 오류는 색이 아니라 아이콘 + 본문색 글로 알린다.
/// </summary>
public sealed class GlassTextBox : GroupPanel
{
    private readonly Ui ui;
    private readonly TextBlock error;
    private readonly FrameworkElement errorRow;

    public GlassTextBox(Ui ui, string automationId, string label, string text, string? placeholder = null, double width = double.NaN, bool multiline = false)
        : base(automationId + ".field", label)
    {
        this.ui = ui;
        var stack = new StackPanel { Spacing = Space.S1 };
        stack.Children.Add(Fonts.Text(label, Typography.Label, ui.Brushes.Muted));
        Box = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder ?? "",
            Width = width,
            MinHeight = multiline ? Size.MemoMinHeight : Size.ControlM,
            AcceptsReturn = false,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            BorderThickness = new Thickness(Size.Hairline),
            Padding = new Thickness(Space.S3, Space.S2, Space.S3, Space.S2),
            CornerRadius = new CornerRadius(Radius.Inner),
        };
        Fonts.Apply(Box, Typography.Body);
        var r = Box.Resources;
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
            r[key] = ui.Brushes.Transparent;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            r[key] = ui.Brushes.Ink;
        r["TextControlForegroundDisabled"] = ui.Brushes.Muted;
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundDisabled" })
            r[key] = ui.Brushes.Muted;
        r["TextControlBorderBrush"] = ui.Brushes.Edge;
        r["TextControlBorderBrushPointerOver"] = ui.Brushes.Muted;
        r["TextControlBorderBrushDisabled"] = ui.Brushes.Edge;
        r["TextControlBorderBrushFocused"] = ui.Brushes.FocusOuter;
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(Size.FocusOuter);
        r["TextControlSelectionHighlightColor"] = ui.Brushes.Accent;
        r["TextControlButtonForeground"] = ui.Brushes.Muted;
        r["TextControlButtonForegroundPointerOver"] = ui.Brushes.Ink;
        AutomationProperties.SetAutomationId(Box, automationId);
        AutomationProperties.SetName(Box, label);
        stack.Children.Add(Box);
        error = Fonts.Wrapped("", Typography.Caption, ui.Brushes.Ink);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S1, Visibility = Visibility.Collapsed };
        row.Children.Add(Icons.Make(Icons.Alert, ui.Brushes.Bad, Size.IconS));
        row.Children.Add(error);
        AutomationProperties.SetAutomationId(row, automationId + ".error");
        errorRow = row;
        stack.Children.Add(row);
        Children.Add(stack);
    }

    public TextBox Box { get; }
    public string Text { get => Box.Text; set => Box.Text = value; }

    /// <summary>오류 문장 (없으면 숨김). 화면 읽기에는 입력 칸의 도움말로도 준다.</summary>
    public void SetError(string? message)
    {
        bool appeared = message is not null && errorRow.Visibility == Visibility.Collapsed;
        error.Text = Fonts.KeepAll(message ?? "");
        AutomationProperties.SetName(error, message ?? "");
        errorRow.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(Box, message ?? "");
        AutomationProperties.SetName((DependencyObject)errorRow, message ?? "");
        // 입력 중에 오류가 생기면 스크롤이 글 상자까지만 보여 줘 아래 오류 글이 잘렸다: 배치가 끝난 뒤 오류 줄까지 보이게
        if (appeared && Box.FocusState != FocusState.Unfocused)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => errorRow.StartBringIntoView());
    }
}

/// <summary>진행 막대 (M5 설계 4.8). 이름 = "10분 중 3분 12초".</summary>
public sealed class Meter : GroupPanel
{
    private readonly Border fill;
    private double fraction;

    public Meter(Ui ui, string automationId) : base(automationId, "")
    {
        Height = Size.MeterHeight;
        var track = Pill.Apply(new Border { Background = ui.Brushes.Faint, BorderBrush = ui.Brushes.Edge, BorderThickness = new Thickness(Size.Hairline) });
        fill = Pill.Apply(new Border { Background = ui.Brushes.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 });
        Children.Add(track);
        Children.Add(fill);
        SizeChanged += (_, _) => Layout();
    }

    public void Set(double value, string label)
    {
        fraction = Math.Clamp(value, 0, 1);
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetItemStatus(this, Math.Round(fraction * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Layout();
    }

    private void Layout() => fill.Width = Math.Max(0, ActualWidth * fraction);
}

/// <summary>
/// 등급 고리 (M5 설계 4.5, 4.8). 채움은 등급 단계(A 4칸 … D 1칸), 관측 부족·판단 불가는 점선 고리. 점수를 만들지 않는다.
/// 색만으로 전하지 않도록 글자(A~D, –, ?)와 등급 이름을 함께 쓴다.
/// </summary>
public sealed class GradeRing : GroupPanel
{
    private readonly Ui ui;
    private readonly Ellipse track;
    private readonly Microsoft.UI.Xaml.Shapes.Path arc;
    private readonly TextBlock letter, caption;

    public GradeRing(Ui ui, string automationId) : base(automationId, "")
    {
        this.ui = ui;
        Width = Height = Size.GradeRing;
        track = new Ellipse { Stroke = ui.Brushes.Faint, StrokeThickness = Size.GradeRingStroke };
        arc = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = Size.GradeRingStroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        letter = Fonts.Text("–", Typography.Display, ui.Brushes.Ink);
        caption = Fonts.Text("", Typography.Caption, ui.Brushes.Muted);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        letter.HorizontalAlignment = caption.HorizontalAlignment = HorizontalAlignment.Center;
        center.Children.Add(letter);
        center.Children.Add(caption);
        AutomationProperties.SetAccessibilityView(track, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(arc, AccessibilityView.Raw);
        Children.Add(track);
        Children.Add(arc);
        Children.Add(center);
    }

    public void Set(string letterText, string name, int fill, bool dashed, Brush brush, string accessible)
    {
        letter.Text = letterText;
        caption.Text = name;
        AutomationProperties.SetName(this, accessible);
        track.StrokeDashArray = dashed ? [ChartStyle.DashOn, ChartStyle.DashOff] : null;
        arc.Stroke = brush;
        double r = (Size.GradeRing - Size.GradeRingStroke) / 2;
        double c = Size.GradeRing / 2;
        double sweep = Math.Clamp(fill, 0, 4) / 4.0 * Math.PI * 2;
        if (fill <= 0) { arc.Data = null; return; }
        if (fill >= 4) sweep = Math.PI * 2 * 0.9999;
        var start = new Windows.Foundation.Point(c, c - r);
        var end = new Windows.Foundation.Point(c + r * Math.Sin(sweep), c - r * Math.Cos(sweep));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment { Point = end, Size = new Windows.Foundation.Size(r, r), IsLargeArc = sweep > Math.PI, SweepDirection = SweepDirection.Clockwise });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        arc.Data = geometry;
    }
}

public enum BannerKind { Info, Warn, Error }

/// <summary>카드 안 한 줄 알림 (M5 설계 4.8): 상태 아이콘 + 본문색 글. 색만으로 전하지 않는다.</summary>
public sealed class Banner : GroupPanel
{
    public Banner(Ui ui, string automationId, BannerKind kind, string text) : base(automationId, text)
    {
        var (icon, brush) = kind switch { BannerKind.Error => (Icons.Alert, ui.Brushes.Bad), BannerKind.Warn => (Icons.Alert, ui.Brushes.Warn), _ => (Icons.Info, ui.Brushes.Accent) };
        var row = new Grid { ColumnSpacing = Space.S3 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var i = Icons.Make(icon, brush, Size.IconM);
        i.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(i);
        var t = Fonts.Wrapped(text, Typography.Body, ui.Brushes.Ink);
        Grid.SetColumn(t, 1);
        row.Children.Add(t);
        AutomationProperties.SetItemStatus(this, kind.ToString().ToLowerInvariant());
        Children.Add(ui.Glass.Surface(GlassLevel.Content, row, Radius.Inner, new Thickness(Space.S4, Space.S3, Space.S4, Space.S3)));
    }
}

/// <summary>화면 머리 (M5): 작은 위 글, 제목, 오른쪽 행동. 스크롤 영역 밖에 둔다.</summary>
public sealed class PageHeader : GroupPanel
{
    public PageHeader(Ui ui, string automationId, string? overline, string title, params UIElement[] actions) : base(automationId, title)
    {
        var grid = new Grid { ColumnSpacing = Space.S4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = Space.S1, VerticalAlignment = VerticalAlignment.Center };
        Overline = Fonts.Text(overline ?? "", Typography.Caption, ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(Overline, automationId + ".overline");
        if (overline is null) Overline.Visibility = Visibility.Collapsed;
        Title = Fonts.Text(title, Typography.Title, ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(Title, automationId + ".title");
        left.Children.Add(Overline);
        left.Children.Add(Title);
        grid.Children.Add(left);
        Actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var a in actions) Actions.Children.Add(a);
        Grid.SetColumn(Actions, 1);
        grid.Children.Add(Actions);
        Children.Add(grid);
    }

    public TextBlock Overline { get; }
    public TextBlock Title { get; }
    public StackPanel Actions { get; }

    public void SetOverline(string? text)
    {
        Overline.Text = text ?? "";
        Overline.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// 눌러서 이동하는 줄 (M5: 최근 측정, 증상 목록, 측정 상태 알약). 템플릿 없는 버튼 + 포인터 덮개 + 두 겹 초점 표시.
/// </summary>
public class RowButton : Button
{
    private readonly Border hover;

    public RowButton(Ui ui, string automationId, string name, UIElement content, bool pill = false)
    {
        Template = Templates.Plain("Button");
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetName(this, name);
        UseSystemFocusVisuals = true;
        FocusVisualPrimaryBrush = ui.Brushes.FocusOuter;
        FocusVisualSecondaryBrush = ui.Brushes.FocusInner;
        FocusVisualPrimaryThickness = new Thickness(Size.FocusOuter);
        FocusVisualSecondaryThickness = new Thickness(Size.FocusInner);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        hover = new Border { Background = ui.Brushes.Faint, CornerRadius = new CornerRadius(Radius.Inner), Opacity = 0, IsHitTestVisible = false };
        var grid = new Grid();
        grid.Children.Add(hover);
        if (content is FrameworkElement fe) fe.Margin = new Thickness(Space.S3, Space.S2, Space.S3, Space.S2);
        grid.Children.Add(content);
        if (pill)
        {
            var border = new Border { Background = ui.Brushes.Faint, BorderBrush = ui.Brushes.Edge, BorderThickness = new Thickness(Size.Hairline), CornerRadius = new CornerRadius(Radius.Inner), Child = grid };
            Content = border;
        }
        else Content = grid;
        RegisterPropertyChangedCallback(IsPointerOverProperty, (_, _) => hover.Opacity = IsPointerOver ? Emphasis.Full : 0);
        ui.Motion.AttachPress(this);
    }
}

/// <summary>등급 배지 (최근 측정 줄): 둥근 칸 + 글자. 색은 등급 상태색이고 글자가 함께 있다.</summary>
public static class Badges
{
    public static FrameworkElement Grade(Ui ui, string letter, Brush brush)
    {
        var t = Fonts.Text(letter, Typography.Headline, ui.Brushes.Ink);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        var b = new Border
        {
            Width = Size.RowBadge, Height = Size.RowBadge, CornerRadius = new CornerRadius(Radius.Inner),
            BorderBrush = brush, BorderThickness = new Thickness(Size.FocusOuter), Background = ui.Brushes.Faint, Child = t,
        };
        AutomationProperties.SetAccessibilityView(b, AccessibilityView.Raw);
        return b;
    }
}
