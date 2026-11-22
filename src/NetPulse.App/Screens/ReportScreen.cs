using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Report;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 리포트 (M8 설계 4): 측정 고르기 · 가리기 · 메모·진단 넣기 · 끊김 상세 수 → 미리 보기 → 내보내기.
/// 화면은 게시된 불변 문서 하나를 그리고, 내보내기는 그 문서 그대로 (<see cref="ReportRequests"/>, 설계 3.6).
/// 앱 기록에는 내보냄 여부·크기·가리기 여부만 남긴다 (경로 없음).
/// </summary>
public sealed class ReportScreen : Screen
{
    private readonly PageHeader header;
    private readonly GlassButton exportButton, exportingButton, prevButton, nextButton;
    private readonly SegmentedChoice maskChoice, detailsChoice;
    private readonly SettingToggle memosToggle, diagnosesToggle;
    private readonly TextBlock pickedText, statusText;
    private readonly GroupPanel optionsCard, previewCard;
    private readonly StackPanel statusArea = new() { Spacing = Space.S3 };
    /// <summary>내보냄·내보내기 실패·만들기 실패 안내: 옵션 카드 위, 머리의 [내보내기] 단추 바로 아래 (150%에서 첫 화면 아래로 밀렸다, M11 C3).</summary>
    private readonly StackPanel noticeArea = new() { Spacing = Space.S3 };
    private StackPanel paperStack = new() { Spacing = Space.S6 };
    private readonly Grid paperHost = new();
    private readonly StackPanel messageHost = new() { Spacing = Space.S5 };
    private readonly Border paper;
    private readonly StackPanel content = new() { Spacing = Space.S5 };
    private readonly StackPanel body = new() { Spacing = Space.S5 };
    private readonly ScrollViewer scroll;
    private readonly ReportRequests requests = new();
    private readonly DocumentBrushes documentBrushes;
    private readonly string version = AppInfo.Current.Version;
    private string? key;
    private ReportOptions options = new();
    private bool exporting, syncing;
    private volatile bool exportHolding;   // 시험: 임시 파일에 절반을 쓰고 멈춘 곳에 닿음 (G4 M8 3라운드 b8)
    private ExportOutcome? lastExport;
    private IReadOnlyList<string> leakKinds = [];
    private string? lastAction;
    private ReportDocument? drawn;
    private bool drawnHighContrast;
    private int drawGen, drawnSections;
    private bool drawComplete;

    /// <summary>마지막 내보내기 (앱 기록·진단 값에는 경로 없이).</summary>
    private sealed record ExportOutcome(bool Ok, long Bytes, bool Masked, string? Problem, string? FileName, string? Folder);

