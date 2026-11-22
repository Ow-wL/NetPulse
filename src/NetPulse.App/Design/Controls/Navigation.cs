using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 왼쪽 탐색 (M2 설계 5.6). 항목은 라디오 버튼(선택 항목 패턴). 탭 한 번으로 들어오고, 위아래 화살표로 옮겨 다니며, Space/Enter로 고른다.
/// 보통 유리 위에 있고 스크롤 영역 밖이다 (배치 규칙, 설계 4.3).
/// </summary>
public sealed class NavRail : GroupPanel
{
    private readonly Ui ui;
    private readonly StackPanel items = new() { Spacing = Space.S1 };
    private readonly StackPanel bottom = new() { Spacing = Space.S1 };
    private readonly Border footer = new();
    private readonly Dictionary<string, RadioButton> byId = [];
    private readonly List<RadioButton> order = [];

    public NavRail(Ui ui, string automationId, string brand) : base(automationId, "탐색")
    {
        this.ui = ui;
        items.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        items.TabFocusNavigation = KeyboardNavigationMode.Once;
        bottom.TabFocusNavigation = KeyboardNavigationMode.Once;
        var column = new Grid { RowSpacing = Space.S5 };
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3, Margin = new Thickness(Space.S2, Space.S1, 0, 0) };
        brandRow.Children.Add(new Border { Width = Size.ControlS, Height = Size.ControlS, CornerRadius = new CornerRadius(Radius.Inner - Space.S1), Background = ui.Brushes.Accent, Child = Icons.Make(Icons.Pulse, ui.Brushes.AccentInk, Size.IconM) });
        var name = Fonts.Text(brand, Typography.Headline, ui.Brushes.Ink);
        name.VerticalAlignment = VerticalAlignment.Center;
        brandRow.Children.Add(name);
        column.Children.Add(brandRow);
        Grid.SetRow(items, 1);
        column.Children.Add(items);
        Grid.SetRow(bottom, 3);
        column.Children.Add(bottom);
        Grid.SetRow(footer, 4);
        column.Children.Add(footer);
        Width = Size.NavRailWidth;
        Children.Add(ui.Glass.Surface(GlassLevel.Control, column, Radius.Outer, new Thickness(Space.S3, Space.S5, Space.S3, Space.S5)));
    }

    public event Action<string>? Selected;

    public string? Current { get; private set; }

    /// <summary>아래쪽 칸 (M5: 측정 상태 알약).</summary>
    public UIElement? Footer { get => footer.Child; set => footer.Child = value; }

    /// <summary>아래쪽에 붙는 항목 (M5: 설정). 같은 선택 그룹이고, 위아래 화살표는 위쪽 목록과 이어진다.</summary>
    public void AddBottom(string id, string label, string icon) => Add(id, label, icon, bottom);

    public void Add(string id, string label, string icon) => Add(id, label, icon, items);

    private void Add(string id, string label, string icon, StackPanel target)
    {
        var text = Fonts.Text(label, Typography.Label, ui.Brushes.Muted);
        text.VerticalAlignment = VerticalAlignment.Center;
        var iconEl = Icons.Make(icon, ui.Brushes.Muted, Size.IconM);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        row.Children.Add(iconEl);
        row.Children.Add(text);
        var bg = new Border { CornerRadius = new CornerRadius(Radius.Inner), Padding = new Thickness(Space.S3, Space.S3 - Space.S0, Space.S3, Space.S3 - Space.S0), Background = ui.Brushes.Transparent, Child = row };
        var button = new RadioButton
        {
            GroupName = AutomationProperties.GetAutomationId(this),
            Template = Templates.Plain("RadioButton"),
            MinWidth = 0,
            Content = bg,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            UseSystemFocusVisuals = true,
            FocusVisualPrimaryBrush = ui.Brushes.FocusOuter,
            FocusVisualSecondaryBrush = ui.Brushes.FocusInner,
            FocusVisualPrimaryThickness = new Thickness(Size.FocusOuter),
            FocusVisualSecondaryThickness = new Thickness(Size.FocusInner),
        };
        AutomationProperties.SetAutomationId(button, "nav." + id);
        AutomationProperties.SetName(button, label);
        void Refresh()
        {
            bool on = button.IsChecked == true;
            bool hover = button.IsPointerOver;
            bg.Background = on || hover ? ui.Brushes.Faint : ui.Brushes.Transparent;
            bg.BorderBrush = on ? ui.Brushes.Edge : null;
            bg.BorderThickness = new Thickness(on ? Size.Hairline : 0);
            // 보조색 글자는 옅은 바탕(Faint) 위에 두지 않는다: 바탕이 깔리면 본문색으로 (대비 계산, G4 M2 2라운드 지적 1)
            text.Foreground = on || hover ? ui.Brushes.Ink : ui.Brushes.Muted;
            if (iconEl is Viewbox { Child: Canvas { Children: [Microsoft.UI.Xaml.Shapes.Path p] } }) p.Stroke = on ? ui.Brushes.Accent : hover ? ui.Brushes.Ink : ui.Brushes.Muted;
        }
        button.Checked += (_, _) => { Refresh(); Current = id; Selected?.Invoke(id); };
        button.Unchecked += (_, _) => Refresh();
        button.RegisterPropertyChangedCallback(ButtonBase.IsPointerOverProperty, (_, _) => Refresh());
        // 위아래 화살표로 초점만 옮긴다 (Space/Enter로 고름). 자동 XY 이동은 이 템플릿에서 움직이지 않아 직접 처리한다 (키 시험에서 발견).
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up)) return;
            int i = order.IndexOf(button) + (e.Key == Windows.System.VirtualKey.Down ? 1 : -1);
            if (i >= 0 && i < order.Count) order[i].Focus(FocusState.Keyboard);
            e.Handled = true;
        };
        byId[id] = button;
        if (ReferenceEquals(target, items)) order.Insert(items.Children.Count, button);
        else order.Add(button);
        target.Children.Add(button);
        Refresh();
    }

    public void Select(string id)
    {
        if (byId.TryGetValue(id, out var b)) b.IsChecked = true;
    }

    public RadioButton? Item(string id) => byId.GetValueOrDefault(id);
}

