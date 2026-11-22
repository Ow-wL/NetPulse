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
    private string? bannersKey, cardsKey, marksKey;
    private object? resultFor;
    private readonly RepresentativeCache reps = new();
    private string? drawnKey;
    private long drawnFrom, drawnTo;
    private TimelineFrame? drawnFrame;
    private int drawnRounds;

    /// <summary>
    /// 그린 뒤 온 회차의 지연이 마지막으로 그린 칸의 범위(최솟값~최댓값)나 세로축을 벗어나거나 선을 끊는지 (G4 M5 1라운드 B3:
    /// 넓은 보기에서 손실 없이 지연만 급증하면 다시 그리기를 건너뛰어 수십 초 동안 안 보였다).
    /// </summary>
    private bool NewValuesStandOut(HostSnapshot s, RecordHeader h)
    {
        if (drawnFrame is not { } f || s.Rounds.Count < drawnRounds) return true;
        reps.Update(s.Rounds, h.Targets);
        TimelinePoint? lastRouter = f.Router.Count > 0 && f.Router[^1].Count > 0 ? f.Router[^1][^1] : null;
        TimelinePoint? lastInternet = f.Internet.Count > 0 && f.Internet[^1].Count > 0 ? f.Internet[^1][^1] : null;
        static bool Out(double? v, TimelinePoint? last, double axis) =>
            v is not double x ? last is not null : last is not { } p || x > p.MaxMs || x < p.MinMs || x > axis;
        for (int i = drawnRounds; i < reps.Count; i++)
        {
            // 회차 번호가 건너뛰면 그래프 선이 끊긴다 (공백 띠가 없을 만큼 짧아도, 2라운드 2)
            if (i > 0 && s.Rounds[i].Round != s.Rounds[i - 1].Round + 1) return true;
            if (Out(reps.Router(i), lastRouter, f.AxisMaxMs) || Out(reps.Internet(i), lastInternet, f.AxisMaxMs)) return true;
        }
        return false;
    }
    private readonly Dictionary<string, CardTexts> cardTexts = [];
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
    // 시험 조건 확인 (M12 설계 A [2-1]): 끝난 스냅숏을 처음 받은 때의 타임라인 폭, 타임라인이 배치된 뒤 받은 스냅숏 수. 장면 계산과 무관하게 센다
    private bool finishedSeen, receivedFinishedBeforeLayout, timelineLaidOut;
    private double? widthAtReceive;
    private int snapshotsAfterLayout;
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
        timeline.SizeChanged += (_, e) => { if (e.NewSize.Width > 0) timelineLaidOut = true; };
        timeline.ColumnsChanged += () => { if (last is not null) RenderTimeline(last); };   // M12 설계 A: 끝난 측정은 새 스냅숏이 없다
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
        Item(Ui.Brushes.Series2, true, "로컬 대상 지연");
        Item(Ui.Brushes.Bad, false, "응답 없음이 관측된 회차");
        Item(Ui.Brushes.Muted, true, "관측 공백");
        Item(Ui.Brushes.Warn, true, "증상 표시");
        return row;
    }

    // ---------------- 그리기 ----------------

    public override void Render(HostSnapshot s)
    {
        last = s;
        if (timelineLaidOut) snapshotsAfterLayout++;
        if (!finishedSeen && s.Finished is not null && s.State != HostState.Measuring)
        {
            finishedSeen = true;
            widthAtReceive = timeline.ActualWidth;
            receivedFinishedBeforeLayout = !timelineLaidOut && timeline.ActualWidth <= 0;
        }
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
        // 끝난 뒤의 경과는 마지막 회차의 시작이라 한 간격이 모자란다 (3분 계획이 "2분 59초 · 남은 1초"로 보였다): 마지막 회차가 덮는 시간까지
        long shown = !s.Measuring && s.Finished is not null && s.Header is { } fh ? Math.Min(s.PlannedMs, s.ElapsedMs + fh.IntervalMs) : s.ElapsedMs;
        meter.Set(s.PlannedMs <= 0 ? 0 : (double)shown / s.PlannedMs, LiveSummary.Progress(shown, s.PlannedMs));
        progress.Text = s.Measuring ? $"{LiveSummary.Progress(s.ElapsedMs, s.PlannedMs)} · 남은 시간 {TimelineModel.Seconds(Math.Max(0, s.PlannedMs - s.ElapsedMs))}"
            : s.Finished is not null ? $"{LiveSummary.Progress(shown, s.PlannedMs)} 측정함" : "측정하지 않음";
        RenderProfile.Time("measure.timeline", () => RenderTimeline(s));
        RenderProfile.Time("measure.banners", () => RenderBanners(s));
        RenderProfile.Time("measure.cards", () => RenderCards(s));
        RenderProfile.Time("measure.marks", () => RenderMarks(s));
        RenderProfile.Time("measure.result", () => RenderResult(s));
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
        var label = ScreenParts.TimeLabel(h.StartedAt, Context.Now);
        bool ended = !s.Measuring;
        // 칸 하나(1 px)가 여러 회차를 덮는 넓은 보기에서는, 보이는 것(구간·표시·링크·공백·보기)이 그대로이고 창이 칸 하나만큼도
        // 움직이지 않았으면 다시 그리지 않는다. 12시간 전체 보기에서 1초마다 다시 그리던 것이 프레임 예산을 넘겼다 (M5 bench)
        long columnMs = (to - from) / Math.Max(1, timeline.Columns);
        var lastBand = s.Bands.Count == 0 ? null : s.Bands[^1];
        var drawKey = $"{s.SessionId}|{view.Label}|{view.Follow}|{view.IsAll}|{ended}|{timeline.Columns}|{s.Bands.Count}|{lastBand?.Kind}|{lastBand?.FromMs}|{lastBand?.OpenEnd}"
            + $"|{s.Marks.Count}|{(s.Marks.Count == 0 ? null : s.Marks[^1].Mark)}|{s.LinkDowns.Count}|{s.LinkDowns.Count(d => d.Ongoing)}|{s.Gaps.Count}";
        bool unchanged = columnMs > 2 * h.IntervalMs && drawKey == drawnKey && Math.Abs(from - drawnFrom) < columnMs && Math.Abs(to - drawnTo) < columnMs
            && !NewValuesStandOut(s, h);
        // 배치 전(폭 0)에는 장면을 만들지 않는다: 열 1개 장면이 된다. 배치되면 타임라인이 ColumnsChanged로 다시 부른다 (M12 설계 A)
        if (timeline.ActualWidth <= 0) drawnKey = null;
        else if (!unchanged)
        {
            var marks = s.Marks.Select(m => new SymptomMark(m.Mark, null, m.AtMs, m.Wall, m.Memo, null, m.Truncated)).ToArray();
            TimelineFrame frame = null!;
            RenderProfile.Time("measure.frame", () => frame = TimelineModel.Frame(s.Rounds, h.Targets, s.Gaps, marks, s.LinkDowns, s.Bands, from, to, timeline.Columns, h.IntervalMs,
                view.Follow && !view.IsAll ? $"최근 {view.Label}" : $"{label(from)}~{label(to)}", reps));
            RenderProfile.Time("measure.draw", () => timeline.Show(frame, label, view.Follow && !ended, $"{label(from)}부터 {label(to)}까지 ({view.Label})"));
            (drawnKey, drawnFrom, drawnTo, drawnFrame, drawnRounds) = (drawKey, from, to, frame, s.Rounds.Count);
        }
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
        // 1초마다 그리므로 내용이 바뀔 때만 다시 만든다 (bench: 매번 새로 만들면 프레임이 튄다)
        var specs = new List<(string Id, BannerKind Kind, string Text)>();
        if (s.StartError is { } err) specs.Add(("measure.banner.starterror", BannerKind.Error, "측정을 시작하지 못했습니다: " + err));
        if (s.Status is { } st)
        {
            if (st.StorageFailing)
                specs.Add(("measure.banner.storage", BannerKind.Error,
                    $"기록을 저장하지 못하고 있습니다. 측정은 계속하며 다시 시도합니다 (저장 대기 {st.Pending}회차)."));
            else if (st.LocationKind == "fallback")
                specs.Add(("measure.banner.fallback", BannerKind.Warn, "기본 위치에 쓸 수 없어 대체 위치에 기록하고 있습니다."));
            if (st.InGap) specs.Add(("measure.banner.gap", BannerKind.Info, "절전 중이었거나 측정이 멈춘 시간입니다. 이 시간은 관측하지 못한 공백으로 남깁니다."));
        }
        if (s.Analysis is { } a && s.Header is { } h)
        {
            if (a.StartedDisconnected)
                specs.Add(("measure.banner.disconnected", BannerKind.Warn, "어느 대상도 응답한 적이 없습니다. 끊긴 상태에서 측정을 시작했을 수 있습니다."));
            else if (a.NoBaselineTargets.Count > 0)
            {
                var names = h.Targets.Where(t => a.NoBaselineTargets.Contains(t.Id)).Select(t => t.Name);
                specs.Add(("measure.banner.nobaseline", BannerKind.Info,
                    $"아직 한 번도 응답하지 않은 대상: {string.Join(", ", names)}. 응답을 막는 장비일 수 있어 구간 판단에서 뺍니다."));
            }
            if (a.Grade.Reason == "undecidable")
                specs.Add(("measure.banner.routehold", BannerKind.Info, "경로가 예상과 달라진 순간이 있어 그때의 원인은 판단하지 않습니다."));
        }
        string key = string.Join(" ¦ ", specs.Select(x => $"{x.Id}|{x.Kind}|{x.Text}"));
        if (key == bannersKey) return;
        bannersKey = key;
        banners.Children.Clear();
        foreach (var (id, kind, text) in specs) banners.Children.Add(new Banner(Ui, id, kind, text));
        banners.Visibility = banners.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>카드 하나의 바뀌는 글. 대상 구성이 같으면 카드(유리 표면)는 그대로 두고 글만 바꾼다.</summary>
    private sealed record CardTexts(TextBlock Latest, TextBlock Loss, TextBlock Avg, TextBlock Streak, TextBlock Last);

    private void RenderCards(HostSnapshot s)
    {
        // 1초마다 그린다: 카드마다 유리 표면을 새로 만들면 프레임이 튀었다 (bench). 대상 구성(이름·역할)이 바뀔 때만 다시 만든다
        string key = string.Join(" ¦ ", s.Cards.Select(c => $"{c.TargetId}|{c.Name}|{c.Role}"));
        if (key != cardsKey)
        {
            cardsKey = key;
            BuildCards(s);
        }
        var now = Context.Now;
        foreach (var card in s.Cards)
        {
            if (!cardTexts.TryGetValue(card.TargetId, out var t)) continue;
            Set(t.Latest, card.Latest);
            Set(t.Loss, $"응답 없음 {card.LossRate} · {card.LossSentence}");
            Set(t.Avg, $"평균 지연 {card.Average}" + (card.Errors > 0 ? $" · 측정 오류 {card.Errors}번 (응답 없음 비율 계산에서 뺌)" : ""));
            Set(t.Streak, $"최대 연속 응답 없음 {card.MaxConsecutiveLoss}번");
            Set(t.Last, $"마지막 성공 {(card.LastSuccess is { } ls ? LiveSummary.Clock(ls, now) : "없음")} · 마지막 응답 없음 {(card.LastLoss is { } ll ? LiveSummary.Clock(ll, now) : "없음")}");
        }

        static void Set(TextBlock t, string text)
        {
            if (t.Text == text) return;
            t.Text = text;
            AutomationProperties.SetName(t, text);
        }
    }

    private void BuildCards(HostSnapshot s)
    {
        cards.Children.Clear();
        cards.ColumnDefinitions.Clear();
        cards.RowDefinitions.Clear();
        cardTexts.Clear();
        int perRow = 3;
        for (int c = 0; c < perRow; c++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (s.Cards.Count == 0) return;
        int rows = (s.Cards.Count + perRow - 1) / perRow;
        for (int r = 0; r < rows; r++) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
            var latest = Fonts.Text("", Typography.Headline, Ui.Brushes.Ink);
            AutomationProperties.SetAutomationId(latest, $"measure.card.{card.TargetId}.latest");
            stack.Children.Add(latest);
            TextBlock Line(string id)
            {
                var t = Fonts.Wrapped("", Typography.Caption, Ui.Brushes.Muted);
                AutomationProperties.SetAutomationId(t, $"measure.card.{card.TargetId}.{id}");
                stack.Children.Add(t);
                return t;
            }
            cardTexts[card.TargetId] = new CardTexts(latest, Line("loss"), Line("avg"), Line("streak"), Line("last"));
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
            if (marksKey == "") return;
            marksKey = "";
            marksBody.Set(ScreenParts.Message(Ui, "measure.marks.empty", Icons.Flag, Ui.Brushes.Muted, "표시한 증상이 없습니다",
                "끊김을 느낀 순간 [증상 표시]를 누르세요 (Ctrl+M). 누른 시각이 기록에 남습니다."), "empty");
            return;
        }
        var now = Context.Now;
        var rows = s.Marks.Select(m =>
        {
            string state = m.State switch { EventWriteState.Pending => " · 저장 다시 시도 중", EventWriteState.Lost => " · 기록하지 못함", _ => "" };
            string draft = s.SessionId is { } sid && Context.Host.Draft(sid, m.Mark) is { } d ? " · 저장하지 않은 메모 있음" : "";
            return (Mark: m, Text: $"{LiveSummary.Clock(m.Wall, now)} · {m.Memo ?? "메모 없음"}{state}{draft}");
        }).ToArray();
        // 1초마다 그리므로 목록이 바뀔 때만 다시 만든다
        string key = string.Join(" ¦ ", rows.Select(r => r.Mark.Mark + "|" + r.Text));
        if (key == marksKey) return;
        marksKey = key;
        marksList.Children.Clear();
        foreach (var (m, text) in rows)
        {
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
        if (s.Measuring || s.Finished is not { } f) { resultHost.Content = null; resultFor = null; return; }
        if (ReferenceEquals(f, resultFor)) return;   // 같은 결과면 유리 표면을 다시 만들지 않는다
        resultFor = f;
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
            var counts = Fonts.Text(a.Episodes.Count == 0 ? "끊김 관측 안 됨" : $"끊김 {a.Episodes.Count}번 · 최장 {TimelineModel.AtLeast(longest)} 이상",
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
        var more = new GlassButton(Ui, ButtonKind.Secondary, "measure.result.records", "기록에서 자세히 보기", Icons.Chevron) { HorizontalAlignment = HorizontalAlignment.Left };
        string finishedId = f.Result.Id;
        more.Click += (_, _) => Context.OpenRecord(Context.Records.KeyOf(finishedId));   // 목록에 아직 없으면 목록 (M7 4.2)
        stack.Children.Add(more);
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
            Context.Toasts.Show(ToastKind.Error, "증상을 기록하지 못했습니다", $"이 시각을 따로 적어 두세요: {LiveSummary.Clock(ticket.Wall, Context.Now)}", autoClose: false);
        RenderMemoState(Context.Host.Snapshot);
    }

    private void OpenMemo(string session, string markId, long atMs, DateTimeOffset wall, string? existing)
    {
        memo = (session, markId, atMs, wall);
        suppressDraft = true;
        memoBox.Text = Context.Host.Draft(session, markId) ?? existing ?? "";
        suppressDraft = false;
        memoTitle.Text = $"{LiveSummary.Clock(wall, Context.Now)}에 증상을 표시했습니다";
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
            EventWriteState.Lost => $"기록하지 못했습니다. 이 시각을 따로 적어 두세요: {LiveSummary.Clock(m.Wall, Context.Now)}",
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
        // 기다리는 동안 다른 표시를 열었거나 글을 고쳤으면 그 패널·글은 그대로 둔다 (G4 M5 1라운드 B1).
        // 고친 글은 호스트의 초안으로 남아 있다 (저장한 그 글일 때만 초안을 지운다)
        if (memo is not { } now || now.Session != m.Session || now.Mark != m.Mark || memoBox.Text != text) { Context.QueueDiag(); return; }
        if (result == EventWriteState.Lost)
        {
            memoBox.SetError("메모를 저장하지 못했습니다. 메모는 지우지 않았으니 다시 저장해 보세요.");
            Context.QueueDiag();
            return;
        }
        if (result == EventWriteState.Pending) Context.Toasts.Show(ToastKind.Info, "메모를 받았습니다", "저장할 수 있게 되면 기록에 씁니다.");
        // 끝난 측정의 메모(노트)는 최근 측정의 첫 메모를 바꿀 수 있다: 목록을 다시 읽는다 (캐시 열쇠에 노트 시각, G4 M5 1라운드 A7)
        if (!Context.Host.Snapshot.Measuring) _ = Context.Recent.RefreshAsync(Context.Host.RunningId);
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
            timelineState = timeline.Diag(),
            timelineCheck = new { receivedFinishedBeforeLayout, widthAtReceive, snapshotsAfterLayout },
        };
    }
}
