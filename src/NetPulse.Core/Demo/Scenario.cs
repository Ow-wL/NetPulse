using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetPulse.Core.Measurement;

namespace NetPulse.Core.Demo;

/// <summary>
/// 데모 시나리오 (M1 설계 8절 재생 계약).
/// - 회차는 0부터, 구간은 from 이상 to 미만.
/// - 겹치면 뒤에 적힌 구간이 이긴다. target "*"는 모든 대상.
/// - 어느 구간에도 들지 않으면 측정 오류("scenario-undefined").
/// - 재생 범위: durationRounds가 있으면 0~durationRounds-1, 없으면 구간 to의 최댓값 미만.
/// - 흔들림은 (시드, 대상 id, 회차)로 정해지는 독립 난수. 호출 순서와 무관하다.
/// - 주소는 문서용 범위(192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24)만 허용한다.
/// </summary>
public sealed class Scenario
{
    public required int Version { get; init; }
    public required string Name { get; init; }
    public required long Seed { get; init; }
    public long? DurationRounds { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public required IReadOnlyList<ScenarioTarget> Targets { get; init; }
    public required IReadOnlyList<ScenarioSegment> Segments { get; init; }

    /// <summary>재생할 회차 수 (0 이상 이 값 미만).</summary>
    public long RoundCount => DurationRounds ?? Segments.Max(s => s.To);

    public IReadOnlyList<ProbeTarget> ProbeTargets =>
        Targets.Select(t => new ProbeTarget(t.Id, t.Role, IPAddress.Parse(t.Address))).ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Scenario Parse(string json)
    {
        Scenario? scenario;
        try { scenario = JsonSerializer.Deserialize<Scenario>(json, JsonOptions); }
        catch (JsonException ex) { throw new ScenarioException("시나리오 JSON을 읽을 수 없습니다: " + ex.Message, ex); }
        if (scenario is null) throw new ScenarioException("시나리오가 비어 있습니다.");
        scenario.Validate();
        return scenario;
    }

    public static Scenario Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    private void Validate()
    {
        if (Version != 1) throw new ScenarioException($"지원하지 않는 시나리오 버전입니다: {Version}");
        if (string.IsNullOrWhiteSpace(Name)) throw new ScenarioException("이름이 없습니다.");
        if (Targets.Count == 0) throw new ScenarioException("대상이 없습니다.");
        if (Segments.Count == 0) throw new ScenarioException("구간이 없습니다.");
        if (DurationRounds is <= 0) throw new ScenarioException("durationRounds는 1 이상이어야 합니다.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Id) || target.Id == "*") throw new ScenarioException("대상 id가 비었거나 '*'입니다.");
            if (!ids.Add(target.Id)) throw new ScenarioException($"대상 id가 겹칩니다: {target.Id}");
            if (target.Role is not ("local" or "external")) throw new ScenarioException($"대상 역할은 local 또는 external이어야 합니다: {target.Id}");
            if (!IPAddress.TryParse(target.Address, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ScenarioException($"IPv4 주소가 아닙니다: {target.Id}");
            if (!IsDocumentationAddress(address)) throw new ScenarioException($"데모에는 문서용 주소만 쓸 수 있습니다: {target.Id}");
        }
        foreach (var (segment, i) in Segments.Select((s, i) => (s, i)))
        {
            if (segment.From < 0 || segment.To <= segment.From) throw new ScenarioException($"구간 {i}: from은 0 이상, to는 from보다 커야 합니다.");
            if (segment.Target != "*" && !ids.Contains(segment.Target)) throw new ScenarioException($"구간 {i}: 없는 대상입니다: {segment.Target}");
            bool hasRtt = segment.Rtt is not null, hasResult = segment.Result is not null;
            if (hasRtt == hasResult) throw new ScenarioException($"구간 {i}: rtt와 result 중 하나만 적어야 합니다.");
            if (hasResult && segment.Result is not ("lost" or "error")) throw new ScenarioException($"구간 {i}: result는 lost 또는 error입니다.");
            if (segment.Rtt is { } rtt && (rtt.Base < 0 || rtt.Jitter < 0)) throw new ScenarioException($"구간 {i}: rtt 값은 0 이상이어야 합니다.");
        }
    }

    internal static bool IsDocumentationAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (b[0] == 192 && b[1] == 0 && b[2] == 2) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}

public sealed class ScenarioTarget
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    public required string Address { get; init; }
}

public sealed class ScenarioSegment
{
    public required long From { get; init; }
    public required long To { get; init; }
    public required string Target { get; init; }
    public ScenarioRtt? Rtt { get; init; }
    public string? Result { get; init; }
}

public sealed class ScenarioRtt
{
    public required double Base { get; init; }
    public double Jitter { get; init; }
}

public sealed class ScenarioException(string message, Exception? inner = null) : Exception(message, inner);
