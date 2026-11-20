using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.App.Legal;
using NetPulse.App.Screens;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;
using Windows.Graphics;

namespace NetPulse.App;

/// <summary>
/// 사용 조건 동의 (M10 설계 A2 [1-1][2-1][2-2]). 동의한 판이 지금 판과 다르면 셸 대신 이 창이 먼저 뜬다.
/// 이 창이 떠 있는 동안 앱은 측정·기록·진단·트레이·셸을 만들지 않는다 (동의 전 경계).
/// 동의하면 <see cref="Accepted"/>(동의 기록을 썼는가)를 알리고, 동의하지 않으면 <see cref="Declined"/>.
/// 진단 값은 자동화 요소 "diag.state"의 이름(JSON)으로 내보낸다 (셸과 같은 방식).
/// </summary>
public sealed class TermsWindow : Window
{
    private readonly Ui ui;
    private readonly AppLog log;
    private readonly GroupPanel root;
    private readonly TextBlock diag;
    private readonly GlassButton accept, decline, fullToggle, legalToggle;
    private readonly Border full = new() { Visibility = Visibility.Collapsed };
    private readonly Border legal = new() { Visibility = Visibility.Collapsed };
    private readonly string? terms;
    private LegalView? legalView;
    private bool done;

    public event Action<bool>? Accepted;
    public event Action? Declined;

