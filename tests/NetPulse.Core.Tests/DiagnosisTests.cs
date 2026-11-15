using System.Diagnostics;
using System.Net;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Evidence;

namespace NetPulse.Core.Tests;

/// <summary>빠른 진단·사이트 점검·진단 기록 (M4 설계 C).</summary>
public sealed class DiagnosisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly IPAddress Gw = IPAddress.Parse("192.0.2.254");

    /// <summary>스크립트로 결과를 정하는 검사기. 지연을 줄 수 있다.</summary>
    private sealed class FakeProbes : IDiagnosticProbes
    {
        public AdapterFacts Adapter = new(true, 1, true, true, false, Gw, true);
        public Func<IPAddress, Task<ProbeOutcomeResult>> Tcp = _ => Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Ok));
        public Func<string, Task<ProbeOutcomeResult>> Dns = _ => Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Ok));
        public Func<string, Task<ProbeOutcomeResult>> Https = _ => Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Ok));
        public ProbeOutcomeResult Ping = new(TargetOutcome.Ok);
        public PortalResult Portal = new(TargetOutcome.Ok, 200, true);
        public LinkFacts Link = new(LinkState.Up, LinkCapability.Supported, false);

        public Task<AdapterFacts> AdapterAsync(CancellationToken ct) => Task.FromResult(Adapter);
        public Task<ProbeOutcomeResult> PingAsync(IPAddress address, CancellationToken ct) => Task.FromResult(Ping);
        public Task<ProbeOutcomeResult> TcpAsync(IPAddress address, int port, CancellationToken ct) => Tcp(address);
        public Task<ProbeOutcomeResult> DnsAsync(string host, CancellationToken ct) => Dns(host);
        public Task<ProbeOutcomeResult> HttpsAsync(string host, CancellationToken ct) => Https(host);
        public Task<PortalResult> PortalAsync(Uri uri, string expected, CancellationToken ct) => Task.FromResult(Portal);
        public Task<LinkFacts> LinkAsync(CancellationToken ct) => Task.FromResult(Link);
    }

    private static readonly DiagnosisSettings Fast = new()
    {
        AdapterTimeout = TimeSpan.FromMilliseconds(300), GatewayTimeout = TimeSpan.FromMilliseconds(300), TcpTimeout = TimeSpan.FromMilliseconds(300),
        DnsTimeout = TimeSpan.FromMilliseconds(300), HttpsTimeout = TimeSpan.FromMilliseconds(300), PortalTimeout = TimeSpan.FromMilliseconds(300),
        LinkTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static Task<DiagnosisResult> Run(FakeProbes p) => QuickDiagnosis.RunAsync(p, Fast, () => T0, TestContext.Current.CancellationToken);

    private static Func<T, Task<ProbeOutcomeResult>> Seq<T>(params TargetOutcome[] outcomes)
    {
        int i = 0;
        return _ => Task.FromResult(new ProbeOutcomeResult(outcomes[Interlocked.Increment(ref i) - 1]));
    }

    [Theory]
    [InlineData(TargetOutcome.Ok, TargetOutcome.Ok, CheckJudgment.Normal, false)]
    [InlineData(TargetOutcome.Ok, TargetOutcome.Timeout, CheckJudgment.Check, false)]
    [InlineData(TargetOutcome.Ok, TargetOutcome.Fail, CheckJudgment.Check, false)]
    [InlineData(TargetOutcome.Fail, TargetOutcome.Fail, CheckJudgment.Fail, false)]
    [InlineData(TargetOutcome.Fail, TargetOutcome.Timeout, CheckJudgment.Fail, true)]
    [InlineData(TargetOutcome.Fail, TargetOutcome.Error, CheckJudgment.Fail, true)]
    [InlineData(TargetOutcome.Error, TargetOutcome.Timeout, CheckJudgment.Unmeasured, false)]
    public void PairCombinationTable(TargetOutcome a, TargetOutcome b, CheckJudgment expected, bool partly)
    {
        Assert.Equal((expected, partly), QuickDiagnosis.Combine([a, b]));
    }

    [Fact]
    public async Task AllGoodIsBasicConnectionNormal()
    {
        var r = await Run(new FakeProbes());
        Assert.All(r.Checks, c => Assert.Equal(CheckJudgment.Normal, c.Judgment));
        Assert.Equal(11, r.Main.Number);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public async Task SlowTargetDoesNotDiscardTheOtherSuccess()
    {
        // 하나는 즉시 성공, 하나는 끝나지 않음: 다른 성공을 버리지 않는다 (1-12)
        var p = new FakeProbes { Tcp = a => a.Equals(IPAddress.Parse("1.1.1.1")) ? Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Ok)) : new TaskCompletionSource<ProbeOutcomeResult>().Task };
        var watch = Stopwatch.StartNew();
        var r = await Run(p);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        var tcp = r[CheckId.Tcp];
        Assert.Equal(CheckJudgment.Check, tcp.Judgment);
        Assert.Equal([TargetOutcome.Ok, TargetOutcome.Timeout], tcp.Targets.Select(t => t.Outcome));
        Assert.Equal(10, r.Main.Number);   // 일부만 성공 (2-6)
        Assert.Contains("외부 TCP 443 대상 2", r.Main.Found);   // 슬롯 이름 대신 "대상 n" (M6 화면 검토)
        Assert.DoesNotContain("t2", r.Main.Found);
    }

    [Fact]
    public async Task LateExceptionsAreObservedAndIgnored()
    {
        var p = new FakeProbes { Dns = async _ => { await Task.Delay(600); throw new InvalidOperationException("늦은 실패"); } };
        var r = await Run(p);
        Assert.Equal(CheckJudgment.Unmeasured, r[CheckId.Dns].Judgment);
        Assert.Equal(RunState.Timeout, r[CheckId.Dns].State);
        Assert.Equal(12, r.Main.Number);   // 핵심 검사 미측정 → 근거 부족 (정상이 아님, 1-11)
        await Task.Delay(500, TestContext.Current.CancellationToken);   // 늦은 예외가 처리되지 않은 예외로 새지 않음
    }

    [Fact]
    public async Task AllTimeoutsAreInsufficientNotNormal()
    {
        var never = new TaskCompletionSource<ProbeOutcomeResult>().Task;
        var p = new FakeProbes { Tcp = _ => never, Dns = _ => never, Https = _ => never };
        var r = await Run(p);
        Assert.Equal(12, r.Main.Number);
        Assert.Contains("외부 TCP 443", r.Main.Found);
    }

    [Fact]
    public async Task OnlyLinkUnmeasuredStillNormalWithNote()
    {
        var p = new FakeProbes { Link = new LinkFacts(LinkState.Up, LinkCapability.Unsupported, false, "no-known-driver") };
        var r = await Run(p);
        Assert.Equal(CheckJudgment.Unmeasured, r[CheckId.Link].Judgment);   // 로그 미지원은 정상이 아님
        Assert.Equal(11, r.Main.Number);
        Assert.Contains("unmeasured:link", r.Notes);
    }

    [Fact]
    public async Task LinkDownNowWinsEvenWithLogDeniedAndOthersUnmeasured()
    {
        var never = new TaskCompletionSource<ProbeOutcomeResult>().Task;
        var p = new FakeProbes { Link = new LinkFacts(LinkState.Down, LinkCapability.Denied, false), Tcp = _ => never, Https = _ => never };
        var r = await Run(p);
        Assert.Equal(CheckJudgment.Fail, r[CheckId.Link].Judgment);
        Assert.Equal(1, r.Main.Number);
    }

    [Fact]
    public async Task LinkNeedsUpNowForNormal()
    {
        var r = await Run(new FakeProbes { Link = new LinkFacts(LinkState.Unknown, LinkCapability.Supported, false) });
        Assert.Equal(CheckJudgment.Unmeasured, r[CheckId.Link].Judgment);
        var recent = await Run(new FakeProbes { Link = new LinkFacts(LinkState.Up, LinkCapability.Supported, true) });
        Assert.Equal(CheckJudgment.Check, recent[CheckId.Link].Judgment);
        Assert.Equal(8, recent.Main.Number);
    }

    [Fact]
    public async Task ConclusionRulesInOrder()
    {
        // 2: HTTPS 정상, TCP 실패
        Assert.Equal(2, (await Run(new FakeProbes { Tcp = Seq<IPAddress>(TargetOutcome.Fail, TargetOutcome.Fail) })).Main.Number);
        // 3: TCP 정상, DNS 실패
        Assert.Equal(3, (await Run(new FakeProbes { Dns = Seq<string>(TargetOutcome.Fail, TargetOutcome.Fail) })).Main.Number);
        // 4: HTTPS 실패, 로그인 확인
        Assert.Equal(4, (await Run(new FakeProbes
        {
            Https = Seq<string>(TargetOutcome.Fail, TargetOutcome.Fail), Portal = new PortalResult(TargetOutcome.Ok, 302, false),
        })).Main.Number);
        // 5: 어댑터 실패 + TCP·HTTPS 실패
        var five = await Run(new FakeProbes
        {
            Adapter = new AdapterFacts(true, 1, true, true, true, Gw, true),   // 169.254 자동 할당
            Tcp = Seq<IPAddress>(TargetOutcome.Fail, TargetOutcome.Fail), Https = Seq<string>(TargetOutcome.Fail, TargetOutcome.Fail),
        });
        Assert.Equal((CheckJudgment.Fail, "apipa"), (five[CheckId.Adapter].Judgment, five[CheckId.Adapter].Code));
        Assert.Equal(5, five.Main.Number);
        // 6: TCP·HTTPS 실패, 게이트웨이 정상 → 공유기 이후
        var six = await Run(new FakeProbes { Tcp = Seq<IPAddress>(TargetOutcome.Fail, TargetOutcome.Fail), Https = Seq<string>(TargetOutcome.Fail, TargetOutcome.Fail) });
        Assert.Equal(6, six.Main.Number);
        Assert.Contains("공유기까지는 응답", six.Main.Found);
        // 7: HTTPS 일부만 성공
        Assert.Equal(7, (await Run(new FakeProbes { Https = Seq<string>(TargetOutcome.Ok, TargetOutcome.Fail) })).Main.Number);
        // 9: 게이트웨이 무응답(확인), 인터넷 됨 — 게이트웨이 무응답을 장애로 단정하지 않음
        var nine = await Run(new FakeProbes { Ping = new ProbeOutcomeResult(TargetOutcome.Fail) });
        Assert.Equal(CheckJudgment.Check, nine[CheckId.Gateway].Judgment);
        Assert.Equal(9, nine.Main.Number);
    }

    [Fact]
    public async Task PartlyUnconfirmedFailureIsNotCalledTotal()
    {
        // TCP·HTTPS 각각 fail + timeout → 6에 "확인하지 못한 대상" (2-6)
        var never = new TaskCompletionSource<ProbeOutcomeResult>().Task;
        int t = 0, h = 0;
        var p = new FakeProbes
        {
            Tcp = _ => Interlocked.Increment(ref t) == 1 ? Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Fail)) : never,
            Https = _ => Interlocked.Increment(ref h) == 1 ? Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Fail)) : never,
        };
        var r = await Run(p);
        Assert.True(r[CheckId.Tcp].PartlyUnconfirmed);
        Assert.Equal(6, r.Main.Number);
        Assert.Contains("확인하지 못한 대상", r.Main.Found);
    }

    [Fact]
    public async Task GatewayUnmeasuredWording()
    {
        var p = new FakeProbes
        {
            Adapter = new AdapterFacts(true, 1, true, true, false, null, true),
            Tcp = Seq<IPAddress>(TargetOutcome.Fail, TargetOutcome.Fail), Https = Seq<string>(TargetOutcome.Fail, TargetOutcome.Fail),
        };
        var r = await Run(p);
        Assert.Equal((CheckJudgment.Unmeasured, RunState.NotRun), (r[CheckId.Gateway].Judgment, r[CheckId.Gateway].State));
        Assert.Equal(6, r.Main.Number);
        Assert.Contains("공유기 상태는 확인하지 못했습니다", r.Main.Found);
    }

    [Fact]
    public async Task CopyTextHasNoAddressesOrDomains()
    {
        var p = new FakeProbes
        {
            Tcp = _ => Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Error, "ProbeError_HostUnreachable")),
            Dns = _ => Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Fail, "dns-HostNotFound")),
        };
        var r = await Run(p);
        var text = QuickDiagnosis.CopyText(r);
        foreach (var forbidden in new[] { "192.0.2.254", "1.1.1.1", "8.8.8.8", "microsoft", "google", "msftconnecttest", "ProbeError", "dns-" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("결론:", text);
        Assert.Contains("대상 1", text);
    }

    [Fact]
    public async Task CompareListsChangedChecks()
    {
        var before = await Run(new FakeProbes());
        var after = await Run(new FakeProbes { Dns = Seq<string>(TargetOutcome.Fail, TargetOutcome.Fail) });
        var changed = Assert.Single(QuickDiagnosis.Compare(before, after));
        Assert.Equal((CheckId.Dns, CheckJudgment.Normal, CheckJudgment.Fail), changed);
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM. ", "example.com")]
    [InlineData("한국.kr", "xn--3e0b707e.kr")]
    public void SiteCheckAcceptsDomains(string input, string ascii)
    {
        Assert.Equal((ascii, (string?)null), SiteCheck.Validate(input));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("localhost", "no-dot")]
    [InlineData("https://example.com", "not-domain")]
    [InlineData("example.com/path", "not-domain")]
    [InlineData("example.com:443", "port-or-ipv6")]
    [InlineData("192.0.2.1", "ip-address")]
    [InlineData("-bad.com", "bad-label")]
    [InlineData("a..com", "bad-label")]
    [InlineData("example.123", "numeric-tld")]
    public void SiteCheckRejectsNonDomains(string input, string error)
    {
        Assert.Equal(((string?)null, error), SiteCheck.Validate(input));
    }

    [Fact]
    public void SiteCheckRejectsTooLongLabelAndName()
    {
        Assert.Equal("bad-label", SiteCheck.Validate(new string('a', 64) + ".com").Error);
        Assert.Equal("too-long", SiteCheck.Validate(string.Join(".", Enumerable.Repeat(new string('a', 60), 5)) + ".com").Error);
    }

    [Fact]
    public async Task SiteCheckRunsDnsAndHttps()
    {
        var p = new FakeProbes { Https = _ => Task.FromResult(new ProbeOutcomeResult(TargetOutcome.Fail, "tls")) };
        var r = await SiteCheck.RunAsync(p, "example.com", Fast, TestContext.Current.CancellationToken);
        Assert.Equal((CheckJudgment.Normal, CheckJudgment.Fail), (r.Dns.Judgment, r.Https.Judgment));
        await Assert.ThrowsAsync<ArgumentException>(() => SiteCheck.RunAsync(p, "not a domain", Fast, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiagnosisRecordRoundTripsAndKeepsRunStates()
    {
        var p = new FakeProbes { Adapter = new AdapterFacts(true, 1, true, true, false, null, true) };   // 게이트웨이 없음 → 게이트웨이 검사 notRun
        var r = await Run(p);
        var json = DiagnosisRecord.ToJson(r, "0.5.0", "20261113T000000Z_abc123");
        Assert.DoesNotContain("192.0.2.254", json);
        var back = DiagnosisRecord.Parse(json);
        Assert.Equal(DiagnosisReadStatus.Ok, back.Status);
        Assert.Equal("20261113T000000Z_abc123", back.MeasurementId);
        Assert.Equal(r.Main.Number, back.MainConclusion);
        var gw = back.Checks.Single(c => c.Id == CheckId.Gateway);
        Assert.Equal((RunState.NotRun, "no-gateway"), (gw.State, gw.Code));
        Assert.Equal(2, back.Checks.Single(c => c.Id == CheckId.Tcp).Targets.Count);
        Assert.Equal("diagnosis-20261113T000000Z.json", DiagnosisRecord.FileName(T0));
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 2", DiagnosisReadStatus.Unsupported)]
    [InlineData("\"judgment\": \"normal\"", "\"judgment\": \"great\"", DiagnosisReadStatus.Unreadable)]
    [InlineData("\"conclusion\": 11", "\"conclusion\": 99", DiagnosisReadStatus.Unreadable)]
    public async Task DiagnosisRecordDamage(string from, string to, DiagnosisReadStatus status)
    {
        var json = DiagnosisRecord.ToJson(await Run(new FakeProbes()), "0.5.0", null);
        Assert.Contains(from, json);
        Assert.Equal(status, DiagnosisRecord.Parse(json.Replace(from, to)).Status);
        Assert.Equal(DiagnosisReadStatus.Unreadable, DiagnosisRecord.Parse("{ 망가진").Status);
    }
}