    public ReportScreen(ScreenContext context) : base(context, "report")
    {
        documentBrushes = new DocumentBrushes(ReportPalette.Document);
        RowSpacing = Space.S4;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        exportButton = new GlassButton(Ui, ButtonKind.Primary, "report.export", "내보내기", Icons.Check);
        exportButton.Click += async (_, _) => await ExportAsync();
        exportingButton = new GlassButton(Ui, ButtonKind.Primary, "report.exporting", "내보내는 중…", Icons.Refresh) { IsEnabled = false };
        header = new PageHeader(Ui, "report.header", null, "리포트", exportButton);
        Children.Add(header);

        // 옵션
        prevButton = new GlassButton(Ui, ButtonKind.Icon, "report.older", "이전 측정", Icons.Back);
        nextButton = new GlassButton(Ui, ButtonKind.Icon, "report.newer", "다음 측정", Icons.Chevron);
        prevButton.Click += (_, _) => Step(+1);
        nextButton.Click += (_, _) => Step(-1);
        pickedText = Fonts.Wrapped("", Typography.Body, Ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(pickedText, "report.picked");
        var pickRow = new Grid { ColumnSpacing = Space.S3 };
        pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickRow.Children.Add(prevButton);
        pickedText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(pickedText, 1);
        pickRow.Children.Add(pickedText);
        Grid.SetColumn(nextButton, 2);
        pickRow.Children.Add(nextButton);

        maskChoice = new SegmentedChoice(Ui, "report.mask", "가리기", [("masked", "통신사에 보낼 때 (가리기)"), ("unmasked", "가리지 않음 (내가 볼 때)")], "masked");
        // 가리기를 켜면 메모 넣기를 끈다 (가린 리포트에는 메모를 넣지 않음, 메모 넣기 스위치도 막는다)
        maskChoice.Changed += v => SetOptions(v == "masked" ? options with { Masked = true, IncludeMemos = false } : options with { Masked = false });
        detailsChoice = new SegmentedChoice(Ui, "report.details", "끊김 상세", [("0", "없음"), ("3", "3개"), ("5", "5개"), ("10", "10개")], "5");
        detailsChoice.Changed += v => SetOptions(options with { EpisodeDetails = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) });
        memosToggle = new SettingToggle(Ui, "report.memos", "증상 메모 넣기 (가리지 않은 리포트에서만, 적은 그대로 들어갑니다)", false) { IsEnabled = false };
        // UI 자동화의 바꾸기(Toggle)는 Click을 내지 않는다: 상태 변화로 받는다
        memosToggle.Checked += (_, _) => SetOptions(options with { IncludeMemos = true });
        memosToggle.Unchecked += (_, _) => SetOptions(options with { IncludeMemos = false });
        diagnosesToggle = new SettingToggle(Ui, "report.diagnoses", "측정 중 진단 넣기", true);
        diagnosesToggle.Checked += (_, _) => SetOptions(options with { IncludeDiagnoses = true });
        diagnosesToggle.Unchecked += (_, _) => SetOptions(options with { IncludeDiagnoses = false });

        var optionStack = new StackPanel { Spacing = Space.S4 };
        optionStack.Children.Add(pickRow);
        optionStack.Children.Add(Labeled("가리기", maskChoice));
        optionStack.Children.Add(memosToggle);
        optionStack.Children.Add(diagnosesToggle);
        optionStack.Children.Add(Labeled("끊김 상세", detailsChoice));
        statusText = Fonts.Wrapped("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(statusText, "report.status");
        AutomationProperties.SetLiveSetting(statusText, AutomationLiveSetting.Polite);
        optionStack.Children.Add(statusText);
        optionsCard = new GroupPanel("report.options", "리포트 옵션");
        optionsCard.Children.Add(Ui.Glass.Surface(GlassLevel.Content, optionStack, Radius.Outer, new Thickness(Space.S6)));

        // 미리 보기: 종이 면
        paperHost.Children.Add(paperStack);
        paper = new Border { Padding = new Thickness(Space.S7), CornerRadius = new CornerRadius(Radius.Inner), BorderThickness = new Thickness(Size.Hairline), Child = paperHost };
        previewCard = new GroupPanel("report.preview", "미리 보기");
        var previewStack = new StackPanel { Spacing = Space.S3 };
        previewStack.Children.Add(Fonts.Text("미리 보기 (내보낼 파일과 같은 내용)", Typography.Label, Ui.Brushes.Ink));
        var paperGroup = new GroupPanel("report.paper", "리포트 미리 보기");   // 글 순서 대조(G3)가 이 아래만 읽는다
        paperGroup.Children.Add(paper);
        previewStack.Children.Add(paperGroup);
        previewCard.Children.Add(previewStack);

        content.Children.Add(noticeArea);
        content.Children.Add(optionsCard);
        content.Children.Add(statusArea);
        content.Children.Add(body);
        // 미리 보기 카드는 몸통에 늘 둔다 (보이기만 바꿈): 상태가 바뀔 때마다 수천 요소의 종이 면을 빼고 다시 넣으면 한 프레임이 100 ms를 넘었다 (M8 bench)
        body.Children.Add(messageHost);
        body.Children.Add(previewCard);
        previewCard.Visibility = Visibility.Collapsed;
        scroll = new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "report.scroll");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        // 숨은 화면은 다시 그리지 않는다 (들어올 때 Entered가 그린다, G4 M8 1라운드 b5)
        requests.Changed += () => DispatcherQueue.TryEnqueue(() => { if (!Context.IsClosing() && Visibility == Visibility.Visible) Rerender(); });
        // 들어온 적이 없으면 목록 변화에 반응하지 않는다: 시작 직후 미리 만든 화면이 기록을 읽어 문서를 만들지 않게 (들어올 때 Entered가 한다)
        Context.Records.Changed += () => DispatcherQueue.TryEnqueue(() => { if (!Context.IsClosing() && entered && Visibility == Visibility.Visible) OnLibraryChanged(); });
        Ui.Effects.Changed += (_, _) => { if (Visibility == Visibility.Visible) Rerender(); };
    }

