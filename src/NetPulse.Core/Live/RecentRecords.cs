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
/// 최근 측정 (홈, M5 설계 3.3). M7부터는 <see cref="RecordLibrary"/>의 앞 몇 개를 그대로 쓴다 (같은 캐시, 분석을 두 번 하지 않음).
/// 다른 위치에서 연 기록은 넣지 않는다.
/// </summary>
public sealed class RecentRecords
{
    private readonly int max;

    public RecentRecords(Func<IReadOnlyList<string>> roots, int max = 3, bool holdAnalysis = false) : this(new RecordLibrary(roots, holdAnalysis), max) { }

    public RecentRecords(RecordLibrary library, int max = 3)
    {
        Library = library;
        this.max = max;
        library.Changed += () => Changed?.Invoke();
    }

    public RecordLibrary Library { get; }

    public IReadOnlyList<RecentItem> Items => Entries.Select(i => i.Summary).ToArray();

    /// <summary>앞 몇 개의 목록 항목 (열쇠로 그 기록 상세를 연다).</summary>
    public IReadOnlyList<LibraryItem> Entries => Library.Items.Where(i => !i.External).Take(max).ToArray();
    public bool Listed => Library.Listed;

    /// <summary>목록이나 분석이 바뀜 (백그라운드 스레드).</summary>
    public event Action? Changed;

    /// <summary>다시 읽고, 앞 몇 개의 분석이 끝날 때까지 기다린다 (분석을 멈춘 실행이면 목록만).</summary>
    public async Task RefreshAsync(string? runningId)
    {
        await Library.RefreshAsync(runningId).ConfigureAwait(false);
        var keys = Library.Items.Where(i => !i.External).Take(max).Select(i => i.Key).ToArray();
        Library.SetVisible(keys);
        await Library.AnalyzedAsync(keys).ConfigureAwait(false);
    }
}
