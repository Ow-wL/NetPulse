using System.Globalization;

namespace NetPulse.Core.Diagnostics;

/// <summary>결론의 무게 (색·아이콘을 고르는 데 쓴다. 색만으로 구별하지 않는다).</summary>
public enum ConclusionWeight { Problem, Caution, Info, Normal, Neutral }

/// <summary>
/// 진단 화면 문구 (M6 설계 3.6). 결론 머리말은 관측을 말하고 단정하지 않는다.
/// 사유 코드는 엔진·검사기가 낼 수 있는 것을 쉬운 말로 바꾸고, 모르는 코드는 "기타 사유 (코드)".
/// </summary>
public static class DiagnosisTexts
{
    public static (string Head, ConclusionWeight Weight) Head(int conclusion) => conclusion switch
    {
        1 => ("어댑터 링크가 내려가 있음", ConclusionWeight.Problem),
        2 => ("웹은 되지만 IPv4 직접 연결이 안 됨", ConclusionWeight.Caution),
        3 => ("이름 해석(DNS)이 되지 않음", ConclusionWeight.Caution),
        4 => ("로그인·프록시에 막혔을 수 있음", ConclusionWeight.Caution),
        5 => ("이 PC의 연결에 문제가 있을 수 있음", ConclusionWeight.Problem),
        6 => ("인터넷 쪽에서 응답이 없음", ConclusionWeight.Problem),
        7 => ("웹(HTTPS) 연결 확인이 필요함", ConclusionWeight.Caution),
        8 => ("최근 링크 끊김 기록이 있음", ConclusionWeight.Info),
        9 => ("인터넷은 되지만 예외가 관측됨", ConclusionWeight.Info),
        10 => ("일부만 성공", ConclusionWeight.Info),
        11 => ("기본 연결 정상", ConclusionWeight.Normal),
        _ => ("진단 근거 부족", ConclusionWeight.Neutral),
    };

    /// <summary>검사가 무엇을 보는지 (처음 화면·펼친 상세).</summary>
    public static string What(CheckId id) => id switch
    {
        CheckId.Adapter => "인터넷으로 나가는 어댑터와 주소·게이트웨이·DNS 설정",
        CheckId.Gateway => "기본 게이트웨이(공유기)에 Ping 한 번",
        CheckId.Tcp => "외부 주소 두 곳에 TCP 443 연결",
        CheckId.Dns => "도메인 두 개의 이름 해석",
        CheckId.Https => "같은 두 도메인에 HTTPS 요청",
        CheckId.Portal => "공용 Wi‑Fi 로그인·프록시가 가로채는지",
        _ => "지금 링크 상태와 최근 30분 링크 기록",
    };

    /// <summary>검사 카드의 한 줄 설명 (판정과 사유로).</summary>
    public static string Explain(CheckResult c)
    {
        if (c.State == RunState.NotRun) return "검사하지 않았습니다: " + Reason(c.Code);
        if (c.State == RunState.Timeout && c.Targets.Count == 0) return "제한시간 안에 답이 없었습니다.";
        if (c.State == RunState.Error && c.Targets.Count == 0) return "검사하지 못했습니다: " + Reason(c.Code);
        int ok = c.Targets.Count(t => t.Outcome == TargetOutcome.Ok);
        return (c.Id, c.Judgment) switch
        {
            (CheckId.Adapter, CheckJudgment.Normal) => c.Facts.GetValueOrDefault("dns") == "none"
                ? "어댑터가 켜져 있고 주소·게이트웨이는 있지만 DNS 서버 설정이 없습니다."   // 판정은 그대로, 설명은 관측 사실대로 (G4 1라운드 a4)
                : "어댑터가 켜져 있고 주소·게이트웨이·DNS 설정이 있습니다.",
            (CheckId.Adapter, _) => Reason(c.Code) + ".",
            (CheckId.Gateway, CheckJudgment.Normal) => "공유기가 Ping에 답했습니다.",
            (CheckId.Gateway, CheckJudgment.Check) => "공유기가 Ping에 답하지 않았습니다 (Ping을 막는 장비일 수 있음).",
            (CheckId.Portal, CheckJudgment.Normal) => "가로채는 로그인 페이지가 없습니다.",
            (CheckId.Portal, CheckJudgment.Check) => "예상과 다른 응답을 받았습니다 (로그인 페이지·프록시일 수 있음).",
            (CheckId.Link, CheckJudgment.Normal) => "링크가 올라가 있고 최근 30분 끊김 기록이 없습니다.",
            (CheckId.Link, CheckJudgment.Check) => "최근 30분 안에 링크가 끊긴 기록이 있습니다.",
            (CheckId.Link, CheckJudgment.Fail) => "링크가 지금 내려가 있습니다.",
            (CheckId.Link, _) => Reason(c.Code) + ".",
            (_, CheckJudgment.Normal) => $"대상 {c.Targets.Count}개 모두 성공했습니다.",
            (_, CheckJudgment.Check) => $"대상 {c.Targets.Count}개 중 {ok}개만 성공했습니다.",
            (_, CheckJudgment.Fail) => c.PartlyUnconfirmed ? "실패했습니다 (확인하지 못한 대상이 있음)." : "모든 대상이 실패했습니다.",
            _ => "확인하지 못했습니다.",
        };
    }

