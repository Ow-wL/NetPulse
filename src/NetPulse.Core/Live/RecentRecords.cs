using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;

namespace NetPulse.Core.Live;

/// <summary>
/// 최근 측정 한 줄 (M5 설계 3.3, 홈). 분석은 백그라운드에서 하고, 끝나기 전에는 <see cref="Analyzing"/>.
/// </summary>
/// <param name="Notice">읽기 중 누락·손상 안내 (RecordReader의 누락 안내와 손상 수)</param>
public sealed record RecentItem(RecordEntry Entry, bool Analyzing, GradeBadge? Badge, int? Outages, long? LongestMinMs, string? ConnectionKind,
    string? FirstMemo, long? DurationMs, string? Notice, string? Error, string? Sentence = null, Grade? Grade = null);

/// <summary>
/// 최근 측정 목록과 분석 캐시 (M5 설계 3.3). 분석 결과는 파일에 저장하지 않고 앱 실행 동안 (측정 ID, 수명, 끝 사유)로 캐시한다.
/// </summary>
public sealed class RecentRecords(Func<IReadOnlyList<string>> roots, int max = 3, bool holdAnalysis = false)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Id, RecordLifecycle Life, EndReason? End, long Notes), RecentItem> cache = [];
    private IReadOnlyList<RecentItem> items = [];
    private int generation;

    public IReadOnlyList<RecentItem> Items { get { lock (gate) return items; } }
    public bool Listed { get; private set; }

    /// <summary>목록이나 분석이 바뀜 (백그라운드 스레드).</summary>
    public event Action? Changed;

    /// <summary>다시 읽는다. 지금 도는 측정은 분석하지 않는다.</summary>
    public Task RefreshAsync(string? runningId) => Task.Run(() =>
    {
        int gen;
        lock (gate) gen = ++generation;
        IReadOnlyList<RecordEntry> entries;
        try { entries = RecordCatalog.List(roots(), max, runningId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { entries = []; }
        var list = entries.Select(e => cache.GetValueOrDefault(Key(e)) ?? Pending(e)).ToArray();
        lock (gate) { if (gen != generation) return; items = list; Listed = true; }
        Changed?.Invoke();
        if (holdAnalysis) return;
        for (int i = 0; i < list.Length; i++)
        {
            if (!list[i].Analyzing) continue;
            var done = Analyze(list[i].Entry);
            lock (gate)
            {
                if (gen != generation) return;
                cache[Key(list[i].Entry)] = done;
                var copy = items.ToArray();
                copy[i] = done;
                items = copy;
            }
            Changed?.Invoke();
        }
    });

    /// <summary>
    /// 캐시 열쇠. 끝난 뒤 메모(노트)가 바뀌면 다시 읽도록 노트 파일의 마지막 쓰기 시각도 넣는다 (G4 M5 1라운드 A7).
    /// </summary>
    private static (string, RecordLifecycle, EndReason?, long) Key(RecordEntry e) => (e.Id, e.Lifecycle, e.EndReason, NotesStamp(e));

    private static long NotesStamp(RecordEntry e)
    {
        long stamp = 0;
        foreach (var folder in e.Segments)
        {
            try
            {
                var f = new FileInfo(Path.Combine(folder, RecordNotes.FileName));
                if (f.Exists) stamp = Math.Max(stamp, f.LastWriteTimeUtc.Ticks ^ f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return stamp;
    }

    private static RecentItem Pending(RecordEntry e) => e.Lifecycle == RecordLifecycle.Running || e.Integrity == RecordIntegrity.Unreadable
        ? new RecentItem(e, false, null, null, null, null, null, null, null, e.Integrity == RecordIntegrity.Unreadable ? "기록을 읽지 못했습니다" : null)
        : new RecentItem(e, true, null, null, null, null, null, null, null, null);

    private static RecentItem Analyze(RecordEntry e)
    {
        try
        {
            var record = RecordReader.Read(e.Segments);
            if (record.Header is null) return new RecentItem(e, false, null, null, null, null, null, null, record.Problem, "기록을 읽지 못했습니다");
            var a = MeasurementAnalysis.Analyze(record);
            var memo = SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks.Select(m => m.Memo).FirstOrDefault(m => m is not null);
            long? duration = record.Summary?.EndMs ?? record.Rounds.LastOrDefault()?.AtMs;
            string? notice = record.MissingNotice;
            if (record.Damage.Total > 0) notice = (notice is null ? "" : notice + " · ") + $"손상된 줄 {record.Damage.Total}개";
            if (e.Lifecycle == RecordLifecycle.NoSummary) notice = (notice is null ? "" : notice + " · ") + "끝 기록 없음";
            return new RecentItem(e, false, LiveSummary.Badge(a.Grade.Grade), a.Episodes.Count,
                a.Episodes.Count == 0 ? null : a.Episodes.Max(x => x.MinDurationMs), PathMapModel.ConnectionKind(record.Events), memo, duration, notice, null,
                a.Grade.Sentence, a.Grade.Grade);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new RecentItem(e, false, null, null, null, null, null, null, null, "기록을 읽지 못했습니다");
        }
    }
}
