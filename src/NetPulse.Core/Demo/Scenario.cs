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

    /// <summary>선택 (M4): 링크가 내려간 회차 구간 (from 이상 to 미만). 데모 경로·링크 관측이 쓴다.</summary>
    public IReadOnlyList<ScenarioLink>? Links { get; init; }

    /// <summary>선택 (M5): 데모 탐지가 볼 연결. 없으면 유선, 가상 아님, 다른 게이트웨이 0.</summary>
    public ScenarioNetwork? Network { get; init; }

    /// <summary>선택 (M5): 입력 경계의 고장 주입 (탐지 늦음·실패, 저장 실패, 절전 공백).</summary>
    public ScenarioFaults? Faults { get; init; }

    /// <summary>선택 (M6): 데모 진단 검사기의 덮어쓰기 (M6 설계 3.2). 없는 필드는 시간축 규칙을 따른다.</summary>
    public ScenarioDiagnosis? Diagnosis { get; init; }

    /// <summary>그 회차에 데모 링크가 내려가 있는지.</summary>
    public bool LinkDownAt(long round) => Links?.Any(l => round >= l.From && round < l.To) ?? false;

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
        foreach (var (link, i) in (Links ?? []).Select((l, i) => (l, i)))
            if (link.From < 0 || link.To <= link.From) throw new ScenarioException($"링크 {i}: from은 0 이상, to는 from보다 커야 합니다.");
        foreach (var target in Targets)
        {
            if (target.Name is { } n && (n.Trim().Length == 0 || n.Length > 40)) throw new ScenarioException($"대상 이름은 1~40자여야 합니다: {target.Id}");
            if (target.Provider is { } p && (p.Trim().Length == 0 || p.Length > 40)) throw new ScenarioException($"사업자 이름은 1~40자여야 합니다: {target.Id}");
        }
        if (Network is { } net)
        {
            if (net.Kind is not ("wired" or "wireless" or "none")) throw new ScenarioException("network.kind는 wired, wireless, none 중 하나입니다.");
            if (net.OtherGateways < 0) throw new ScenarioException("network.otherGateways는 0 이상입니다.");
        }
        if (Faults is { } f)
        {
            if (f.DetectDelayMs is < 0) throw new ScenarioException("faults.detectDelayMs는 0 이상입니다.");
            if (f.Detect is not (null or "unknown")) throw new ScenarioException("faults.detect는 unknown만 쓸 수 있습니다.");
            if (f.StorageFailFrom is < 0) throw new ScenarioException("faults.storageFailFrom은 0 이상입니다.");
            if (f.StorageRecoverAt is { } r && (f.StorageFailFrom is not { } from || r <= from)) throw new ScenarioException("faults.storageRecoverAt은 storageFailFrom보다 커야 합니다.");
            if ((f.SuspendAt is null) != (f.SuspendSeconds is null)) throw new ScenarioException("faults.suspendAt과 suspendSeconds는 함께 적어야 합니다.");
            if (f.SuspendAt is < 1 || f.SuspendSeconds is < 1) throw new ScenarioException("faults.suspendAt과 suspendSeconds는 1 이상입니다.");
        }
        if (Diagnosis is { } d) d.Validate();
    }

    /// <summary>데모에 쓸 수 있는 도메인 (문서용): *.example, *.test, example.com·net·org와 그 하위 (M6 설계 3.2).</summary>
    public static bool IsDocumentationDomain(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.EndsWith(".example", StringComparison.Ordinal) || h.EndsWith(".test", StringComparison.Ordinal)
            || h is "example.com" or "example.net" or "example.org"
            || h.EndsWith(".example.com", StringComparison.Ordinal) || h.EndsWith(".example.net", StringComparison.Ordinal) || h.EndsWith(".example.org", StringComparison.Ordinal);
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
    /// <summary>선택 (M5): 화면에 보일 이름. 없으면 id.</summary>
    public string? Name { get; init; }
    /// <summary>선택 (M5): 사업자 (외부 대상 다양성 판단). 없으면 비움.</summary>
    public string? Provider { get; init; }
}

/// <summary>데모 탐지가 볼 연결 (M5).</summary>
public sealed class ScenarioNetwork
{
    public string Kind { get; init; } = "wired";
    public bool Virtual { get; init; }
    public int OtherGateways { get; init; }
}

/// <summary>입력 경계의 고장 주입 (M5 설계 3.4). 집계·저장 로직은 제품 코드 그대로 탄다.</summary>
public sealed class ScenarioFaults
{
    /// <summary>탐지가 이만큼 늦게 끝난다 (실제 ms).</summary>
    public int? DetectDelayMs { get; init; }
    /// <summary>"unknown"이면 탐지 실패 (경로 조회 실패).</summary>
    public string? Detect { get; init; }
    /// <summary>두 저장 위치 모두 측정을 시작하지 못한다.</summary>
    public bool StorageStartFail { get; init; }
    /// <summary>이 회차부터 기록 쓰기가 실패한다 (StorageRecoverAt 회차에서 회복).</summary>
    public long? StorageFailFrom { get; init; }
    public long? StorageRecoverAt { get; init; }
    /// <summary>이 회차에서 데모 절전 알림을 보내고 측정 시간을 SuspendSeconds만큼 건너뛴다.</summary>
    public long? SuspendAt { get; init; }
    public int? SuspendSeconds { get; init; }
}

