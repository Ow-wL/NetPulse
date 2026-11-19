using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>대화상자에서 고른 것 (세 번째 단추가 있는 대화상자, M9 처음 숨김 안내).</summary>
public enum DialogChoice { Cancel, Confirm, Alternate }

/// <summary>
/// 알림 (M2 설계 5.6). 창 안 최상위 층의 오른쪽 위에 쌓인다 (Popup을 쓰지 않음, 설계 3절).
/// 글이 많은 떠 있는 것이라 차폐층 위 보통 유리로 그린다 (기획서 7.2, 대비 계산의 Dialog 단계).
/// 화면 읽기: 정보·성공·주의는 정중하게, 오류는 즉시 알린다.
/// </summary>
public sealed class ToastHost : StackPanel
{
    private readonly Ui ui;
    private readonly List<DispatcherQueueTimer> timers = [];   // 필드로 붙잡아 둔다 (M0 교훈)
    private int sequence;

    /// <param name="bottom">메인 창 (M5): 화면 머리·카드를 가리지 않게 오른쪽 아래. 전시 화면은 도구 막대 아래 (M2).</param>
    public ToastHost(Ui ui, bool bottom = false)
    {
        this.ui = ui;
        Spacing = Space.S3;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Margin = bottom ? new Thickness(0, 0, Space.S6, Space.S6) : new Thickness(0, Space.S8 + Space.S8, Space.S6, 0);
        Width = Size.ToastWidth;
    }

    public int Count => Children.Count;

    /// <summary>창을 닫기 직전: 자동 닫기 타이머를 모두 멈추고 알림을 치운다 (닫힌 창에서 타이머가 요소를 건드리지 않게).</summary>
    public void StopAll()
    {
        foreach (var t in timers) t.Stop();
        timers.Clear();
        Children.Clear();
    }

    private bool held;
    private readonly List<(ToastKind Kind, string Title, string Body, bool AutoClose)> waiting = [];

    /// <summary>
    /// 창 숨김 (M9 설계 A1): 보이는 알림을 치우고, 숨긴 동안 오는 알림은 요소·모션·타이머를 만들지 않고 쌓아 둔다 (G4 M9 3라운드 b1:
    /// 숨기기 전에 시작한 메모 저장의 실패가 숨긴 뒤에 알림·페이드·타이머를 만들었다). 다시 열 때 <see cref="Release"/>.
    /// </summary>
    public void Hold()
    {
        held = true;
        StopAll();
    }

    /// <summary>다시 열기: 숨긴 동안 쌓인 알림 중 최근 것 몇 개를 보인다.</summary>
    public void Release()
    {
        if (!held) return;
        held = false;
        var list = waiting.TakeLast(3).ToArray();
        waiting.Clear();
        foreach (var w in list) Show(w.Kind, w.Title, w.Body, w.AutoClose);
    }

    public void Show(ToastKind kind, string title, string body, bool autoClose = true)
    {
        if (held) { waiting.Add((kind, title, body, autoClose)); return; }
        var id = "toast." + kind.ToString().ToLowerInvariant() + "." + sequence++;
        var (icon, brush) = kind switch
        {
            ToastKind.Success => (Icons.Check, ui.Brushes.Ok),
            ToastKind.Warning => (Icons.Alert, ui.Brushes.Warn),
            ToastKind.Error => (Icons.Cross, ui.Brushes.Bad),
            _ => (Icons.Info, ui.Brushes.Accent),
        };
        var toast = new GroupPanel(id, title + ". " + body);
        var grid = new Grid { ColumnSpacing = Space.S3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Icons.Make(icon, brush, Size.IconL));
        var texts = new StackPanel { Spacing = Space.S1 };
        texts.Children.Add(Fonts.Text(title, Typography.Label, ui.Brushes.Ink));
        texts.Children.Add(Fonts.Wrapped(body, Typography.Body, ui.Brushes.Ink));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var close = new GlassButton(ui, ButtonKind.Icon, id + ".close", "알림 닫기", Icons.Cross);
        close.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        // 차폐층(알림 밑에만) 위 보통 유리
        var scrim = new Border { Background = ui.Brushes.Scrim, CornerRadius = new CornerRadius(Radius.Outer) };
        toast.Children.Add(scrim);
        toast.Children.Add(ui.Glass.Surface(GlassLevel.Dialog, grid, Radius.Outer, new Thickness(Space.S4)));
        AutomationProperties.SetLiveSetting(toast, kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        Children.Add(toast);
        ui.Motion.Enter(toast);
        close.Click += (_, _) => Remove(toast);
        toast.Loaded += (_, _) =>
        {
            var peer = FrameworkElementAutomationPeer.FromElement(toast) ?? FrameworkElementAutomationPeer.CreatePeerForElement(toast);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                kind == ToastKind.Error ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                title + ". " + body, id);
        };
        if (autoClose && !ui.Options.FreezeMotion)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = MotionTokens.ToastLifetime;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => { timers.Remove(timer); if (!toast.FocusState.HasFlag(FocusState.Keyboard)) Remove(toast); };
            timers.Add(timer);
            timer.Start();
        }
    }

    private void Remove(UIElement toast)
    {
        if (Children.Contains(toast)) Children.Remove(toast);
    }
}

/// <summary>
/// 대화상자 (M2 설계 5.6). 창 전체를 차폐층으로 덮고 가운데 보통 유리. 별도 창이나 Popup을 쓰지 않는다.
/// 열면 첫 버튼에 초점, 탭 순환을 대화상자 안에 가두고, Esc로 닫으며, 닫으면 연 컨트롤로 초점을 돌려준다.
/// 열려 있는 동안 뒤 화면은 입력을 받지 않고 자동화의 컨트롤·콘텐츠 보기에서 빠진다.
/// </summary>
public sealed class DialogHost : Grid
{
    private readonly Ui ui;
    private readonly UIElement behind;
    private Control? opener;
    private GroupPanel? panel;