    /// <summary>
    /// 관측 범위 줄 (설계 3.6 [1-3]): 결론과 무관하게, 핵심 검사와 게이트웨이의 일부 성공·미확인·무응답 단서를 그대로 보인다.
    /// 모두 정상인 검사는 줄을 만들지 않는다.
    /// </summary>
    public static IReadOnlyList<string> Scope(IReadOnlyList<CheckResult> checks)
    {
        var lines = new List<string>();
        foreach (var c in checks.Where(c => c.Id is CheckId.Adapter or CheckId.Tcp or CheckId.Dns or CheckId.Https or CheckId.Gateway))
        {
            string name = QuickDiagnosis.CheckName(c.Id);
            if (c.Id == CheckId.Gateway)
            {
                if (c.Judgment == CheckJudgment.Check) lines.Add("게이트웨이: 응답 없음 (Ping을 막는 장비일 수 있음)");
                else if (c.Judgment == CheckJudgment.Unmeasured) lines.Add("게이트웨이: 확인하지 못함");
                continue;
            }
            if (c.Targets.Count > 0)
            {
                int ok = c.Targets.Count(t => t.Outcome == TargetOutcome.Ok);
                int unconfirmed = c.Targets.Count(t => t.Outcome is TargetOutcome.Timeout or TargetOutcome.Error);
                if (c.Judgment == CheckJudgment.Check) lines.Add($"{name}: 대상 {c.Targets.Count}개 중 {ok}개 성공 (일부만 성공)");
                else if (c.Judgment == CheckJudgment.Fail && unconfirmed > 0) lines.Add($"{name}: 실패, 확인하지 못한 대상 {unconfirmed}개");
                else if (c.Judgment == CheckJudgment.Unmeasured) lines.Add($"{name}: 확인하지 못함");
            }
            else if (c.Judgment == CheckJudgment.Unmeasured) lines.Add($"{name}: 확인하지 못함");
        }
        return lines;
    }

