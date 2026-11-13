using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace NetPulse.Core.Diagnostics;

/// <summary>사이트 점검 결과 (F2, M4 설계 C3). DNS 한 줄, HTTPS 한 줄.</summary>
public sealed record SiteCheckResult(string Domain, CheckResult Dns, CheckResult Https);

/// <summary>사이트 점검 (F2). 입력 검증과 두 검사.</summary>
public static partial class SiteCheck
{
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex Label();

    /// <summary>
    /// 도메인 검증: 점 하나 이상, 레이블 규칙(영문·숫자·하이픈, 앞뒤 하이픈 없음, 63자), 전체 253자.
    /// IP 주소·스킴·경로·포트는 거절. 국제 도메인은 IDN(퓨니코드)으로 바꾼 뒤 검사한다.
    /// </summary>
    public static (string? Ascii, string? Error) Validate(string input)
    {
        var text = (input ?? "").Trim().TrimEnd('.');
        if (text.Length == 0) return (null, "empty");
        if (text.Contains("://", StringComparison.Ordinal) || text.Contains('/') || text.Contains('?') || text.Contains('#')) return (null, "not-domain");
        if (text.Contains(':')) return (null, "port-or-ipv6");
        if (IPAddress.TryParse(text, out _)) return (null, "ip-address");
        string ascii;
        if (text.All(char.IsAscii)) ascii = text.ToLowerInvariant();   // ASCII는 아래 레이블 규칙으로 검사한다
        else
        {
            try { ascii = new IdnMapping().GetAscii(text).ToLowerInvariant(); }
            catch (ArgumentException) { return (null, "invalid-idn"); }
        }
        if (ascii.Length > 253) return (null, "too-long");
        var labels = ascii.Split('.');
        if (labels.Length < 2) return (null, "no-dot");
        if (labels.Any(l => l.Length == 0 || l.Length > 63 || !Label().IsMatch(l))) return (null, "bad-label");
        if (labels[^1].All(char.IsDigit)) return (null, "numeric-tld");
        return (ascii, null);
    }

    public static async Task<SiteCheckResult> RunAsync(IDiagnosticProbes probes, string domain, DiagnosisSettings settings, CancellationToken ct = default)
    {
        var (ascii, error) = Validate(domain);
        if (ascii is null) throw new ArgumentException("도메인 형식이 아닙니다: " + error, nameof(domain));
        var dns = QuickDiagnosis.Pair(CheckId.Dns, settings.DnsTimeout, [c => probes.DnsAsync(ascii, c)], ct);
        var https = QuickDiagnosis.Pair(CheckId.Https, settings.HttpsTimeout, [c => probes.HttpsAsync(ascii, c)], ct);
        return new SiteCheckResult(ascii, await dns.ConfigureAwait(false), await https.ConfigureAwait(false));
    }
}
