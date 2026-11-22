using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Analysis;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 홈 (M5 설계 4.5): 머리(날짜·칩), 큰 카드(등급 고리·한 문장·숫자 줄·측정 시작/중지·빠른 진단), 경로 맵, 최근 2분, 최근 측정.
/// 기준 이미지 배치. 빠른 진단 카드는 M6에서 더한다. 점수는 만들지 않는다.
/// </summary>
public sealed class HomeScreen : Screen
{
    private readonly RepresentativeCache reps = new();

    /// <summary>
    /// 숨어 있는 동안에도 대표값 캐시를 회차마다 이어 둔다 (M5 이월 2): 숨은 화면은 그리지 않아, 긴 측정 뒤 첫 홈 이동에서
    /// 4만여 회차를 한 번에 계산하느라 한 프레임이 예산(50 ms)을 넘었다. 이어 붙이기만이라 회차마다 비용은 작다.
    /// </summary>
    public void Warm(HostSnapshot s)
    {
        if (s.Header is { } h && s.Rounds.Count > 0) reps.Update(s.Rounds, h.Targets);
    }
    private readonly PageHeader header;
    private readonly StackPanel chips = new() { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
    private readonly GradeRing ring;
    private readonly TextBlock headline, numbers, status;
    private readonly GlassButton start, stop, firstRun, quick;
    private readonly StackPanel heroTexts;
    private readonly ContentControl heroBanner = new();
    private Banner? termsBanner;   // 동의 기록을 못 쓴 실행 (M10 A2): 이번 실행 내내 보인다
    private readonly PathMapPanel map;
    private readonly TimelinePanel timeline;
    private readonly CardBody chartBody, recentBody;
    private readonly GroupPanel heroCard, mapCard, chartCard, recentCard, diagCard;
    private readonly Grid side = new() { RowSpacing = Space.S5 };
    private readonly CardBody diagBody;
    private string diagKey = "";
    private readonly FrameworkElement chartEmpty;
    private readonly StackPanel recentList = new() { Spacing = Space.S1 };
    private string recentState = "";

    public HomeScreen(ScreenContext context) : base(context, "home")
    {
        RowSpacing = Space.S5;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        header = new PageHeader(Ui, "home.header", "", "네트워크 상태", chips);
        Children.Add(header);

        var grid = new Grid { ColumnSpacing = Space.S5, RowSpacing = Space.S5 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 넓은 창에서는 화면을 채우고(아래 줄이 남은 높이를 가짐), 좁으면(배율 150%) 세로로 스크롤한다.
        // 머리(칩)는 스크롤 밖이라 보통 유리 밑으로 내용이 지나가지 않는다
        var scroll = new ScrollViewer { Content = grid, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "home.scroll");
        scroll.SizeChanged += (_, e) =>
        {
            grid.MinHeight = e.NewSize.Height;
            Arrange(grid, e.NewSize.Width >= Size.HomeTwoColumns);
        };
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        // 큰 카드
        ring = new GradeRing(Ui, "home.grade");
        headline = Fonts.Wrapped("", Typography.Headline, Ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(headline, "home.sentence");
        numbers = Fonts.Wrapped("", Typography.Body, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(numbers, "home.numbers");
        status = Fonts.Text("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(status, "home.status");
        if (Context.Options.HideMeasuredText)
        {
            // 실측 대비 시험 (M5 설계 7.4): 측정 대상 글자만 투명하게 그려 그 뒤 배경을 찍는다 (공유 브러시는 바꾸지 않는다)
            headline.Foreground = numbers.Foreground = status.Foreground = Ui.Brushes.Transparent;
        }
        start = new GlassButton(Ui, ButtonKind.Primary, "home.start", "측정 시작", Icons.Play);
        stop = new GlassButton(Ui, ButtonKind.Primary, "home.stop", "측정 중지", Icons.Stop);
        firstRun = new GlassButton(Ui, ButtonKind.Primary, "home.firstrun", "첫 실행 안내 열기", Icons.Play);
        quick = new GlassButton(Ui, ButtonKind.Secondary, "home.quick", "빠른 진단", Icons.Diagnose);
        start.Click += async (_, _) =>
        {
            if (await Context.Host.StartAsync()) Context.Navigate("measure", chartCard);
        };
        stop.Click += async (_, _) => await Context.Host.StopAsync();
        firstRun.Click += (_, _) => Context.Navigate("firstrun", null);
        quick.Click += (_, _) => Context.Navigate("diagnose", null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        buttons.Children.Add(start);
        buttons.Children.Add(stop);
        buttons.Children.Add(firstRun);
        buttons.Children.Add(quick);
        heroTexts = new StackPanel { Spacing = Space.S3, VerticalAlignment = VerticalAlignment.Center };
        heroTexts.Children.Add(status);
        heroTexts.Children.Add(headline);
        heroTexts.Children.Add(numbers);
        heroTexts.Children.Add(heroBanner);
        heroTexts.Children.Add(buttons);
        var hero = new Grid { ColumnSpacing = Space.S7 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ring.VerticalAlignment = VerticalAlignment.Center;
        hero.Children.Add(ring);
        Grid.SetColumn(heroTexts, 1);
        hero.Children.Add(heroTexts);
        heroCard = new GroupPanel("home.hero", "지금 상태");
        heroCard.Children.Add(Ui.Glass.Surface(GlassLevel.Content, hero, Radius.Outer, new Thickness(Space.S7, Space.S6, Space.S7, Space.S6)));
        grid.Children.Add(heroCard);

        // 경로 맵
        map = new PathMapPanel(Ui, "home.map");
        (mapCard, var mapBody) = ScreenParts.Card(Ui, "home.mapcard", "연결 경로", PathMapView.ConceptNote);
        mapBody.Set(map);
        Grid.SetColumn(mapCard, 1);
        grid.Children.Add(mapCard);

        // 최근 2분
        timeline = new TimelinePanel(Ui, "home.timeline", compact: true);
        timeline.ColumnsChanged += () => { if (lastSnapshot is { } s) RenderTimeline(s, Context.Now); };   // M12 설계 A
        chartEmpty = ScreenParts.Message(Ui, "home.chart.empty", Icons.Chart, Ui.Brushes.Muted, "측정하지 않는 중", "측정을 시작하면 최근 2분이 여기 그려집니다.");
        (chartCard, chartBody) = ScreenParts.Card(Ui, "home.chart", "최근 2분", "실시간 · 1초 간격");
        Grid.SetRow(chartCard, 1);
        grid.Children.Add(chartCard);

        // 최근 측정
        var all = new GlassButton(Ui, ButtonKind.Ghost, "home.recent.all", "전체 보기");
        all.Click += (_, _) => Context.Navigate("records", null);
        (recentCard, recentBody) = ScreenParts.Card(Ui, "home.recent", "최근 측정", null, all);
        // 빠른 진단 카드 (M6 설계 4.3): 오른쪽 열에서 최근 측정 위 (한 열이면 최근 측정 위)
        var openDiag = new GlassButton(Ui, ButtonKind.Ghost, "home.diagnosis.open", "진단 열기");
        openDiag.Click += (_, _) => Context.Navigate("diagnose", null);
        (diagCard, diagBody) = ScreenParts.Card(Ui, "home.diagnosis", "빠른 진단", null, openDiag);
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.Children.Add(diagCard);
        Grid.SetRow(recentCard, 1);
        side.Children.Add(recentCard);
        Grid.SetRow(side, 1);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        // 최근 측정이 바뀌면 큰 카드(지난 측정)도 함께 다시 그린다
        // 보이는 화면만 그린다 (화면은 숨겨 두므로 부모 유무로는 알 수 없다). 닫는 중이면 요소를 건드리지 않는다
        Context.Recent.Changed += () => DispatcherQueue.TryEnqueue(() => { if (!Context.IsClosing() && Visibility == Visibility.Visible) Render(Context.Host.Snapshot); });
    }

    public override IReadOnlyList<UIElement> Parts => [header, heroCard, mapCard, chartCard, diagCard, recentCard];

    private bool? twoColumns;

    /// <summary>두 열(기준 이미지) 또는 한 열(좁은 창). 한 열이면 위에서부터 큰 카드, 경로 맵, 최근 2분, 최근 측정.</summary>
    private void Arrange(Grid grid, bool two)
    {
        if (twoColumns == two) return;
        twoColumns = two;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (two)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Place(heroCard, 0, 0); Place(mapCard, 0, 1); Place(chartCard, 1, 0); Place(side, 1, 1);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(heroCard, 0, 0); Place(mapCard, 1, 0); Place(chartCard, 2, 0); Place(side, 3, 0);
        }
    }

    private static void Place(FrameworkElement e, int row, int column)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
    }
    public override FrameworkElement? SharedTarget => null;
    public FrameworkElement ChartCard => chartCard;

    public override void Render(HostSnapshot s)
    {
        var now = Context.Now;
        header.SetOverline(LiveSummary.Date(now));
        RenderProfile.Time("home.chips", () => RenderChips(s));
        RenderProfile.Time("home.hero", () => RenderHero(s, now));
        RenderProfile.Time("home.map", () => map.Show(s.Map));
        lastSnapshot = s;
        RenderTimeline(s, now);
        RenderProfile.Time("home.recent", RenderRecent);
        RenderDiagnosis();
    }

    private HostSnapshot? lastSnapshot;

    private void RenderTimeline(HostSnapshot s, DateTimeOffset now)
    {
        if (s.Measuring && s.Header is { } h)
        {
            chartBody.Set(timeline, "normal");
            // 배치 전(폭 0)에는 장면을 만들지 않는다. 배치되면 ColumnsChanged로 다시 온다 (M12 설계 A)
            if (timeline.ActualWidth <= 0) return;
            long end = Math.Max(s.ElapsedMs, 1);
            long from = Math.Max(0, end - 120_000L);
            var frame = TimelineModel.Frame(s.Rounds, h.Targets, s.Gaps, s.Marks.Select(m => new SymptomMark(m.Mark, null, m.AtMs, m.Wall, m.Memo, null, m.Truncated)).ToArray(),
                s.LinkDowns, s.Bands, from, end, timeline.Columns, h.IntervalMs, "최근 2분", reps);
            RenderProfile.Time("home.timeline", () => timeline.Show(frame, ScreenParts.TimeLabel(h.StartedAt, now), followingNow: true, null));
        }
        else chartBody.Set(chartEmpty, "empty");
    }

    /// <summary>빠른 진단 카드: 이번 실행의 마지막 결과, 아니면 진단 기록의 최근 결과, 없으면 시작 안내.</summary>
    private void RenderDiagnosis()
    {
        var d = Context.Diagnosis.Snapshot;
        var now = Context.Now;
        // 날짜도 열쇠에 넣는다: 진단이 그대로여도 자정을 넘기면 "오늘"이 "어제"로 바뀌어야 한다 (M6 G4 4라운드 이월 4)
        string key = $"{d.Version}|{now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}";
        if (key == diagKey) return;
        diagKey = key;
        (int Conclusion, DateTimeOffset At)? latest = d.Last is { } l ? (l.Result.Main.Number, l.Result.EndedAt) : d.Recent is { } r ? (r.Conclusion, r.StartedAt) : null;
        if (d.Operation != DiagnosisOperation.None)
        {
            diagBody.Set(ScreenParts.Message(Ui, "home.diagnosis.running", Icons.Diagnose, Ui.Brushes.Accent, "진단 중", "진단 화면에서 진행을 볼 수 있습니다."), "running");
            return;
        }
        if (latest is not { } x)
        {
            var go = new GlassButton(Ui, ButtonKind.Secondary, "home.diagnosis.start", "진단 시작", Icons.Play);
            go.Click += (_, _) => { Context.Diagnosis.StartDiagnosis(); Context.Navigate("diagnose", null); };
            var empty = new StackPanel { Spacing = Space.S3 };
            empty.Children.Add(ScreenParts.Message(Ui, "home.diagnosis.empty", Icons.Diagnose, Ui.Brushes.Muted, "아직 진단하지 않았습니다", "빠른 진단으로 지금 연결을 검사해 보세요."));
            empty.Children.Add(go);
            diagBody.Set(empty, "empty");
            return;
        }
        var (head, weight) = DiagnosisTexts.Head(x.Conclusion);
        var (icon, brush) = DiagnosisLook.Weight(Ui, weight);
        diagBody.Set(ScreenParts.Message(Ui, "home.diagnosis.last", icon, brush, head, "지난 진단 · " + LiveSummary.When(x.At, now)), "normal");
    }

    private void RenderChips(HostSnapshot s)
    {
        chips.Children.Clear();
        if (s.ConnectionKind is not null) chips.Children.Add(ScreenParts.InfoChip(Ui, "home.chip.kind", "연결", PathMapModel.KindText(s.ConnectionKind)));
        if (s.Measuring && s.Latest is { } latest && s.Header is { } h)
        {
            var (router, internet) = TimelineModel.Representative(latest, h.Targets);
            if (h.Targets.Any(t => t.Role == NetPulse.Core.Targets.TargetRole.Local))
                chips.Children.Add(ScreenParts.InfoChip(Ui, "home.chip.local", PathMapModel.LocalName(h.Targets), router is double r ? PathMapModel.Ms(r) : "응답 없음"));
            chips.Children.Add(ScreenParts.InfoChip(Ui, "home.chip.internet", "인터넷", internet is double i ? PathMapModel.Ms(i) : "응답 없음"));
        }
    }

    private void RenderHero(HostSnapshot s, DateTimeOffset now)
    {
        bool measuring = s.State is HostState.Measuring or HostState.Stopping;
        start.Visibility = s.State is HostState.Idle or HostState.Starting ? Visibility.Visible : Visibility.Collapsed;
        start.IsEnabled = s.State == HostState.Idle;
        stop.Visibility = measuring ? Visibility.Visible : Visibility.Collapsed;
        stop.IsEnabled = s.State == HostState.Measuring;
        firstRun.Visibility = s.State == HostState.NoProfile ? Visibility.Visible : Visibility.Collapsed;
        heroBanner.Content = s.StartError is { } err ? new Banner(Ui, "home.starterror", BannerKind.Error, "측정을 시작하지 못했습니다: " + err)
            : Context.Resident.TermsNotSaved ? termsBanner ??= new Banner(Ui, "home.termsnotsaved", BannerKind.Warn, "사용 조건 동의를 저장하지 못했습니다. 이번 실행에만 적용하고, 다음 실행에 다시 묻습니다.")
            : null;

        if (s.State == HostState.NoProfile)
        {
            SetRing(Grade.Insufficient, "구성 없음");
            Set(status, "측정 구성 없음");
            Set(headline, "측정 구성을 확인해 주세요");
            Set(numbers, "첫 실행 안내에서 연결을 확인하면 두 번의 조작으로 첫 측정을 시작할 수 있습니다.");
            return;
        }
        if (s.State == HostState.Starting)
        {
            SetRing(Grade.Insufficient, "시작 중");
            Set(status, "측정 시작 중");
            Set(headline, "측정을 시작하는 중입니다");
            Set(numbers, "");
            return;
        }
        if (measuring)
        {
            var g = s.Analysis?.Grade;
            SetRing(g?.Grade ?? Grade.Insufficient, null);
            Set(status, "측정 중 · 잠정 · " + LiveSummary.Progress(s.ElapsedMs, s.PlannedMs));
            Set(headline, g is null || g.Grade == Grade.Insufficient && g.Reason == "too-few-rounds"
                ? "측정 중입니다. 1분 이상 측정해야 등급을 낼 수 있습니다." : g.Sentence);
            Set(numbers, s.Stats is { } st && s.Header is { } h ? LiveSummary.Numbers(s.ElapsedMs, s.Analysis, s.Rounds, h.Targets, st, reps) : "");
            return;
        }
        // 측정하지 않음: 이번 실행의 끝난 측정, 아니면 최근 측정의 첫 줄
        if (s.Finished is { Analysis: { } fa } f)
        {
            SetRing(fa.Grade.Grade, null);
            Set(status, "지난 측정 · " + LiveSummary.When(f.Record?.Header?.StartedAt ?? now, now));
            Set(headline, fa.Grade.Sentence);
            Set(numbers, f.Record?.Stats is { } st && f.Record.Header is { } fh
                ? LiveSummary.Numbers(f.Record.Summary?.EndMs ?? 0, fa, f.Record.Rounds, fh.Targets, st, reps) : "");
            return;
        }
        var last = Context.Recent.Items.FirstOrDefault(i => i.Grade is not null);
        if (last is not null)
        {
            SetRing(last.Grade!.Value, null);
            Set(status, "지난 측정 · " + LiveSummary.When(last.Entry.StartedAt ?? now, now));
            Set(headline, last.Sentence ?? "");
            Set(numbers, last.Outages is int o ? $"끊김 {o}번" + (last.DurationMs is long d ? $" · {TimelineModel.Seconds(d)} 측정" : "") : "");
            return;
        }
        SetRing(Grade.Insufficient, "기록 없음");
        Set(status, "측정하지 않음");
        Set(headline, "아직 측정한 기록이 없습니다");
        Set(numbers, "측정을 시작하면 연결 상태를 등급과 한 문장으로 알려 드립니다.");
    }

    private void SetRing(Grade g, string? nameOverride)
    {
        var b = LiveSummary.Badge(g);
        string name = nameOverride ?? b.Name;
        ring.Set(b.Letter, name, b.Fill, b.Dashed, ScreenParts.GradeBrush(Ui, nameOverride is null ? g : null), "등급 " + name);
    }

    private static void Set(TextBlock t, string text)
    {
        t.Text = Fonts.KeepAll(text);
        AutomationProperties.SetName(t, text);
        t.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderRecent()
    {
        var entries = Context.Recent.Entries;   // 열쇠와 요약을 같은 순간에 읽는다
        var items = entries.Select(e => e.Summary).ToArray();
        string state = !Context.Recent.Listed ? "loading" : items.Length == 0 ? "empty" : items.Any(i => i.Error is not null) ? "error" : items.Any(i => i.Analyzing) ? "loading" : "normal";
        if (!Context.Recent.Listed)
        {
            recentBody.Set(GlassCard.Skeleton(Ui), "loading");
            recentState = state;
            return;
        }
        if (items.Length == 0)
        {
            recentBody.Set(ScreenParts.Message(Ui, "home.recent.empty", Icons.Info, Ui.Brushes.Muted, "아직 측정 기록이 없습니다", "측정을 마치면 여기에 결과가 쌓입니다."), "empty");
            recentState = state;
            return;
        }
        recentList.Children.Clear();
        var now = Context.Now;
        for (int i = 0; i < items.Length; i++) recentList.Children.Add(Row(items[i], i, now, entries[i].Key));
        recentBody.Set(recentList, state);
        AutomationProperties.SetItemStatus(recentBody, state);
        recentState = state;
    }

    private UIElement Row(RecentItem item, int index, DateTimeOffset now, string key)
    {
        var grid = new Grid { ColumnSpacing = Space.S4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string letter = item.Badge?.Letter ?? (item.Analyzing ? "…" : item.Entry.Lifecycle == RecordLifecycle.Running ? "●" : "!");
        grid.Children.Add(Badges.Grade(Ui, letter, ScreenParts.GradeBrush(Ui, item.Grade)));
        var texts = new StackPanel { Spacing = Space.S0, VerticalAlignment = VerticalAlignment.Center };
        string title = item.Entry.StartedAt is { } st ? LiveSummary.When(st, now) : item.Entry.Id;
        texts.Children.Add(Fonts.Text(title, Typography.Label, Ui.Brushes.Ink));
        var sub = new List<string>();
        if (item.DurationMs is long d) sub.Add(TimelineModel.Seconds(d));
        if (item.ConnectionKind is { } k) sub.Add(PathMapModel.KindText(k));
        if (item.FirstMemo is { } memo) sub.Add(memo);
        if (item.Notice is { } notice) sub.Add(notice);
        if (item.Error is { } error) sub.Add(error);
        if (item.Entry.Lifecycle == RecordLifecycle.Running) sub.Add("측정 중");
        var subtitle = Fonts.Text(string.Join(" · ", sub), Typography.Caption, Ui.Brushes.Muted);
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(subtitle, string.Join(" · ", sub));
        texts.Children.Add(subtitle);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        string count = item.Analyzing ? "분석 중" : item.Outages is int o ? $"{o}번" : "";
        string longest = item.LongestMinMs is long l ? $"최장 {TimelineModel.AtLeast(l)} 이상" : item.Outages == 0 ? "끊김" : "";
        var c = Fonts.Text(count, Typography.Label, Ui.Brushes.Ink);
        c.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(c);
        var lt = Fonts.Text(longest, Typography.Caption, Ui.Brushes.Muted);
        lt.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(lt);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        string name = $"{title}, 등급 {item.Badge?.Name ?? (item.Analyzing ? "분석 중" : "없음")}, 끊김 {item.Outages?.ToString() ?? "모름"}번" + (sub.Count > 0 ? ", " + string.Join(", ", sub) : "");
        var row = new RowButton(Ui, $"home.recent.{index}", name, grid);
        row.Click += (_, _) => Context.OpenRecord(key);   // 그 기록 상세 (측정 중이면 기록 화면이 측정 화면으로 보낸다, M7 4.2)
        AutomationProperties.SetItemStatus(row, item.Analyzing ? "analyzing" : item.Error is not null ? "error" : "ready");
        return row;
    }

    public override object? Diag() => new { recent = recentState, recentCount = Context.Recent.Items.Count, mapCandidate = map.Candidate.ToString(), timelineState = timeline.Diag() };
}
