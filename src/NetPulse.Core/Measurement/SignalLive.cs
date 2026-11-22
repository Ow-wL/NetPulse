using NetPulse.Core.Evidence;

namespace NetPulse.Core.Measurement;

/// <summary>
/// 어댑터 하나의 지금까지 신호 관측 (M12 설계 C [1-4] 실시간 사본). 반복 표본은 기록에 줄로 쓰지 않으므로
/// 지금 묶음의 마지막 표본(<see cref="SignalRun.LastMs"/>)은 여기서만 안다. 화면은 이벤트가 아니라 이것으로 마지막 표본까지 그린다
/// (<see cref="LinkLive"/>와 같은 방식).
/// </summary>
/// <param name="Alias">링크 근거와 같은 별칭 (같은 어댑터 Key면 같은 별칭)</param>
/// <param name="Closed">닫힌 묶음 (시간 순, 바뀌지 않는 사본)</param>
/// <param name="Current">지금 묶음 (관측이 끝났으면 null)</param>
/// <param name="Method">마지막 읽기 방법 ("realtime"·"rssi")</param>
/// <param name="Code">마지막 읽기의 사유 코드</param>
/// <param name="EndedMs">관측 종료 시각 (어댑터가 사라짐). 관측 중이면 null</param>
public sealed record SignalAdapterLive(string Alias, int InterfaceIndex, string? Kind, IReadOnlyList<SignalRun> Closed, SignalRun? Current,
    string? Method, string? Code, long? EndedMs)
{
    /// <summary>닫힌 묶음 + 지금 묶음 (그리기용).</summary>
    public IEnumerable<SignalRun> Runs => Current is { } c ? Closed.Append(c) : Closed;

    /// <summary>지금(<paramref name="atMs"/>)까지의 통계. 관측이 끝났으면 그 종료 시각에서 자른다 (끝 요약과 같은 함수, [2-3]).</summary>
    public SignalStats StatsAt(long atMs, long intervalMs) => SignalStatistics.Compute(Runs, intervalMs, EndedMs is long e ? Math.Min(e, atMs) : atMs);
}

/// <summary>
/// 신호 관측의 지금 상태 (M12 설계 C). <see cref="SummaryWritten"/>이면 끝 요약 줄이 이미 이벤트에 있다.
/// </summary>
/// <param name="Configured">신호 공급자가 있음 (없으면 무선 어댑터가 있어도 읽지 않았다)</param>
/// <param name="IntervalMs">표본 하나가 대표하는 시간 (측정 간격)</param>
public sealed record SignalLive(bool Configured, long IntervalMs, IReadOnlyList<SignalAdapterLive> Adapters, bool SummaryWritten)
{
    public static readonly SignalLive None = new(false, 0, [], false);

    public SignalAdapterLive? For(string alias) => Adapters.FirstOrDefault(a => a.Alias == alias);
}