    /// <summary>사유 코드 → 쉬운 말. 정해진 코드만 해석하고, 모르는 코드는 그대로 괄호에 둔다.</summary>
    public static string Reason(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "사유 없음";
        switch (code)
        {
            case "apipa": return "자동 할당 주소(169.254)만 있습니다";
            case "no-ipv4": return "IPv4 주소가 없습니다";
            case "no-gateway": return "기본 게이트웨이가 없습니다";
            case "no-route": return "인터넷 쪽 경로를 찾지 못했습니다";
            case "adapter-down": return "어댑터가 내려가 있습니다";
            case "interface-not-found": return "경로의 어댑터를 찾지 못했습니다";
            case "adapter-unmeasured": return "어댑터 검사가 끝나지 않아 건너뛰었습니다";
            case "timeout": return "제한시간 안에 답이 없었습니다";
            case "cancelled": return "취소했습니다";
            case "not-run": return "실행하지 않았습니다";
            case "now-unknown": return "지금 링크 상태를 읽지 못했습니다";
            case "log-timeout": return "링크 기록 조회가 제한시간을 넘었습니다";
            case "log-unsupported": return "이 어댑터는 링크 기록을 남기지 않습니다";
            case "log-denied": return "링크 기록을 읽을 권한이 없습니다";
            case "no-address": return "이름에 해당하는 주소가 없습니다";
            case "tls": return "보안 연결(TLS)을 맺지 못했습니다";
            case "dns": return "이름을 찾지 못했습니다";
            case "TimedOut": return "응답 없음 (시간 초과)";
            case "demo-fail": return "응답 없음 (데모)";
            case "demo-error": return "검사 오류 (데모)";
            case "scenario-undefined": return "데모 시나리오에 정의되지 않은 시각입니다";
            case "scenario-error": return "검사 오류 (데모)";
            case "ProbeError": return "검사 중 오류";
            case "access-denied": return "읽을 권한이 없습니다";
            case "not-found": return "어댑터를 찾지 못했습니다";
            case "not-allowed": return "허용된 폴더가 아닙니다";
            case "missing": return "폴더가 없습니다";
            case "missing-tool": return "필요한 Windows 명령을 찾지 못했습니다";
            case "start-failed": return "명령을 시작하지 못했습니다";
            case "log-not-found": return "링크 기록을 찾지 못했습니다";
            case "no-known-driver": return "이 어댑터의 링크 기록 형식을 알지 못합니다";
            case "wlan-log-unavailable": return "무선 연결 기록을 읽을 수 없습니다";
            case "lookback-truncated": return "링크 기록이 앞부분만 남아 있습니다";
        }
        foreach (var (prefix, text) in new[] { ("GetIfEntry2_", "어댑터 상태를 읽지 못했습니다"), ("GetBestRoute_", "경로를 조회하지 못했습니다"),
            ("GetBestInterface_", "경로의 어댑터를 조회하지 못했습니다"), ("NotifyIpInterfaceChange_", "링크 변화 알림을 받을 수 없습니다") })
            if (code.StartsWith(prefix, StringComparison.Ordinal)) return $"{text} (오류 {code[prefix.Length..]})";
        if (code.StartsWith("dns-", StringComparison.Ordinal)) return $"이름 해석 실패 ({code[4..]})";
        if (code.StartsWith("socket-", StringComparison.Ordinal)) return $"연결 실패 ({code[7..]})";
        if (code.StartsWith("http-", StringComparison.Ordinal))
            return int.TryParse(code[5..], NumberStyles.None, CultureInfo.InvariantCulture, out int status) ? $"HTTP 응답 {status}" : $"HTTP 오류 ({code[5..]})";
        if (code.StartsWith("log-", StringComparison.Ordinal)) return $"링크 기록을 읽지 못했습니다 ({code[4..]})";
        if (code.StartsWith("ProbeError_", StringComparison.Ordinal)) return $"검사 중 오류 ({code[11..]})";
        if (code.StartsWith("exit-", StringComparison.Ordinal)) return $"명령이 실패했습니다 (종료 코드 {code[5..]})";
        if (code.EndsWith("Exception", StringComparison.Ordinal)) return $"검사 중 오류 ({code})";
        if (Enum.TryParse<System.Net.NetworkInformation.IPStatus>(code, out _)) return $"응답 없음 ({code})";
        return $"기타 사유 ({code})";
    }

    /// <summary>사이트 점검 입력 오류 코드 → 칸 오류 글.</summary>
    public static string InputError(string code) => code switch
    {
        "empty" => "도메인을 적어 주세요.",
        "not-domain" => "주소(https://…)나 경로가 아니라 도메인만 적어 주세요 (예: example.com).",
        "port-or-ipv6" => "포트 번호나 IPv6 주소는 쓸 수 없습니다. 도메인만 적어 주세요.",
        "ip-address" => "IP 주소가 아니라 도메인을 적어 주세요.",
        "invalid-idn" => "국제 도메인 형식이 올바르지 않습니다.",
        "too-long" => "도메인이 너무 깁니다 (253자 이하).",
        "no-dot" => "점(.)이 있는 도메인을 적어 주세요 (예: example.com).",
        "bad-label" => "도메인에 쓸 수 없는 글자가 있습니다 (영문·숫자·하이픈).",
        "numeric-tld" => "마지막 부분이 숫자인 도메인은 쓸 수 없습니다.",
        _ => "도메인 형식이 아닙니다.",
    };

    /// <summary>DNS 캐시 비우기 결과 메시지.</summary>
    public static string FlushMessage(bool ok, string? code) => ok
        ? "DNS 캐시를 비웠습니다. 다시 검사한 결과입니다."
        : code == "timeout" ? "DNS 캐시 비우기가 10초 안에 끝나지 않았습니다. 다시 검사한 결과입니다."
        : $"DNS 캐시를 비우지 못했습니다: {Reason(code)}. 다시 검사한 결과입니다.";   // 사유에 괄호가 있어 겹 괄호를 피함
}
