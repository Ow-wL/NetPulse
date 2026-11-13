using System.Net;

namespace NetPulse.Core.Measurement;

/// <summary>측정 대상. M1은 최소한만 둔다. 이름·순서·프로필은 M3에서 더한다.</summary>
/// <param name="Id">대상 식별자 (시나리오·기록에서 쓰는 이름)</param>
/// <param name="Role">"local" 또는 "external" (기획서 6.2 끊김 판정의 구분에 쓰인다)</param>
/// <param name="Address">IPv4 주소</param>
public sealed record ProbeTarget(string Id, string Role, IPAddress Address);

public enum ProbeOutcome
{
    /// <summary>응답을 받았다.</summary>
    Success,

    /// <summary>요청을 보냈지만 응답이 없었다.</summary>
    Lost,

    /// <summary>측정 자체를 하지 못했다. 손실과 구별하며 손실률 분모에서 뺀다 (기존 앱 판단 규칙).</summary>
    Error,
}

/// <param name="Outcome">결과 종류</param>
/// <param name="RttMs">성공일 때만 왕복 시간</param>
/// <param name="Status">원래 상태 문자열 (예: "Success", "TimedOut", "scenario-undefined")</param>
public readonly record struct ProbeResult(ProbeOutcome Outcome, double? RttMs, string Status)
{
    public static ProbeResult Success(double rttMs) => new(ProbeOutcome.Success, rttMs, "Success");

    public static ProbeResult Lost(string status) => new(ProbeOutcome.Lost, null, status);

    public static ProbeResult Error(string status) => new(ProbeOutcome.Error, null, status);
}

/// <summary>
/// 대상 하나를 한 번 잰다. 실제 구현(ICMP)과 데모 구현(시나리오 재생)이 같은 자리에 들어간다 (기획서 4.3).
/// 회차 번호를 받는 이유: 데모가 회차와 대상만으로 같은 결과를 내도록 (호출 순서와 무관).
/// </summary>
public interface IProbe
{
    Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken);
}
