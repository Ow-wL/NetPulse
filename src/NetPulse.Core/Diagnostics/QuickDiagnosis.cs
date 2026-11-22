using System.Diagnostics;
using System.Net;
using System.Text;
using NetPulse.Core.Evidence;

namespace NetPulse.Core.Diagnostics;

/// <summary>대상 하나의 검사 결과 (M4 설계 C1).</summary>
public enum TargetOutcome
{
    Ok,
    /// <summary>확정 실패 (응답 없음, 이름 없음, HTTP 오류 상태 등)</summary>
    Fail,
    /// <summary>검사 자체를 못 함 (소켓 오류, 예외)</summary>
    Error,
    /// <summary>제한시간 초과</summary>
    Timeout,
}

/// <summary>검사 판정 (정상 / 확인 / 실패 / 미측정).</summary>
public enum CheckJudgment { Normal, Check, Fail, Unmeasured }

public enum CheckId { Adapter, Gateway, Tcp, Dns, Https, Portal, Link }

/// <summary>실행 상태 (M4 설계 C4): 결과 있음 / 실행 안 함 / 실행했으나 시간 초과 / 오류.</summary>
public enum RunState { Run, NotRun, Timeout, Error }

/// <summary>대상 하나의 결과. 대상은 순번(t1, t2)으로만 부른다 (주소·도메인 값을 남기지 않음).</summary>
public sealed record TargetCheck(string Slot, TargetOutcome Outcome, string? Code, TimeSpan Elapsed);

/// <param name="PartlyUnconfirmed">실패지만 확인하지 못한 대상이 있음 ("실패(일부 미확인)", 2-6)</param>
/// <param name="Facts">화면용 사실 (예: "linkNow" = up). 주소는 넣지 않는다</param>
public sealed record CheckResult(CheckId Id, CheckJudgment Judgment, bool PartlyUnconfirmed, RunState State, string? Code,
    IReadOnlyList<TargetCheck> Targets, TimeSpan Elapsed, IReadOnlyDictionary<string, string> Facts);

/// <summary>대상 검사기의 한 번 결과.</summary>
public sealed record ProbeOutcomeResult(TargetOutcome Outcome, string? Code = null);

/// <summary>어댑터 검사 사실. 게이트웨이 주소는 게이트웨이 검사에만 쓰고 기록·복사에 넣지 않는다.</summary>
public sealed record AdapterFacts(bool RouteFound, int ActiveInterfaces, bool AdapterUp, bool HasIpv4, bool Apipa, IPAddress? Gateway, bool HasDns, string? Code = null);

/// <summary>네트워크 로그인 검사 결과.</summary>
public sealed record PortalResult(TargetOutcome Outcome, int? Status, bool BodyMatches, string? Code = null);

/// <summary>링크 검사 사실: 지금 상태(직접 한 번 읽기)와 최근 30분 로그.</summary>
public sealed record LinkFacts(LinkState Now, LinkCapability Log, bool RecentDown, string? Code = null);

/// <summary>검사기 (M4 설계 C1). 실제 구현은 Platform. 제한시간은 조율자가 건다 (검사기는 취소에 응해야 하지만 안 해도 결과를 기다리지 않음).</summary>
public interface IDiagnosticProbes
{
    Task<AdapterFacts> AdapterAsync(CancellationToken ct);
    Task<ProbeOutcomeResult> PingAsync(IPAddress address, CancellationToken ct);
    Task<ProbeOutcomeResult> TcpAsync(IPAddress address, int port, CancellationToken ct);
    Task<ProbeOutcomeResult> DnsAsync(string host, CancellationToken ct);
    Task<ProbeOutcomeResult> HttpsAsync(string host, CancellationToken ct);
    Task<PortalResult> PortalAsync(Uri uri, string expected, CancellationToken ct);
    Task<LinkFacts> LinkAsync(CancellationToken ct);
}