public sealed class ScenarioSegment
{
    public required long From { get; init; }
    public required long To { get; init; }
    public required string Target { get; init; }
    public ScenarioRtt? Rtt { get; init; }
    public string? Result { get; init; }
}

/// <summary>링크 다운 구간 (M4 데모).</summary>
public sealed class ScenarioLink
{
    public required long From { get; init; }
    public required long To { get; init; }
}

public sealed class ScenarioRtt
{
    public required double Base { get; init; }
    public double Jitter { get; init; }
}

public sealed class ScenarioException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>데모 진단 덮어쓰기 (M6 설계 3.2). 결과 값은 ok, fail, error, timeout.</summary>
public sealed class ScenarioDiagnosis
{
    public ScenarioDiagAdapter? Adapter { get; init; }
    public string? Gateway { get; init; }
    public IReadOnlyList<string>? Tcp { get; init; }
    public IReadOnlyList<string>? Dns { get; init; }
    public IReadOnlyList<string>? Https { get; init; }
    public ScenarioDiagPortal? Portal { get; init; }
    public ScenarioDiagLink? Link { get; init; }
    /// <summary>사이트 점검 도메인 → 결과 (문서용 도메인만).</summary>
    public IReadOnlyDictionary<string, ScenarioDiagSite>? Site { get; init; }
    /// <summary>DNS 캐시 비우기의 데모 결과 (ok, fail, timeout). 없으면 ok.</summary>
    public string? FlushDns { get; init; }
    /// <summary>검사별 지연 ms (adapter, gateway, tcp, dns, https, portal, link, site, flushDns): 진행 장면용.</summary>
    public IReadOnlyDictionary<string, int>? DelayMs { get; init; }

    internal static readonly string[] Outcomes = ["ok", "fail", "error", "timeout"];
    private static readonly string[] DelayKeys = ["adapter", "gateway", "tcp", "dns", "https", "portal", "link", "site", "flushDns"];

    internal void Validate()
    {
        void Outcome(string? v, string where)
        {
            if (v is not null && !Outcomes.Contains(v)) throw new ScenarioException($"diagnosis.{where}: ok, fail, error, timeout 중 하나입니다.");
        }
        void Pair(IReadOnlyList<string>? v, string where)
        {
            if (v is null) return;
            if (v.Count is < 1 or > 2) throw new ScenarioException($"diagnosis.{where}: 대상 1~2개입니다.");
            foreach (var x in v) Outcome(x, where);
        }
        Outcome(Gateway, "gateway");
        Pair(Tcp, "tcp");
        Pair(Dns, "dns");
        Pair(Https, "https");
        if (FlushDns is not (null or "ok" or "fail" or "timeout")) throw new ScenarioException("diagnosis.flushDns: ok, fail, timeout 중 하나입니다.");
        if (Portal is { } p && (p.Status is { } st && (st < 100 || st > 599))) throw new ScenarioException("diagnosis.portal.status: 100~599입니다.");
        if (Portal is { } p2) Outcome(p2.Outcome, "portal.outcome");
        if (Link is { } l)
        {
            if (l.Now is not (null or "up" or "down" or "unknown")) throw new ScenarioException("diagnosis.link.now: up, down, unknown 중 하나입니다.");
            if (l.Log is not (null or "supported" or "unsupported" or "denied")) throw new ScenarioException("diagnosis.link.log: supported, unsupported, denied 중 하나입니다.");
        }
        foreach (var (domain, site) in Site ?? new Dictionary<string, ScenarioDiagSite>())
        {
            if (!Scenario.IsDocumentationDomain(domain)) throw new ScenarioException($"diagnosis.site: 데모에는 문서용 도메인만 쓸 수 있습니다: {domain}");
            Outcome(site.Dns, "site.dns");
            Outcome(site.Https, "site.https");
        }
        foreach (var (key, ms) in DelayMs ?? new Dictionary<string, int>())
        {
            if (!DelayKeys.Contains(key)) throw new ScenarioException($"diagnosis.delayMs: 모르는 검사입니다: {key}");
            if (ms is < 0 or > 30_000) throw new ScenarioException($"diagnosis.delayMs.{key}: 0~30000입니다.");
        }
    }
}

public sealed class ScenarioDiagAdapter
{
    public bool Up { get; init; } = true;
    public bool Ipv4 { get; init; } = true;
    public bool Apipa { get; init; }
    public bool Gateway { get; init; } = true;
    public bool Dns { get; init; } = true;
    /// <summary>false면 경로를 못 찾음 (활성 어댑터 수는 Up이면 1, 아니면 0).</summary>
    public bool Route { get; init; } = true;
}

public sealed class ScenarioDiagPortal
{
    /// <summary>요청 결과 (ok면 Status·Body로 판정). 기본 ok.</summary>
    public string? Outcome { get; init; }
    public int? Status { get; init; } = 200;
    public bool Body { get; init; } = true;
}

public sealed class ScenarioDiagLink
{
    public string? Now { get; init; }
    public string? Log { get; init; }
    public bool RecentDown { get; init; }
}

public sealed class ScenarioDiagSite
{
    public string? Dns { get; init; }
    public string? Https { get; init; }
}