/// <summary>설정 켜고 끄기 (토글 패턴). 손잡이 이동은 모션 도구 (줄이기면 바로).</summary>
public sealed class SettingToggle : ToggleButton
{
    private readonly Ui ui;
    private readonly Border track;
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse knob;

    public SettingToggle(Ui ui, string automationId, string label, bool initial)
    {
        this.ui = ui;
        Template = Templates.Plain("ToggleButton");
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetName(this, label);
        UseSystemFocusVisuals = true;
        FocusVisualPrimaryBrush = ui.Brushes.FocusOuter;
        FocusVisualSecondaryBrush = ui.Brushes.FocusInner;
        FocusVisualPrimaryThickness = new Thickness(Size.FocusOuter);
        FocusVisualSecondaryThickness = new Thickness(Size.FocusInner);
        IsChecked = initial;
        knob = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = Size.ToggleKnob, Height = Size.ToggleKnob, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gap, 0, 0, 0) };
        track = Pill.Apply(new Border { Width = Size.ToggleTrackWidth, Height = Size.ToggleTrackHeight, BorderThickness = new Thickness(Size.Hairline), Child = knob });
        var row = new Grid { ColumnSpacing = Space.S4, Padding = new Thickness(0, Space.S2, 0, Space.S2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Fonts.Text(label, Typography.Body, ui.Brushes.Ink);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        Grid.SetColumn(track, 1);
        row.Children.Add(track);
        row.Background = ui.Brushes.Transparent;
        Content = row;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Checked += (_, _) => Update(true);
        Unchecked += (_, _) => Update(true);
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? Emphasis.Full : Emphasis.Disabled;   // 막힌 스위치는 흐리게 (리포트: 가린 문서의 메모 넣기)
        Opacity = IsEnabled ? Emphasis.Full : Emphasis.Disabled;
        Update(false);
    }

    private static double Gap => (Size.ToggleTrackHeight - Size.ToggleKnob) / 2 - Size.Hairline;

    private void Update(bool animate)
    {
        bool on = IsChecked == true;
        track.Background = on ? ui.Brushes.Accent : ui.Brushes.Faint;
        track.BorderBrush = on ? ui.Brushes.Accent : ui.Brushes.Edge;
        knob.Fill = on ? ui.Brushes.AccentInk : ui.Brushes.Ink;
        float x = on ? (float)(Size.ToggleTrackWidth - Size.ToggleKnob - Gap * 2 - Size.Hairline * 2) : 0;
        if (animate) ui.Motion.MoveTo(knob, new Vector3(x, 0, 0));
        else ui.Motion.Place(knob, new Vector3(x, 0, 0));
    }
}