    public TermsWindow(StartupOptions options, AppLog log)
    {
        this.log = log;
        Title = "NetPulse";
        AppWindow.ResizeClient(new SizeInt32(Size.WindowWidth, Size.WindowHeight));
        var look = AppSettings.Default;
        if (options.Theme is { } t) look = look with { Theme = t };
        ui = new Ui(this, options, log, look);
        terms = LegalTexts.Terms;

        root = new GroupPanel("terms.root", "NetPulse 사용 조건") { Background = ui.Brushes.Bg };
        var column = new StackPanel { Spacing = Space.S5, MaxWidth = Size.GuideWidth, HorizontalAlignment = HorizontalAlignment.Left };
        column.Children.Add(new PageHeader(ui, "terms.header", "처음 실행", "사용 조건"));

        var (card, body) = ScreenParts.Card(ui, "terms.card", "사용 조건에 동의해 주세요");
        var summary = new StackPanel { Spacing = Space.S3 };
        summary.Children.Add(Fonts.Wrapped("NetPulse 자체는 MIT 라이선스입니다. 이 프로그램에는 Microsoft Windows App SDK, .NET 런타임 같은 제3자 구성요소가 들어 있고, "
            + "그 사용 조건(Microsoft Software License Terms 포함)이 함께 적용됩니다.", Typography.Body, ui.Brushes.Ink));
        summary.Children.Add(Fonts.Wrapped("이 조건에 동의해야 NetPulse를 쓸 수 있습니다. 다시 배포할 때도 받는 사람에게 같은 조건과 고지를 함께 전해야 합니다. "
            + "요약은 원문을 대신하지 않으며, 원문이 우선합니다.", Typography.Body, ui.Brushes.Muted));
        body.Set(summary);
        column.Children.Add(card);

        fullToggle = new GlassButton(ui, ButtonKind.Secondary, "terms.full", "사용 조건 전문", Icons.Chevron);
        legalToggle = new GlassButton(ui, ButtonKind.Secondary, "terms.legal", "라이선스와 고지", Icons.Chevron);
        fullToggle.Click += (_, _) => ToggleFull();
        legalToggle.Click += (_, _) => ToggleLegal();
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        toggles.Children.Add(fullToggle);
        toggles.Children.Add(legalToggle);
        column.Children.Add(toggles);
        column.Children.Add(full);
        column.Children.Add(legal);

        bool readable = terms is not null && LegalTexts.Items() is not null;
        if (!readable)
            column.Children.Add(new Banner(ui, "terms.failed", BannerKind.Error, "사용 조건 원문을 읽지 못해 동의할 수 없습니다. 배포 zip의 TERMS.txt를 확인하고 프로그램을 다시 받아 주세요."));

        accept = new GlassButton(ui, ButtonKind.Primary, "terms.accept", "동의하고 시작", Icons.Play) { IsEnabled = readable };
        decline = new GlassButton(ui, ButtonKind.Secondary, "terms.decline", "동의하지 않음");
        accept.Click += (_, _) => Accept();
        decline.Click += (_, _) => Decline();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        buttons.Children.Add(accept);
        buttons.Children.Add(decline);
        column.Children.Add(buttons);

        var scroll = new ScrollViewer { Content = column, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(Space.S7, Space.S6, Space.S5, Space.S6) };
        AutomationProperties.SetAutomationId(scroll, "terms.scroll");
        diag = new TextBlock { Opacity = 0, IsHitTestVisible = false, Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(diag, "diag.state");
        // 시험 전용: 누르면 이 화면의 배치 검사를 기록에 남긴다 (셸과 같은 진단 단추, G5 장면)
        var layoutProbe = new Button { Opacity = 0, Width = 0, Height = 0, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(layoutProbe, "diag.layout");
        layoutProbe.Click += (_, _) => log.Write("selftest", new { kind = "layout", screen = "terms", report = LayoutProbe.Report(root, diag) });
        root.Children.Add(scroll);
        root.Children.Add(diag);
        root.Children.Add(layoutProbe);

        Content = ui.Theme.Host(ScaleHost(options));
        ui.Root = root;
        ui.Theme.Apply(ui.Effects.Mode);
        AppWindow.Closing += (_, e) => { if (done) return; e.Cancel = true; Decline(); };   // 창 닫기 = 동의하지 않음
        root.Loaded += (_, _) => { WriteDiag(); (readable ? accept : decline).Focus(FocusState.Programmatic); };
        log.Write("terms", new { step = "shown", terms = TermsConsent.CurrentTerms, readable });
    }

    private UIElement ScaleHost(StartupOptions options)
    {
        if (options.UiScale <= 1) return root;
        var box = new Viewbox { Stretch = Stretch.Fill, Child = root };
        void Fit(double w, double h) { root.Width = w / options.UiScale; root.Height = h / options.UiScale; }
        SizeChanged += (_, e) => Fit(e.Size.Width, e.Size.Height);
        box.Loaded += (_, _) => Fit(Bounds.Width, Bounds.Height);
        return box;
    }

    private void ToggleFull()
    {
        bool open = full.Visibility != Visibility.Visible;
        if (open && full.Child is null && terms is not null)
        {
            var text = new TextBlock { Text = terms, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Fonts.Apply(text, Typography.Body);
            text.Foreground = ui.Brushes.Ink;
            AutomationProperties.SetAutomationId(text, "terms.fulltext");
            full.Child = ui.Glass.Surface(GlassLevel.Control, text, Radius.Inner, new Thickness(Space.S5));
        }
        full.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(fullToggle, open ? "펼침" : "접힘");
        WriteDiag();
    }

    private void ToggleLegal()
    {
        bool open = legal.Visibility != Visibility.Visible;
        if (open && legalView is null)
        {
            legalView = new LegalView(ui, "terms.legal", 360);
            legalView.Changed += WriteDiag;
            legal.Child = legalView;
        }
        legal.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(legalToggle, open ? "펼침" : "접힘");
        WriteDiag();
    }

    private void Accept()
    {
        if (done || !accept.IsEnabled) return;
        done = true;
        accept.IsEnabled = decline.IsEnabled = false;
        bool saved = TermsConsent.TryAccept(ui.Options.UserData, DateTimeOffset.Now);
        log.Write("terms", new { step = "accepted", terms = TermsConsent.CurrentTerms, saved });
        Accepted?.Invoke(saved);
    }

    private void Decline()
    {
        if (done) return;
        done = true;
        log.Write("terms", new { step = "declined" });
        Declined?.Invoke();
    }

    /// <summary>두 번째 실행의 "열기": 이 창을 앞으로.</summary>
    public void BringToFront()
    {
        AppWindow.Show();
        Activate();
    }

    /// <summary>셸이 뜬 뒤 또는 종료 때 닫는다.</summary>
    public void CloseNow()
    {
        done = true;
        ui.Detach();
        Close();
    }

    private void WriteDiag()
    {
        var state = new
        {
            screen = "terms",
            terms = TermsConsent.CurrentTerms,
            canAccept = accept.IsEnabled,
            full = full.Visibility == Visibility.Visible,
            legal = legal.Visibility == Visibility.Visible,
            legalItems = legalView?.Items?.Count,
            legalExpanded = legalView?.Expanded ?? -1,
        };
        diag.Text = JsonSerializer.Serialize(state);
    }
}
