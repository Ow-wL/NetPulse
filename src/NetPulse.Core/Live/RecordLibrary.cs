using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;

namespace NetPulse.Core.Live;

/// <summary>목록 한 항목: 출처 열쇠, 카탈로그 항목, 출처 도장, 요약(분석 전이면 Analyzing).</summary>
/// <param name="Key">출처 열쇠: 알려진 위치면 "known:" + 조각 경로 집합, 다른 위치면 "ext:n:" + 조각 경로 집합</param>
/// <param name="Damage">손상 종류별 알림 (분석 뒤)</param>
/// <param name="Status">읽기 상태 (분석 뒤). 비교 고르기 판단에 쓴다</param>
public sealed record LibraryItem(string Key, RecordEntry Entry, bool External, string Stamp, RecentItem Summary,
    IReadOnlyList<string> Damage, RecordStatus? Status, IReadOnlyList<string> Notices);

/// <summary>상세·비교로 연 기록: 원본 + 전체 분석.</summary>
public sealed record OpenedRecord(string Key, string Stamp, MeasurementRecord Record, AnalysisResult Analysis, RecordLifecycle Lifecycle);

/// <summary>
/// 기록 목록과 분석 (M7 설계 3.1, 구현 중 결정 3·6).
/// <list type="bullet">
/// <item>목록은 개수 제한 없이 최신순. 같은 ID라도 출처(조각 경로 집합)가 다르면 다른 항목.</item>
/// <item>출처 도장 = 조각 폴더마다 파일 이름·길이·마지막 쓰기 시각. 바뀐 항목만 다시 분석하고, 분석하는 동안 바뀌면 결과를 버리고 다시.</item>
/// <item>분석은 백그라운드 하나. 우선순위: ① 연 기록(상세·비교) ② 화면에 보이는 줄 ③ 나머지 목록 순서.</item>
/// <item>원본 + 전체 분석 보유는 최대 3개 (핀 = 열린 비교·상세는 내보내지 않음). 목록 분석의 임시 원본은 요약만 남기고 놓는다.</item>
/// <item>측정 중 기록은 분석하지 않는다. 읽지 못한 폴더도 목록에 둔다.</item>
/// <item>갱신은 UI 밖에서, 진행 중이면 합친다 (마지막 요청 하나).</item>
/// </list>
/// </summary>
public sealed class RecordLibrary(Func<IReadOnlyList<string>> roots, bool holdAnalysis = false, bool holdList = false)
{
    public const int HoldLimit = 3;

    private readonly object gate = new();
    private IReadOnlyList<LibraryItem> items = [];
    private readonly Dictionary<string, (string Stamp, RecentItem Summary, IReadOnlyList<string> Damage, RecordStatus? Status)> summaries = [];
    private readonly List<OpenedRecord> held = [];          // 최근 쓴 것이 뒤
    private readonly HashSet<string> pinned = [];
    private readonly HashSet<string> visible = [];
    private readonly List<(string Key, TaskCompletionSource<OpenedRecord?> Done)> opens = [];
    private readonly List<(string Id, IReadOnlyList<string> Segments, IReadOnlyList<string> Notices)> externals = [];
    private string? runningId;
    private Task refresh = Task.CompletedTask;
    private bool refreshAgain, refreshing, workerRunning;

    public IReadOnlyList<LibraryItem> Items { get { lock (gate) return items; } }

    /// <summary>시험: 갱신 루프가 끝내기로 정한 뒤, 작업이 완료되기 전 (그 틈에 온 요청을 재현).</summary>
    internal Action? AfterLastPassForTest { get; set; }

    /// <summary>시험: 작업자가 한 항목(열쇠)의 분석을 시작하기 직전, 잠금 밖 (열기 요청과 목록 분석의 순서를 고정).</summary>
    internal Action<string>? BeforeAnalyzeForTest { get; set; }
    public bool Listed { get; private set; }

    /// <summary>목록·요약이 바뀜 (백그라운드 스레드).</summary>
    public event Action? Changed;

    /// <summary>시험·bench가 보는 지금 보유 수.</summary>
    public int HeldCount { get { lock (gate) return held.Count; } }

    // ---------------- 목록 ----------------