/// <summary>여러 선택지 중 하나 (라디오 의미). 화살표로 이동, Space로 선택.</summary>
public sealed class SegmentedChoice : GroupPanel
{
    private readonly Dictionary<string, RadioButton> byValue = [];

    /// <summary>읽기 전용 (측정 중 편집 화면): 마우스도 키보드도 고르지 못한다 (G4 M5 1라운드 B5).</summary>
    public bool ReadOnly
    {
        get => !IsHitTestVisible;
        set
        {
            IsHitTestVisible = !value;
            foreach (var b in byValue.Values) b.IsEnabled = !value;
        }
    }

    public SegmentedChoice(Ui ui, string automationId, string label, IReadOnlyList<(string Value, string Label)> options, string initial)
        : base(automationId, label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S1, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled, TabFocusNavigation = KeyboardNavigationMode.Once };
        foreach (var (value, text) in options)
        {
            var t = Fonts.Text(text, Typography.Label, ui.Brushes.Muted);
            var bg = Pill.Apply(new Border { Padding = new Thickness(Space.S4, Space.S2 - Space.S0, Space.S4, Space.S2 - Space.S0), Background = ui.Brushes.Transparent, Child = t });
            var b = new RadioButton
            {
                GroupName = automationId,
                Template = Templates.Plain("RadioButton"),
            MinWidth = 0,
                Content = bg,
                UseSystemFocusVisuals = true,
                FocusVisualPrimaryBrush = ui.Brushes.FocusOuter,
                FocusVisualSecondaryBrush = ui.Brushes.FocusInner,
                FocusVisualPrimaryThickness = new Thickness(Size.FocusOuter),
                FocusVisualSecondaryThickness = new Thickness(Size.FocusInner),
            };
            AutomationProperties.SetAutomationId(b, automationId + "." + value);
            AutomationProperties.SetName(b, text);
            void Refresh()
            {
                bool on = b.IsChecked == true;
                bg.Background = on ? ui.Brushes.Accent : ui.Brushes.Transparent;
                t.Foreground = on ? ui.Brushes.AccentInk : ui.Brushes.Muted;
            }
            b.Checked += (_, _) => { Refresh(); Changed?.Invoke(value); };
            b.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is not (Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Left)) return;
                int i = row.Children.IndexOf(b) + (e.Key == Windows.System.VirtualKey.Right ? 1 : -1);
                if (i >= 0 && i < row.Children.Count && row.Children[i] is Control next) next.Focus(FocusState.Keyboard);
                e.Handled = true;
            };
            b.Unchecked += (_, _) => Refresh();
            b.IsChecked = value == initial;
            Refresh();
            byValue[value] = b;
            row.Children.Add(b);
        }
        Children.Add(Pill.Apply(new Border
        {
            Background = ui.Brushes.Transparent,   // 보조색 글자가 옅은 바탕 위에 오지 않게 (G4 M2 2라운드 지적 1)
            BorderBrush = ui.Brushes.Edge,
            BorderThickness = new Thickness(Size.Hairline),
            Padding = new Thickness(Space.S1),
            Child = row,
        }));
        HorizontalAlignment = HorizontalAlignment.Left;
        // 알약은 세로로 늘이지 않는다: 가로 줄이 높아지면 반경이 높이를 따라가 큰 원이 됐다 (M11 C3, 프로필 편집)
        VerticalAlignment = VerticalAlignment.Center;
    }

    public event Action<string>? Changed;

    public FrameworkElement? Item(string value) => byValue.GetValueOrDefault(value);

    public void Set(string value)
    {
        if (byValue.TryGetValue(value, out var b)) b.IsChecked = true;
    }
}
