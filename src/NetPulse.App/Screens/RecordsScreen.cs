using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Analysis;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 기록 (M7 설계 4): 측정 목록 / 기록 상세 / 비교 / 진단 기록. 한 화면 안에서 바꾼다 (뒤로 = 목록, 스크롤·선택 유지).
/// 값은 <see cref="RecordLibrary"/>·<see cref="RecordView"/>·<see cref="MeasurementComparison"/>·<see cref="StoredDiagnosisView"/>로만 정한다.
/// </summary>
public sealed class RecordsScreen : Screen
{
    private enum Mode { List, Detail, Compare, Diagnoses, Diagnosis }

    private readonly PageHeader header;
    private readonly SegmentedChoice tabs;
    private readonly GlassButton compareButton, openButton, back;
    private readonly StackPanel content = new() { Spacing = Space.S5 };
    private readonly ScrollViewer scroll;
    private Mode mode = Mode.List;
    private int shown = Size.RecordsPage;
    private bool picking;
    private readonly List<string> picked = [];
    private string? detailKey, openedFromRowId;
    private OpenedRecord? detail;
    private (OpenedRecord A, OpenedRecord B)? comparison;
    private string? loadError;
    private int expanded = -1;
    // 메모 고치기 (설계 3.5): 같은 상세 안에서는 여러 칸을 열 수 있고, 다시 그려도(끊김 펼침·다른 메모 열기·목록 갱신) 입력한 글이 남는다.
    // 상세를 떠나면 버린다 (G4 M7 1라운드 b1)
    private readonly List<string> editing = [];
    private readonly Dictionary<string, string> memoDrafts = [];
    private readonly Dictionary<string, TextBlock> memoStateText = [];
    private string? focusMark;
    private readonly Dictionary<string, string> memoState = [];
    // 열기·비교 요청 세대: 모드를 바꾸거나 새로 열 때마다 올라간다. 비동기 완료는 자기 세대일 때만 반영한다 (b4)
    private int requestGen;
    private bool syncingTabs;
    private string? copyState, lastAction, externalError;
    private IReadOnlyList<string> externalNotices = [];
    private readonly TimelineView view = new(TimelineView.Steps.Length - 1);
    private TimelinePanel? timeline;
    private TextBlock? timelineRange;
    private IReadOnlyList<(StoredDiagnosis Stored, string File)> diagnoses = [];
    private int diagnosesSkipped;
    private bool diagnosesLoaded;
    private StoredDiagnosis? openDiagnosis;
    private string renderedKey = "";
    private int renderGen;
    private bool detailComplete;   // 상세의 아래 카드까지 그려짐 (한 차례 늦게 만든다)
    private Mode? renderedMode;

    // ---------------- bench·시험 고리 ----------------

    /// <summary>상세가 열려 그려짐.</summary>
    public bool DetailReady => mode == Mode.Detail && detail is not null && detailComplete;
    /// <summary>비교 표가 그려짐.</summary>
    public bool CompareReady => mode == Mode.Compare && comparison is not null;
    /// <summary>상세 타임라인 조작 (bench).</summary>
    public void Timeline(TimelineCommand c) => OnTimeline(c);
    /// <summary>두 항목 비교 (bench: 고르기 화면 없이).</summary>
    public Task Compare(string a, string b) { LeaveDetail(); return CompareAsync(a, b); }

