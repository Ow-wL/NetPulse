using System.Net;
using System.Net.Sockets;

namespace NetPulse.Core.Diagnostics;

/// <summary>
/// 진단 기본값 (M9 설계 B4): 외부 TCP 주소 둘과 확인 도메인 둘만 바꾼다. 포털 주소·기대 글·제한시간은 그대로.
/// 진단은 시작할 때 설정 사본을 잡아 끝까지 쓴다 (<c>QuickDiagnosis.RunAsync</c>가 받는 값).
/// </summary>
public static class DiagnosisDefaults
{
    /// <summary>칸 이름 → 오류 글. 비어 있으면 맞다.</summary>
    public static IReadOnlyDictionary<string, string> Validate(IReadOnlyList<string> tcpTargets, IReadOnlyList<string> domains)
    {
        var e = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < tcpTargets.Count; i++)
            if (!IsIpv4(tcpTargets[i])) e[$"tcp.{i}"] = "IPv4 주소를 적어 주세요 (예: 192.0.2.1).";
        if (tcpTargets.Count == 2 && !e.ContainsKey("tcp.1") && !e.ContainsKey("tcp.0") && tcpTargets[0].Trim() == tcpTargets[1].Trim())
            e["tcp.1"] = "두 주소가 같습니다. 서로 다른 사업자의 주소를 적어 주세요.";
        for (int i = 0; i < domains.Count; i++)
            if (!IsHostName(domains[i])) e[$"domain.{i}"] = "도메인 이름을 적어 주세요 (예: www.example.com).";
        if (domains.Count == 2 && !e.ContainsKey("domain.0") && !e.ContainsKey("domain.1") && string.Equals(domains[0].Trim(), domains[1].Trim(), StringComparison.OrdinalIgnoreCase))
            e["domain.1"] = "두 도메인이 같습니다.";
        return e;
    }

    /// <summary>점 넷의 IPv4 (선행 0·축약 형태 없음).</summary>
    public static bool IsIpv4(string text)
    {
        var t = text.Trim();
        return t.Count(c => c == '.') == 3 && IPAddress.TryParse(t, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == t;
    }

    /// <summary>ASCII 호스트 이름: 라벨 1~63자(영숫자·하이픈, 하이픈으로 시작·끝 없음), 전체 253자, 점 하나 이상, 마지막 라벨이 숫자만이 아님.</summary>
    public static bool IsHostName(string text)
    {
        var t = text.Trim().TrimEnd('.');
        if (t.Length is 0 or > 253 || !t.Contains('.')) return false;
        var labels = t.Split('.');
        foreach (var l in labels)
        {
            if (l.Length is 0 or > 63 || l[0] == '-' || l[^1] == '-') return false;
            if (!l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }
        return !labels[^1].All(char.IsAsciiDigit);
    }

    /// <summary>설정 값을 진단 설정으로. 값이 없거나 맞지 않는 쌍은 기본값 (기록에는 칸 이름만).</summary>
    public static (DiagnosisSettings Settings, IReadOnlyList<string> Rejected) From(IReadOnlyList<string>? tcpTargets, IReadOnlyList<string>? domains)
    {
        var s = new DiagnosisSettings();
        var rejected = new List<string>();
        if (tcpTargets is { Count: 2 })
        {
            if (Validate(tcpTargets, []).Count == 0) s = s with { TcpTargets = tcpTargets.Select(t => IPAddress.Parse(t.Trim())).ToArray() };
            else rejected.Add("tcpTargets");
        }
        if (domains is { Count: 2 })
        {
            if (Validate([], domains).Count == 0) s = s with { Domains = domains.Select(d => d.Trim().TrimEnd('.')).ToArray() };
            else rejected.Add("domains");
        }
        return (s, rejected);
    }
}
