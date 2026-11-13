using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Analysis;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 측정 (M5 설계 4.5): 머리(진행 막대·시작/중지·증상 표시·측정 구성), 겹침 타임라인, 메모 패널, 알림 배너, 대상 카드, 증상 목록, 끝난 결과.
/// 머리는 스크롤 밖, 나머지는 스크롤 안 (보통 유리 밑으로 스크롤 내용이 지나가지 않는다).
/// </summary>
public sealed class MeasureScreen : Screen
{
    private readonly PageHeader header;
    private readonly Meter meter;
    private readonly TextBlock progress;
    private readonly GlassButton start, stop, mark, config;
    private readonly TimelinePanel timeline;
    private readonly GroupPanel timelineCard;
    private readonly CardBody timelineBody;
    private readonly GlassButton zoomIn, zoomOut, all, now, left, right;
    private readonly TimelineView view = new();
    private readonly StackPanel timelineStack = new() { Spacing = Space.S3 };
    private readonly TextBlock range;
    private readonly StackPanel content = new() { Spacing = Space.S5 };
    private readonly StackPanel banners = new() { Spacing = Space.S2 };
    private readonly Grid cards = new() { ColumnSpacing = Space.S4, RowSpacing = Space.S4 };
    private readonly GroupPanel memoPanel;
    private readonly TextBlock memoTitle, memoState;
    private readonly GlassTextBox memoBox;
    private readonly GlassButton memoSave, memoSkip;
    private readonly GroupPanel marksCard;
    private readonly CardBody marksBody;
    private readonly StackPanel marksList = new() { Spacing = Space.S1 };
    private readonly ContentControl resultHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer scroll;
    private HostSnapshot? last;
    private (string Session, string Mark, long AtMs, DateTimeOffset Wall)? memo;
    private bool suppressDraft;

