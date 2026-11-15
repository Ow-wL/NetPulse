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
/// <item>쓰기를 넣기 <b>전에</b> <see cref="Saving"/>으로 알린다: 측정 호스트가 같은 기록 폴더·표시의 옛 초안을 버리고 세대를 올려,
/// 이미 직렬 작업에 들어간 옛 초안 쓰기도 실행 때 건너뛴다 (G4 M7 1라운드 a1: 저장이 끝나기 전 창을 닫으면 옛 초안이 새 글을 덮었다).</item>
/// <item>알림에는 노트 폴더가 함께 간다: 같은 ID의 다른 위치 복사본을 고쳐도 원본의 초안은 건드리지 않는다 (a2).</item>
/// <item>화면을 떠난 뒤 실패한 글은 앱 실행 동안 보관해, 상세로 돌아오면 칸 오류 + 다시 저장.</item>
/// </list>
/// </summary>
public sealed class RecordMemos(NotesWriter writer, Func<DateTimeOffset> now)
{
    private readonly object gate = new();
    private long counter;
    private readonly Dictionary<(string Key, string Mark), long> latest = [];
    private readonly Dictionary<(string Key, string Mark), string?> failed = [];

    /// <summary>쓰기를 넣기 직전 (노트 폴더, 측정 ID, 표시). 백그라운드 스레드. 처리기가 끝난 뒤에 쓰기를 넣는다.</summary>
    public event Action<string, string, string>? Saving;

    /// <summary>저장됨 (노트 폴더, 측정 ID, 표시). 백그라운드 스레드.</summary>
    public event Action<string, string, string>? Saved;

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
        EventWriteState state;
        if (folder is null) state = EventWriteState.Lost;
        else
        {
            // 처리기는 기록 폴더를 찾느라 디스크를 읽을 수 있어 UI 밖에서 부른다. 끝난 뒤에 쓰기를 넣어야 세대 순서가 맞다
            if (Saving is { } saving) await Task.Run(() => saving(folder, measurementId, mark)).ConfigureAwait(false);
            state = await writer.WriteAsync(folder, measurementId, mark, markAtMs, now(), text).ConfigureAwait(false);
        }
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
        if (state == EventWriteState.Saved) Saved?.Invoke(folder!, measurementId, mark);
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
