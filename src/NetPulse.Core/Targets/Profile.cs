using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NetPulse.Core.Measurement;

namespace NetPulse.Core.Targets;

/// <summary>대상의 역할 (M3 설계 3.1). 판정(M4)이 로컬 장비와 외부를 구별하는 데 쓴다.</summary>
public enum TargetRole { Local, External }

public enum ProbeMethodKind { Icmp, Tcp }

/// <summary>측정 방식. TCP는 포트가 있어야 한다.</summary>
public sealed record ProbeMethod(ProbeMethodKind Kind, int Port = 0)
{
    public static readonly ProbeMethod Icmp = new(ProbeMethodKind.Icmp);
    public static ProbeMethod Tcp(int port) => new(ProbeMethodKind.Tcp, port);
    public override string ToString() => Kind == ProbeMethodKind.Icmp ? "icmp" : $"tcp:{Port}";
}

/// <summary>측정 대상 하나 (M3 설계 3.1). 사용자 순서는 실제 경로를 증명하지 않는다.</summary>
public sealed record Target(string Id, string Name, IPAddress Address, TargetRole Role, string? Provider, ProbeMethod Method, bool Enabled = true)
{
    public ProbeTarget ToProbeTarget() => new(Id, Role == TargetRole.Local ? "local" : "external", Address);
}

public enum ExpectedConnection { Any, Wired, Wireless }

/// <summary>측정 프로필 (M3 설계 3.2). 주기는 1초 고정.</summary>
public sealed record Profile(string Id, string Name, IReadOnlyList<Target> Targets, TimeSpan Timeout, TimeSpan Duration, ExpectedConnection Expected)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(200), MaxTimeout = TimeSpan.FromMilliseconds(900);
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1), MaxDuration = TimeSpan.FromMinutes(720);
    public const int MaxTargets = 8;

    public IReadOnlyList<Target> ActiveTargets => Targets.Where(t => t.Enabled).ToArray();
}

public enum IssueSeverity { Error, Warning }

public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message);

/// <summary>프로필 검증 (M3 설계 3.2). 오류가 있으면 저장·시작을 거부하고, 경고는 시작하되 알린다.</summary>
public static partial class ProfileValidator
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,32}$")]
    private static partial Regex IdPattern();

    public static IReadOnlyList<ValidationIssue> Validate(Profile p)
    {
        var issues = new List<ValidationIssue>();
        void Error(string code, string message) => issues.Add(new(IssueSeverity.Error, code, message));
        void Warn(string code, string message) => issues.Add(new(IssueSeverity.Warning, code, message));

        var active = p.ActiveTargets;
        if (active.Count == 0) Error("no-targets", "측정할 대상이 없습니다.");
        if (active.Count > Profile.MaxTargets) Error("too-many-targets", $"켠 대상은 {Profile.MaxTargets}개까지입니다.");
        foreach (var t in p.Targets)
        {
            if (!IdPattern().IsMatch(t.Id)) Error("bad-id", $"대상 Id 형식이 틀렸습니다: {t.Id}");
            if (t.Address.AddressFamily != AddressFamily.InterNetwork) Error("not-ipv4", $"{t.Name}: IPv4 주소만 측정합니다.");
            if (t.Method.Kind == ProbeMethodKind.Tcp && t.Method.Port is < 1 or > 65535) Error("bad-port", $"{t.Name}: TCP 포트가 틀렸습니다.");
            if (string.IsNullOrWhiteSpace(t.Name)) Error("no-name", $"{t.Id}: 이름이 없습니다.");
        }
        foreach (var dup in p.Targets.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Error("duplicate-id", $"대상 Id가 겹칩니다: {dup.Key}");
        if (p.Timeout < Profile.MinTimeout || p.Timeout > Profile.MaxTimeout)
            Error("timeout-range", $"응답 제한시간은 {Profile.MinTimeout.TotalMilliseconds}~{Profile.MaxTimeout.TotalMilliseconds} ms입니다.");
        if (p.Duration < Profile.MinDuration || p.Duration > Profile.MaxDuration)
            Error("duration-range", $"측정 시간은 {Profile.MinDuration.TotalMinutes}~{Profile.MaxDuration.TotalMinutes}분입니다.");

        var external = active.Where(t => t.Role == TargetRole.External).ToArray();
        var providers = external.Select(t => t.Provider).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (external.Length < 2 || providers < 2)
            Warn("external-diversity", "서로 다른 사업자의 외부 대상이 둘 이상 있어야 '외부 연결 단절'을 판단할 수 있습니다.");
        if (!active.Any(t => t.Role == TargetRole.Local))
            Warn("no-local", "로컬 장비(공유기 등) 대상이 없어 문제 구간을 좁히기 어렵습니다.");
        return issues;
    }

    public static bool HasErrors(IEnumerable<ValidationIssue> issues) => issues.Any(i => i.Severity == IssueSeverity.Error);
}