    public MeasureScreen(ScreenContext context) : base(context, "measure")
    {
        RowSpacing = Space.S4;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        start = new GlassButton(Ui, ButtonKind.Primary, "measure.start", "측정 시작", Icons.Play);
        stop = new GlassButton(Ui, ButtonKind.Primary, "measure.stop", "측정 중지", Icons.Stop);
        mark = new GlassButton(Ui, ButtonKind.Secondary, "measure.mark", "증상 표시", Icons.Flag);
        config = new GlassButton(Ui, ButtonKind.Secondary, "measure.config", "측정 구성", Icons.Pencil);
        start.Click += async (_, _) => await Context.Host.StartAsync();
        stop.Click += async (_, _) => await StopAsync();
        mark.Click += (_, _) => MarkSymptom();
        config.Click += (_, _) => Context.Navigate("profile", null);
        AutomationProperties.SetAcceleratorKey(mark, "Ctrl+M");
        header = new PageHeader(Ui, "measure.header", "", "측정", start, stop, mark, config);
        Children.Add(header);

        var progressRow = new Grid { ColumnSpacing = Space.S4 };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meter = new Meter(Ui, "measure.progress") { VerticalAlignment = VerticalAlignment.Center };
        progressRow.Children.Add(meter);
        progress = Fonts.Text("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(progress, "measure.progress.text");
        Grid.SetColumn(progress, 1);
        progressRow.Children.Add(progress);
        Grid.SetRow(progressRow, 1);
        Children.Add(progressRow);

        // 타임라인 카드 (조작 버튼은 카드 머리)
        timeline = new TimelinePanel(Ui, "measure.timeline", compact: false);
        timeline.Command += OnTimeline;
        zoomOut = new GlassButton(Ui, ButtonKind.Icon, "measure.zoomout", "축소", Icons.Minus);
        zoomIn = new GlassButton(Ui, ButtonKind.Icon, "measure.zoomin", "확대", Icons.Plus);
        left = new GlassButton(Ui, ButtonKind.Icon, "measure.left", "과거로", Icons.Back);
        right = new GlassButton(Ui, ButtonKind.Icon, "measure.right", "지금 쪽으로", Icons.Chevron);
        all = new GlassButton(Ui, ButtonKind.Icon, "measure.all", "전체 보기", Icons.ZoomAll);
        now = new GlassButton(Ui, ButtonKind.Secondary, "measure.now", "지금으로", Icons.Now);
        zoomOut.Click += (_, _) => OnTimeline(TimelineCommand.ZoomOut);
        zoomIn.Click += (_, _) => OnTimeline(TimelineCommand.ZoomIn);
        left.Click += (_, _) => OnTimeline(TimelineCommand.PanLeft);
        right.Click += (_, _) => OnTimeline(TimelineCommand.PanRight);
        all.Click += (_, _) => OnTimeline(TimelineCommand.ShowAll);
        now.Click += (_, _) => OnTimeline(TimelineCommand.ToNow);
        (timelineCard, timelineBody) = ScreenParts.Card(Ui, "measure.timelinecard", "타임라인", "", left, right, zoomOut, zoomIn, all, now);
        // 보이는 범위는 카드 머리가 아니라 본문 위 한 줄 (좁은 창에서 머리의 조작 버튼과 다투지 않게)
        range = Fonts.Text("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(range, "measure.timeline.range");
        timelineStack.Children.Add(range);
        timelineStack.Children.Add(timeline);
        timelineStack.Children.Add(Legend());
        timelineBody.Set(timelineStack);
        content.Children.Add(timelineCard);

        // 메모 패널 (비모달, 타임라인 아래)
        memoTitle = Fonts.Text("", Typography.Label, Ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(memoTitle, "measure.memo.title");
        memoState = Fonts.Wrapped("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(memoState, "measure.memo.state");
        memoBox = new GlassTextBox(Ui, "measure.memo.text", "메모 (선택)", "", "무슨 일이 있었나요? 예: 화상회의 중 끊김", double.NaN, multiline: false);
        memoBox.Box.MaxLength = SymptomMarks.MaxMemoLength;
        memoBox.Box.TextChanged += (_, _) => { if (!suppressDraft && memo is { } m) Context.Host.SetDraft(m.Session, m.Mark, memoBox.Text); };
        memoBox.Box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = SaveMemoAsync(); } };
        memoSave = new GlassButton(Ui, ButtonKind.Primary, "measure.memo.save", "저장", Icons.Check);
        memoSkip = new GlassButton(Ui, ButtonKind.Ghost, "measure.memo.skip", "건너뛰기");
        memoSave.Click += async (_, _) => await SaveMemoAsync();
        memoSkip.Click += (_, _) => CloseMemo(skip: true);
        var memoButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
        memoButtons.Children.Add(memoSave);
        memoButtons.Children.Add(memoSkip);
        var memoStack = new StackPanel { Spacing = Space.S3 };
        memoStack.Children.Add(memoTitle);
        memoStack.Children.Add(memoBox);
        memoStack.Children.Add(memoState);
        memoStack.Children.Add(memoButtons);
        memoPanel = new GroupPanel("measure.memo", "증상 메모") { Visibility = Visibility.Collapsed };
        memoPanel.Children.Add(Ui.Glass.Surface(GlassLevel.Content, memoStack, Radius.Outer, new Thickness(Space.S6)));
        content.Children.Add(memoPanel);

        content.Children.Add(banners);
        content.Children.Add(resultHost);
        content.Children.Add(cards);
        (marksCard, marksBody) = ScreenParts.Card(Ui, "measure.marks", "증상 표시", "목록에서 고르면 타임라인이 그 시각으로 갑니다");
        content.Children.Add(marksCard);

        scroll = new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "measure.scroll");
        Grid.SetRow(scroll, 2);
        Children.Add(scroll);
    }

    public override IReadOnlyList<UIElement> Parts => [header, timelineCard, cards, marksCard];
    public override FrameworkElement? SharedTarget => timelineCard;

    private UIElement Legend()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S5 };
        void Item(Microsoft.UI.Xaml.Media.Brush brush, bool dashed, string text)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
            var line = new Microsoft.UI.Xaml.Shapes.Line { X1 = 0, X2 = Space.S4, Y1 = Space.S2, Y2 = Space.S2, Stroke = brush, StrokeThickness = ChartStyle.Line, VerticalAlignment = VerticalAlignment.Center };
            if (dashed) line.StrokeDashArray = [ChartStyle.DashOn, ChartStyle.DashOff];
            AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            item.Children.Add(line);
            item.Children.Add(Fonts.Text(text, Typography.Caption, Ui.Brushes.Muted));
            row.Children.Add(item);
        }
        Item(Ui.Brushes.Accent, false, "인터넷 지연");
        Item(Ui.Brushes.Series2, true, "공유기 지연");
        Item(Ui.Brushes.Bad, false, "응답 없음이 관측된 회차");
        Item(Ui.Brushes.Muted, true, "관측 공백");
        Item(Ui.Brushes.Warn, true, "증상 표시");
        return row;
    }

    // ---------------- 그리기 ----------------

    public override void Render(HostSnapshot s)
    {
        last = s;
        bool measuring = s.State == HostState.Measuring;
        start.Visibility = s.State is HostState.Idle or HostState.Starting or HostState.NoProfile ? Visibility.Visible : Visibility.Collapsed;
        start.IsEnabled = s.State == HostState.Idle;
        stop.Visibility = s.Measuring ? Visibility.Visible : Visibility.Collapsed;
        stop.IsEnabled = measuring;
        mark.IsEnabled = measuring;
        string profile = s.Profile?.Name ?? "측정 구성 없음";
        header.SetOverline(s.State switch
        {
            HostState.Measuring => $"{profile} · 측정 중",
            HostState.Stopping => $"{profile} · 멈추는 중",
            HostState.Starting => $"{profile} · 시작하는 중",
            _ => s.Finished is not null ? $"{profile} · 끝남" : profile,
        });
        meter.Set(s.PlannedMs <= 0 ? 0 : (double)s.ElapsedMs / s.PlannedMs, LiveSummary.Progress(s.ElapsedMs, s.PlannedMs));
        progress.Text = s.Measuring || s.Finished is not null ? $"{LiveSummary.Progress(s.ElapsedMs, s.PlannedMs)} · 남은 시간 {TimelineModel.Seconds(Math.Max(0, s.PlannedMs - s.ElapsedMs))}" : "측정하지 않음";
        RenderTimeline(s);
        RenderBanners(s);
        RenderCards(s);
        RenderMarks(s);
        RenderResult(s);
        RenderMemoState(s);
    }

    private void RenderTimeline(HostSnapshot s)
    {
        if (s.Header is not { } h || s.Rounds.Count == 0)
        {
            var empty = ScreenParts.Message(Ui, "measure.timeline.empty", Icons.Chart, Ui.Brushes.Muted,
                s.State == HostState.Starting ? "측정을 시작하는 중입니다" : "측정하지 않는 중", "측정을 시작하면 지연과 응답 없음, 증상 표시가 한 축에 그려집니다.");
            timelineBody.Set(empty, "empty");
            foreach (var b in new[] { zoomIn, zoomOut, all, now, left, right }) b.IsEnabled = false;
            return;
        }
        timelineBody.Set(timelineStack, "normal");
        long nowMs = Math.Max(1, s.ElapsedMs);
        var (from, to) = view.Window(nowMs);
        var label = ScreenParts.TimeLabel(h.StartedAt);
        var marks = s.Marks.Select(m => new SymptomMark(m.Mark, null, m.AtMs, m.Wall, m.Memo, null, m.Truncated)).ToArray();
        var frame = TimelineModel.Frame(s.Rounds, h.Targets, s.Gaps, marks, s.LinkDowns, s.Bands, from, to, timeline.Columns, h.IntervalMs,
            view.Follow && !view.IsAll ? $"최근 {view.Label}" : $"{label(from)}~{label(to)}");
        bool ended = !s.Measuring;
        timeline.Show(frame, label, view.Follow && !ended, $"{label(from)}부터 {label(to)}까지 ({view.Label})");
        range.Text = $"{label(from)} ~ {(view.Follow && !ended ? "지금" : label(to))} · {view.Label}";
        AutomationProperties.SetName(range, range.Text);
        zoomIn.IsEnabled = view.StepIndex > 0 || view.IsAll;
        zoomOut.IsEnabled = !view.IsAll;
        all.IsEnabled = !view.IsAll;
        left.IsEnabled = !view.IsAll && from > 0;
        right.IsEnabled = !view.IsAll && !view.Follow;
        now.IsEnabled = !view.Follow;
    }

    /// <summary>타임라인 조작 (bench·시험도 이 경로).</summary>
    public void Command(TimelineCommand c) => OnTimeline(c);

    private void OnTimeline(TimelineCommand c)
    {
        long nowMs = Math.Max(1, last?.ElapsedMs ?? 1);
        switch (c)
        {
            case TimelineCommand.ZoomIn: view.ZoomIn(nowMs); break;
            case TimelineCommand.ZoomOut: view.ZoomOut(nowMs); break;
            case TimelineCommand.PanLeft: view.PanLeft(nowMs); break;
            case TimelineCommand.PanRight: view.PanRight(nowMs); break;
            case TimelineCommand.ToStart: view.ToStart(nowMs); break;
            case TimelineCommand.ToNow: view.ToNow(); break;
            case TimelineCommand.ShowAll: view.ShowAll(nowMs); break;
        }
        if (last is not null) RenderTimeline(last);
        Context.QueueDiag();
    }

    private void RenderBanners(HostSnapshot s)
    {
        banners.Children.Clear();
        if (s.StartError is { } err) banners.Children.Add(new Banner(Ui, "measure.banner.starterror", BannerKind.Error, "측정을 시작하지 못했습니다: " + err));
        if (s.Status is { } st)
        {
            if (st.StorageFailing)
                banners.Children.Add(new Banner(Ui, "measure.banner.storage", BannerKind.Error,
                    $"기록을 저장하지 못하고 있습니다. 측정은 계속하며 다시 시도합니다 (저장 대기 {st.Pending}회차)."));
            else if (st.LocationKind == "fallback")
                banners.Children.Add(new Banner(Ui, "measure.banner.fallback", BannerKind.Warn, "기본 위치에 쓸 수 없어 대체 위치에 기록하고 있습니다."));
            if (st.InGap) banners.Children.Add(new Banner(Ui, "measure.banner.gap", BannerKind.Info, "절전 중이었거나 측정이 멈춘 시간입니다. 이 시간은 관측하지 못한 공백으로 남깁니다."));
        }
        if (s.Analysis is { } a && s.Header is { } h)
        {
            if (a.StartedDisconnected)
                banners.Children.Add(new Banner(Ui, "measure.banner.disconnected", BannerKind.Warn, "어느 대상도 응답한 적이 없습니다. 끊긴 상태에서 측정을 시작했을 수 있습니다."));
            else if (a.NoBaselineTargets.Count > 0)
            {
                var names = h.Targets.Where(t => a.NoBaselineTargets.Contains(t.Id)).Select(t => t.Name);
                banners.Children.Add(new Banner(Ui, "measure.banner.nobaseline", BannerKind.Info,
                    $"응답 기준선이 없는 대상: {string.Join(", ", names)}. 응답을 막는 장비일 수 있어 구간 판단에서 뺍니다."));
            }
            if (a.Grade.Reason == "undecidable")
                banners.Children.Add(new Banner(Ui, "measure.banner.routehold", BannerKind.Info, "경로가 예상과 달라진 순간이 있어 그때의 원인은 판단하지 않습니다."));
        }
        banners.Visibility = banners.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderCards(HostSnapshot s)
    {
        cards.Children.Clear();
        cards.ColumnDefinitions.Clear();
        cards.RowDefinitions.Clear();
        int perRow = 3;
        for (int c = 0; c < perRow; c++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (s.Cards.Count == 0) return;
        int rows = (s.Cards.Count + perRow - 1) / perRow;
        for (int r = 0; r < rows; r++) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var now = Context.Now;
        for (int i = 0; i < s.Cards.Count; i++)
        {
            var card = s.Cards[i];
            var stack = new StackPanel { Spacing = Space.S2 };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Fonts.Text(card.Name, Typography.Label, Ui.Brushes.Ink);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(name);
            var role = Fonts.Text(card.Role, Typography.Caption, Ui.Brushes.Muted);
            Grid.SetColumn(role, 1);
            head.Children.Add(role);
            stack.Children.Add(head);
            var latest = Fonts.Text(card.Latest, Typography.Headline, Ui.Brushes.Ink);
            AutomationProperties.SetAutomationId(latest, $"measure.card.{card.TargetId}.latest");
            stack.Children.Add(latest);
            void Line(string id, string text)
            {
                var t = Fonts.Wrapped(text, Typography.Caption, Ui.Brushes.Muted);
                AutomationProperties.SetAutomationId(t, $"measure.card.{card.TargetId}.{id}");
                stack.Children.Add(t);
            }
            Line("loss", $"손실 {card.LossRate} · {card.LossSentence}");
            Line("avg", $"평균 지연 {card.Average}" + (card.Errors > 0 ? $" · 검사 오류 {card.Errors}회 (손실에서 뺌)" : ""));
            Line("streak", $"최대 연속 실패 {card.MaxConsecutiveLoss}회");
            Line("last", $"마지막 성공 {(card.LastSuccess is { } ls ? LiveSummary.Clock(ls) : "없음")} · 마지막 실패 {(card.LastLoss is { } ll ? LiveSummary.Clock(ll) : "없음")}");
            var panel = new GroupPanel($"measure.card.{card.TargetId}", card.Name);
            panel.Children.Add(Ui.Glass.Surface(GlassLevel.Content, stack, Radius.Outer, new Thickness(Space.S5)));
            Grid.SetRow(panel, i / perRow);
            Grid.SetColumn(panel, i % perRow);
            cards.Children.Add(panel);
        }
    }

    private void RenderMarks(HostSnapshot s)
    {
        if (s.Marks.Count == 0)
        {
            marksBody.Set(ScreenParts.Message(Ui, "measure.marks.empty", Icons.Flag, Ui.Brushes.Muted, "표시한 증상이 없습니다",
                "끊김을 느낀 순간 [증상 표시]를 누르세요 (Ctrl+M). 누른 시각이 기록에 남습니다."), "empty");
            return;
        }
        marksList.Children.Clear();
        foreach (var m in s.Marks)
        {
            string state = m.State switch { EventWriteState.Pending => " · 저장 다시 시도 중", EventWriteState.Lost => " · 기록하지 못함", _ => "" };
            string draft = s.SessionId is { } sid && Context.Host.Draft(sid, m.Mark) is { } d ? " · 저장하지 않은 메모 있음" : "";
            string text = $"{LiveSummary.Clock(m.Wall)} · {m.Memo ?? "메모 없음"}{state}{draft}";
            var t = Fonts.Text(text, Typography.Body, Ui.Brushes.Ink);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new RowButton(Ui, $"measure.marks.{m.Mark}", text, t);
            var captured = m;
            row.Click += (_, _) => SelectMark(captured);
            marksList.Children.Add(row);
        }
        marksBody.Set(marksList, "normal");
    }

    private void RenderResult(HostSnapshot s)
    {
        if (s.Measuring || s.Finished is not { } f) { resultHost.Content = null; return; }
        var stack = new StackPanel { Spacing = Space.S3 };
        string reason = f.Result.EndReason switch { EndReason.Completed => "계획한 시간을 마쳤습니다", EndReason.Stopped => "중지했습니다", EndReason.Error => "오류로 멈췄습니다", _ => "끝났습니다" };
        var title = Fonts.Text("측정이 끝났습니다 · " + reason, Typography.Label, Ui.Brushes.Muted);
        stack.Children.Add(title);
        if (f.Analysis is { } a)
        {
            var badge = LiveSummary.Badge(a.Grade.Grade);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
            row.Children.Add(Badges.Grade(Ui, badge.Letter, ScreenParts.GradeBrush(Ui, a.Grade.Grade)));
            var g = Fonts.Text(badge.Name, Typography.Headline, Ui.Brushes.Ink);
            g.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(g, "measure.result.grade");
            row.Children.Add(g);
            stack.Children.Add(row);
            var sentence = Fonts.Wrapped(a.Grade.Sentence, Typography.Body, Ui.Brushes.Ink);
            AutomationProperties.SetAutomationId(sentence, "measure.result.sentence");
            stack.Children.Add(sentence);
            long longest = a.Episodes.Count == 0 ? 0 : a.Episodes.Max(e => e.MinDurationMs);
            var counts = Fonts.Text(a.Episodes.Count == 0 ? "끊김 관측 안 됨" : $"끊김 {a.Episodes.Count}회 · 최장 {TimelineModel.Seconds(longest)} 이상",
                Typography.Body, Ui.Brushes.Muted);
            AutomationProperties.SetAutomationId(counts, "measure.result.outages");
            stack.Children.Add(counts);
        }
        else stack.Children.Add(Fonts.Wrapped("기록을 다시 읽지 못해 결과를 계산하지 못했습니다.", Typography.Body, Ui.Brushes.Ink));
        if (f.Result.Unsaved > 0)
            stack.Children.Add(new Banner(Ui, "measure.result.unsaved", BannerKind.Warn, $"저장하지 못한 회차가 {f.Result.Unsaved}개 있습니다. 결과는 저장된 회차만으로 계산했습니다."));
        if (f.Result.SealFailed)
            stack.Children.Add(new Banner(Ui, "measure.result.sealfailed", BannerKind.Error, "기록을 제때 마감하지 못했습니다. 기록 끝 일부가 빠졌을 수 있습니다."));
        if (f.Record?.MissingNotice is { } notice) stack.Children.Add(new Banner(Ui, "measure.result.notice", BannerKind.Warn, notice));
        stack.Children.Add(Fonts.Wrapped("기록 화면에서 자세히 보기는 다음 단계에서 만듭니다.", Typography.Caption, Ui.Brushes.Muted));
        var panel = new GroupPanel("measure.result", "측정 결과");
        panel.Children.Add(Ui.Glass.Surface(GlassLevel.Content, stack, Radius.Outer, new Thickness(Space.S6)));
        resultHost.Content = panel;
    }

    // ---------------- 증상 표시와 메모 ----------------

    /// <summary>증상 표시 (버튼, Ctrl+M). 누르는 즉시 시각이 확정된다. 열린 패널의 초안은 버리지 않고 먼저 저장한다.</summary>
    public async void MarkSymptom()
    {
        if (memo is { } open && memoBox.Text.Trim().Length > 0) _ = Context.Host.SaveMemoAsync(open.Session, open.Mark, memoBox.Text);
        var ticket = Context.Host.MarkSymptom();
        if (ticket is null)
        {
            Context.Toasts.Show(ToastKind.Warning, "증상을 표시하지 못했습니다", "측정 중일 때만 표시할 수 있습니다.");
            return;
        }
        var session = Context.Host.Snapshot.SessionId ?? "";
        OpenMemo(session, ticket.Mark, ticket.AtMs, ticket.Wall, null);
        var saved = await ticket.Saved;
        if (saved == EventWriteState.Lost)
            Context.Toasts.Show(ToastKind.Error, "증상을 기록하지 못했습니다", $"이 시각을 따로 적어 두세요: {LiveSummary.Clock(ticket.Wall)}", autoClose: false);
        RenderMemoState(Context.Host.Snapshot);
    }

    private void OpenMemo(string session, string markId, long atMs, DateTimeOffset wall, string? existing)
    {
        memo = (session, markId, atMs, wall);
        suppressDraft = true;
        memoBox.Text = Context.Host.Draft(session, markId) ?? existing ?? "";
        suppressDraft = false;
        memoTitle.Text = $"{LiveSummary.Clock(wall)}에 증상을 표시했습니다";
        AutomationProperties.SetName(memoTitle, memoTitle.Text);
        memoBox.SetError(null);
        memoPanel.Visibility = Visibility.Visible;
        RenderMemoState(Context.Host.Snapshot);
        memoBox.Box.Focus(FocusState.Programmatic);
        Context.QueueDiag();
    }

    private void SelectMark(SymptomView m)
    {
        view.CenterOn(m.AtMs, Math.Max(1, last?.ElapsedMs ?? 1));
        if (last is not null) RenderTimeline(last);
        if (last?.SessionId is { } sid) OpenMemo(sid, m.Mark, m.AtMs, m.Wall, m.Memo);
    }

    private void RenderMemoState(HostSnapshot s)
    {
        if (memo is not { } m) return;
        var view = s.Marks.FirstOrDefault(x => x.Mark == m.Mark);
        bool ended = !s.Measuring;
        string text = ended ? "측정이 끝났습니다. 메모는 기록에 따로 붙습니다." : view?.State switch
        {
            EventWriteState.Pending => "아직 저장하지 못해 다시 시도 중입니다.",
            EventWriteState.Lost => $"기록하지 못했습니다. 이 시각을 따로 적어 두세요: {LiveSummary.Clock(m.Wall)}",
            _ => "누른 시각은 기록에 남았습니다. 메모는 선택입니다.",
        };
        memoState.Text = Fonts.KeepAll(text);
        AutomationProperties.SetName(memoState, text);
    }

    private async Task SaveMemoAsync()
    {
        if (memo is not { } m) return;
        var text = memoBox.Text;
        memoSave.IsEnabled = false;
        var result = await Context.Host.SaveMemoAsync(m.Session, m.Mark, text);
        memoSave.IsEnabled = true;
        if (result == EventWriteState.Lost)
        {
            memoBox.SetError("메모를 저장하지 못했습니다. 메모는 지우지 않았으니 다시 저장해 보세요.");
            Context.QueueDiag();
            return;
        }
        if (result == EventWriteState.Pending) Context.Toasts.Show(ToastKind.Info, "메모를 받았습니다", "저장소가 회복되면 기록에 씁니다.");
        CloseMemo(skip: false);
    }

    private void CloseMemo(bool skip)
    {
        if (skip && memo is { } m) Context.Host.SetDraft(m.Session, m.Mark, null);
        memo = null;
        memoPanel.Visibility = Visibility.Collapsed;
        (mark.IsEnabled ? mark : start).Focus(FocusState.Programmatic);
        Context.QueueDiag();
    }

    private async Task StopAsync()
    {
        if (memo is { } m && memoBox.Text.Trim().Length > 0) Context.Host.SetDraft(m.Session, m.Mark, memoBox.Text);
        await Context.Host.StopAsync();   // 초안을 먼저 넣고 멈춘다
        if (memo is { } after && Context.Host.Draft(after.Session, after.Mark) is null) CloseMemo(skip: false);
    }

    public override bool Escape()
    {
        if (memo is null) return false;
        CloseMemo(skip: true);
        return true;
    }

    public override object? Diag()
    {
        var (from, to) = view.Window(Math.Max(1, last?.ElapsedMs ?? 1));
        return new
        {
            memoOpen = memo is not null, memoMark = memo?.Mark, memoDraftLength = memoBox.Text.Length,
            window = new { from, to, step = view.Label, follow = view.Follow, all = view.IsAll },
            timeline = AutomationProperties.GetName(timeline),
        };
    }
}