    public RecordsScreen(ScreenContext context) : base(context, "records")
    {
        RowSpacing = Space.S4;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        tabs = new SegmentedChoice(Ui, "records.tabs", "보기", [("measurements", "측정 목록"), ("diagnoses", "진단 기록")], "measurements");
        // 프로그램이 탭을 맞출 때(상세 열기 등)는 바꾸기 처리를 하지 않는다: 진단 기록 탭에서 상세를 열면 목록으로 덮였다 (b3)
        tabs.Changed += v => { if (syncingTabs) return; if (v == "diagnoses") ShowDiagnoses(); else ShowList(); };
        compareButton = new GlassButton(Ui, ButtonKind.Secondary, "records.compare", "비교", Icons.Layers);
        compareButton.Click += (_, _) => ToggleCompare();
        openButton = new GlassButton(Ui, ButtonKind.Ghost, "records.open", "다른 위치의 기록 열기", Icons.Layers);
        openButton.Click += async (_, _) => await PickExternalAsync();
        back = new GlassButton(Ui, ButtonKind.Secondary, "records.back", "목록으로", Icons.Back);
        back.Click += (_, _) => GoBack();
        header = new PageHeader(Ui, "records.header", null, "기록", tabs, compareButton, openButton);
        Children.Add(header);

        scroll = new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "records.scroll");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
        Context.Records.Changed += () => DispatcherQueue.TryEnqueue(() => { if (!Context.IsClosing() && Visibility == Visibility.Visible) Render(Context.Host.Snapshot); });
    }

    public override IReadOnlyList<UIElement> Parts => [header, scroll];

    public override void Entered()
    {
        _ = Context.Records.RefreshAsync(Context.Host.RunningId);
        // 진단 기록도 들어올 때마다 다시 읽는다 (다른 화면에서 새로 돌린 진단, b2). 바뀌었을 때만 다시 그린다
        if (diagnosesLoaded) _ = LoadDiagnosesAsync();
    }

    private void SetTab(string value)
    {
        syncingTabs = true;
        try { tabs.Set(value); }
        finally { syncingTabs = false; }
    }

    // ---------------- 들어오는 길 ----------------

    /// <summary>그 항목 상세 (null이면 목록).</summary>
    public void Show(string? key)
    {
        if (key is null) { ShowList(); return; }
        OpenDetail(key, null);
    }

    /// <summary>다른 위치의 기록 (시작 인수·폴더 선택). 경로는 기록하지 않는다.</summary>
    public void OpenExternal(string folder)
    {
        var (key, error, notices) = Context.Records.AddExternal(folder);
        externalError = error;
        externalNotices = notices;
        Context.Log.Write("recordsExternal", new { ok = key is not null });
        if (key is null) { ShowList(); return; }
        _ = OpenWhenListedAsync(key);
    }

    private async Task OpenWhenListedAsync(string key)
    {
        int req = requestGen;
        await Context.Records.RefreshAsync(Context.Host.RunningId);
        DispatcherQueue.TryEnqueue(() => { if (!Context.IsClosing() && req == requestGen) OpenDetail(key, null); });
    }

    private async Task PickExternalAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Context.WindowHandle());
        picker.FileTypeFilter.Add("*");
        Windows.Storage.StorageFolder? folder;
        try { folder = await picker.PickSingleFolderAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { folder = null; }
        if (folder is null || Context.IsClosing()) return;
        OpenExternal(folder.Path);
    }

    // ---------------- 모드 바꾸기 ----------------

    private void ShowList()
    {
        LeaveDetail();
        mode = Mode.List;
        SetTab("measurements");
        Rerender();
    }

    private void ShowDiagnoses()
    {
        LeaveDetail();
        mode = Mode.Diagnoses;
        picking = false;
        picked.Clear();
        SetTab("diagnoses");
        if (!diagnosesLoaded) _ = LoadDiagnosesAsync();
        Rerender();
    }

    private int diagnosesLoads;

    private async Task LoadDiagnosesAsync()
    {
        int load = ++diagnosesLoads;
        var store = Context.Diagnosis.Store;
        var (items, skipped) = await Task.Run(store.List);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Context.IsClosing() || load != diagnosesLoads) return;
            bool same = diagnosesLoaded && skipped == diagnosesSkipped && items.Select(x => x.File).SequenceEqual(diagnoses.Select(x => x.File));
            diagnoses = items;
            diagnosesSkipped = skipped;
            diagnosesLoaded = true;
            if (!same) Rerender();
        });
    }

    private void OpenDetail(string key, string? fromRow)
    {
        var item = Context.Records.Items.FirstOrDefault(i => i.Key == key);
        if (item?.Entry.Lifecycle == RecordLifecycle.Running) { Context.Navigate("measure", null); return; }
        LeaveDetail();
        int req = ++requestGen;
        mode = Mode.Detail;
        SetTab("measurements");
        detailKey = key;
        openedFromRowId = fromRow;
        detail = null;
        loadError = null;
        expanded = -1;
        Context.Records.SetPinned([key]);
        Rerender();
        _ = LoadDetailAsync(key, req);
    }

    private async Task LoadDetailAsync(string key, int req)
    {
        var opened = await Context.Records.OpenAsync(key);
        // 타임라인의 창과 무관한 부분은 UI 밖에서 미리 구한다 (12시간 기록에서 UI 프레임을 막지 않게)
        (TimelineData Timeline, RecordDetailView View)? prepared = opened?.Record.Header is null ? null : await Task.Run(() => (Timeline: TimelineData.Build(opened).Warm(), View: RecordView.Detail(opened.Record, opened.Analysis, opened.Lifecycle)));
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Context.IsClosing() || req != requestGen || detailKey != key || mode != Mode.Detail) return;
            detail = opened;
            if (prepared is { } p) { timelineData = p.Timeline; detailView = (opened!, p.View); }
            loadError = opened is null ? "기록을 읽지 못했습니다." : null;
            view.ShowAll(EndMs(opened));
            Rerender();
            scroll.ChangeView(null, 0, null, true);
            // 다시 그리면 [목록으로]가 새 줄로 옮겨 붙어 아직 배치 전이다: 프레임을 그린 뒤에 초점을 준다 (M7 UI 시험 1차: 초점이 탐색으로 빠짐)
            OnePerFrame(() => { if (req == requestGen && mode == Mode.Detail) back.Focus(FocusState.Programmatic); });
        });
    }

    private static long EndMs(OpenedRecord? r) => Math.Max(1, r?.Record.Rounds.LastOrDefault()?.AtMs ?? 1);

    /// <summary>
    /// 상세·비교를 떠날 때: 저장하지 않은 메모 초안은 버리고 알린다 (구현 중 결정 4). 그 기록의 원본·분석 참조도 놓는다:
    /// 화면이 쥐고 있으면 라이브러리의 보유 한도(3개) 밖에서 기록이 남았다 (b5). 진행 중인 열기·비교 완료는 버린다 (b4).
    /// </summary>
    private void LeaveDetail()
    {
        if (editing.Count > 0 && mode == Mode.Detail)
            Context.Toasts.Show(ToastKind.Info, "저장하지 않은 메모를 버렸습니다", "다시 고치려면 그 기록을 여세요.");
        editing.Clear();
        memoDrafts.Clear();
        focusMark = null;
        requestGen++;
        if (mode is Mode.Detail or Mode.Compare) Context.Records.SetPinned([]);
        detail = null;
        comparison = null;
        detailView = null;
        timelineData = null;
        timeline = null;
        timelineRange = null;
    }

    private void GoBack()
    {
        string? row = openedFromRowId;
        if (mode == Mode.Diagnosis) { mode = Mode.Diagnoses; Rerender(); return; }
        ShowList();
        if (row is not null) FocusLater(row, Mode.List);
    }

    /// <summary>
    /// 줄이 나눠 그려진 뒤에 초점을 준다: 프레임 조각 큐 맨 뒤에 넣어(앞선 줄 만들기가 먼저 끝남) 찾고, 아직 없거나 받지 못하면
    /// 몇 프레임 더 해 본다 (b6: 목록 9번째 줄 뒤·끊김 줄은 다음 프레임에 생겨 초점 복귀가 빠졌다).
    /// </summary>
    private void FocusLater(string id, Mode expected, int tries = 10)
    {
        OnePerFrame(() =>
        {
            if (mode != expected) return;
            if (FindRow(id) is { } c && c.Focus(FocusState.Programmatic)) return;
            if (tries > 1) FocusLater(id, expected, tries - 1);
        });
    }

    private Control? FindRow(string id) =>
        Descendants(content).OfType<Control>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    public override bool Escape()
    {
        if (mode is Mode.Detail or Mode.Compare or Mode.Diagnosis) { GoBack(); return true; }
        if (picking) { ToggleCompare(); return true; }
        return false;
    }

    private void ToggleCompare()
    {
        if (mode != Mode.List) ShowList();
        picking = !picking;
        picked.Clear();
        Rerender();
    }

    private void Pick(string key)
    {
        if (!picked.Remove(key)) picked.Add(key);
        if (picked.Count == 2)
        {
            var (a, b) = (picked[0], picked[1]);
            picking = false;
            picked.Clear();
            _ = CompareAsync(a, b);
            return;
        }
        Rerender();
    }

    private async Task CompareAsync(string a, string b)
    {
        int req = ++requestGen;
        mode = Mode.Compare;
        comparison = null;
        loadError = null;
        Context.Records.SetPinned([a, b]);
        Rerender();
        var ra = await Context.Records.OpenAsync(a);
        var rb = await Context.Records.OpenAsync(b);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Context.IsClosing() || req != requestGen || mode != Mode.Compare) return;
            if (ra is null || rb is null) loadError = "한쪽 기록을 읽지 못해 비교하지 못했습니다.";
            else comparison = (ra, rb);
            Rerender();
        });
    }

    private void Rerender()
    {
        renderedKey = "";
        Render(Context.Host.Snapshot);
    }

    // ---------------- 그리기 ----------------

    public override void Render(HostSnapshot snapshot)
    {
        var items = Context.Records.Items;
        string key = $"{mode}|{items.Count}|{string.Join(",", items.Select(i => i.Stamp.Length + (i.Summary.Analyzing ? "a" : "d")))}|{Context.Records.Listed}"
            + $"|{shown}|{picking}|{string.Join(",", picked)}|{detail?.Stamp}|{loadError}|{expanded}|{string.Join(",", editing)}|{comparison is null}|{diagnosesLoaded}|{openDiagnosis?.StartedAt}";
        if (key == renderedKey) return;
        renderedKey = key;
        compareButton.IsEnabled = mode is Mode.List or Mode.Compare or Mode.Detail;
        AutomationProperties.SetItemStatus(compareButton, picking ? "picking" : "off");
        content.Children.Clear();
        renderGen++;
        detailComplete = false;
        bool modeChanged = renderedMode is { } rm && rm != mode;
        renderedMode = mode;
        switch (mode)
        {
            case Mode.List: RenderProfile.Time("records.list", () => RenderList(items)); break;
            case Mode.Detail: RenderProfile.Time("records.detail", RenderDetail); break;
            case Mode.Compare: RenderProfile.Time("records.compare", RenderCompare); break;
            case Mode.Diagnoses: RenderDiagnoses(); break;
            case Mode.Diagnosis: RenderDiagnosis(); break;
        }
        // 목록 ↔ 상세·비교·진단 기록: 화면 전환과 같은 교차 페이드 (줄이기면 짧은 페이드, 진행도 멈춤은 시험)
        if (modeChanged) Ui.Motion.CrossFade(null, content.Children.ToArray());
        Context.QueueDiag();
    }

    // ---------------- 목록 ----------------

    private void RenderList(IReadOnlyList<LibraryItem> items)
    {
        if (externalError is { } err) content.Children.Add(new Banner(Ui, "records.external.error", BannerKind.Error, err));
        if (picking)
            content.Children.Add(new Banner(Ui, "records.picking", BannerKind.Info, $"비교할 기록 두 개를 고르세요 ({picked.Count}/2). Esc로 그만둡니다."));
        if (!Context.Records.Listed)
        {
            var (card, body) = ScreenParts.Card(Ui, "records.list", "측정 목록");
            body.Set(GlassCard.Skeleton(Ui), "loading");
            content.Children.Add(card);
            return;
        }
        if (items.Count == 0)
        {
            var (card, body) = ScreenParts.Card(Ui, "records.list", "측정 목록");
            var stack = new StackPanel { Spacing = Space.S4 };
            stack.Children.Add(ScreenParts.Message(Ui, "records.empty", Icons.Info, Ui.Brushes.Muted, "아직 측정 기록이 없습니다", "측정을 마치면 여기에 결과가 쌓입니다."));
            var go = new GlassButton(Ui, ButtonKind.Primary, "records.start", "측정 화면으로", Icons.Play) { HorizontalAlignment = HorizontalAlignment.Left };
            go.Click += (_, _) => Context.Navigate("measure", null);
            stack.Children.Add(go);
            body.Set(stack, "empty");
            content.Children.Add(card);
            return;
        }
        var list = new StackPanel { Spacing = Space.S1 };
        list.KeyDown += OnListKey;   // 위·아래 화살표로 줄 이동 (설계 4.4)
        var visible = items.Take(shown).ToArray();
        // 화면에 먼저 보이는 줄만 이번에 만들고 나머지는 다음 차례에 (한 프레임에 30줄을 만들고 배치하면 예산 50 ms에 닿았다, M7 bench)
        int first = Math.Min(visible.Length, Size.RecordsFirstRows);
        for (int i = 0; i < first; i++) list.Children.Add(Row(visible[i], i));
        if (first < visible.Length)
        {
            int gen = renderGen;
            OnePerFrame(() =>
            {
                if (gen != renderGen) return;
                for (int i = first; i < visible.Length; i++) list.Children.Insert(i, Row(visible[i], i));
                Context.QueueDiag();
            });
        }
        Context.Records.SetVisible(visible.Select(v => v.Key));
        if (items.Count > shown)
        {
            var more = new GlassButton(Ui, ButtonKind.Ghost, "records.more", $"더 보기 ({items.Count - shown}개 남음)", Icons.Down);
            more.Click += (_, _) => { shown += Size.RecordsPage; Rerender(); };
            list.Children.Add(more);
        }
        int unreadable = items.Count(i => i.Summary.Error is not null);
        if (unreadable > 0)
        {
            var t = Fonts.Wrapped($"읽지 못한 기록 {unreadable}개 (줄을 열면 폴더를 열어 볼 수 있습니다)", Typography.Caption, Ui.Brushes.Muted);
            AutomationProperties.SetAutomationId(t, "records.unreadable");
            list.Children.Add(t);
        }
        var (listCard, listBody) = ScreenParts.Card(Ui, "records.list", "측정 목록", $"{items.Count}개 · 최신순");
        string state = items.Any(i => i.Summary.Analyzing) ? "analyzing" : "normal";
        listBody.Set(list, state);
        content.Children.Add(listCard);
    }

    private void OnListKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)) return;
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is not Control c) return;
        const string prefix = "records.row.";
        string id = AutomationProperties.GetAutomationId(c);
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(id[prefix.Length..], out int n)) return;
        int next = e.Key == Windows.System.VirtualKey.Up ? n - 1 : n + 1;
        if (next >= 0 && FindRow(prefix + next) is { } target && target.Focus(FocusState.Keyboard)) e.Handled = true;
    }

    private UIElement Row(LibraryItem item, int index)
    {
        var s = item.Summary;
        var e = item.Entry;
        var grid = new Grid { ColumnSpacing = Space.S4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bool isPicked = picked.Contains(item.Key);
        string? cannot = picking ? MeasurementComparison.CannotPick(item.Status ?? (s.Error is not null ? RecordStatus.Unreadable : RecordStatus.Ok), e.Lifecycle) : null;
        string letter = isPicked ? "✓" : s.Badge?.Letter ?? (s.Analyzing ? "…" : e.Lifecycle == RecordLifecycle.Running ? "●" : "!");
        grid.Children.Add(Badges.Grade(Ui, letter, isPicked ? Ui.Brushes.Accent : ScreenParts.GradeBrush(Ui, s.Grade)));

        var texts = new StackPanel { Spacing = Space.S0 };
        string when = e.StartedAt is { } st ? LiveSummary.When(st, Context.Now) : "시각 모름";
        var title = new List<string> { when };
        if (s.DurationMs is long d) title.Add(TimelineModel.Seconds(d));
        if (e.Targets.Count > 0) title.Add(RecordView.TargetsSummary(e.Targets));
        if (item.External) title.Add("다른 위치");
        texts.Children.Add(Fonts.Text(string.Join(" · ", title), Typography.Label, Ui.Brushes.Ink));
        string sentence = s.Analyzing ? "분석 중…" : s.Sentence ?? s.Error ?? (e.Lifecycle == RecordLifecycle.Running ? "측정 중" : "");
        var sentenceText = Fonts.Text(sentence, Typography.Body, Ui.Brushes.Ink);
        sentenceText.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(sentenceText);
        var sub = new List<string>();
        if (s.Outages is int o) sub.Add(o == 0 ? "끊김 관측 안 됨" : $"끊김 {o}번");
        if (s.LongestMinMs is long l) sub.Add($"최장 {TimelineModel.Seconds(l)} 이상");
        if (s.FirstMemo is { } memo) sub.Add(memo);
        if (cannot is not null) sub.Add(cannot);
        var subText = Fonts.Text(string.Join(" · ", sub), Typography.Caption, Ui.Brushes.Muted);
        subText.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(subText);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var chips = new StackPanel { Spacing = Space.S1, VerticalAlignment = VerticalAlignment.Center };
        string end = e.Lifecycle == RecordLifecycle.Running ? "측정 중" : e.Lifecycle == RecordLifecycle.NoSummary ? "끝 기록 없음" : RecordView.EndText(e.Lifecycle, e.EndReason);
        var endChip = new StatusChip(Ui, $"records.row.{index}.end", e.Lifecycle == RecordLifecycle.NoSummary ? Status.Warn : Status.Unknown, end);
        chips.Children.Add(endChip);
        if (s.Error is not null || e.Integrity != RecordIntegrity.Ok || item.Damage.Count > 0)
            chips.Children.Add(new StatusChip(Ui, $"records.row.{index}.integrity", s.Error is not null ? Status.Bad : Status.Warn,
                s.Error is not null ? "읽지 못함" : e.Integrity == RecordIntegrity.MissingSegments ? "조각 누락" : "일부 손상"));
        Grid.SetColumn(chips, 2);
        grid.Children.Add(chips);

        string name = $"{string.Join(", ", title)}, 등급 {s.Badge?.Name ?? (s.Analyzing ? "분석 중" : "없음")}, {string.Join(", ", sub)}, {end}"
            + (picking ? isPicked ? ", 비교에 고름" : cannot is null ? ", 비교에 고를 수 있음" : "" : "");
        string id = $"records.row.{index}";
        var row = new RowButton(Ui, id, name, grid) { IsEnabled = !(picking && cannot is not null) };
        row.Click += (_, _) => { if (picking) Pick(item.Key); else OpenDetail(item.Key, id); };
        AutomationProperties.SetItemStatus(row, isPicked ? "picked" : s.Analyzing ? "analyzing" : s.Error is not null ? "error" : "ready");
        return row;
    }

    // ---------------- 상세 ----------------

    /// <summary>
    /// "목록으로" 줄. 버튼은 한 개를 다시 쓰므로, 이전 그리기의 줄에서 떼어 낸 뒤 붙인다
    /// (부모가 있는 요소를 다른 패널에 넣으면 XAML이 0x800F1000으로 앱을 즉시 끝낸다. M7 UI 시험 1차).
    /// 이전 줄은 직접 들고 있다가 뗀다: 화면에서 빠진 줄의 버튼은 Parent로 찾지 못하는 경우가 있었다.
    /// </summary>
    private StackPanel BackBar()
    {
        backBar?.Children.Remove(back);
        backBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        backBar.Children.Add(back);
        return backBar;
    }

    private StackPanel? backBar;

    private void RenderDetail()
    {
        content.Children.Add(BackBar());
        var item = Context.Records.Items.FirstOrDefault(i => i.Key == detailKey);
        foreach (var n in item?.External == true ? externalNotices : [])
            content.Children.Add(new Banner(Ui, "records.external.notice", BannerKind.Info, n));
        if (loadError is not null || (detail is not null && detail.Record.Header is null))
        {
            var (card, body) = ScreenParts.Card(Ui, "records.detail.error", "기록을 열지 못했습니다");
            var stack = new StackPanel { Spacing = Space.S3 };
            stack.Children.Add(Fonts.Wrapped(loadError ?? "기록 머리를 읽지 못했습니다.", Typography.Body, Ui.Brushes.Ink));
            stack.Children.Add(FolderButton(item));
            body.Set(stack, "error");
            content.Children.Add(card);
            return;
        }
        if (detail is null)
        {
            var (card, body) = ScreenParts.Card(Ui, "records.detail.loading", "기록을 여는 중");
            var stack = new StackPanel { Spacing = Space.S3 };
            if (item?.Entry is { } e)
                stack.Children.Add(Fonts.Wrapped($"{(e.StartedAt is { } st ? LiveSummary.When(st, Context.Now) : "")} · {RecordView.TargetsSummary(e.Targets)}", Typography.Body, Ui.Brushes.Muted));
            stack.Children.Add(GlassCard.Skeleton(Ui));
            body.Set(stack, "loading");
            content.Children.Add(card);
            return;
        }
        var r = detail.Record;
        var a = detail.Analysis;
        // 화면 모델은 기록마다 한 번 (펼치기·메모 고치기로 다시 그릴 때마다 12시간 기록의 추세를 다시 구하지 않게)
        if (!ReferenceEquals(detailView?.For, detail))
        {
            var d0 = detail;
            RenderProfile.Time("records.detail.view", () => detailView = (d0, RecordView.Detail(d0.Record, d0.Analysis, d0.Lifecycle)));
        }
        var v = detailView!.Value.View;
        var h = r.Header!;
        var label = ScreenParts.TimeLabel(h.StartedAt, Context.Now);

        // 결론
        var conclusion = new StackPanel { Spacing = Space.S2 };
        var headRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        headRow.Children.Add(Badges.Grade(Ui, v.Badge.Letter, ScreenParts.GradeBrush(Ui, v.Grade)));
        var ink = Context.Options.HideMeasuredText ? Ui.Brushes.Transparent : Ui.Brushes.Ink;
        var gradeText = Fonts.Text("등급 " + v.GradeName, Typography.Headline, ink);
        AutomationProperties.SetAutomationId(gradeText, "records.detail.grade");
        AutomationProperties.SetName(gradeText, "등급 " + v.GradeName);
        gradeText.VerticalAlignment = VerticalAlignment.Center;
        headRow.Children.Add(gradeText);
        conclusion.Children.Add(headRow);
        conclusion.Children.Add(Named("records.detail.sentence", v.Sentence, Typography.Body, ink));
        foreach (var n in v.Notes) conclusion.Children.Add(Fonts.Wrapped(n, Typography.Caption, Ui.Brushes.Muted));
        conclusion.Children.Add(Named("records.detail.coverage", v.Coverage, Typography.Caption, Ui.Brushes.Muted));
        conclusion.Children.Add(Named("records.detail.end", $"{LiveSummary.When(h.StartedAt, Context.Now)} · {v.End}" + (v.Save is { } sv ? " · " + sv : ""), Typography.Caption, Ui.Brushes.Muted));
        conclusion.Children.Add(Named("records.detail.setup", $"{v.Targets} · {v.Setup}", Typography.Caption, Ui.Brushes.Muted));
        var conclusionCard = new GroupPanel("records.detail.conclusion", "결론");
        conclusionCard.Children.Add(Ui.Glass.Surface(GlassLevel.Content, conclusion, Radius.Outer, new Thickness(Space.S6)));
        content.Children.Add(conclusionCard);
        foreach (var (d, i) in v.Damage.Select((d, i) => (d, i)))
            content.Children.Add(new Banner(Ui, $"records.detail.damage.{i}", BannerKind.Warn, d));

        // 타임라인
        timeline = new TimelinePanel(Ui, "records.timeline", compact: false);
        timeline.Command += OnTimeline;
        timeline.Picked += OnTimelinePicked;
        var zoomOut = new GlassButton(Ui, ButtonKind.Icon, "records.zoomout", "축소", Icons.Minus);
        var zoomIn = new GlassButton(Ui, ButtonKind.Icon, "records.zoomin", "확대", Icons.Plus);
        var left = new GlassButton(Ui, ButtonKind.Icon, "records.left", "앞으로", Icons.Back);
        var right = new GlassButton(Ui, ButtonKind.Icon, "records.right", "뒤로", Icons.Chevron);
        var all = new GlassButton(Ui, ButtonKind.Icon, "records.all", "전체 보기", Icons.ZoomAll);
        zoomOut.Click += (_, _) => OnTimeline(TimelineCommand.ZoomOut);
        zoomIn.Click += (_, _) => OnTimeline(TimelineCommand.ZoomIn);
        left.Click += (_, _) => OnTimeline(TimelineCommand.PanLeft);
        right.Click += (_, _) => OnTimeline(TimelineCommand.PanRight);
        all.Click += (_, _) => OnTimeline(TimelineCommand.ShowAll);
        var (timelineCard, timelineBody) = ScreenParts.Card(Ui, "records.detail.timelinecard", "타임라인", "", left, right, zoomOut, zoomIn, all);
        var tstack = new StackPanel { Spacing = Space.S3 };
        timelineRange = Fonts.Text("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(timelineRange, "records.timeline.range");
        tstack.Children.Add(timelineRange);
        tstack.Children.Add(timeline);
        tstack.Children.Add(Legend());
        timelineBody.Set(tstack, "normal");
        // 크기가 바뀔 때마다 배치 중에 바로 그리지 않고 프레임마다 한 번만 (첫 그리기 30 ms가 배치 프레임에 겹쳤다, M7 bench)
        timeline.SizeChanged += (_, _) => QueueTimeline();
        content.Children.Add(timelineCard);
        QueueTimeline();

        // 아래 카드(통계·추세·끊김·메모·진단)는 다음 차례에 만든다: 한 프레임에 상세 전체를 만들고 배치하면 12시간 기록에서
        // 예산(50 ms)을 넘었다 (M7 UI 시험 1차 bench). 그 사이 다른 그리기가 있었으면 버린다.
        int gen = renderGen;
        var opened = detail;
        OnePerFrame(() =>
        {
            if (gen != renderGen || !ReferenceEquals(opened, detail) || mode != Mode.Detail) return;
            RenderProfile.Time("records.detail.rest", () => RenderDetailRest(opened, v, item, label));
            detailComplete = true;
            Context.QueueDiag();
        });
    }

    // ---------------- 프레임 나누기 ----------------

    private readonly Queue<Action> perFrame = new();
    private bool frameHooked, timelineQueued;

    /// <summary>
    /// 무거운 그리기 조각을 프레임마다 하나씩 한다 (한 틈에 몰아서 하면 조각을 나눈 보람이 없다). 다음 프레임을 그린 뒤 낮은 우선순위로.
    /// 12시간 기록의 상세·목록을 한 프레임에 만들고 배치하면 예산(50 ms)을 넘었다 (M7 UI 시험 1차 bench).
    /// </summary>
    private void OnePerFrame(Action action)
    {
        perFrame.Enqueue(action);
        if (frameHooked) return;
        frameHooked = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += RunOnePerFrame;
    }

    private void RunOnePerFrame(object? sender, object e)
    {
        if (perFrame.Count == 0 || Context.IsClosing())
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= RunOnePerFrame;
            frameHooked = false;
            perFrame.Clear();
            return;
        }
        var next = perFrame.Dequeue();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (!Context.IsClosing()) next(); });
    }

    private void QueueTimeline()
    {
        if (timelineQueued) return;
        timelineQueued = true;
        OnePerFrame(() => { timelineQueued = false; DrawTimeline(); });
    }

    private void RenderDetailRest(OpenedRecord opened, RecordDetailView v, LibraryItem? item, Func<long, string> label)
    {
        var r = opened.Record;
        var a = opened.Analysis;
        var h = r.Header!;
        // 통계
        var stats = new StackPanel { Spacing = Space.S2 };
        if (v.Verdicts.Count == 0) stats.Children.Add(Fonts.Wrapped("판정별로 셀 끊김이 없습니다.", Typography.Body, Ui.Brushes.Muted));
        foreach (var line in v.Verdicts) stats.Children.Add(Named($"records.detail.verdict.{line.Verdict}", $"{line.Name}: {line.Text}", Typography.Body, Ui.Brushes.Ink));
        if (v.CountNote is { } note) stats.Children.Add(Fonts.Wrapped(note, Typography.Caption, Ui.Brushes.Muted));
        foreach (var t in v.TargetLines)
        {
            var box = new StackPanel { Spacing = Space.S0 };
            box.Children.Add(Fonts.Text($"{t.Name} ({(t.Role == TargetRole.Local ? "로컬" : "외부")} · {t.Method})", Typography.Label, Ui.Brushes.Ink));
            box.Children.Add(Fonts.Wrapped($"{t.Loss} · 평균 {t.Average} · 최대 {t.Max} · 최대 연속 손실 {t.Consecutive} · 마지막 성공 {t.LastSuccess} · 마지막 손실 {t.LastLoss}", Typography.Caption, Ui.Brushes.Muted));
            // 이름 붙은 묶음은 GroupPanel이어야 UI 자동화 트리에 나온다 (StackPanel은 peer가 없어 숨는다. M7 UI 시험 1차)
            var group = new GroupPanel($"records.detail.target.{t.TargetId}", $"{t.Name}, {t.Loss}, 평균 {t.Average}, 최대 {t.Max}, 최대 연속 손실 {t.Consecutive}");
            group.Children.Add(box);
            stats.Children.Add(group);
        }
        var (statsCard, statsBody) = ScreenParts.Card(Ui, "records.detail.stats", "통계");
        statsBody.Set(stats, "normal");


        // 지연 추세
        var trend = new TrendChart(Ui, "records.trend");
        var roles = h.Targets.ToDictionary(t => t.Id, t => t.Role);
        var lines = v.Trend.Where(t => !t.NoBaseline).Select(t => (t, roles.GetValueOrDefault(t.TargetId, TargetRole.External))).ToArray();
        long end = EndMs(detail);
        RenderProfile.Time("records.detail.trend", () => trend.SetData(lines, 0, end, label, TrendSummary(v.Trend)));
        var trendStack = new StackPanel { Spacing = Space.S2 };
        trendStack.Children.Add(trend);
        foreach (var t in v.Trend.Where(t => t.NoBaseline)) trendStack.Children.Add(Fonts.Wrapped($"{t.Name}: 값이 적어 추세 없음", Typography.Caption, Ui.Brushes.Muted));
        trendStack.Children.Add(Fonts.Wrapped(RecordView.TrendNote, Typography.Caption, Ui.Brushes.Muted));
        var (trendCard, trendBody) = ScreenParts.Card(Ui, "records.detail.trend", "지연 추세", "5분마다 중앙값 · 실선 외부 대상, 점선 로컬 대상");
        trendBody.Set(trendStack, lines.Length == 0 ? "empty" : "normal");

        // 끊김 목록
        var episodes = new StackPanel { Spacing = Space.S1 };
        if (v.NoEpisodes is { } none)
        {
            episodes.Children.Add(Named("records.episodes.none", none, Typography.Body, Ui.Brushes.Ink));
            episodes.Children.Add(Fonts.Wrapped(v.Coverage, Typography.Caption, Ui.Brushes.Muted));
        }
        RenderProfile.Time("records.detail.episodes", () => { for (int i = 0; i < v.Episodes.Count; i++) episodes.Children.Add(EpisodeRow(v.Episodes[i], a.Episodes[i], i, label)); });
        var (episodeCard, episodeBody) = ScreenParts.Card(Ui, "records.detail.episodes", "끊김 목록", v.Episodes.Count == 0 ? null : $"{v.Episodes.Count}번 · 줄을 펼치면 주변 회차");
        episodeBody.Set(episodes, v.Episodes.Count == 0 ? "empty" : "normal");

        // 증상 표시와 메모
        var (marksCard, marksBody) = ScreenParts.Card(Ui, "records.detail.marks", "증상 표시와 메모");
        RenderProfile.Time("records.detail.marks", () => marksBody.Set(MarksPanel(r, h, label), "normal"));

        // 이 측정 중 진단
        var during = (diagnosesLoaded ? diagnoses : []).Where(x => x.Stored.MeasurementId == h.Id).ToArray();
        if (!diagnosesLoaded) _ = LoadDiagnosesAsync();
        var diagStack = new StackPanel { Spacing = Space.S2 };
        if (during.Length == 0) diagStack.Children.Add(Named("records.detail.diagnoses.none", "이 측정 중에 돌린 빠른 진단이 없습니다.", Typography.Body, Ui.Brushes.Muted));
        foreach (var (x, i) in during.Select((x, i) => (x, i)))
        {
            var (head, _, _) = StoredDiagnosisView.Row(x.Stored);
            var row = new RowButton(Ui, $"records.detail.diagnosis.{i}", $"{head}, {LiveSummary.When(x.Stored.StartedAt, Context.Now)}",
                Fonts.Text($"{head} · {LiveSummary.When(x.Stored.StartedAt, Context.Now)}", Typography.Body, Ui.Brushes.Ink));
            var stored = x.Stored;
            row.Click += (_, _) => { LeaveDetail(); openDiagnosis = stored; mode = Mode.Diagnosis; SetTab("diagnoses"); Rerender(); };
            diagStack.Children.Add(row);
        }
        diagStack.Children.Add(FolderButton(item));
        var (diagCard, diagBody) = ScreenParts.Card(Ui, "records.detail.diagnoses", "이 측정 중 진단", during.Length == 0 ? null : $"{during.Length}개");
        diagBody.Set(diagStack, "normal");

        var grid = TwoColumns([statsCard], [episodeCard, marksCard, diagCard]);
        content.Children.Add(trendCard);
        content.Children.Add(grid);
    }

    private void DrawTimeline() => RenderProfile.Time("records.timeline", DrawTimelineNow);

    private static string TrendSummary(IReadOnlyList<TrendLine> lines) =>
        "지연 추세. " + string.Join(". ", lines.Select(t => t.NoBaseline ? $"{t.Name} 값이 적어 추세 없음"
            : $"{t.Name} 처음 {PathMapModel.Ms(t.Segments[0][0].MedianMs)}, 마지막 {PathMapModel.Ms(t.Segments[^1][^1].MedianMs)}, 가장 높은 창 {PathMapModel.Ms(t.Segments.SelectMany(s => s).Max(p => p.MedianMs))}"));

    private TextBlock Named(string id, string text, TypeStyle style, Microsoft.UI.Xaml.Media.Brush brush)
    {
        var t = Fonts.Wrapped(text, style, brush);
        AutomationProperties.SetAutomationId(t, id);
        AutomationProperties.SetName(t, text);
        return t;
    }

    /// <summary>넓으면 두 열, 좁으면 한 열 (M5 홈 기준).</summary>
    private Grid TwoColumns(UIElement[] leftCards, UIElement[] rightCards)
    {
        var grid = new Grid { ColumnSpacing = Space.S5, RowSpacing = Space.S5 };
        var leftStack = new StackPanel { Spacing = Space.S5 };
        foreach (var c in leftCards) leftStack.Children.Add(c);
        var rightStack = new StackPanel { Spacing = Space.S5 };
        foreach (var c in rightCards) rightStack.Children.Add(c);
        grid.Children.Add(leftStack);
        grid.Children.Add(rightStack);
        void Arrange(double width)
        {
            bool two = width >= Size.HomeTwoColumns;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (two) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            else { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            Grid.SetColumn(rightStack, two ? 1 : 0);
            Grid.SetRow(rightStack, two ? 0 : 1);
        }
        grid.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        Arrange(Size.HomeTwoColumns);
        return grid;
    }

    private GlassButton FolderButton(LibraryItem? item)
    {
        var b = new GlassButton(Ui, ButtonKind.Ghost, "records.detail.folder", "기록 폴더 열기", Icons.Layers) { HorizontalAlignment = HorizontalAlignment.Left };
        b.IsEnabled = item is { Entry.Segments.Count: > 0 };
        b.Click += (_, _) =>
        {
            if (item is null) return;
            var target = RecordMemos.NotesFolder(item.Entry.Segments) ?? item.Entry.Segments[0];
            var allowed = item.External ? item.Entry.Segments : [.. Context.Host.World.RecordPaths];
            var r = Context.Diagnosis.World.Actions.OpenFolder(target, allowed);
            lastAction = "folder:" + (item.External ? "external" : "record") + (r.Ok ? "" : ":" + r.Code);
            Context.QueueDiag();
        };
        return b;
    }

    // ---------------- 타임라인 ----------------

    private void OnTimeline(TimelineCommand c)
    {
        long nowMs = EndMs(detail);
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
        DrawTimeline();
        Context.QueueDiag();
    }

    /// <summary>끊김 줄 → 타임라인을 그 구간으로 (설계 4.1).</summary>
    private void CenterOn(Episode e)
    {
        if (detail is null) return;
        var rounds = detail.Record.Rounds;
        long from = rounds.FirstOrDefault(r => r.Round >= e.FirstRound)?.AtMs ?? 0;
        long to = rounds.LastOrDefault(r => r.Round <= e.LastRound)?.AtMs ?? from;
        long nowMs = EndMs(detail);
        while (!view.IsAll && TimelineView.Steps[view.StepIndex] < to - from + 4 * (detail.Record.Header?.IntervalMs ?? 1000) && view.StepIndex < TimelineView.Steps.Length - 2)
            view.ZoomOut(nowMs);
        if (view.IsAll) view.ZoomIn(nowMs);
        view.CenterOn((from + to) / 2, nowMs);
        DrawTimeline();
    }

    /// <summary>타임라인을 누른 자리(폭 비율)에 끊김이 있으면 그 줄로: 펼치고 초점 (설계 4.1).</summary>
    private void OnTimelinePicked(double fraction)
    {
        if (detail?.Record.Header is not { } h || timeline is null) return;
        var (from, to) = view.Window(EndMs(detail));
        long at = from + (long)Math.Round(Math.Clamp(fraction, 0, 1) * (to - from));
        long slack = Math.Max(h.IntervalMs, (to - from) / Math.Max(1, timeline.Columns) * 3);   // 좁은 띠도 누를 수 있게 몇 열 여유
        var rounds = detail.Record.Rounds;
        int index = -1;
        long best = long.MaxValue;
        for (int i = 0; i < detail.Analysis.Episodes.Count; i++)
        {
            var e = detail.Analysis.Episodes[i];
            long s = rounds.FirstOrDefault(r => r.Round >= e.FirstRound)?.AtMs ?? 0;
            long t = rounds.LastOrDefault(r => r.Round <= e.LastRound)?.AtMs ?? s;
            long distance = at < s ? s - at : at > t ? at - t : 0;
            if (distance <= slack && distance < best) { best = distance; index = i; }
        }
        lastPicked = index;
        Context.QueueDiag();
        if (index < 0) return;
        expanded = index;
        Rerender();
        FocusLater($"records.episode.{index}", Mode.Detail);
    }

    private int? lastPicked;

    private void DrawTimelineNow()
    {
        if (detail?.Record.Header is not { } h || timeline is null || timeline.ActualWidth <= 0) return;
        var r = detail.Record;
        long nowMs = EndMs(detail);
        var (from, to) = view.Window(nowMs);
        var label = ScreenParts.TimeLabel(h.StartedAt, Context.Now);
        // 창과 무관한 부분(증상 표시·링크 끊김·띠·회차 대표값)은 기록마다 한 번만 구한다. 보통은 상세를 열 때 백그라운드에서
        // 미리 구해 둔다 (M7 UI 시험 1차: 12시간 기록에서 그릴 때마다 4만 회차를 다시 구해 프레임 예산을 넘김)
        if (!ReferenceEquals(timelineData?.For, detail))
        {
            var built = TimelineData.Build(detail);
            RenderProfile.Time("records.timeline.prepare", () => built.Warm());
            timelineData = built;
        }
        var td = timelineData!;
        TimelineFrame frame = default!;
        RenderProfile.Time("records.timeline.frame", () => frame = TimelineModel.Frame(r.Rounds, h.Targets, r.Gaps, td.Marks, td.Downs, td.Bands, from, to, timeline.Columns, h.IntervalMs, $"{label(from)}~{label(to)}", td.Reps));
        RenderProfile.Time("records.timeline.show", () => timeline.Show(frame, label, false, $"{label(from)}부터 {label(to)}까지 ({view.Label})"));
        if (timelineRange is not null)
        {
            timelineRange.Text = $"{label(from)} ~ {label(to)} · {view.Label}";
            AutomationProperties.SetName(timelineRange, timelineRange.Text);
        }
    }

    private TimelineData? timelineData;
    private (OpenedRecord For, RecordDetailView View)? detailView;

    /// <summary>한 기록의 타임라인에서 보기 창과 무관한 부분. Warm은 UI 밖에서 불러도 된다 (그 뒤 UI만 읽는다).</summary>
    private sealed class TimelineData(OpenedRecord record, IReadOnlyList<SymptomMark> marks, LinkDownView[] downs, IReadOnlyList<TimelineBand> bands)
    {
        public OpenedRecord For { get; } = record;
        public IReadOnlyList<SymptomMark> Marks { get; } = marks;
        public LinkDownView[] Downs { get; } = downs;
        public IReadOnlyList<TimelineBand> Bands { get; } = bands;
        public RepresentativeCache Reps { get; } = new();

        public static TimelineData Build(OpenedRecord o)
        {
            var r = o.Record;
            var h = r.Header!;
            return new TimelineData(o, SymptomMarks.FromEvents(r.Events, r.NoteLines).Marks,
                o.Analysis.Links.Downs.Select(d => new LinkDownView(d.Adapter, d.Source, d.FromMs, d.ToMs, false)).ToArray(),
                TimelineModel.Bands(r.Rounds, h.Targets, r.Gaps, h.IntervalMs));
        }

        public TimelineData Warm()
        {
            Reps.Update(For.Record.Rounds, For.Record.Header!.Targets);
            return this;
        }
    }

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

    // ---------------- 끊김 줄과 구간 상세 ----------------

    private UIElement EpisodeRow(EpisodeLine line, Episode episode, int index, Func<long, string> label)
    {
        var stack = new StackPanel { Spacing = Space.S1 };
        var top = new StackPanel { Spacing = Space.S0 };
        top.Children.Add(Fonts.Text($"{LiveSummary.When(line.Start, Context.Now)} · {line.Duration}", Typography.Label, Ui.Brushes.Ink));
        top.Children.Add(Fonts.Wrapped(line.Verdicts, Typography.Body, Ui.Brushes.Ink));
        if (line.Candidate is { } c) top.Children.Add(Fonts.Wrapped(c, Typography.Caption, Ui.Brushes.Muted));
        string name = $"끊김 {index + 1}, {LiveSummary.When(line.Start, Context.Now)}, {line.Duration}, {line.Verdicts}" + (line.Candidate is { } cc ? ", " + cc : "");
        var toggle = new RowButton(Ui, $"records.episode.{index}", name, top);
        bool open = expanded == index;
        AutomationProperties.SetItemStatus(toggle, open ? "expanded" : "collapsed");
        toggle.Click += (_, _) =>
        {
            expanded = expanded == index ? -1 : index;
            Rerender();
            if (expanded == index) CenterOn(episode);
            // 펼쳐도 접어도 그 줄로 초점을 돌려준다: 줄은 아래 카드와 함께 다음 프레임에 다시 생긴다 (b6)
            FocusLater($"records.episode.{index}", Mode.Detail);
        };
        stack.Children.Add(toggle);
        if (open && detail is not null) stack.Children.Add(EpisodeDetailPanel(episode, index));
        return stack;
    }

    private UIElement EpisodeDetailPanel(Episode episode, int index)
    {
        var d = EpisodeDetail.For(detail!.Record, detail.Analysis, episode);
        var h = detail.Record.Header!;
        var names = h.Targets.Where(t => t.Enabled).ToDictionary(t => t.Id, t => t.Name);
        var offset = TimeSpan.FromMinutes(h.UtcOffsetMinutes);
        var panel = new StackPanel { Spacing = Space.S2, Margin = new Thickness(Space.S5, 0, 0, Space.S3) };
        if (episode.Parts.Count > 1)
        {
            panel.Children.Add(Fonts.Text("판정이 바뀐 구간", Typography.Label, Ui.Brushes.Ink));
            foreach (var p in RecordView.Parts(episode))
                panel.Children.Add(Fonts.Wrapped($"· {p.Verdict} · {p.Duration} · {p.Candidate}", Typography.Caption, Ui.Brushes.Ink));
        }
        panel.Children.Add(Fonts.Text("주변 회차 (" + string.Join(" | ", names.Values) + ")", Typography.Label, Ui.Brushes.Ink));
        if (d.NothingBefore) panel.Children.Add(Fonts.Text("앞 회차 없음 (측정 처음)", Typography.Caption, Ui.Brushes.Muted));
        foreach (var row in d.Rows)
        {
            string text = row.Round is null ? "관측 공백 (이 사이 관측 없음)"
                : $"{(row.InEpisode ? "▸ " : "  ")}{row.Wall.ToOffset(offset):HH:mm:ss} " + (row.Gap ? "관측 없음" : string.Join(" | ", row.Cells.Select(RecordView.CellText)));
            panel.Children.Add(Fonts.Text(text, Typography.Caption, row.InEpisode ? Ui.Brushes.Ink : Ui.Brushes.Muted));
            if (d.OmitAfter == row.Round && d.OmittedRounds > 0) panel.Children.Add(Fonts.Text($"  … 가운데 {d.OmittedRounds}회차 생략", Typography.Caption, Ui.Brushes.Muted));
        }
        if (d.NothingAfter) panel.Children.Add(Fonts.Text("뒤 회차 없음 (측정 끝)", Typography.Caption, Ui.Brushes.Muted));
        panel.Children.Add(Fonts.Wrapped("시작 시점 경로: " + string.Join(", ", d.Routes.Select(x => $"{names.GetValueOrDefault(x.TargetId, x.TargetId)} {RecordView.RouteText(x)}" + (x.InterfaceIndex is int ix ? $" (인터페이스 {ix})" : ""))), Typography.Caption, Ui.Brushes.Muted));
        panel.Children.Add(Fonts.Wrapped(d.Links.Count == 0 ? "앞뒤 3초 안 링크 끊김 관측 없음"
            : "링크 끊김 관측: " + string.Join(", ", d.Links.Select(l => $"{l.Adapter} {TimelineModel.Seconds(l.ToMs - l.FromMs)}")) + (d.MoreLinks > 0 ? $" 외 {d.MoreLinks}건" : ""), Typography.Caption, Ui.Brushes.Muted));
        foreach (var m in d.Marks)
            panel.Children.Add(Fonts.Wrapped($"증상 표시 {m.Wall.ToOffset(offset):HH:mm:ss}" + (m.Memo is { } memo ? " · " + memo : ""), Typography.Caption, Ui.Brushes.Ink));
        if (d.EvidenceDamaged) panel.Children.Add(Fonts.Wrapped("근거 일부를 읽지 못했습니다.", Typography.Caption, Ui.Brushes.Muted));
        var copy = new GlassButton(Ui, ButtonKind.Ghost, $"records.episode.{index}.copy", "상세 복사", Icons.Card) { HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += async (_, _) => await CopyAsync(RecordView.Copy(h, d));
        panel.Children.Add(copy);
        var group = new GroupPanel($"records.episode.{index}.detail", $"끊김 {index + 1} 상세");   // UI 자동화에 보이는 묶음
        group.Children.Add(panel);
        return group;
    }

    private async Task CopyAsync(string text)
    {
        bool wrote = false, verified = false;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            wrote = true;
            verified = await Clipboard.GetContent().GetTextAsync() == text;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException) { }
        if (Context.IsClosing()) return;
        copyState = verified ? "ok" : wrote ? "unverified" : "failed";
        lastCopy = verified ? text : null;
        if (verified) Context.Toasts.Show(ToastKind.Success, "끊김 상세를 복사했습니다", "주소·대상 이름·메모는 넣지 않았습니다.");
        else if (wrote) Context.Toasts.Show(ToastKind.Warning, "복사했지만 확인하지 못했습니다", "붙여 넣어 내용을 확인해 주세요.");
        else Context.Toasts.Show(ToastKind.Error, "복사하지 못했습니다", "다른 프로그램이 클립보드를 쓰고 있을 수 있습니다. 다시 시도해 주세요.");
        Context.QueueDiag();
    }

    private string? lastCopy;

    // ---------------- 메모 ----------------

    private UIElement MarksPanel(MeasurementRecord r, RecordHeader h, Func<long, string> label)
    {
        var stack = new StackPanel { Spacing = Space.S2 };
        memoStateText.Clear();
        var marks = SymptomMarks.FromEvents(r.Events, r.NoteLines).Marks;
        if (marks.Count == 0)
        {
            stack.Children.Add(Named("records.marks.none", "이 측정에는 증상 표시가 없습니다.", Typography.Body, Ui.Brushes.Muted));
            return stack;
        }
        bool editable = detail?.Lifecycle != RecordLifecycle.Running;
        foreach (var (m, i) in marks.Select((m, i) => (m, i)))
        {
            var row = new StackPanel { Spacing = Space.S1 };
            string when = label(m.AtMs);
            if (editing.Contains(m.Mark))
            {
                // 입력 칸은 다시 그릴 때마다 새로 만든다: 글은 초안에서 (없으면 저장하지 못한 글, 그다음 저장된 메모)
                bool failed = Context.Memos.TryFailedDraft(detailKey!, m.Mark, out var failedText);
                string text = memoDrafts.TryGetValue(m.Mark, out var draft) ? draft : failed ? failedText ?? "" : m.Memo ?? "";
                var box = new GlassTextBox(Ui, $"records.mark.{i}.text", $"{when} 메모", text, "무슨 일이 있었나요?", double.NaN, multiline: false);
                box.Box.MaxLength = SymptomMarks.MaxMemoLength;
                if (failed) box.SetError("저장하지 못했습니다. 다시 저장해 보세요.");
                var mark = m;
                box.Box.TextChanged += (_, _) => memoDrafts[mark.Mark] = box.Text;
                var save = new GlassButton(Ui, ButtonKind.Primary, $"records.mark.{i}.save", "저장", Icons.Check);
                var cancel = new GlassButton(Ui, ButtonKind.Ghost, $"records.mark.{i}.cancel", "취소");
                save.Click += async (_, _) => await SaveMemoAsync(mark, box.Text);
                box.Box.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await SaveMemoAsync(mark, box.Text); } };
                cancel.Click += (_, _) => { editing.Remove(mark.Mark); memoDrafts.Remove(mark.Mark); Rerender(); };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
                buttons.Children.Add(save);
                buttons.Children.Add(cancel);
                row.Children.Add(box);
                row.Children.Add(buttons);
                // 상태 글 자리는 늘 둔다: 저장을 시작하면 다시 그리지 않고 이 글만 바꾼다 (b9: "저장하는 중…"이 화면에 나오지 않았다)
                string? st = memoState.GetValueOrDefault(m.Mark);
                var state = Named($"records.mark.{i}.state", st ?? "", Typography.Caption, Ui.Brushes.Muted);
                state.Visibility = st is null ? Visibility.Collapsed : Visibility.Visible;
                memoStateText[m.Mark] = state;
                row.Children.Add(state);
                // 막 연 칸에만 초점 (다시 그릴 때마다 초점을 가져가면 끊김 줄을 펼칠 때 초점을 빼앗는다)
                if (focusMark == m.Mark)
                {
                    focusMark = null;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => box.Box.Focus(FocusState.Programmatic));
                }
            }
            else
            {
                string text = $"{when} · {m.Memo ?? "메모 없음"}" + (m.Truncated ? " (200자에서 잘림)" : "");
                var line = Named($"records.mark.{i}.memo", text, Typography.Body, m.Memo is null ? Ui.Brushes.Muted : Ui.Brushes.Ink);
                row.Children.Add(line);
                if (editable)
                {
                    var edit = new GlassButton(Ui, ButtonKind.Ghost, $"records.mark.{i}.edit", "메모 고치기", Icons.Pencil) { HorizontalAlignment = HorizontalAlignment.Left };
                    var mark = m;
                    edit.Click += (_, _) => { if (!editing.Contains(mark.Mark)) editing.Add(mark.Mark); focusMark = mark.Mark; Rerender(); };
                    row.Children.Add(edit);
                }
                if (memoState.GetValueOrDefault(m.Mark) is { } st) row.Children.Add(Named($"records.mark.{i}.state", st, Typography.Caption, Ui.Brushes.Muted));
            }
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>상태 글: 값을 남기고, 지금 칸이 있으면 그 글만 바꾼다 (다시 그리지 않음).</summary>
    private void SetMemoState(string mark, string text)
    {
        memoState[mark] = text;
        if (memoStateText.TryGetValue(mark, out var t))
        {
            t.Text = Fonts.KeepAll(text);
            AutomationProperties.SetName(t, text);
            t.Visibility = Visibility.Visible;
        }
        Context.QueueDiag();
    }

    private async Task SaveMemoAsync(SymptomMark mark, string text)
    {
        if (detail is not { } d || detailKey is not { } key) return;
        var item = Context.Records.Items.FirstOrDefault(i => i.Key == key);
        if (item is null) return;
        int req = requestGen;   // 이 상세를 떠나거나 다른 기록을 열면 바뀐다 (b4)
        bool Here() => !Context.IsClosing() && req == requestGen && detailKey == key && mode == Mode.Detail;
        SetMemoState(mark.Mark, "저장하는 중…");
        var result = await Context.Memos.SaveAsync(key, item.Entry.Segments, d.Record.Header!.Id, mark.Mark, mark.AtMs, text);
        if (Context.IsClosing()) return;
        if (!result.Latest) return;   // 더 새 요청이 있다: 오래된 완료는 반영하지 않는다
        if (result.State == EventWriteState.Saved)
        {
            SetMemoState(mark.Mark, result.Truncated ? "저장했습니다 (200자에서 잘라 저장)" : "저장했습니다");
            // 저장하는 동안 더 고친 글이 있으면 칸과 초안을 남긴다
            if (Here() && (!memoDrafts.TryGetValue(mark.Mark, out var now) || now == text)) { editing.Remove(mark.Mark); memoDrafts.Remove(mark.Mark); }
            await Context.Records.RefreshAsync(Context.Host.RunningId);
            if (!Here()) return;
            var reopened = await Context.Records.OpenAsync(key);
            DispatcherQueue.TryEnqueue(() => { if (Here() && reopened is not null) { detail = reopened; Rerender(); } });
        }
        else
        {
            memoState[mark.Mark] = "저장하지 못했습니다";
            if (Here()) Rerender();   // 칸 오류 + 초안 유지 (초안은 memoDrafts에 그대로)
            else Context.Toasts.Show(ToastKind.Error, "메모를 저장하지 못했습니다", "그 기록을 다시 열면 다시 저장할 수 있습니다.");
        }
        Context.QueueDiag();
    }

    // ---------------- 비교 ----------------

    private void RenderCompare()
    {
        content.Children.Add(BackBar());
        if (loadError is { } err)
        {
            content.Children.Add(new Banner(Ui, "records.compare.failed", BannerKind.Error, err));
            return;
        }
        if (comparison is not { } pair)
        {
            var (card, body) = ScreenParts.Card(Ui, "records.compare.loading", "비교할 기록을 여는 중");
            body.Set(GlassCard.Skeleton(Ui), "loading");
            content.Children.Add(card);
            return;
        }
        var c = MeasurementComparison.Compare(new CompareSide(pair.A.Record, pair.A.Analysis, pair.A.Lifecycle), new CompareSide(pair.B.Record, pair.B.Analysis, pair.B.Lifecycle));
        string Name(OpenedRecord o) => o.Record.Header is { } hh ? LiveSummary.When(hh.StartedAt, Context.Now) : "기록";
        string na = "A: " + Name(pair.A), nb = "B: " + Name(pair.B);
        foreach (var (cond, i) in c.Conditions.Select((x, i) => (x, i)))
            content.Children.Add(new Banner(Ui, $"records.compare.condition.{i}", BannerKind.Warn, ConditionText(cond, pair)));

        var measure = new StackPanel { Spacing = Space.S2 };
        measure.Children.Add(CompareLine("records.compare.grade", "등급", Sentences.GradeName(c.Grade.A), Sentences.GradeName(c.Grade.B), c.Grade.Lower, c.Grade.Suppressed, lowerIsWorse: true));
        measure.Children.Add(CompareLine("records.compare.outages", "끊김 수", $"{c.Outages.A}번", $"{c.Outages.B}번", Lower.None, c.Outages.Suppressed));
        measure.Children.Add(CompareLine("records.compare.perhour", "시간당 끊김", c.PerHour.A is double pa ? $"{pa:0.#}번" : "계산 안 함", c.PerHour.B is double pb ? $"{pb:0.#}번" : "계산 안 함", c.PerHour.Lower, c.PerHour.Suppressed));
        string L((long MinMs, bool Open)? x) => x is { } v ? $"{TimelineModel.Seconds(v.MinMs)} 이상" + (v.Open ? " (끝을 모름)" : "") : "없음";
        measure.Children.Add(CompareLine("records.compare.longest", "최장 끊김", L(c.Longest.A), L(c.Longest.B), c.Longest.Lower, c.Longest.Suppressed));
        measure.Children.Add(Fonts.Wrapped("화살표는 표시 임계(시간당 0.5번, 10초, 0.5%p)를 넘은 차이에만 붙습니다. 통계적으로 의미 있는 차이나 회선 개선을 뜻하지 않습니다.", Typography.Caption, Ui.Brushes.Muted));
        var (measureCard, measureBody) = ScreenParts.Card(Ui, "records.compare.measure", "측정 전체", $"{na} · {nb}");
        measureBody.Set(measure, "normal");
        content.Children.Add(measureCard);

        var targets = new StackPanel { Spacing = Space.S3 };
        if (c.Targets.Count == 0) targets.Children.Add(Fonts.Wrapped("공통 대상이 없어 대상별 비교를 하지 않습니다.", Typography.Body, Ui.Brushes.Muted));
        foreach (var (p, i) in c.Targets.Select((p, i) => (p, i)))
        {
            var box = new StackPanel { Spacing = Space.S1 };
            box.Children.Add(Fonts.Text($"{p.A.Target.Name} ({RecordView.MethodText(p.A.Target.Method)})", Typography.Label, Ui.Brushes.Ink));
            string Loss(TargetSide s) => s.LossRate is double rr ? $"{s.Valid}번 중 {s.Lost}번 ({LiveSummary.Percent(rr)})" : "값 없음";
            box.Children.Add(CompareLine($"records.compare.target.{i}.loss", "응답 없음", Loss(p.A), Loss(p.B), p.LossRate, p.LossSuppressed));
            string Run(TargetSide s) => s.Valid == 0 ? "값 없음" : $"{s.MaxConsecutive}번" + (s.LongestRunMinMs is long ms ? $" · {TimelineModel.Seconds(ms)} 이상" : "") + (s.LongestRunOpen ? " (끝을 모름)" : "");
            box.Children.Add(CompareLine($"records.compare.target.{i}.run", "최대 연속 손실", Run(p.A), Run(p.B), p.Consecutive, p.ConsecutiveSuppressed));
            string Ms(double? x) => x is double v ? PathMapModel.Ms(v) : "값 없음";
            box.Children.Add(CompareLine($"records.compare.target.{i}.latency", "평균 · 최대 지연", $"{Ms(p.A.AvgMs)} · {Ms(p.A.MaxMs)}", $"{Ms(p.B.AvgMs)} · {Ms(p.B.MaxMs)}", Lower.None, "값만"));
            box.Children.Add(CompareLine($"records.compare.target.{i}.errors", "검사 오류", $"{p.A.Errors}번", $"{p.B.Errors}번", Lower.None, "값만"));
            targets.Children.Add(box);
        }
        foreach (var u in c.NotCompared)
            targets.Children.Add(Fonts.Wrapped($"비교 안 함: {u.Target.Name} ({(u.InA ? "A" : "B")}에만) — {u.Reason}", Typography.Caption, Ui.Brushes.Muted));
        var (targetCard, targetBody) = ScreenParts.Card(Ui, "records.compare.targets", "대상별");
        targetBody.Set(targets, "normal");
        content.Children.Add(targetCard);
    }

    private static string ConditionText(CompareCondition c, (OpenedRecord A, OpenedRecord B) p)
    {
        var a = p.A.Record.Header!;
        var b = p.B.Record.Header!;
        return c switch
        {
            CompareCondition.IntervalDiffers => $"측정 주기가 달라 ({a.IntervalMs / 1000.0:0.#}초 / {b.IntervalMs / 1000.0:0.#}초) 연속 손실 횟수를 그대로 비교할 수 없습니다.",
            CompareCondition.TimeoutDiffers => $"응답 제한시간이 달라 ({a.TimeoutMs / 1000.0:0.#}초 / {b.TimeoutMs / 1000.0:0.#}초) 손실률의 뜻이 다릅니다.",
            CompareCondition.LengthDiffers => "측정 길이가 달라 끊김 수는 시간당으로 봅니다.",
            CompareCondition.LowCoverage => "관측하지 못한 시간이 많아 비교가 부정확합니다.",
            CompareCondition.Damaged => "한쪽 기록에 읽지 못한 부분이 있습니다.",
            CompareCondition.ExpectedDiffers => $"예상 연결 유형이 다릅니다 ({RecordView.ExpectedText(a.Expected)} / {RecordView.ExpectedText(b.Expected)}).",
            CompareCondition.CompositionDiffers => "대상 구성이 달라 측정 전체 기준이 다릅니다.",
            _ => "공통 대상이 없어 대상별 비교를 하지 않습니다.",
        };
    }

    /// <summary>비교 한 줄: 이름, A 값, B 값, 우열(아이콘 + 글). 색만으로 말하지 않는다.</summary>
    private UIElement CompareLine(string id, string label, string a, string b, Lower lower, string? suppressed, bool lowerIsWorse = false)
    {
        string verdict = lower switch
        {
            Lower.A => lowerIsWorse ? "B가 더 좋음" : "A가 더 적음",
            Lower.B => lowerIsWorse ? "A가 더 좋음" : "B가 더 적음",
            _ => suppressed is { } s && s != "값만" ? "비교 안 함 (" + s + ")" : "",
        };
        var grid = new GroupPanel(id) { ColumnSpacing = Space.S3 };   // UI 자동화에 보이는 줄
        foreach (var w in new[] { 2.0, 3.0, 3.0, 3.0 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Cell(int col, string text, Microsoft.UI.Xaml.Media.Brush brush)
        {
            var t = Fonts.Wrapped(text, Typography.Caption, brush);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        Cell(0, label, Ui.Brushes.Muted);
        Cell(1, a, Ui.Brushes.Ink);
        Cell(2, b, Ui.Brushes.Ink);
        Cell(3, verdict, lower == Lower.None ? Ui.Brushes.Muted : Ui.Brushes.Ink);
        AutomationProperties.SetName(grid, $"{label}, A {a}, B {b}" + (verdict.Length > 0 ? ", " + verdict : ""));
        AutomationProperties.SetItemStatus(grid, lower.ToString());
        return grid;
    }

    // ---------------- 진단 기록 ----------------

    private void RenderDiagnoses()
    {
        var (card, body) = ScreenParts.Card(Ui, "records.diagnoses", "진단 기록", diagnosesLoaded ? $"{diagnoses.Count}개 · 최신순" : null);
        if (!diagnosesLoaded) { body.Set(GlassCard.Skeleton(Ui), "loading"); content.Children.Add(card); return; }
        var stack = new StackPanel { Spacing = Space.S1 };
        if (diagnoses.Count == 0)
            stack.Children.Add(ScreenParts.Message(Ui, "records.diagnoses.empty", Icons.Diagnose, Ui.Brushes.Muted, "아직 진단 기록이 없습니다", "빠른 진단을 돌리면 여기에 쌓입니다."));
        foreach (var (x, i) in diagnoses.Select((x, i) => (x, i)))
        {
            var (head, weight, during) = StoredDiagnosisView.Row(x.Stored);
            var (icon, brush) = DiagnosisLook.Weight(Ui, weight);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
            row.Children.Add(Icons.Make(icon, brush, Size.IconM));
            var texts = new StackPanel();
            texts.Children.Add(Fonts.Text(head, Typography.Label, Ui.Brushes.Ink));
            texts.Children.Add(Fonts.Text(LiveSummary.When(x.Stored.StartedAt, Context.Now) + (during ? " · 측정 중 진단" : ""), Typography.Caption, Ui.Brushes.Muted));
            row.Children.Add(texts);
            var b = new RowButton(Ui, $"records.diagnosis.{i}", $"{head}, {LiveSummary.When(x.Stored.StartedAt, Context.Now)}" + (during ? ", 측정 중 진단" : ""), row);
            var stored = x.Stored;
            b.Click += (_, _) => { openDiagnosis = stored; mode = Mode.Diagnosis; Rerender(); };
            stack.Children.Add(b);
        }
        if (diagnosesSkipped > 0) stack.Children.Add(Named("records.diagnoses.skipped", $"읽지 못한 진단 기록 {diagnosesSkipped}개는 건너뛰었습니다.", Typography.Caption, Ui.Brushes.Muted));
        body.Set(stack, diagnoses.Count == 0 ? "empty" : "normal");
        content.Children.Add(card);
    }

    private void RenderDiagnosis()
    {
        content.Children.Add(BackBar());
        if (openDiagnosis is not { } s) return;
        var v = StoredDiagnosisView.From(s);
        var (icon, brush) = DiagnosisLook.Weight(Ui, v.Weight);
        var stack = new StackPanel { Spacing = Space.S3 };
        var headRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        headRow.Children.Add(Icons.Make(icon, brush, Size.IconL));
        var head = Named("records.diagnosis.head", v.Head, Typography.Headline, Ui.Brushes.Ink);
        head.VerticalAlignment = VerticalAlignment.Center;
        headRow.Children.Add(head);
        stack.Children.Add(headRow);
        stack.Children.Add(Named("records.diagnosis.when", $"{LiveSummary.When(v.StartedAt, Context.Now)}에 검사한 결과" + (v.MeasurementId is not null ? " · 측정 중 진단" : ""), Typography.Caption, Ui.Brushes.Muted));
        if (v.Found is { } found)
        {
            stack.Children.Add(Fonts.Text("그때 결과에 대한 설명", Typography.Label, Ui.Brushes.Ink));
            stack.Children.Add(Named("records.diagnosis.found", found, Typography.Body, Ui.Brushes.Ink));
            for (int i = 0; i < v.Steps.Count; i++) stack.Children.Add(Fonts.Wrapped($"{i + 1}. {v.Steps[i]}", Typography.Body, Ui.Brushes.Ink));
        }
        if (v.Mismatch is { } mm) stack.Children.Add(Named("records.diagnosis.mismatch", mm, Typography.Caption, Ui.Brushes.Muted));
        if (v.Also.Count > 0) stack.Children.Add(Fonts.Wrapped("다른 가능성: " + string.Join(" · ", v.Also), Typography.Caption, Ui.Brushes.Muted));
        stack.Children.Add(Fonts.Wrapped(StoredDiagnosisView.RecheckHint, Typography.Body, Ui.Brushes.Ink));
        var go = new GlassButton(Ui, ButtonKind.Secondary, "records.diagnosis.run", "빠른 진단 열기", Icons.Diagnose) { HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += (_, _) => Context.Navigate("diagnose", null);
        stack.Children.Add(go);
        var card = new GroupPanel("records.diagnosis", "지난 진단");
        card.Children.Add(Ui.Glass.Surface(GlassLevel.Content, stack, Radius.Outer, new Thickness(Space.S6)));
        content.Children.Add(card);
        var checks = new StackPanel { Spacing = Space.S3 };
        foreach (var c in v.Checks)
        {
            var cc = new CheckCard(Ui, c.Id);
            cc.Show(c);
            checks.Children.Add(cc);
        }
        content.Children.Add(checks);
    }

    // ---------------- 진단 값 ----------------

    public override object? Diag()
    {
        var items = Context.Records.Items;
        return new
        {
            mode = mode.ToString(),
            listed = Context.Records.Listed,
            count = items.Count,
            shown = Math.Min(shown, items.Count),
            analyzing = items.Count(i => i.Summary.Analyzing),
            unreadable = items.Count(i => i.Summary.Error is not null),
            picking,
            picked = picked.Count,
            // 열쇠·경로는 내보내지 않는다 (사용자 경로, 설계 3.7): 순번과 측정 ID만
            rows = items.Take(shown).Select((i, n) => new
            {
                n, i.Entry.Id, i.External, analyzing = i.Summary.Analyzing, grade = i.Summary.Grade?.ToString(), outages = i.Summary.Outages, i.Summary.LongestMinMs,
                memo = i.Summary.FirstMemo, life = i.Entry.Lifecycle.ToString(), integrity = i.Entry.Integrity.ToString(), error = i.Summary.Error is not null, damage = i.Damage.Count,
            }).ToArray(),
            detail = detail is { } d && mode == Mode.Detail ? new
            {
                d.Record.Header?.Id, grade = d.Analysis.Grade.Grade.ToString(), sentence = d.Analysis.Grade.Sentence, episodes = d.Analysis.Episodes.Count,
                end = RecordView.EndText(d.Lifecycle, d.Record.Summary?.EndReason ?? d.Record.EndReason), expanded,
                memos = SymptomMarks.FromEvents(d.Record.Events, d.Record.NoteLines).Marks.Select(m => m.Memo).ToArray(),
                window = d.Record.Header is null ? null : view.Window(EndMs(d)).ToString(),
            } : null,
            detailLoading = mode == Mode.Detail && detail is null && loadError is null,
            detailComplete = DetailReady,   // 아래 카드까지 그려짐 (한 프레임 늦게 만든다)
            loadError,
            editing = editing.LastOrDefault(),
            editingCount = editing.Count,
            memoDrafts = memoDrafts.Count,
            timelinePicked = lastPicked,
            memoState = memoState.Values.ToArray(),
            comparison = comparison is { } p && mode == Mode.Compare ? MeasurementComparison.Compare(
                new CompareSide(p.A.Record, p.A.Analysis, p.A.Lifecycle), new CompareSide(p.B.Record, p.B.Analysis, p.B.Lifecycle)) is { } c ? new
                {
                    conditions = c.Conditions.Select(x => x.ToString()).ToArray(), grade = c.Grade.Lower.ToString(), perHour = c.PerHour.Lower.ToString(),
                    longest = c.Longest.Lower.ToString(), targets = c.Targets.Select(t => new { t.A.Target.Id, loss = t.LossRate.ToString(), run = t.Consecutive.ToString() }).ToArray(),
                    notCompared = c.NotCompared.Count,
                } : null : null,
            diagnoses = diagnosesLoaded ? diagnoses.Count : -1,
            diagnosesSkipped,
            diagnosis = openDiagnosis is { } s && mode == Mode.Diagnosis ? new { s.MainConclusion, explained = StoredDiagnosisView.From(s).Found is not null } : null,
            copyState,
            copyText = lastCopy,
            lastAction,
            externalError,
            externalNotices = externalNotices.Count,
            held = Context.Records.HeldCount,
        };
    }
}
