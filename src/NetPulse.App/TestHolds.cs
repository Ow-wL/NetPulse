namespace NetPulse.App;

/// <summary>
/// 시험 전용 멈춤 지점 (M10 설계 D3 [1-8]). <c>--memo-hold n</c>, <c>--records-apply-hold</c>가 켜고, 데모 + 명시한 사용자 폴더에서만 받는다.
/// 멈춘 곳은 진단 값 <c>holds</c>(이름마다 들어감·풀림 횟수)에 보이고, 시험이 숨은 단추 <c>diag.release</c>로 모두 푼다.
/// 창을 닫을 때도 모두 푼다 (멈춘 채 종료해도 작업이 끝나게).
/// <list type="bullet">
/// <item><c>memo</c>: n번째 메모 쓰기가 <b>쓰기를 마친 뒤 결과를 화면에 알리기 직전</b>.</item>
/// <item><c>records.apply</c>: 첫 기록 상세 열기가 <b>읽기를 마치고 작업 표를 놓은 뒤 화면 반영 직전</b> (옮기기 독점이 이 열기를 기다리지 않는다).</item>
/// </list>
/// </summary>
public static class TestHolds
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, (int Entered, int Released)> counts = new(StringComparer.Ordinal);
    private static readonly List<(string Name, TaskCompletionSource Release)> waiting = [];
    private static int memoHoldAt, memoSaves;
    private static bool recordsApplyOnce;

    public static bool Active { get; private set; }

    public static event Action? Changed;

    public static void Configure(StartupOptions options)
    {
        if (!options.TestInjectionAllowed) return;
        if (options.MemoHold is { } m) { memoHoldAt = int.Parse(m, System.Globalization.CultureInfo.InvariantCulture); Active = true; }
        if (options.RecordsApplyHold) { recordsApplyOnce = true; Active = true; }
    }

    /// <summary>메모 쓰기 완료 뒤 (화면 반영 전).</summary>
    public static Task MemoAsync()
    {
        if (memoHoldAt == 0) return Task.CompletedTask;
        lock (gate) { if (++memoSaves != memoHoldAt) return Task.CompletedTask; }
        return Enter("memo");
    }

    /// <summary>기록 상세 열기 결과의 화면 반영 직전 (한 번만).</summary>
    public static Task RecordsApplyAsync()
    {
        lock (gate) { if (!recordsApplyOnce) return Task.CompletedTask; recordsApplyOnce = false; }
        return Enter("records.apply");
    }

    private static Task Enter(string name)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            var c = counts.GetValueOrDefault(name);
            counts[name] = (c.Entered + 1, c.Released);
            waiting.Add((name, release));
        }
        Changed?.Invoke();
        return release.Task;
    }

    /// <summary>멈춘 것을 모두 푼다 (시험 단추, 창 닫기).</summary>
    public static void ReleaseAll()
    {
        List<(string Name, TaskCompletionSource Release)> list;
        lock (gate)
        {
            list = [.. waiting];
            waiting.Clear();
            foreach (var (name, _) in list) { var c = counts[name]; counts[name] = (c.Entered, c.Released + 1); }
        }
        foreach (var (_, r) in list) r.TrySetResult();
        if (list.Count > 0) Changed?.Invoke();
    }

    public static object Diag()
    {
        lock (gate) return counts.ToDictionary(kv => kv.Key, kv => new { entered = kv.Value.Entered, released = kv.Value.Released }, StringComparer.Ordinal);
    }
}
