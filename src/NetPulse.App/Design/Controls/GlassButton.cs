using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

public enum ButtonKind { Primary, Secondary, Ghost, Icon }

/// <summary>
/// 버튼 (M2 설계 5.6): 주요(강조색 칠), 보조(보통 유리), 고스트(투명), 아이콘.
/// Button을 바탕으로 해 키보드(Enter/Space), 호출 패턴, 초점을 그대로 쓴다. 초점은 두 겹 표시.
/// 포인터 올림은 옅은 덮개, 누름은 살짝 가라앉음(모션 도구).
/// </summary>
public sealed class GlassButton : Button
{
    private readonly Border hover;
    private bool forcedHover;

    public GlassButton(Ui ui, ButtonKind kind, string automationId, string? label, string? icon = null)
    {
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

        var ink = kind == ButtonKind.Primary ? ui.Brushes.AccentInk : ui.Brushes.Ink;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (icon is not null) row.Children.Add(Icons.Make(icon, ink, Size.IconM));
        if (label is not null && kind != ButtonKind.Icon) row.Children.Add(Fonts.Text(label, Typography.Label, ink));

        double height = kind == ButtonKind.Icon ? Size.ControlM : Size.ControlM;
        // 여백은 내용 줄에 두고 덮개는 버튼 전체를 덮는다 (포인터 올림이 버튼 전체에 보이게)
        row.Margin = kind == ButtonKind.Icon ? new Thickness(0) : new Thickness(Space.S5, 0, Space.S5, 0);
        var padding = new Thickness(0);
        hover = new Border { Background = ui.Brushes.Faint, CornerRadius = new CornerRadius(Radius.Inner), Opacity = 0, IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(hover);
        inner.Children.Add(row);

        UIElement body = kind switch
        {
            ButtonKind.Secondary => ui.Glass.Surface(GlassLevel.Control, inner, Radius.Inner, padding),
            _ => new Border
            {
                Background = kind == ButtonKind.Primary ? ui.Brushes.Accent : ui.Brushes.Transparent,
                CornerRadius = new CornerRadius(Radius.Inner),
                Padding = padding,
                Child = inner,
            },
        };
        if (body is FrameworkElement fe)
        {
            fe.Height = height;
            if (kind == ButtonKind.Icon) fe.Width = height;
        }
        Content = body;

        RegisterPropertyChangedCallback(IsPointerOverProperty, (_, _) => UpdateHover());
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => Opacity = IsEnabled ? 1 : Emphasis.Disabled);
        ui.Motion.AttachPress(this);
    }

    /// <summary>전시 화면 캡처용: 포인터 없이 포인터 올림 모습을 보인다.</summary>
    public void ForceHover(bool value)
    {
        forcedHover = value;
        UpdateHover();
    }

    private void UpdateHover() => hover.Opacity = IsPointerOver || forcedHover ? 1 : 0;
}
