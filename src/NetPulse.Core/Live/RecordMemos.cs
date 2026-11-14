using NetPulse.Core.Measurement;
using NetPulse.Core.Records;

namespace NetPulse.Core.Live;

/// <summary>메모 저장 요청 하나의 결과. Latest가 거짓이면 그 사이 더 새 요청이 있어 화면에 반영하지 않는다.</summary>
public sealed record MemoSaveResult(EventWriteState State, bool Latest, bool Truncated, string? Text);

/// <summary>
/// 기록 화면의 메모 고치기 (M7 설계 3.5 [1-10], 구현 중 결정 4).
/// <list type="bullet">
/// <item>쓰기는 측정 화면과 <b>같은</b> <see cref="NotesWriter"/> 인스턴스 (앱에 하나인 직렬 작업, 1초·3초 재시도 뒤 유실).</item>
/// <item>(기록 출처, 표시)별 세대 번호: 완료가 와도 더 새 요청이 있으면 Latest = 거짓.</item>
/// <item>저장되면 <see cref="Saved"/>로 알린다: 측정 호스트가 같은 표시의 옛 초안을 버려 종료 때 덮어쓰지 않게.</item>
/// <item>화면을 떠난 뒤 실패한 글은 앱 실행 동안 보관해, 상세로 돌아오면 칸 오류 + 다시 저장.</item>
/// </list>
/// </summary>
public sealed class RecordMemos(NotesWriter writer, Func<DateTimeOffset> now)
{
    private readonly object gate = new();
    private long counter;
    private readonly Dictionary<(string Key, string Mark), long> latest = [];
    private readonly Dictionary<(string Key, string Mark), string?> failed = [];

    /// <summary>저장됨 (측정 ID, 표시). 백그라운드 스레드.</summary>
    public event Action<string, string>? Saved;

    /// <summary>
    /// 그 기록의 노트 폴더: 요약이 있는 조각 (없으면 마지막 조각). 측정 호스트와 같은 규칙.
    /// </summary>
    public static string? NotesFolder(IReadOnlyList<string> segments) =>
        segments.FirstOrDefault(f => File.Exists(Path.Combine(f, "summary.json"))) ?? segments.LastOrDefault();

    /// <param name="key">기록 출처 열쇠 (<see cref="RecordLibrary"/>)</param>
    public async Task<MemoSaveResult> SaveAsync(string key, IReadOnlyList<string> segments, string measurementId, string mark, long markAtMs, string? memo)
    {
        var (text, truncated) = SymptomMarks.Clean(memo);
        long generation;
        lock (gate)
        {
            generation = ++counter;
            latest[(key, mark)] = generation;
        }
        var folder = NotesFolder(segments);
        var state = folder is null ? EventWriteState.Lost
            : await writer.WriteAsync(folder, measurementId, mark, markAtMs, now(), text).ConfigureAwait(false);
        bool isLatest;
        lock (gate)
        {
            isLatest = latest.GetValueOrDefault((key, mark)) == generation;
            if (isLatest)
            {
                if (state == EventWriteState.Saved) failed.Remove((key, mark));
                else failed[(key, mark)] = memo;
            }
        }
        if (state == EventWriteState.Saved) Saved?.Invoke(measurementId, mark);
        return new MemoSaveResult(state, isLatest, truncated, text);
    }

    /// <summary>저장하지 못해 남겨 둔 글 (없으면 null, 빈 글로 지우려다 실패했으면 빈 문자열).</summary>
    public bool TryFailedDraft(string key, string mark, out string? text)
    {
        lock (gate) return failed.TryGetValue((key, mark), out text);
    }

    public void ForgetFailed(string key, string mark)
    {
        lock (gate) failed.Remove((key, mark));
    }
}
