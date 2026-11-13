using NetPulse.Core.Measurement;

namespace NetPulse.Core.Records;

/// <summary>대상 하나의 회차 결과. 지연은 성공일 때만, 사유는 검사 오류일 때만 있다.</summary>
public sealed record TargetResult(string TargetId, ProbeOutcome Outcome, double? RttMs, string? Error);

/// <summary>
/// 회차 하나 (M3 설계 6.2의 rounds.jsonl 한 줄). 경과 시간(ms)은 단조 시계, Wall은 표시용 UTC.
/// Gap이면 이 회차는 관측 없음(절전 경계에 걸침)으로 다룬다.
/// </summary>
/// <param name="EndMs">측정 공급자 결과를 다 받은 경과 ms (M4 설계 A7). 옛 기록에는 없다(null): 읽는 쪽이 추정한다.</param>
public sealed record RoundData(long Round, long DueMs, long AtMs, DateTimeOffset Wall, long Skipped, bool Gap, IReadOnlyList<TargetResult> Results, long? EndMs = null);

/// <summary>회차 시점에 관측한 대상별 경로 (M3 설계 6.4).</summary>
public sealed record RouteSample(string TargetId, int? InterfaceIndex, string? Kind, bool Ok, string? Error);

public enum GapReason { Suspend, Stall }

/// <summary>
/// 관측 공백 (M3 설계 7절): 시간 경계와 직전·직후 회차. 회차 번호가 이어져 있어도 공백일 수 있다.
/// </summary>
public sealed record GapSpan(GapReason Reason, long FromMs, long ToMs, DateTimeOffset FromWall, DateTimeOffset ToWall, long? LastRoundBefore, long? FirstRoundAfter);