    public DialogHost(Ui ui, UIElement behind)
    {
        this.ui = ui;
        this.behind = behind;
        Visibility = Visibility.Collapsed;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public event Action<bool>? Closed;

    /// <summary>마지막으로 닫힐 때 고른 것 (<see cref="Closed"/>의 false는 취소와 세 번째 단추 둘 다).</summary>
    public DialogChoice LastChoice { get; private set; }

    /// <param name="alternate">null이 아니면 취소와 확인 사이에 세 번째 단추 (보조 단추, <c>dialog.alternate</c>).</param>
    public void Open(Control? openedBy, string title, string message, string confirm, string cancel, FocusState focus = FocusState.Programmatic, string? alternate = null)
    {
        opener = openedBy;
        Children.Clear();
        Children.Add(new Border { Background = ui.Brushes.Scrim });
        var stack = new StackPanel { Spacing = Space.S4 };
        stack.Children.Add(Fonts.Text(title, Typography.Headline, ui.Brushes.Ink));
        stack.Children.Add(Fonts.Wrapped(message, Typography.Body, ui.Brushes.Muted));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new GlassButton(ui, ButtonKind.Primary, "dialog.confirm", confirm);
        var no = new GlassButton(ui, ButtonKind.Secondary, "dialog.cancel", cancel);
        ok.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);
        buttons.Children.Add(no);
        if (alternate is not null)
        {
            var alt = new GlassButton(ui, ButtonKind.Secondary, "dialog.alternate", alternate);
            alt.Click += (_, _) => Close(DialogChoice.Alternate);
            buttons.Children.Add(alt);
        }
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        panel = new GroupPanel("dialog.panel", title)
        {
            Width = Size.DialogWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TabFocusNavigation = KeyboardNavigationMode.Cycle,
        };
        panel.Children.Add(ui.Glass.Surface(GlassLevel.Dialog, stack, Radius.Outer, new Thickness(Space.S6)));
        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; CloseFromEscape(); };
        panel.KeyboardAccelerators.Add(esc);
        Children.Add(panel);
        Visibility = Visibility.Visible;
        behind.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(behind, AccessibilityView.Raw);
        ui.Motion.Enter(panel);
        no.Loaded += (_, _) => no.Focus(focus);
    }

    /// <summary>
    /// 내용을 담는 대화상자 (M10 A3 라이선스와 고지): 닫기 단추 하나. 내용은 높이 제한 안에서 스크롤한다.
    /// 닫으면 연 단추로 초점이 돌아간다 (<see cref="Close(DialogChoice)"/>).
    /// </summary>
    public void OpenContent(Control? openedBy, string title, UIElement content, string close, double width, double maxContentHeight)
    {
        opener = openedBy;
        Children.Clear();
        Children.Add(new Border { Background = ui.Brushes.Scrim });
        // 제목·내용·단추 세 줄 격자: 창이 낮으면(150% 배율 등) 내용 줄만 줄고 [닫기]는 늘 보인다 (M10 UI 시험 1차: 단추가 창 밖으로 밀렸다)
        var stack = new Grid { RowSpacing = Space.S4 };
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.Children.Add(Fonts.Text(title, Typography.Headline, ui.Brushes.Ink));
        var scroller = new ScrollViewer { Content = content, MaxHeight = maxContentHeight, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, 0) };
        AutomationProperties.SetAutomationId(scroller, "dialog.scroll");
        Grid.SetRow(scroller, 1);
        stack.Children.Add(scroller);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3, HorizontalAlignment = HorizontalAlignment.Right };
        var no = new GlassButton(ui, ButtonKind.Secondary, "dialog.cancel", close);
        no.Click += (_, _) => Close(false);
        buttons.Children.Add(no);
        Grid.SetRow(buttons, 2);
        stack.Children.Add(buttons);
        panel = new GroupPanel("dialog.panel", title)
        {
            Width = width,
            Margin = new Thickness(0, Space.S6, 0, Space.S6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TabFocusNavigation = KeyboardNavigationMode.Cycle,
        };
        panel.Children.Add(ui.Glass.Surface(GlassLevel.Dialog, stack, Radius.Outer, new Thickness(Space.S6)));
        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; CloseFromEscape(); };
        panel.KeyboardAccelerators.Add(esc);
        Children.Add(panel);
        Visibility = Visibility.Visible;
        behind.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(behind, AccessibilityView.Raw);
        ui.Motion.Enter(panel);
        no.Loaded += (_, _) => no.Focus(FocusState.Programmatic);
    }

    /// <summary>Esc 키와 같은 경로 (자체 시험도 이것을 부른다).</summary>
    public void CloseFromEscape() => Close(false);

    public void Close(bool confirmed) => Close(confirmed ? DialogChoice.Confirm : DialogChoice.Cancel);

    public void Close(DialogChoice choice)
    {
        if (!IsOpen) return;
        LastChoice = choice;
        bool confirmed = choice == DialogChoice.Confirm;
        Visibility = Visibility.Collapsed;
        Children.Clear();
        panel = null;
        behind.IsHitTestVisible = true;
        AutomationProperties.SetAccessibilityView(behind, AccessibilityView.Content);
        opener?.Focus(FocusState.Programmatic);
        Closed?.Invoke(confirmed);
    }
}