/// <summary>빠른 진단 기본값 (설정 화면은 M9).</summary>
public sealed record DiagnosisSettings
{
    public IReadOnlyList<IPAddress> TcpTargets { get; init; } = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];
    public int TcpPort { get; init; } = 443;
    public IReadOnlyList<string> Domains { get; init; } = ["www.microsoft.com", "www.google.com"];
    public Uri PortalUri { get; init; } = new("http://www.msftconnecttest.com/connecttest.txt");
    public string PortalExpected { get; init; } = "Microsoft Connect Test";
    public TimeSpan AdapterTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan GatewayTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan TcpTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HttpsTimeout { get; init; } = TimeSpan.FromSeconds(4.5);
    public TimeSpan PortalTimeout { get; init; } = TimeSpan.FromSeconds(4.5);
    public TimeSpan LinkTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>결론 하나 (M4 설계 C2): 찾은 것 → 해볼 것.</summary>
public sealed record Conclusion(int Number, string Found, IReadOnlyList<string> Steps);

/// <summary>빠른 진단 결과.</summary>
public sealed record DiagnosisResult(
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, IReadOnlyList<CheckResult> Checks,
    Conclusion Main, IReadOnlyList<int> AlsoMatched, IReadOnlyList<string> Notes)
{
    public CheckResult this[CheckId id] => Checks.First(c => c.Id == id);
}

/// <summary>빠른 진단 (F1, M4 설계 C). 검사 조율과 판정·결론은 여기, 실제 검사는 검사기.</summary>
public static class QuickDiagnosis
{
    /// <param name="progress">검사 하나가 끝날 때마다(제한시간·오류 포함) 그 결과를 알린다 (M6 설계 3.1). 판정·결론·반환값은 그대로</param>
    public static async Task<DiagnosisResult> RunAsync(IDiagnosticProbes probes, DiagnosisSettings settings, Func<DateTimeOffset> now, CancellationToken ct = default,
        IProgress<CheckResult>? progress = null)
    {
        var started = now();
        async Task<CheckResult> Report(Task<CheckResult> check)
        {
            var r = await check.ConfigureAwait(false);
            progress?.Report(r);
            return r;
        }
        var adapterTask = Single(CheckId.Adapter, settings.AdapterTimeout, c => probes.AdapterAsync(c), ct);
        var tcp = Report(Pair(CheckId.Tcp, settings.TcpTimeout, settings.TcpTargets.Take(2).Select(a => (Func<CancellationToken, Task<ProbeOutcomeResult>>)(c => probes.TcpAsync(a, settings.TcpPort, c))).ToArray(), ct));
        var dns = Report(Pair(CheckId.Dns, settings.DnsTimeout, settings.Domains.Take(2).Select(d => (Func<CancellationToken, Task<ProbeOutcomeResult>>)(c => probes.DnsAsync(d, c))).ToArray(), ct));
        var https = Report(Pair(CheckId.Https, settings.HttpsTimeout, settings.Domains.Take(2).Select(d => (Func<CancellationToken, Task<ProbeOutcomeResult>>)(c => probes.HttpsAsync(d, c))).ToArray(), ct));
        var portalTask = Single(CheckId.Portal, settings.PortalTimeout, c => probes.PortalAsync(settings.PortalUri, settings.PortalExpected, c), ct);
        var linkTask = Single(CheckId.Link, settings.LinkTimeout, c => probes.LinkAsync(c), ct);
        var portal = Report(Judged(portalTask, r => JudgePortal(r.Value, r.State, r.Elapsed, r.Code)));
        var link = Report(Judged(linkTask, r => JudgeLink(r.Value, r.State, r.Elapsed, r.Code)));

        var adapterRaw = await adapterTask.ConfigureAwait(false);
        var adapter = JudgeAdapter(adapterRaw.Value, adapterRaw.State, adapterRaw.Elapsed, adapterRaw.Code);
        progress?.Report(adapter);
        CheckResult gateway;
        if (adapterRaw.Value?.Gateway is { } gw)
        {
            var (ping, state, elapsed, code) = await Single(CheckId.Gateway, settings.GatewayTimeout, c => probes.PingAsync(gw, c), ct).ConfigureAwait(false);
            gateway = JudgeGateway(ping, state, elapsed, code);
        }
        else gateway = new CheckResult(CheckId.Gateway, CheckJudgment.Unmeasured, false, RunState.NotRun,
            adapterRaw.Value is null ? "adapter-unmeasured" : "no-gateway", [], TimeSpan.Zero, new Dictionary<string, string>());
        progress?.Report(gateway);

        var checks = new List<CheckResult>
        {
            adapter, gateway, await tcp.ConfigureAwait(false), await dns.ConfigureAwait(false), await https.ConfigureAwait(false),
            await portal.ConfigureAwait(false), await link.ConfigureAwait(false),
        };
        var (main, also, notes) = Conclude(checks);
        return new DiagnosisResult(started, now(), checks, main, also, notes);
    }

