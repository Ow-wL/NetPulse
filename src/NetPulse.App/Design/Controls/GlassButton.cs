using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

public enum ButtonKind { Primary, Secondary, Ghost, Icon }

/// <summary>
/// 버튼 (M2 설계 5.6): 주요(강조색 칠), 보조(보통 유리), 고스트(투명), 아이콘.
/// Button을 바탕으로 해 키보드(Enter/Space), 호출 패턴, 초점을 그대로 쓴다. 초점은 두 겹 표시.
/// 포인터 올림은 옅은 덮개, 누름은 살짝 가라앉음(모션 도구).
/// 비활성 (M12 설계 F, DECISIONS "M12 비활성 단추 대비"): 단추 전체를 흐리지 않는다. 글·아이콘은 비활성 토큰(<c>DisabledInk</c>, 3:1 이상),
/// 테두리는 점선(<c>DisabledEdge</c>), 주요 단추는 강조색 칠 대신 보통 유리 바탕. 그래서 누를 수 있는지가 색만이 아니라 채움·테두리 모양으로도 보인다.
/// </summary>
public sealed class GlassButton : Button
{
    private readonly Ui ui;
    private readonly ButtonKind kind;
    private readonly Border hover;
    private readonly Grid inner;
    private readonly StackPanel row;
    private readonly FrameworkElement body;
    private readonly Border? plain;
    private readonly TextBlock? labelText;
    private readonly Rectangle disabledEdge;
    private readonly Brush enabledInk;
    private bool forcedHover;

    public GlassButton(Ui ui, ButtonKind kind, string automationId, string? label, string? icon = null)
    {
        this.ui = ui;
        this.kind = kind;
        Template = Templates.Plain("Button");
        AutomationProperties.SetAutomationId(this, automationId);
        if (label is not null) AutomationProperties.SetName(this, label);
        UseSystemFocusVisuals = true;
        FocusVisualPrimaryBrush = ui.Brushes.FocusOuter;
        FocusVisualSecondaryBrush = ui.Brushes.FocusInner;
        FocusVisualPrimaryThickness = new Thickness(Size.FocusOuter);
        FocusVisualSecondaryThickness = new Thickness(Size.FocusInner);
        FocusVisualMargin = new Thickness(-Size.FocusOuter);
        HorizontalAlignment = HorizontalAlignment.Left;

        enabledInk = kind == ButtonKind.Primary ? ui.Brushes.AccentInk : ui.Brushes.Ink;
        var ink = Ink(true);
        row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (icon is not null) row.Children.Add(IconElement = Icons.Make(icon, ink, Size.IconM));
        if (label is not null && kind != ButtonKind.Icon) row.Children.Add(labelText = Fonts.Text(label, Typography.Label, ink));

        double height = kind == ButtonKind.Icon ? Size.ControlM : Size.ControlM;
        // 여백은 내용 줄에 두고 덮개는 버튼 전체를 덮는다 (포인터 올림이 버튼 전체에 보이게)
        row.Margin = kind == ButtonKind.Icon ? new Thickness(0) : new Thickness(Space.S5, 0, Space.S5, 0);
        var padding = new Thickness(0);
        hover = new Border { Background = ui.Brushes.Faint, CornerRadius = new CornerRadius(Radius.Inner), Opacity = 0, IsHitTestVisible = false };
        inner = new Grid();
        inner.Children.Add(hover);
        inner.Children.Add(row);

        if (kind == ButtonKind.Secondary) body = ui.Glass.Surface(GlassLevel.Control, inner, Radius.Inner, padding);
        else body = plain = new Border
        {
            Background = kind == ButtonKind.Primary ? ui.Brushes.Accent : ui.Brushes.Transparent,
            CornerRadius = new CornerRadius(Radius.Inner),
            Padding = padding,
            Child = inner,
        };
        body.Height = height;
        if (kind == ButtonKind.Icon) body.Width = height;
        // 비활성 점선 테두리는 바탕 위 맨 위 층 (보조 단추는 유리 테두리 자리에 겹친다)
        disabledEdge = new Rectangle
        {
            Stroke = ui.Brushes.DisabledEdge,
            StrokeThickness = Size.Hairline,
            StrokeDashArray = [Size.DisabledDashOn, Size.DisabledDashOff],
            RadiusX = Radius.Inner,
            RadiusY = Radius.Inner,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        var frame = new Grid();
        frame.Children.Add(body);
        frame.Children.Add(disabledEdge);
        Content = frame;

        RegisterPropertyChangedCallback(IsPointerOverProperty, (_, _) => UpdateHover());
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => UpdateEnabled());
        ui.Motion.AttachPress(this);
    }

    /// <summary>글·아이콘 색. 실측 대비 시험(<c>--hide-measured-text</c>, M12 설계 F)에서는 내용만 투명하게 (바탕·테두리는 그대로).</summary>
    private Brush Ink(bool enabled) => ui.Options.HideMeasuredText ? ui.Brushes.Transparent : enabled ? enabledInk : ui.Brushes.DisabledInk;

    private void UpdateEnabled()
    {
        bool on = IsEnabled;
        var ink = Ink(on);
        if (labelText is not null) labelText.Foreground = ink;
        if (IconElement is not null) Icons.SetStroke(IconElement, ink);
        // 비활성 주요 단추는 강조색을 빼고 보통 유리 바탕 (비활성 글 토큰은 이 유리 위에서 3:1, Design DisabledButtonContrastTests)
        if (kind == ButtonKind.Primary && plain is not null) plain.Background = on ? ui.Brushes.Accent : ui.Glass.Brush(GlassLevel.Control);
        disabledEdge.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>아이콘 요소 (없으면 null). 펼침 화살표처럼 상태에 따라 돌릴 때 쓴다.</summary>
    public FrameworkElement? IconElement { get; }

    /// <summary>전시 화면 캡처용: 포인터 없이 포인터 올림 모습을 보인다.</summary>
    public void ForceHover(bool value)
    {
        forcedHover = value;
        UpdateHover();
    }

    private void UpdateHover() => hover.Opacity = IsPointerOver || forcedHover ? 1 : 0;

    /// <summary>
    /// 목록 줄처럼 쓰는 넓은 버튼 (M10 고지 목록): 내용을 왼쪽에 붙이고, 이름이 버튼보다 길면 줄을 바꾼다 (높이는 글에 맞춰 는다).
    /// 가운데 정렬 그대로면 긴 이름의 양 끝이 잘렸다 (M10 UI 시험 1차). 줄임표는 글자 잘림 검사에 걸려 쓰지 않는다.
    /// </summary>
    public void AlignStart()
    {
        var items = row.Children.ToArray();
        row.Children.Clear();
        if (!double.IsNaN(body.Height)) { body.MinHeight = body.Height; body.Height = double.NaN; }
        var grid = new Grid { ColumnSpacing = row.Spacing, Margin = new Thickness(row.Margin.Left, Space.S2, row.Margin.Right, Space.S2), VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == items.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn((FrameworkElement)items[i], i);
            if (items[i] is TextBlock label) label.TextWrapping = TextWrapping.WrapWholeWords;
            grid.Children.Add(items[i]);
        }
        inner.Children.Remove(row);
        inner.Children.Add(grid);
    }
}
