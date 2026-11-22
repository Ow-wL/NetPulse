using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Detection;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;
using NetPulse.Core.Targets;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 첫 실행 안내 (M5 설계 4.5, N14): 탐지 결과를 보여주고 [이대로 측정 시작] 한 번으로 첫 측정을 시작한다.
/// 네트워크 없음·탐지 실패면 이유와 [다시 확인]·[대상 직접 입력]. 저장된 구성을 읽지 못했으면 그 안내를 위에.
/// </summary>
public sealed class FirstRunScreen : Screen
{
    private readonly PageHeader header;
    private readonly StackPanel column = new() { Spacing = Space.S5, MaxWidth = Size.GuideWidth, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly GroupPanel card;
    private readonly CardBody body;
    private readonly SegmentedChoice duration;
    private readonly GlassTextBox minutes;
    private readonly GlassButton startButton, edit, retry, manual;
    private readonly ContentControl notice = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel durationRow;
    private readonly DispatcherQueueTimer slowTimer;
    private DetectionResult? result;
    private bool detecting, slow;
    private CancellationTokenSource? detectCancel;

    public FirstRunScreen(ScreenContext context) : base(context, "firstrun")
    {
        RowSpacing = Space.S5;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        header = new PageHeader(Ui, "firstrun.header", "첫 실행 안내", "연결을 확인합니다");
        Children.Add(header);

        if (Context.Host.LoadStatus is ProfileLoadStatus.Unreadable or ProfileLoadStatus.UnknownVersion or ProfileLoadStatus.Invalid)
            column.Children.Add(new Banner(Ui, "firstrun.unreadable", BannerKind.Warn, "저장된 측정 구성을 읽지 못해 따로 보관했습니다. 연결을 다시 확인해 새로 만듭니다."));
        (card, body) = ScreenParts.Card(Ui, "firstrun.detect", "연결 확인", null);
        column.Children.Add(card);
        column.Children.Add(notice);

        duration = new SegmentedChoice(Ui, "firstrun.duration", "측정 시간", [("10", "10분"), ("30", "30분"), ("60", "1시간"), ("custom", "직접")], "10");
        minutes = new GlassTextBox(Ui, "firstrun.minutes", "측정 시간 (분, 1~720)", "10", null, Size.FieldS) { Visibility = Visibility.Collapsed };
        duration.Changed += v => { selectedDuration = v; minutes.Visibility = v == "custom" ? Visibility.Visible : Visibility.Collapsed; Validate(); };
        minutes.Box.TextChanged += (_, _) => Validate();
        durationRow = new StackPanel { Spacing = Space.S2 };
        durationRow.Children.Add(Fonts.Text("측정 시간", Typography.Label, Ui.Brushes.Muted));
        durationRow.Children.Add(duration);
        durationRow.Children.Add(minutes);
        column.Children.Add(durationRow);

        startButton = new GlassButton(Ui, ButtonKind.Primary, "firstrun.start", "이대로 측정 시작", Icons.Play);
        edit = new GlassButton(Ui, ButtonKind.Secondary, "firstrun.edit", "대상 고치기", Icons.Pencil);
        retry = new GlassButton(Ui, ButtonKind.Secondary, "firstrun.retry", "다시 확인", Icons.Refresh);
        manual = new GlassButton(Ui, ButtonKind.Secondary, "firstrun.manual", "대상 직접 입력", Icons.Pencil);
        startButton.Click += async (_, _) => await StartAsync();
        edit.Click += (_, _) => OpenEditor();
        manual.Click += (_, _) => OpenEditor();
        retry.Click += (_, _) => Detect();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        buttons.Children.Add(startButton);
        buttons.Children.Add(edit);
        buttons.Children.Add(manual);
        buttons.Children.Add(retry);
        column.Children.Add(buttons);

        var scroll = new ScrollViewer { Content = column, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "firstrun.scroll");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        slowTimer = DispatcherQueue.CreateTimer();
        slowTimer.Interval = Waits.Detect;
        slowTimer.IsRepeating = false;
        slowTimer.Tick += (_, _) => { slow = detecting; RenderDetection(); };
    }

    public override IReadOnlyList<UIElement> Parts => [header, column];

    public override void Entered()
    {
        if (result is null && !detecting) Detect();
    }

    public override void Render(HostSnapshot snapshot) => RenderDetection();

    private async void Detect()
    {
        detectCancel?.Cancel();
        detectCancel = new CancellationTokenSource();
        detecting = true;
        slow = false;
        result = null;
        RenderDetection();
        slowTimer.Start();
        try
        {
            var r = await Context.Host.DetectAsync(detectCancel.Token);
            result = r;
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            detecting = false;
            slowTimer.Stop();
        }
        Context.Log.Write("detection", new { status = result.Status.ToString(), kind = result.Kind?.ToString(), targets = result.Proposal?.ActiveTargets.Count ?? 0 });
        RenderDetection();
        Context.QueueDiag();
    }

    /// <summary>시작할 프로필: 저장된 구성이 있으면 그것, 없으면 탐지 제안 (측정 시간은 고른 값).</summary>
    private Profile? Proposal()
    {
        // 저장한 구성이 있으면 그 측정 시간을 그대로 쓴다 (편집에서 바꾼 시간이 첫 실행의 고른 시간에 덮이던 문제)
        if (Context.Host.CurrentProfile is { } saved) return saved;
        var p = result?.Proposal;
        if (p is null) return null;
        return Minutes() is int m ? p with { Duration = TimeSpan.FromMinutes(m) } : p;
    }

    private string selectedDuration = "10";

    /// <summary>고른 측정 시간 (분). 직접 입력이 범위 밖이면 null.</summary>
    private int? Minutes()
    {
        string v = selectedDuration == "custom" ? minutes.Text.Trim() : selectedDuration;
        return int.TryParse(v, out var m) && m >= Profile.MinDuration.TotalMinutes && m <= Profile.MaxDuration.TotalMinutes ? m : null;
    }

    private void Validate()
    {
        bool bad = selectedDuration == "custom" && Minutes() is null;
        minutes.SetError(bad ? "1~720 사이의 분을 적어 주세요." : null);
        startButton.IsEnabled = !detecting && !bad && Proposal() is { } p && !ProfileValidator.HasErrors(ProfileValidator.Validate(p));
    }

    private void RenderDetection()
    {
        header.Title.Text = detecting ? "연결을 확인하는 중입니다" : result?.Proposal is not null ? "연결을 확인했습니다" : "연결을 확인하지 못했습니다";
        notice.Content = null;
        if (detecting)
        {
            var wait = new StackPanel { Spacing = Space.S3 };
            wait.Children.Add(GlassCard.Skeleton(Ui));
            if (slow) wait.Children.Add(Fonts.Wrapped("확인이 오래 걸리고 있습니다. 다시 확인하거나 대상을 직접 입력할 수 있습니다.", Typography.Body, Ui.Brushes.Muted));
            body.Set(wait, "loading");
            edit.Visibility = Visibility.Collapsed;
            manual.Visibility = slow ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = slow ? Visibility.Visible : Visibility.Collapsed;
            startButton.IsEnabled = false;
            return;
        }
        if (result is null) return;
        var stack = new StackPanel { Spacing = Space.S3 };
        var proposal = Context.Host.CurrentProfile ?? result.Proposal;
        if (result.Proposal is null && Context.Host.CurrentProfile is null)
        {
            string why = result.Status switch
            {
                DetectionStatus.NoNetwork => "작동 중인 네트워크 연결이 없습니다. 케이블이나 Wi‑Fi 연결을 확인한 뒤 다시 확인하세요.",
                _ => result.Notice ?? "연결 정보를 읽지 못했습니다. 다시 확인하세요.",
            };
            stack.Children.Add(ScreenParts.Message(Ui, "firstrun.problem", Icons.Alert, Ui.Brushes.Warn, "측정 대상을 정하지 못했습니다", why));
            body.Set(stack, "error");
            edit.Visibility = Visibility.Collapsed;
            manual.Visibility = Visibility.Visible;
            retry.Visibility = Visibility.Visible;
            startButton.IsEnabled = false;
            return;
        }
        string kind = result.Kind switch { InterfaceKind.Wired => "유선", InterfaceKind.Wireless => "Wi‑Fi", InterfaceKind.Tunnel => "터널", _ => "알 수 없음" };
        stack.Children.Add(Row("firstrun.kind", "연결", kind));
        var local = proposal!.ActiveTargets.FirstOrDefault(t => t.Role == TargetRole.Local);
        stack.Children.Add(Row("firstrun.local", "로컬 대상", local is null ? "찾지 못함 (공유기를 측정하지 않습니다)" : $"{local.Name} {local.Address}"));
        var externals = proposal.ActiveTargets.Where(t => t.Role == TargetRole.External).ToArray();
        stack.Children.Add(Row("firstrun.external", "외부 대상", externals.Length == 0 ? "없음" : string.Join(", ", externals.Select(t => t.Name))));
        if (Context.Host.CurrentProfile is { } savedProfile)
            stack.Children.Add(Row("firstrun.saved", "측정 구성", $"저장한 측정 구성 \"{savedProfile.Name}\" · 측정 시간 {NetPulse.Core.Presentation.TimelineModel.Seconds((long)savedProfile.Duration.TotalMilliseconds)}"));
        durationRow.Visibility = Context.Host.CurrentProfile is null ? Visibility.Visible : Visibility.Collapsed;
        body.Set(stack, "normal");
        var notes = new StackPanel { Spacing = Space.S2 };
        if (result.Virtual == Tristate.Yes)
            notes.Children.Add(new Banner(Ui, "firstrun.virtual", BannerKind.Warn, "VPN이나 가상 어댑터를 통해 연결된 것 같습니다. 측정 결과가 실제 회선과 다를 수 있습니다."));
        if (result.OtherGateways > 0)
            notes.Children.Add(new Banner(Ui, "firstrun.gateways", BannerKind.Info, $"다른 네트워크 연결에도 공유기(게이트웨이)가 {result.OtherGateways}개 더 있습니다. Windows가 고른 경로로 측정합니다."));
        if (result.Status != DetectionStatus.Ok && result.Notice is { } n)
            notes.Children.Add(new Banner(Ui, "firstrun.notice", BannerKind.Warn, n));
        foreach (var w in ProfileValidator.Validate(proposal).Where(i => i.Severity == IssueSeverity.Warning))
            notes.Children.Add(new Banner(Ui, "firstrun.warn." + w.Code, BannerKind.Info, w.Message));
        notice.Content = notes.Children.Count == 0 ? null : notes;
        edit.Visibility = Visibility.Visible;
        manual.Visibility = Visibility.Collapsed;
        retry.Visibility = Visibility.Visible;
        Validate();
    }

    private FrameworkElement Row(string id, string label, string value)
    {
        var g = new Grid { ColumnSpacing = Space.S4 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Size.FieldS) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Fonts.Text(label, Typography.Label, Ui.Brushes.Muted));
        var v = Fonts.Wrapped(value, Typography.Body, Ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(v, id);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    private void OpenEditor()
    {
        // 첫 실행 문맥: 저장된 구성이 없으면 탐지 제안(없으면 빈 구성)으로 편집을 시작하고, 저장하면 이 화면으로 돌아온다
        if (Context.Host.CurrentProfile is null)
            ProfileEditor.Seed = result?.Proposal is { } p ? p with { Duration = TimeSpan.FromMinutes(Minutes() ?? 10) } : null;
        ProfileEditor.ReturnTo = "firstrun";
        Context.Navigate("profile", null);
    }

    private async Task StartAsync()
    {
        if (Proposal() is not { } p) return;
        startButton.IsEnabled = false;
        try
        {
            var set = Context.Host.Profiles is { } existing
                ? existing with { Profiles = existing.Profiles.Select(x => x.Id == p.Id ? p : x).ToArray(), LastUsedId = p.Id }
                : new ProfileSet([p], p.Id);
            Context.Host.SaveProfiles(set);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Context.Toasts.Show(ToastKind.Error, "측정 구성을 저장하지 못했습니다", ex.Message);
            startButton.IsEnabled = true;
            return;
        }
        Context.Log.Write("firstRunStart", new { targets = p.ActiveTargets.Count, minutes = p.Duration.TotalMinutes });
        if (await Context.Host.StartAsync()) Context.Navigate("measure", null);
        else Context.Navigate("measure", null);   // 시작 실패도 측정 화면의 배너로 보인다
    }

    public override object? Diag() => new
    {
        detecting, slow, status = result?.Status.ToString(), startEnabled = startButton.IsEnabled,
        proposal = Proposal()?.ActiveTargets.Select(t => t.Name).ToArray(), minutes = Minutes(),
    };
}