    private FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Spacing = Space.S1 };
        stack.Children.Add(Fonts.Text(label, Typography.Caption, Ui.Brushes.Muted));
        stack.Children.Add(control);
        return stack;
    }

    public override IReadOnlyList<UIElement> Parts => [header, scroll];

    public override void Render(HostSnapshot snapshot) { }

    private bool entered;

    /// <summary>다시 열기: 숨김으로 멈춘 종이 면 그리기를 이어 간다 (M9 설계 A1).</summary>
    protected override void OnResume()
    {
        if (Visibility == Visibility.Visible && pausedDraw is { } resume)
        {
            pausedDraw = null;
            drawing = resume;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += resume;
        }
    }

    /// <summary>
    /// 숨김: 프레임 구독을 직접 끊는다 (G4 M9 1라운드 b2). 숨은 창에서는 렌더링 콜백이 오지 않을 수 있어, 콜백 안에서 스스로 끊기를 기다리면
    /// 구독이 남는다. 그리던 종이 면은 다시 열 때 이어 그리고, 떼던 지난 종이 면은 보이지 않으니 한 번에 뗀다.
    /// </summary>
    protected override void OnSuspend()
    {
        if (drawing is { } d)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= d;
            pausedDraw = d;
            drawing = null;
        }
        if (clearing is { } c)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= c;
            clearing = null;
            if (clearingOld is { } old) paperHost.Children.Remove(old);
            clearingOld = null;
        }
    }

    public override int FrameHooks => (drawing is null ? 0 : 1) + (clearing is null ? 0 : 1);

    private EventHandler<object>? drawing, clearing;
    private Panel? clearingOld;

    public override void Entered()
    {
        entered = true;
        if (pausedDraw is { } resume)
        {
            pausedDraw = null;
            drawing = resume;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += resume;
        }
        _ = Context.Records.RefreshAsync(Context.Host.RunningId);
        OnLibraryChanged();
        Rerender();
    }

    /// <summary>기록 상세의 "리포트 만들기" (그 측정이 골라진 채로).</summary>
    public void Show(string recordKey)
    {
        keyId = Context.Records.Items.FirstOrDefault(i => i.Key == recordKey)?.Entry.Id ?? keyId;
        if (key != recordKey || requests.Latest?.Key != recordKey)
        {
            key = recordKey;
            Request();
        }
        Rerender();
    }

    /// <summary>
    /// 들어가기 전에 고를 측정을 정한다 (기록 상세의 "리포트 만들기"): 들어가며 기본 측정(가장 최근)으로 문서를 만들기 시작했다가
    /// 바로 버리는 일이 없게 (M8 bench, GPT 교차 검토).
    /// </summary>
    public void Preselect(string recordKey)
    {
        key = recordKey;
        keyId = Context.Records.Items.FirstOrDefault(i => i.Key == recordKey)?.Entry.Id ?? keyId;
    }

    /// <summary>고른 기록의 측정 Id: 옮기기로 출처 열쇠가 바뀌어도 같은 측정을 계속 고른다 (G4 M9 1라운드 b3).</summary>
    private string? keyId;

    // ---------------- 고르기 ----------------

    /// <summary>끝난 측정 (최신순). 측정 중인 기록은 만들지 않는다 (설계 3.1).</summary>
    private IReadOnlyList<LibraryItem> Candidates() => Context.Records.Items.Where(i => i.Entry.Lifecycle != RecordLifecycle.Running).ToArray();

    private void OnLibraryChanged()
    {
        if (!Context.Records.Listed) return;
        var list = Candidates();
        if (key is not null && keyId is not null && list.All(i => i.Key != key) && list.FirstOrDefault(i => !i.External && i.Entry.Id == keyId) is { } moved)
        {
            key = moved.Key;   // 같은 측정, 새 위치: 다시 만든다 (경로가 바뀌어 도장도 바뀐다)
            Request();
        }
        if (key is null || list.All(i => i.Key != key))
        {
            // 기본: 가장 최근에 끝난 측정
            string? next = list.FirstOrDefault()?.Key;
            if (next != key)
            {
                key = next;
                if (key is not null) Request();
            }
        }
        else if (list.First(i => i.Key == key).Stamp is var stamp && requests.Latest is { } latest && latest.Key == key && latest.Stamp != stamp)
        {
            // 도장이 바뀜 (메모 저장 등): 게시 문서를 오래됨으로 두고 다시 만든다 (설계 3.6)
            requests.StampChanged(key, stamp);
            Request();
        }
        keyId = list.FirstOrDefault(i => i.Key == key)?.Entry.Id ?? keyId;
        if (Visibility == Visibility.Visible) Rerender();
    }

    private void Step(int by)
    {
        var list = Candidates();
        int i = list.ToList().FindIndex(x => x.Key == key);
        int j = Math.Clamp(i + by, 0, list.Count - 1);
        if (list.Count == 0 || j == i) return;
        key = list[j].Key;
        keyId = list[j].Entry.Id;
        Request();
        Rerender();
    }

    private void SetOptions(ReportOptions next)
    {
        if (syncing || next == options) return;
        options = next;
        Request();
        Rerender();
    }

    // ---------------- 만들기 ----------------

    private bool IsLatest(ReportRequest r) => requests.Latest?.Number == r.Number;

    private void Request()
    {
        if (key is null) return;
        var item = Context.Records.Items.FirstOrDefault(i => i.Key == key);
        if (item is null) return;
        leakKinds = [];
        lastExport = null;
        var req = requests.Begin(key, item.Stamp, options);
        // 문서 만들기는 한 번에 하나 (설계 3.6 "백그라운드 하나"): 도는 동안 온 요청은 마지막 것만 기다린다 (G4 M8 1라운드 b6)
        pendingBuild = req;
        if (!buildRunning) _ = BuildLoopAsync();
    }

    private ReportRequest? pendingBuild;
    private bool buildRunning;

    private async Task BuildLoopAsync()
    {
        buildRunning = true;
        try
        {
            while (pendingBuild is { } r)
            {
                pendingBuild = null;
                await BuildAsync(r);
            }
        }
        finally { buildRunning = false; }
    }

    private async Task BuildAsync(ReportRequest req)
    {
        try
        {
            if (!IsLatest(req)) return;   // 이미 밀려난 요청은 기록을 열지 않는다
            var opened = await Context.Records.OpenAsync(req.Key);
            if (!IsLatest(req)) return;
            if (opened is null) { requests.Fail(req, ReportError.Unreadable); return; }
            var store = Context.Diagnosis.Store;
            var now = Context.Now;
            var doc = await Task.Run(() =>
            {
                if (!IsLatest(req)) return null;
                IReadOnlyList<NetPulse.Core.Diagnostics.StoredDiagnosis> diags = req.Options.IncludeDiagnoses ? store.List().Items.Select(x => x.Stored).ToArray() : [];
                return ReportBuilder.Build(new ReportInput(req.Key, req.Stamp, opened.Record, opened.Analysis, opened.Lifecycle, diags, req.Options, now, version));
            });
            if (doc is not null) requests.Complete(req, doc);
        }
        catch (ReportException ex) { requests.Fail(req, ex.Error); }
        catch (Exception ex)
        {
            Context.Log.Write("reportBuildFailed", new { type = ex.GetType().Name });
            requests.Fail(req, "build");
        }
    }

    // ---------------- 그리기 ----------------

    private void Rerender()
    {
        // 옵션 카드는 빼지 않는다: 빼고 다시 넣으면 그 안의 초점(가리기 단추 등)이 탐색 막대로 튀었다 (M8 G5 캡처)
        messageHost.Children.Clear();
        statusArea.Children.Clear();
        noticeArea.Children.Clear();
        var list = Candidates();
        bool listed = Context.Records.Listed;
        if (listed && list.Count == 0)
        {
            var (card, body) = ScreenParts.Card(Ui, "report.empty", "끝난 측정이 없습니다");
            var stack = new StackPanel { Spacing = Space.S3 };
            stack.Children.Add(Fonts.Wrapped("측정을 한 번 마치면 그 기록으로 통신사에 보낼 리포트를 만들 수 있습니다.", Typography.Body, Ui.Brushes.Muted));
            var go = new GlassButton(Ui, ButtonKind.Secondary, "report.empty.measure", "측정 화면으로", Icons.Pulse) { HorizontalAlignment = HorizontalAlignment.Left };
            go.Click += (_, _) => Context.Navigate("measure", null);
            stack.Children.Add(go);
            body.Set(stack, "empty");
            optionsCard.Visibility = Visibility.Collapsed;
            previewCard.Visibility = Visibility.Collapsed;
            messageHost.Children.Add(card);
            SetExport();
            Context.QueueDiag();
            return;
        }

        int index = list.ToList().FindIndex(i => i.Key == key);
        var item = index >= 0 ? list[index] : null;
        pickedText.Text = item is null ? "기록 목록을 읽는 중…" : Picked(item);
        AutomationProperties.SetName(pickedText, pickedText.Text);
        prevButton.IsEnabled = index >= 0 && index < list.Count - 1;
        nextButton.IsEnabled = index > 0;
        SyncOptions();
        optionsCard.Visibility = Visibility.Visible;

        var state = requests.State;
        statusText.Text = state switch
        {
            ReportState.Building => "만드는 중… 끝나면 내보낼 수 있습니다.",
            ReportState.Stale => "기록이 바뀌어 다시 만드는 중입니다.",
            ReportState.Failed => "만들지 못했습니다.",
            ReportState.Ready when !options.Masked => "가리지 않은 리포트입니다. 대상 이름·주소, 사업자 이름, 네트워크 어댑터 이름, 측정 ID가 그대로 들어갑니다.",
            ReportState.Ready => "미리 보기는 내보낼 파일과 같은 내용입니다.",
            _ => "",
        };
        AutomationProperties.SetName(statusText, statusText.Text);

        if (!options.Masked) statusArea.Children.Add(new Banner(Ui, "report.unmasked", BannerKind.Warn, ReportTexts.UnmaskedNote));
        if (lastExport is { } done) noticeArea.Children.Add(ExportBanner(done));

        if (state == ReportState.Failed)
        {
            noticeArea.Children.Add(ErrorCard("report.failed", "리포트를 만들지 못했습니다", FailureText(requests.Failure), records: true));
            previewCard.Visibility = Visibility.Collapsed;
            ClearPaper();
        }
        else if (leakKinds.Count > 0)
        {
            noticeArea.Children.Add(ErrorCard("report.redaction", "식별 정보가 남아 있어 내보내지 않았습니다",
                $"가린 리포트에서 원본 값이 발견됐습니다 ({string.Join(", ", leakKinds)}). 다시 만들어 보세요.", records: false));
            previewCard.Visibility = Visibility.Collapsed;
            ClearPaper();
        }
        else if (requests.Published is { } doc)
        {
            previewCard.Visibility = Visibility.Visible;
            Draw(doc);
        }
        else
        {
            var (card, body) = ScreenParts.Card(Ui, "report.building", "리포트를 만드는 중");
            body.Set(GlassCard.Skeleton(Ui), "loading");
            messageHost.Children.Add(card);
            previewCard.Visibility = Visibility.Collapsed;
            ClearPaper();
        }
        SetExport();
        Context.QueueDiag();
    }

    private static string Picked(LibraryItem i)
    {
        var when = i.Entry.StartedAt is { } st ? st.ToLocalTime().ToString("M월 d일 tt h:mm", ScreenParts.Ko) : "시작 시각 없음";
        var grade = i.Summary.Grade is { } g ? " · 등급 " + NetPulse.Core.Analysis.Sentences.GradeName(g) : "";
        var life = i.Entry.Lifecycle == RecordLifecycle.NoSummary ? " · 정상 종료 기록 없음" : "";
        return $"측정: {when} · {RecordView.TargetsSummary(i.Entry.Targets)}{grade}{life}";
    }

    private void SyncOptions()
    {
        syncing = true;
        try
        {
            maskChoice.Set(options.Masked ? "masked" : "unmasked");
            detailsChoice.Set(options.EpisodeDetails.ToString(System.Globalization.CultureInfo.InvariantCulture));
            memosToggle.IsChecked = options.IncludeMemos;
            memosToggle.IsEnabled = !options.Masked;
            diagnosesToggle.IsChecked = options.IncludeDiagnoses;
        }
        finally { syncing = false; }
    }

    private static string FailureText(object? failure) => failure switch
    {
        ReportError.Running => "측정 중인 기록으로는 리포트를 만들지 않습니다. 측정이 끝난 뒤에 만드세요.",
        ReportError.Unsupported => "지원하지 않는 형식의 기록입니다.",
        ReportError.Unreadable => "기록을 읽지 못했습니다. 기록 화면에서 상태를 확인하세요.",
        _ => "리포트를 만드는 중에 문제가 생겼습니다. 다시 만들어 보세요.",
    };

    private GroupPanel ErrorCard(string id, string title, string text, bool records)
    {
        var (card, body) = ScreenParts.Card(Ui, id, title);
        var stack = new StackPanel { Spacing = Space.S3 };
        stack.Children.Add(Fonts.Wrapped(text, Typography.Body, Ui.Brushes.Ink));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
        var retry = new GlassButton(Ui, ButtonKind.Secondary, id + ".retry", "다시 만들기", Icons.Refresh);
        retry.Click += (_, _) => { Request(); Rerender(); };
        row.Children.Add(retry);
        if (records)
        {
            var go = new GlassButton(Ui, ButtonKind.Ghost, id + ".records", "기록 화면으로", Icons.Menu);
            go.Click += (_, _) => Context.OpenRecord(null);
            row.Children.Add(go);
        }
        stack.Children.Add(row);
        body.Set(stack, "error");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (retry.IsLoaded) retry.Focus(FocusState.Programmatic); });
        return card;
    }

    private FrameworkElement ExportBanner(ExportOutcome done)
    {
        if (!done.Ok) return new Banner(Ui, "report.export.failed", BannerKind.Error, "내보내지 못했습니다. " + (done.Problem ?? "") + " 미리 보기는 그대로입니다.");
        var group = new GroupPanel("report.export.done", "내보냄");
        var row = new Grid { ColumnSpacing = Space.S3 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Fonts.Wrapped($"내보냈습니다: {done.FileName} ({Kb(done.Bytes)}, {(done.Masked ? "가린 리포트" : "가리지 않은 리포트")})", Typography.Body, Ui.Brushes.Ink);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        var folder = new GlassButton(Ui, ButtonKind.Ghost, "report.export.folder", "폴더에서 보기", Icons.Layers);
        folder.Click += (_, _) =>
        {
            if (done.Folder is not { } f) return;
            var r = Context.Diagnosis.World.Actions.OpenFolder(f, [f]);
            lastAction = "folder" + (r.Ok ? "" : ":" + r.Code);
            Context.QueueDiag();
        };
        Grid.SetColumn(folder, 1);
        row.Children.Add(folder);
        group.Children.Add(Ui.Glass.Surface(GlassLevel.Content, row, Radius.Inner, new Thickness(Space.S4, Space.S3, Space.S4, Space.S3)));
        return group;
    }

    private static string Kb(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{Math.Round(bytes / 1024.0):0} KB";

    private void ClearPaper()
    {
        drawn = null;
        drawGen++;
        pausedDraw = null;
        drawnSections = 0;
        drawComplete = false;
        if (paperStack.Children.Count == 0) return;
        // 지난 종이 면은 숨기고 프레임마다 조금씩 뗀다: 수천 요소를 한 번에 떼면 한 프레임이 길어졌다 (M8 bench)
        var old = paperStack;
        old.Visibility = Visibility.Collapsed;
        paperStack = new StackPanel { Spacing = Space.S6 };
        paperHost.Children.Add(paperStack);
        void OnFrame(object? sender, object e)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (old.Children.Count > 0 && watch.Elapsed < FrameBudget.PieceSlice) RemoveLastPiece(old);
            if (old.Children.Count > 0 && !Context.IsClosing()) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
            if (ReferenceEquals(clearing, (EventHandler<object>)OnFrame)) { clearing = null; clearingOld = null; }
            paperHost.Children.Remove(old);
        }
        if (clearing is { } previous)
        {
            // 앞의 떼기가 아직이면 그것은 한 번에 끝낸다 (구독은 하나만)
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= previous;
            if (clearingOld is { } prevOld) paperHost.Children.Remove(prevOld);
        }
        clearing = OnFrame;
        clearingOld = old;
        if (Suspended) { OnSuspend(); return; }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>
    /// 문서를 종이 면에 그린다. 같은 문서·같은 색이면 다시 만들지 않는다. 절마다 한 차례씩 (큰 문서를 한 프레임에 만들지 않게).
    /// </summary>
    private EventHandler<object>? pausedDraw;

    private void Draw(ReportDocument doc)
    {
        bool hc = Ui.Effects.Mode.HighContrast;
        if (ReferenceEquals(doc, drawn) && hc == drawnHighContrast) return;
        ClearPaper();
        drawn = doc;
        drawnHighContrast = hc;
        var look = hc ? ReportLook.System(Ui.Brushes) : ReportLook.Document(documentBrushes);
        if (Context.Options.HideMeasuredText) look = look with { Ink = Ui.Brushes.Transparent };   // G5 실측 대비: 글자 없는 바탕 캡처
        paper.Background = look.Paper;
        paper.BorderBrush = look.Edge;
        paperStack.Children.Add(ReportParts.Head(doc, look));
        // 조각(절 머리·블록·표 줄 묶음)을 프레임마다 몇 ms 안에서만 붙인다: 절 하나를 통째로 만들면 최악 사례에서 한 프레임이 170 ms였다 (M8 bench)
        int gen = drawGen;
        var pieces = doc.Sections.Select((s, k) => (s, k))
            .SelectMany(x => ReportParts.Pieces(x.s, look, paperStack).Select((a, n) => (Action)(() => { a(); if (n == 0) drawnSections = x.k + 1; })))
            .GetEnumerator();
        void OnFrame(object? sender, object e)
        {
            if (gen != drawGen || Context.IsClosing())
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
                if (ReferenceEquals(pausedDraw, (EventHandler<object>)OnFrame)) pausedDraw = null;
                if (ReferenceEquals(drawing, (EventHandler<object>)OnFrame)) drawing = null;
                return;
            }
            if (Visibility != Visibility.Visible || Suspended)
            {
                // 화면을 떠남: 숨은 종이 면을 계속 만들지 않는다. 들어오면 이어서 (G4 M8 1라운드 b5)
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
                if (ReferenceEquals(drawing, (EventHandler<object>)OnFrame)) drawing = null;
                pausedDraw = OnFrame;
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (!pieces.MoveNext())
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
                    if (ReferenceEquals(drawing, (EventHandler<object>)OnFrame)) drawing = null;
                    drawComplete = true;
                    Context.QueueDiag();
                    return;
                }
                pieces.Current();
            }
            while (watch.Elapsed < FrameBudget.PieceSlice);
        }
        if (drawing is { } previousDraw) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= previousDraw;   // 옛 세대 (다음 프레임에 스스로 끊기 전에)
        if (Suspended || Visibility != Visibility.Visible) { drawing = null; pausedDraw = OnFrame; return; }
        drawing = OnFrame;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>
    /// 지난 종이 면에서 맨 끝의 작은 조각 하나를 뗀다 (붙일 때와 같은 단위: 절 머리·블록·표 줄 묶음). 절 하나를 통째로 떼면 한 번에 길어졌다.
    /// </summary>
    private static void RemoveLastPiece(Panel root)
    {
        Panel p = root;
        for (int depth = 0; depth < 8; depth++)
        {
            var last = p.Children[^1];
            Panel? inner = last switch
            {
                Grid g when g.RowDefinitions.Count > 0 => null,   // 표 줄 묶음은 한 조각
                Panel lp when lp.Children.Count > 0 => lp,
                Border { Child: Panel bp } when bp.Children.Count > 0 => bp,
                _ => null,
            };
            if (inner is null) break;
            p = inner;
        }
        p.Children.RemoveAt(p.Children.Count - 1);
    }

    private void SetExport()
    {
        bool can = requests.ForExport() is not null && leakKinds.Count == 0 && !exporting;
        exportButton.IsEnabled = can;
        header.Actions.Children.Clear();
        header.Actions.Children.Add(exporting ? exportingButton : exportButton);
    }

    // ---------------- 내보내기 ----------------

    private async Task ExportAsync()
    {
        var doc = requests.ForExport();
        if (doc is null || exporting) return;
        if (!doc.Options.Masked && !await ConfirmUnmaskedAsync()) { lastAction = "unmasked-cancel"; Context.QueueDiag(); return; }
        string? path = Context.Options.ReportOut is { } outFolder ? System.IO.Path.Combine(outFolder, doc.FileName) : await PickAsync(doc.FileName);
        if (path is null || Context.IsClosing()) { lastAction = "cancel"; Context.QueueDiag(); return; }   // 취소: 아무 일도 하지 않는다
        // 기록을 옮기는 동안은 내보내지 않는다 (M9 설계 B3 출입문). 표는 내보내기 작업이 끝날 때 놓는다
        var ticket = Context.Resident.WorkGate.TryEnter();
        if (ticket is null)
        {
            lastAction = "moving";
            Context.Toasts.Show(ToastKind.Warning, "기록을 옮기는 중입니다", "옮기기가 끝난 뒤 내보내세요.");
            Context.QueueDiag();
            return;
        }
        exporting = true;
        SetExport();
        Context.QueueDiag();
        var fault = Context.Options.ReportFail switch { "write" => ReportFault.Write, "move" => ReportFault.Move, "leak" => ReportFault.Leak, _ => ReportFault.None };
        bool slow = Context.Options.ReportFail == "slow";
        // 시험: 임시 파일에 절반을 쓴 뒤 멈춰 있는다 (쓰는 중 닫기, G4 M8 2라운드 b1)
        // hold: 취소되면 곧 풀림. hold-long: 취소를 무시하고 닫기의 공통 한도보다 오래 멈춤 (닫기가 한도 없이 정리를 기다리는지 시험)
        // 멈춘 곳에 닿으면 진단을 UI 스레드에 다시 게시한다 (내보내기 시작의 진단 게시가 먼저 끝났어도 시험이 신호를 받게, G4 M8 4라운드 b)
        exportHolding = false;
        var ui = DispatcherQueue;
        void Reached()
        {
            exportHolding = true;
            ui.TryEnqueue(() => Context.QueueDiag());
        }
        Action<CancellationToken>? hold = Context.Options.ReportFail switch
        {
            "hold" => t => { Reached(); t.WaitHandle.WaitOne(Waits.ReportSlowTest); },
            "hold-long" => _ => { Reached(); Thread.Sleep(Waits.ReportHoldPastClose); },
            _ => null,
        };
        // 내보내기는 앱 종료 절차가 기다린다 (StopExportAsync): 취소를 보내면 쓰기 전이든 쓰는 중이든 멈추고 임시 파일을 지운다 (G4 M8 1·2라운드 b1)
        exportCts = new CancellationTokenSource();
        var token = exportCts.Token;
        var work = Task.Run(async () =>
        {
            var r = ReportExport.Render(doc, fault == ReportFault.Leak ? ReportFault.Leak : ReportFault.None);
            if (!r.Ok) return (r, (ReportWriteResult?)null);
            if (slow) await Task.Delay(Waits.ReportSlowTest, token);
            token.ThrowIfCancellationRequested();
            return (r, ReportFile.Write(path, r.Html!, overwrite: true, fault is ReportFault.Write or ReportFault.Move ? fault : ReportFault.None, token, hold));
        }, token);
        exportTask = work;
        _ = work.ContinueWith(_ => ticket.Dispose(), TaskScheduler.Default);
        ReportExport.Rendered rendered;
        ReportWriteResult? written;
        try { (rendered, written) = await work; }
        catch (OperationCanceledException)
        {
            exporting = false;
            Context.Log.Write("reportExportCancelled", new { });
            return;
        }
        if (Context.IsClosing()) return;
        exporting = false;
        if (!rendered.Ok)
        {
            leakKinds = rendered.LeakKinds.Count > 0 ? rendered.LeakKinds : ["문서 검사"];
            Context.Log.Write("reportLeak", new { kinds = rendered.LeakKinds, problems = rendered.Problems.Count });
        }
        else if (written is { Ok: true })
        {
            lastExport = new ExportOutcome(true, written.Bytes, doc.Options.Masked, null, System.IO.Path.GetFileName(path), System.IO.Path.GetDirectoryName(path));
            // 결과 줄은 옵션 아래라 큰 배율에서는 화면 밖일 수 있다: 알림도 띄운다
            Context.Toasts.Show(ToastKind.Success, "리포트를 내보냈습니다", lastExport.FileName ?? "");
            Context.Log.Write("reportExported", new { bytes = written.Bytes, masked = doc.Options.Masked });
        }
        else
        {
            lastExport = new ExportOutcome(false, 0, doc.Options.Masked, written?.Problem, null, null);
            Context.Toasts.Show(ToastKind.Error, "리포트를 내보내지 못했습니다", written?.Problem ?? "파일을 쓰지 못했습니다.");
            Context.Log.Write("reportExportFailed", new { error = written?.Error.ToString() });
        }
        Rerender();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (leakKinds.Count == 0 && exportButton.IsLoaded) exportButton.Focus(FocusState.Programmatic);
        });
    }

    private Task exportTask = Task.CompletedTask;
    private CancellationTokenSource? exportCts;

    /// <summary>
    /// 앱을 닫을 때: 취소를 보내고(동기, 첫 await 전) 쓰기·임시 파일 정리가 끝날 때까지. 쓰기는 조각마다 취소를 확인하므로 곧 끝난다.
    /// 옮기기를 이미 마쳤으면 완성된 결과 파일이 남는다. 예외는 삼킨다.
    /// </summary>
    /// <summary>
    /// 내보내기에 취소를 보내고 작업자(임시 파일 정리 포함)가 끝나기를 기다린다. UI 문맥으로 돌아오지 않는다:
    /// 세션 종료는 UI 스레드를 막고 이 작업을 기다린다 (G4 M9 1라운드 b1).
    /// </summary>
    public async Task StopExportAsync()
    {
        exportCts?.Cancel();
        try { await exportTask.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private Task<bool> ConfirmUnmaskedAsync()
    {
        var done = new TaskCompletionSource<bool>();
        void Closed(bool ok)
        {
            Context.Dialog.Closed -= Closed;
            done.TrySetResult(ok);
        }
        Context.Dialog.Closed += Closed;
        Context.Dialog.Open(exportButton, "가리지 않은 리포트를 내보낼까요?", "대상 이름·주소, 사업자 이름, 네트워크 어댑터 이름, 측정 ID가 그대로 들어갑니다. 내가 볼 때만 쓰세요.", "내보내기", "취소");
        return done.Task;
    }

    private async Task<string?> PickAsync(string fileName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Context.WindowHandle());
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        picker.FileTypeChoices.Add("HTML 문서", [".html"]);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        try { return (await picker.PickSaveFileAsync())?.Path; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return null; }
    }

    // ---------------- 시험 ----------------

    /// <summary>미리 보기가 다 그려짐 (bench·시험).</summary>
    public bool PreviewReady => drawn is not null && drawComplete && requests.State == ReportState.Ready;

    /// <summary>bench: 옵션을 바로 바꾼다 (화면 조작 없이).</summary>
    public void BenchOptions(ReportOptions next) => SetOptions(next);

    /// <summary>bench: 미리 보기를 세로 비율로 스크롤.</summary>
    public void BenchScroll(double fraction) => scroll.ChangeView(null, scroll.ScrollableHeight * fraction, null, true);

    public override object? Diag()
    {
        var list = Candidates();
        var item = list.FirstOrDefault(i => i.Key == key);
        var doc = requests.Published;
        return new
        {
            listed = Context.Records.Listed,
            candidates = list.Count,
            index = list.ToList().FindIndex(i => i.Key == key),
            id = item?.Entry.Id,
            state = requests.State.ToString(),
            masked = options.Masked, memos = options.IncludeMemos, diagnoses = options.IncludeDiagnoses, details = options.EpisodeDetails,
            published = doc is null ? null : new
            {
                masked = doc.Options.Masked, sections = doc.Sections.Select(s => s.Id).ToArray(), fileName = doc.FileName,
                fields = doc.Fields().Count(),
            },
            drawnSections,
            previewReady = PreviewReady,
            highContrast = drawnHighContrast,
            exportEnabled = exportButton.IsEnabled && !exporting,
            exporting,
            exportHolding,
            export = lastExport is { } e ? new { e.Ok, e.Bytes, e.Masked, e.FileName, problem = e.Problem } : null,
            leak = leakKinds,
            failure = requests.Failure?.ToString(),
            lastAction,
        };
    }
}