    private static async Task<CheckResult> Judged<T>(Task<(T? Value, RunState State, TimeSpan Elapsed, string? Code)> raw,
        Func<(T? Value, RunState State, TimeSpan Elapsed, string? Code), CheckResult> judge) where T : class =>
        judge(await raw.ConfigureAwait(false));

    // ---------------- 제한시간 걸기 ----------------

    /// <summary>검사 하나에 제한을 건다. 늦게 끝난 작업의 예외는 관찰만 하고 버린다.</summary>
    internal static async Task<(T? Value, RunState State, TimeSpan Elapsed, string? Code)> Single<T>(CheckId id, TimeSpan timeout,
        Func<CancellationToken, Task<T>> run, CancellationToken outer) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var watch = Stopwatch.StartNew();
        Task<T> task;
        try { task = run(cts.Token); }
        catch (Exception ex) { return (null, RunState.Error, watch.Elapsed, ex.GetType().Name); }
        var finished = await Task.WhenAny(task, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);
        if (finished != task)
        {
            cts.Cancel();
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return (null, RunState.Timeout, watch.Elapsed, "timeout");
        }
        try { return (await task.ConfigureAwait(false), RunState.Run, watch.Elapsed, null); }
        catch (Exception ex) { return (null, RunState.Error, watch.Elapsed, ex.GetType().Name); }
    }

    /// <summary>두 대상 검사: 대상마다 따로 제한하고 대상별 결과를 보존한다 (하나가 늦어도 다른 성공을 버리지 않음, 1-12).</summary>
    internal static async Task<CheckResult> Pair(CheckId id, TimeSpan timeout, IReadOnlyList<Func<CancellationToken, Task<ProbeOutcomeResult>>> runs, CancellationToken outer)
    {
        var watch = Stopwatch.StartNew();
        var tasks = runs.Select((run, i) => Single(id, timeout, run, outer)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var targets = results.Select((r, i) => new TargetCheck("t" + (i + 1),
            r.State switch
            {
                RunState.Run => r.Value!.Outcome,
                RunState.Timeout => TargetOutcome.Timeout,
                _ => TargetOutcome.Error,
            }, r.Value?.Code ?? r.Code, r.Elapsed)).ToArray();
        var (judgment, partly) = Combine(targets.Select(t => t.Outcome).ToArray());
        var state = targets.Any(t => t.Outcome is TargetOutcome.Ok or TargetOutcome.Fail) ? RunState.Run
            : targets.All(t => t.Outcome == TargetOutcome.Timeout) ? RunState.Timeout : RunState.Error;
        return new CheckResult(id, judgment, partly, state, null, targets, watch.Elapsed, new Dictionary<string, string>());
    }

    /// <summary>두 대상 조합표 (M4 설계 C1, 2-6).</summary>
    public static (CheckJudgment Judgment, bool PartlyUnconfirmed) Combine(IReadOnlyList<TargetOutcome> outcomes)
    {
        int ok = outcomes.Count(o => o == TargetOutcome.Ok);
        int fail = outcomes.Count(o => o == TargetOutcome.Fail);
        if (ok == outcomes.Count && ok > 0) return (CheckJudgment.Normal, false);
        if (ok > 0) return (CheckJudgment.Check, false);
        if (fail == outcomes.Count && fail > 0) return (CheckJudgment.Fail, false);
        if (fail > 0) return (CheckJudgment.Fail, true);
        return (CheckJudgment.Unmeasured, false);
    }

    // ---------------- 한 대상 검사 판정 ----------------

    internal static CheckResult JudgeAdapter(AdapterFacts? f, RunState state, TimeSpan elapsed, string? code)
    {
        var facts = new Dictionary<string, string>();
        if (f is null) return new CheckResult(CheckId.Adapter, CheckJudgment.Unmeasured, false, state, code, [], elapsed, facts);
        facts["route"] = f.RouteFound ? "found" : "none";
        facts["gateway"] = f.Gateway is null ? "none" : "present";
        facts["dns"] = f.HasDns ? "present" : "none";
        CheckJudgment j;
        string? reason = f.Code;
        if (!f.RouteFound) { j = f.ActiveInterfaces > 0 ? CheckJudgment.Check : CheckJudgment.Fail; reason ??= "no-route"; }
        else if (!f.AdapterUp) { j = CheckJudgment.Fail; reason ??= "adapter-down"; }
        else if (!f.HasIpv4 || f.Apipa) { j = CheckJudgment.Fail; reason ??= f.Apipa ? "apipa" : "no-ipv4"; }
        else if (f.Gateway is null) { j = CheckJudgment.Check; reason ??= "no-gateway"; }
        else j = CheckJudgment.Normal;
        return new CheckResult(CheckId.Adapter, j, false, RunState.Run, reason, [], elapsed, facts);
    }

    internal static CheckResult JudgeGateway(ProbeOutcomeResult? r, RunState state, TimeSpan elapsed, string? code)
    {
        var target = new TargetCheck("t1", r?.Outcome ?? (state == RunState.Timeout ? TargetOutcome.Timeout : TargetOutcome.Error), r?.Code ?? code, elapsed);
        // 무응답은 확인 (Ping을 막는 장비일 수 있음 — 이어받을 규칙: 게이트웨이 무응답을 곧 장애로 단정하지 않음)
        var j = target.Outcome switch { TargetOutcome.Ok => CheckJudgment.Normal, TargetOutcome.Fail => CheckJudgment.Check, _ => CheckJudgment.Unmeasured };
        return new CheckResult(CheckId.Gateway, j, false, r is null ? state : RunState.Run, r?.Code ?? code, [target], elapsed, new Dictionary<string, string>());
    }

    internal static CheckResult JudgePortal(PortalResult? r, RunState state, TimeSpan elapsed, string? code)
    {
        var facts = new Dictionary<string, string>();
        if (r is null) return new CheckResult(CheckId.Portal, CheckJudgment.Unmeasured, false, state, code, [], elapsed, facts);
        if (r.Status is int s) facts["status"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CheckJudgment j;
        if (r.Outcome == TargetOutcome.Ok && r.Status == 200 && r.BodyMatches) j = CheckJudgment.Normal;
        else if (r.Status is int st && (st == 200 || st is >= 300 and < 500)) j = CheckJudgment.Check;
        else j = CheckJudgment.Unmeasured;
        return new CheckResult(CheckId.Portal, j, false, RunState.Run, r.Code, [], elapsed, facts);
    }

    internal static CheckResult JudgeLink(LinkFacts? f, RunState state, TimeSpan elapsed, string? code)
    {
        var facts = new Dictionary<string, string>();
        if (f is null) return new CheckResult(CheckId.Link, CheckJudgment.Unmeasured, false, state, code, [], elapsed, facts);
        facts["now"] = Evidence.LinkEvidenceBuilder.Text(f.Now);
        facts["log"] = Evidence.LinkEvidenceBuilder.Text(f.Log);
        CheckJudgment j;
        string? reason = f.Code;
        // 지금 다운이면 로그 상태와 무관하게 실패
        if (f.Now is LinkState.Down or LinkState.Removed) j = CheckJudgment.Fail;
        else if (f.RecentDown) j = CheckJudgment.Check;
        else if (f.Now == LinkState.Up && f.Log == LinkCapability.Supported) j = CheckJudgment.Normal;
        else { j = CheckJudgment.Unmeasured; reason ??= f.Now != LinkState.Up ? "now-unknown" : "log-" + Evidence.LinkEvidenceBuilder.Text(f.Log); }
        return new CheckResult(CheckId.Link, j, false, RunState.Run, reason, [], elapsed, facts);
    }

    // ---------------- 결론 (C2) ----------------

    private static readonly string[] CoreChecks = ["Adapter", "Tcp", "Dns", "Https"];

    public static (Conclusion Main, IReadOnlyList<int> Also, IReadOnlyList<string> Notes) Conclude(IReadOnlyList<CheckResult> checks)
    {
        CheckResult C(CheckId id) => checks.First(c => c.Id == id);
        bool Is(CheckId id, params CheckJudgment[] js) => js.Contains(C(id).Judgment);
        var adapter = C(CheckId.Adapter);
        var gateway = C(CheckId.Gateway);
        var tcp = C(CheckId.Tcp);
        var https = C(CheckId.Https);

        string Partly(CheckResult c) => c.PartlyUnconfirmed ? " (확인하지 못한 대상이 있음)" : "";
        var rules = new List<(int N, bool Match, Func<Conclusion> Make)>
        {
            (1, Is(CheckId.Link, CheckJudgment.Fail), () => new(1, "네트워크 어댑터 연결(케이블·Wi‑Fi)이 지금 끊겨 있습니다.",
                ["케이블이 꽂혀 있는지, 포트 불빛이 켜져 있는지 확인하세요.", "다른 포트나 케이블로 바꿔 보세요.", "Windows 설정에서 어댑터를 껐다가 다시 켜 보세요."])),
            (2, Is(CheckId.Https, CheckJudgment.Normal, CheckJudgment.Check) && Is(CheckId.Tcp, CheckJudgment.Fail), () => new(2,
                "웹 응답은 확인됐지만 IPv4 직접 연결은 되지 않습니다" + Partly(tcp) + ".", ["IPv6, 프록시, VPN을 쓰고 있는지 확인하세요."])),
            (3, Is(CheckId.Tcp, CheckJudgment.Normal, CheckJudgment.Check) && Is(CheckId.Dns, CheckJudgment.Fail), () => new(3,
                "인터넷 연결은 되지만 이름 해석(DNS)이 되지 않습니다" + Partly(C(CheckId.Dns)) + ".",
                ["DNS 캐시를 비운 뒤(확인 후 실행) 다시 검사하세요.", "어댑터의 DNS 서버 설정을 확인하세요."])),
            (4, Is(CheckId.Https, CheckJudgment.Fail, CheckJudgment.Check) && Is(CheckId.Portal, CheckJudgment.Check), () => new(4,
                "공용 Wi‑Fi 로그인이나 프록시 정책 때문에 웹 연결이 막혀 있을 수 있습니다.", ["브라우저로 아무 웹 페이지나 열어 로그인 화면이 나오는지 확인하세요."])),
            (5, Is(CheckId.Adapter, CheckJudgment.Fail) && Is(CheckId.Tcp, CheckJudgment.Fail) && Is(CheckId.Https, CheckJudgment.Fail), () => new(5,
                "이 PC의 네트워크 연결에 문제가 있을 수 있습니다" + Partly(tcp) + ".", ["케이블이나 Wi‑Fi 접속 상태를 확인하세요."])),
            (6, Is(CheckId.Tcp, CheckJudgment.Fail) && Is(CheckId.Https, CheckJudgment.Fail), () => new(6,
                (gateway.Judgment switch
                {
                    CheckJudgment.Normal => "공유기까지는 응답하지만 그 너머(인터넷 쪽)로 나가지 못합니다",
                    CheckJudgment.Check => "공유기와 인터넷 모두 응답하지 않습니다 (공유기가 Ping을 막고 있을 수도 있음)",
                    _ => "인터넷 쪽으로 나가지 못합니다. 공유기 상태는 확인하지 못했습니다",
                }) + (tcp.PartlyUnconfirmed || https.PartlyUnconfirmed ? " (확인하지 못한 대상이 있음)" : "") + ".",
                ["다른 기기도 인터넷이 안 되는지 확인하세요.", "공유기를 다시 시작해 보세요.", "계속되면 통신사에 문의하세요."])),
            (7, Is(CheckId.Https, CheckJudgment.Fail, CheckJudgment.Check), () => new(7,
                "웹(HTTPS) 연결에 추가 확인이 필요합니다" + (https.Judgment == CheckJudgment.Check ? " (일부만 성공)" : "") + ".",
                ["프록시·VPN 설정, PC 시계, 보안 프로그램의 인증서 검사를 확인하세요."])),
            (8, Is(CheckId.Link, CheckJudgment.Check), () => new(8, "최근 30분 안에 링크가 끊긴 적이 있습니다 (지금 어댑터 기준).",
                ["가끔 끊긴다면 측정으로 기록하세요."])),
            (9, (Is(CheckId.Tcp, CheckJudgment.Normal, CheckJudgment.Check) || Is(CheckId.Https, CheckJudgment.Normal, CheckJudgment.Check))
                && (Is(CheckId.Gateway, CheckJudgment.Check) || Is(CheckId.Portal, CheckJudgment.Check)), () => new(9,
                "인터넷은 되지만 확인할 점이 있습니다 (공유기가 Ping에 답하지 않았거나 로그인 페이지 같은 응답을 받음).", [])),
            (10, new[] { CheckId.Tcp, CheckId.Dns, CheckId.Https }.Any(id => Is(id, CheckJudgment.Check))
                && !new[] { CheckId.Tcp, CheckId.Dns, CheckId.Https }.Any(id => Is(id, CheckJudgment.Fail)), () => new(10,
                // 슬롯 이름(t1) 대신 "대상 n", 조사 "이(가)" 없이 (M6 화면 검토)
                "일부만 성공했습니다. 안 된 대상: " + string.Join(", ", new[] { CheckId.Tcp, CheckId.Dns, CheckId.Https }.Where(id => Is(id, CheckJudgment.Check))
                    .Select(id => CheckName(id) + " " + string.Join("·", C(id).Targets.Where(t => t.Outcome != TargetOutcome.Ok).Select(t => SlotLabel(t.Slot)))))
                + ".", ["그 대상만의 문제일 수 있습니다. 다시 검사해 보세요."])),
            (11, Is(CheckId.Adapter, CheckJudgment.Normal) && Is(CheckId.Tcp, CheckJudgment.Normal) && Is(CheckId.Dns, CheckJudgment.Normal)
                && Is(CheckId.Https, CheckJudgment.Normal), () => new(11, "기본 연결은 정상입니다.", ["가끔 끊긴다면 측정으로 기록하세요."])),
        };
        var matched = rules.Where(r => r.Match).ToArray();
        Conclusion main;
        if (matched.Length > 0) main = matched[0].Make();
        else
        {
            var missing = checks.Where(c => c.Judgment == CheckJudgment.Unmeasured && CoreChecks.Contains(c.Id.ToString())).Select(c => CheckName(c.Id));
            main = new Conclusion(12, "진단 근거가 부족합니다. 확인하지 못한 검사: " + string.Join(", ", missing.DefaultIfEmpty("없음")) + ".",
                ["잠시 뒤 다시 검사하세요.", "가끔 끊긴다면 측정으로 기록하세요."]);
        }
        var also = matched.Skip(1).Select(r => r.N).ToArray();
        var notes = new List<string>();
        foreach (var c in checks.Where(c => c.Judgment == CheckJudgment.Unmeasured)) notes.Add("unmeasured:" + c.Id.ToString().ToLowerInvariant());
        foreach (var c in checks.Where(c => c.PartlyUnconfirmed)) notes.Add("partly-unconfirmed:" + c.Id.ToString().ToLowerInvariant());
        return (main, also, notes);
    }

    /// <summary>대상 슬롯("t1")의 사람이 읽는 이름("대상 1").</summary>
    public static string SlotLabel(string slot) =>
        slot.Length > 1 && slot[0] == 't' && int.TryParse(slot.AsSpan(1), out int n) ? "대상 " + n : slot;

    public static string CheckName(CheckId id) => id switch
    {
        CheckId.Adapter => "어댑터", CheckId.Gateway => "공유기(게이트웨이)", CheckId.Tcp => "외부 연결(TCP 443)", CheckId.Dns => "DNS",
        CheckId.Https => "HTTPS", CheckId.Portal => "네트워크 로그인", _ => "링크 근거",
    };

    public static string JudgmentName(CheckJudgment j) => j switch
    {
        CheckJudgment.Normal => "정상", CheckJudgment.Check => "확인 필요", CheckJudgment.Fail => "실패", _ => "확인 못 함",   // "확인"이 정상 확인으로, "측정"이 진단과 섞여 읽혔다 (M11 문구 점검)
    };

    /// <summary>이전 결과와 판정이 바뀐 검사 (화면은 M6).</summary>
    public static IReadOnlyList<(CheckId Id, CheckJudgment Before, CheckJudgment After)> Compare(DiagnosisResult before, DiagnosisResult after) =>
        after.Checks.Select(c => (c.Id, Before: before.Checks.FirstOrDefault(b => b.Id == c.Id)?.Judgment ?? CheckJudgment.Unmeasured, After: c.Judgment))
            .Where(x => x.Before != x.After).ToArray();

    /// <summary>
    /// 복사 텍스트 (M4 설계 C2): 검사 판정·결론·안내만. 주소·도메인 값·어댑터 설명·예외 메시지·사용자 대상 이름은 넣지 않는다.
    /// 사유는 정해진 코드만 (자유 문자열이 아닌 것만) 붙인다.
    /// </summary>
    public static string CopyText(DiagnosisResult r)
    {
        var sb = new StringBuilder();
        sb.Append("NetPulse 빠른 진단 ").AppendLine(r.StartedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var c in r.Checks)
        {
            sb.Append("- ").Append(CheckName(c.Id)).Append(": ").Append(JudgmentName(c.Judgment));
            if (c.PartlyUnconfirmed) sb.Append(" (일부 미확인)");
            if (c.Targets.Count > 1) sb.Append(" [").Append(string.Join(", ", c.Targets.Select(t => SlotLabel(t.Slot) + " " + OutcomeName(t.Outcome)))).Append(']');
            sb.AppendLine();
        }
        sb.Append("결론: ").AppendLine(r.Main.Found);
        foreach (var step in r.Main.Steps) sb.Append("  · ").AppendLine(step);
        return sb.ToString();
    }

    public static string OutcomeName(TargetOutcome o) => o switch
    {
        TargetOutcome.Ok => "성공", TargetOutcome.Fail => "실패", TargetOutcome.Error => "오류", _ => "시간 초과",
    };
}