    /// <summary>다시 읽는다. 진행 중인 갱신이 있으면 그 뒤 한 번만 더 (합침).</summary>
    public Task RefreshAsync(string? running)
    {
        if (holdList) return Task.CompletedTask;   // G5 "목록 읽는 중" 장면
        lock (gate)
        {
            runningId = running;
            // 돌고 있는지는 작업의 완료가 아니라 같은 잠금 안의 표시로 본다: 루프가 끝내기로 정한 뒤 작업이 완료되기 전에 온
            // 요청이 합쳐지기만 하고 사라졌다 (G4 M7 1라운드 a4)
            if (refreshing) { refreshAgain = true; return refresh; }
            refreshing = true;
            refreshAgain = false;
            refresh = Task.Run(RefreshLoop);
            return refresh;
        }
    }

    private void RefreshLoop()
    {
        try
        {
            while (true)
            {
                string? running;
                lock (gate) { running = runningId; refreshAgain = false; }
                BuildList(running);
                bool done;
                lock (gate) { done = !refreshAgain; if (done) refreshing = false; }
                if (done) { AfterLastPassForTest?.Invoke(); return; }
            }
        }
        catch
        {
            lock (gate) refreshing = false;
            throw;
        }
    }

    private void BuildList(string? running)
    {
        IReadOnlyList<RecordEntry> entries;
        try { entries = RecordCatalog.List(roots(), int.MaxValue, running); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { entries = []; }
        List<(string Id, IReadOnlyList<string> Segments, IReadOnlyList<string> Notices)> ext;
        lock (gate) ext = [.. externals];
        var list = new List<LibraryItem>();
        for (int i = 0; i < ext.Count; i++)
        {
            var e = RecordCatalog.EntryFor(ext[i].Id, ext[i].Segments, running);
            list.Add(Make($"ext:{i}:" + string.Join("|", ext[i].Segments), e, true, ext[i].Notices));
        }
        foreach (var e in entries) list.Add(Make("known:" + string.Join("|", e.Segments.Order(StringComparer.OrdinalIgnoreCase)), e, false, []));
        lock (gate)
        {
            items = list;
            Listed = true;
            // 도장이 바뀐 보유분은 놓는다 (다시 열면 다시 읽음). 핀은 그대로 두되 다음 열기에서 새로 읽는다
            held.RemoveAll(h => !pinned.Contains(h.Key) && list.FirstOrDefault(x => x.Key == h.Key) is { } x && x.Stamp != h.Stamp);
        }
        Changed?.Invoke();
        Kick();
    }

    private LibraryItem Make(string key, RecordEntry e, bool external, IReadOnlyList<string> notices)
    {
        string stamp = Stamp(e.Segments);
        lock (gate)
        {
            if (summaries.TryGetValue(key, out var s) && s.Stamp == stamp)
                return new LibraryItem(key, e, external, stamp, s.Summary with { Entry = e }, s.Damage, s.Status, notices);
        }
        return new LibraryItem(key, e, external, stamp, Pending(e), [], null, notices);
    }

    /// <summary>출처 도장: 조각 폴더마다 파일 이름·길이·마지막 쓰기 시각 (구현 중 결정 3·6).</summary>
    public static string Stamp(IEnumerable<string> segments)
    {
        var parts = new List<string>();
        foreach (var folder in segments.Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    parts.Add($"{folder}/{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { parts.Add(folder + ":unreadable"); }
        }
        return string.Join(";", parts);
    }

    /// <summary>화면에 보이는 줄 (분석 우선순위 ②).</summary>
    public void SetVisible(IEnumerable<string> keys)
    {
        lock (gate)
        {
            visible.Clear();
            visible.UnionWith(keys);
        }
        Kick();
    }

    /// <summary>그 항목들의 분석이 끝날 때까지 (분석을 멈춘 실행이면 바로). 목록에서 사라진 열쇠는 기다리지 않는다.</summary>
    public async Task AnalyzedAsync(IReadOnlyCollection<string> keys)
    {
        if (holdAnalysis) return;
        while (true)
        {
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() => wait.TrySetResult();
            Changed += OnChanged;
            try
            {
                if (!Items.Any(i => keys.Contains(i.Key) && i.Summary.Analyzing)) return;
                await Task.WhenAny(wait.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            }
            finally { Changed -= OnChanged; }
        }
    }

    // ---------------- 열기 ----------------

    /// <summary>상세·비교로 연다 (우선순위 ①). 측정 중·없는 항목이면 null. 같은 도장으로 보유 중이면 바로.</summary>
    public Task<OpenedRecord?> OpenAsync(string key)
    {
        lock (gate)
        {
            var item = items.FirstOrDefault(i => i.Key == key);
            if (item is null || item.Entry.Lifecycle == RecordLifecycle.Running) return Task.FromResult<OpenedRecord?>(null);
            var have = held.FirstOrDefault(h => h.Key == key && h.Stamp == item.Stamp);
            if (have is not null) { Touch(have); return Task.FromResult<OpenedRecord?>(have); }
            var done = new TaskCompletionSource<OpenedRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
            opens.Add((key, done));
            Kick();
            return done.Task;
        }
    }

    /// <summary>
    /// 방금 끝난 측정의 원본·분석을 측정 호스트와 같은 인스턴스로 보유한다 (다시 읽지 않음, 구현 중 결정 3).
    /// 그 기록의 목록 항목이 있으면 그 열쇠·도장으로.
    /// </summary>
    public bool Share(string measurementId, MeasurementRecord record, AnalysisResult analysis)
    {
        lock (gate)
        {
            var item = items.FirstOrDefault(i => !i.External && i.Entry.Id == measurementId);
            if (item is null || item.Entry.Lifecycle == RecordLifecycle.Running) return false;
            Hold(new OpenedRecord(item.Key, item.Stamp, record, analysis, item.Entry.Lifecycle));
            // 요약도 이 결과로 채운다 (목록 분석이 같은 기록을 다시 읽지 않게)
            var (summary, damage) = Summarize(item.Entry, record, analysis);
            summaries[item.Key] = (item.Stamp, summary, damage, record.Status);
            var copy = items.ToArray();
            int at = Array.FindIndex(copy, i => i.Key == item.Key);
            copy[at] = copy[at] with { Summary = summary, Damage = damage, Status = record.Status };
            items = copy;
        }
        Changed?.Invoke();
        return true;
    }

    private static (RecentItem Summary, IReadOnlyList<string> Damage) Summarize(RecordEntry e, MeasurementRecord record, AnalysisResult a)
    {
        var marks = SymptomMarks.FromEvents(record.Events, record.NoteLines);
        var memo = marks.Marks.Select(m => m.Memo).FirstOrDefault(m => m is not null);
        long? duration = record.Summary?.EndMs ?? record.Rounds.LastOrDefault()?.AtMs;
        var damage = RecordView.DamageNotices(record, marks.Ignored);
        string? notice = e.Lifecycle == RecordLifecycle.NoSummary ? "끝 기록 없음" : damage.Count > 0 ? "읽지 못한 부분 있음" : null;
        return (new RecentItem(e, false, LiveSummary.Badge(a.Grade.Grade), a.Episodes.Count,
            a.Episodes.Count == 0 ? null : a.Episodes.Max(x => x.MinDurationMs), PathMapModel.ConnectionKind(record.Events), memo, duration, notice, null,
            a.Grade.Sentence, a.Grade.Grade), damage);
    }

    /// <summary>핀: 열린 비교·상세는 보유 상한에서 내보내지 않는다.</summary>
    public void SetPinned(IEnumerable<string> keys)
    {
        lock (gate)
        {
            pinned.Clear();
            pinned.UnionWith(keys);
            Trim();
        }
    }

    /// <summary>bench: 핀이 아닌 보유분을 모두 놓는다 ("캐시 없는 열기·비교" 구간을 그 조건에서 시작하려고, 설계 5절).</summary>
    public void DropUnpinned()
    {
        lock (gate) held.RemoveAll(h => !pinned.Contains(h.Key));
    }

    /// <summary>알려진 위치에서 그 측정 ID의 항목 열쇠 (측정 화면 결과 카드 → 상세). 없으면 null.</summary>
    public string? KeyOf(string measurementId)
    {
        lock (gate) return items.FirstOrDefault(i => !i.External && i.Entry.Id == measurementId)?.Key;
    }

    /// <summary>다른 위치의 기록을 이번 실행 동안 목록 맨 위에 둔다. 돌려주는 열쇠로 연다.</summary>
    public (string? Key, string? Error, IReadOnlyList<string> Notices) AddExternal(string folder)
    {
        var found = RecordLocator.FindExternal(folder, roots());
        if (found.Error is not null || found.Id is null) return (null, found.Error, []);
        int index;
        lock (gate)
        {
            index = externals.FindIndex(x => x.Segments.SequenceEqual(found.Segments, StringComparer.OrdinalIgnoreCase));
            if (index < 0) { externals.Add((found.Id, found.Segments, found.Notices)); index = externals.Count - 1; }
        }
        return ($"ext:{index}:" + string.Join("|", found.Segments), null, found.Notices);
    }

    /// <summary>이번 실행에서 연 다른 위치의 조각 폴더 (폴더 열기 허용 범위: 정확히 이 폴더들).</summary>
    public IReadOnlyList<string> ExternalFolders { get { lock (gate) return externals.SelectMany(x => x.Segments).ToArray(); } }

    // ---------------- 분석 작업 ----------------

    private void Kick()
    {
        lock (gate)
        {
            if (workerRunning || holdAnalysis) return;
            workerRunning = true;
        }
        _ = Task.Run(Work);
    }

    private void Work()
    {
        while (true)
        {
            string key;
            TaskCompletionSource<OpenedRecord?>? open = null;
            LibraryItem? item;
            lock (gate)
            {
                if (opens.Count > 0)
                {
                    (key, open) = opens[0];
                    opens.RemoveAt(0);
                    string k = key;
                    item = items.FirstOrDefault(i => i.Key == k);
                }
                else
                {
                    item = items.Where(i => i.Summary.Analyzing).OrderBy(i => visible.Contains(i.Key) ? 0 : 1).FirstOrDefault();
                    if (item is null) { workerRunning = false; return; }
                    key = item.Key;
                }
            }
            if (item is null) { open?.TrySetResult(null); continue; }

            BeforeAnalyzeForTest?.Invoke(item.Key);
            var (opened, summary, damage, status) = Analyze(item);
            bool stable = Stamp(item.Entry.Segments) == item.Stamp;
            lock (gate)
            {
                if (stable)
                {
                    summaries[item.Key] = (item.Stamp, summary, damage, status);
                    var copy = items.ToArray();
                    int at = Array.FindIndex(copy, i => i.Key == item.Key && i.Stamp == item.Stamp);
                    if (at >= 0) copy[at] = copy[at] with { Summary = summary, Damage = damage, Status = status };
                    items = copy;
                    if (open is not null && opened is not null) Hold(opened);
                }
            }
            if (open is not null)
            {
                if (stable) open.TrySetResult(opened);
                else lock (gate) opens.Insert(0, (item.Key, open));   // 읽는 동안 바뀜: 버리고 다시 (다음 목록 갱신이 새 도장을 준다)
            }
            Changed?.Invoke();
            if (!stable) Task.Run(() => RefreshAsync(runningId)).Wait();
        }
    }

    private static (OpenedRecord? Opened, RecentItem Summary, IReadOnlyList<string> Damage, RecordStatus? Status) Analyze(LibraryItem item)
    {
        var e = item.Entry;
        try
        {
            var record = RecordReader.Read(e.Segments);
            if (record.Header is null) return (null, Failed(e, record.Problem), [], record.Status);
            var a = MeasurementAnalysis.Analyze(record);
            var (summary, damage) = Summarize(e, record, a);
            return (new OpenedRecord(item.Key, item.Stamp, record, a, e.Lifecycle), summary, damage, record.Status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return (null, Failed(e, null), [], RecordStatus.Unreadable);
        }
    }

    private static RecentItem Failed(RecordEntry e, string? problem) =>
        new(e, false, null, null, null, null, null, null, problem, "기록을 읽지 못했습니다");

    private static RecentItem Pending(RecordEntry e) => e.Lifecycle == RecordLifecycle.Running || e.Integrity == RecordIntegrity.Unreadable
        ? new RecentItem(e, false, null, null, null, null, null, null, null, e.Integrity == RecordIntegrity.Unreadable ? "기록을 읽지 못했습니다" : null)
        : new RecentItem(e, true, null, null, null, null, null, null, null, null);

    /// <summary>gate 안.</summary>
    private void Hold(OpenedRecord r)
    {
        held.RemoveAll(h => h.Key == r.Key);
        held.Add(r);
        Trim();
    }

    /// <summary>gate 안. 핀이 아닌 가장 오래 쓰지 않은 것부터 내보낸다.</summary>
    private void Trim()
    {
        while (held.Count > HoldLimit)
        {
            int i = held.FindIndex(h => !pinned.Contains(h.Key));
            if (i < 0) break;
            held.RemoveAt(i);
        }
    }

    /// <summary>gate 안.</summary>
    private void Touch(OpenedRecord r)
    {
        held.Remove(r);
        held.Add(r);
    }
}
