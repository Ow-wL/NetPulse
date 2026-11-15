using System.Net;
using NetPulse.Core.Demo;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M6 설계 7.1: 데모 진단 검사기, 진행 알림, 진단 수명, 진단 기록, 폴더 허용, 화면 문구 (화면 없이).</summary>
public sealed class M6DiagnosisTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Scenario Load(string name, string? diagnosis = null)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json"));
        if (diagnosis is not null) text = text.Replace("\"version\": 1,", "\"version\": 1, \"diagnosis\": " + diagnosis + ",");
        return Scenario.Parse(text);
    }

    /// <summary>시간축을 그 회차에 멈춘 데모 세계.</summary>
    private static DemoWorld Frozen(Scenario s, long round)
    {
        var tb = new DemoTimeBase(Start, 1);
        tb.Freeze(TimeSpan.FromSeconds(round));
        return new DemoWorld(s, tb);
    }

    /// <summary>시험용 짧은 제한시간.</summary>
    private static DiagnosisSettings Fast(Scenario s) => DemoDiagnosticProbes.Settings(s) with
    {
        AdapterTimeout = TimeSpan.FromMilliseconds(300), GatewayTimeout = TimeSpan.FromMilliseconds(300), TcpTimeout = TimeSpan.FromMilliseconds(300),
        DnsTimeout = TimeSpan.FromMilliseconds(300), HttpsTimeout = TimeSpan.FromMilliseconds(300), PortalTimeout = TimeSpan.FromMilliseconds(300),
        LinkTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static Task<DiagnosisResult> Run(DemoWorld w, IProgress<CheckResult>? progress = null) =>
        QuickDiagnosis.RunAsync(new DemoDiagnosticProbes(w), Fast(w.Scenario), () => Start, Ct, progress);

    // ---------------- 데모 검사기 ----------------

    [Fact]
    public async Task TimelineDecidesTheChecksAndFrozenTimeGivesOneRound()
    {
        var s = Load("outage-long");
        var calm = await Run(Frozen(s, 30));
        Assert.Equal(11, calm.Main.Number);
        var down = await Run(Frozen(s, 100));                       // 외부 응답 없음 구간 (90~149)
        Assert.Equal(CheckJudgment.Fail, down[CheckId.Tcp].Judgment);
        Assert.Equal(CheckJudgment.Fail, down[CheckId.Https].Judgment);
        Assert.Equal(CheckJudgment.Normal, down[CheckId.Gateway].Judgment);
        Assert.Equal(6, down.Main.Number);
        Assert.Equal(RunState.Timeout, down[CheckId.Portal].State);  // 인터넷 쪽 무응답은 실제 제한시간 경로로
    }

    [Fact]
    public async Task OverridesWinAndTimeoutTakesTheRealTimeoutPath()
    {
        var s = Load("steady", """{ "tcp": ["ok", "timeout"], "dns": ["fail", "fail"], "https": ["ok", "ok"], "gateway": "fail" }""");
        var r = await Run(Frozen(s, 10));
        Assert.Equal(CheckJudgment.Check, r[CheckId.Tcp].Judgment);
        Assert.Equal(TargetOutcome.Timeout, r[CheckId.Tcp].Targets[1].Outcome);
        Assert.Equal(CheckJudgment.Fail, r[CheckId.Dns].Judgment);
        Assert.Equal(CheckJudgment.Check, r[CheckId.Gateway].Judgment);   // 무응답은 확인 (Ping 차단 가능)
        Assert.Equal(3, r.Main.Number);
    }

    [Fact]
    public async Task SameHostGetsTheSameAnswerInBothFeaturesAndUndefinedRoundsStayErrors()
    {
        var s = Load("steady", """{ "site": { "a.example": { "dns": "fail", "https": "fail" }, "shop.example": { "dns": "ok", "https": "timeout" } } }""");
        var p = new DemoDiagnosticProbes(Frozen(s, 10));
        Assert.Equal(TargetOutcome.Fail, (await p.DnsAsync("a.example", Ct)).Outcome);   // 슬롯 도메인이라도 site가 이긴다 (같은 호스트 같은 답)
        Assert.Equal(TargetOutcome.Ok, (await p.DnsAsync("b.example", Ct)).Outcome);     // 슬롯 규칙: 대응 외부 대상의 회차
        Assert.Equal(TargetOutcome.Ok, (await p.DnsAsync("docs.example.org", Ct)).Outcome);
        Assert.Equal(TargetOutcome.Fail, (await p.DnsAsync("real-site.com", Ct)).Outcome);
        var site = await SiteCheck.RunAsync(p, "shop.example", Fast(s), Ct);
        Assert.Equal(CheckJudgment.Normal, site.Dns.Judgment);
        Assert.Equal(RunState.Timeout, site.Https.State);
        var beyond = new DemoDiagnosticProbes(Frozen(Load("steady"), 999_999));          // 시나리오 밖 회차
        Assert.Equal(TargetOutcome.Error, (await beyond.TcpAsync(IPAddress.Parse(Load("steady").Targets.First(t => t.Role == "external").Address), 443, Ct)).Outcome);
    }

    [Fact]
    public void DemoSettingsUseOnlyDocumentationValues()
    {
        var s = DemoDiagnosticProbes.Settings(Load("steady"));
        Assert.All(s.TcpTargets, a => Assert.True(Scenario.IsDocumentationAddress(a)));
        Assert.All(s.Domains, d => Assert.True(Scenario.IsDocumentationDomain(d)));
        Assert.True(Scenario.IsDocumentationDomain(s.PortalUri.Host));
        Assert.Throws<ScenarioException>(() => Load("steady", """{ "site": { "www.google.com": { "dns": "ok" } } }"""));
        Assert.Throws<ScenarioException>(() => Load("steady", """{ "tcp": ["maybe"] }"""));
    }

    [Fact]
    public async Task ProgressReportsEveryCheckOnceWithTheReturnedValues()
    {
        var seen = new List<CheckResult>();
        var r = await Run(Frozen(Load("steady"), 10), new Sync(c => { lock (seen) seen.Add(c); }));
        Assert.Equal(7, seen.Count);
        Assert.Equal(r.Checks.Select(c => c.Id).Order(), seen.Select(c => c.Id).Order());
        foreach (var c in seen) Assert.Same(r[c.Id], c);
    }

    private sealed class Sync(Action<CheckResult> act) : IProgress<CheckResult>
    {
        public void Report(CheckResult value) => act(value);
    }

    // ---------------- 진단 수명 ----------------

    private (DiagnosisHost Host, DemoSystemActions Actions, DiagnosisStore Store) NewHost(Scenario s, Func<string?>? measurement = null, long round = 10)
    {
        var w = Frozen(s, round);
        var actions = new DemoSystemActions(s);
        var store = new DiagnosisStore(dir.Sub("diag-" + Guid.NewGuid().ToString("N")[..6]));
        var host = new DiagnosisHost(new DiagnosisWorld(new DemoDiagnosticProbes(w), Fast(s), actions), store, measurement ?? (() => null), () => Start, "test");
        return (host, actions, store);
    }

    [Fact]
    public async Task OneOperationAtATimeAndFlushQueuedDuringADiagnosisHoldsTheSlot()
    {
        var s = Load("steady", """{ "delayMs": { "adapter": 250, "dns": 250 } }""");
        var (host, actions, _) = NewHost(s);
        var ops = new List<DiagnosisOperation>();
        host.Changed += () => { lock (ops) ops.Add(host.Snapshot.Operation); };
        Assert.Equal(DiagnosisRequest.Started, host.StartDiagnosis());
        Assert.Equal(DiagnosisRequest.Busy, host.StartDiagnosis());
        Assert.Equal(DiagnosisRequest.Queued, host.RequestFlush());          // 진단 중: 예약, 칸을 바로 점유
        Assert.Equal(DiagnosisOperation.Flushing, host.Snapshot.Operation);
        Assert.Equal(FlushStage.Waiting, host.Snapshot.Flush);
        Assert.Equal(DiagnosisRequest.Busy, host.RequestFlush());            // 예약 중 재요청
        Assert.Equal(DiagnosisRequest.Busy, host.StartDiagnosis());
        await EventuallyWithin(() => host.Snapshot.Operation == DiagnosisOperation.None && host.Snapshot.Last?.Flush is not null, "흐름 끝", TimeSpan.FromSeconds(10));
        Assert.Equal(["flushDns"], actions.Calls);
        lock (ops)
        {
            int first = ops.IndexOf(DiagnosisOperation.Flushing);
            Assert.DoesNotContain(DiagnosisOperation.None, ops.Skip(first).Take(ops.Count - first - 1));   // 앞 진단이 끝나도 None이 끼지 않음
        }
        Assert.True(host.Snapshot.Last!.Flush!.Ok);
        Assert.Equal(DiagnosisRequest.Started, host.RequestFlush());         // 칸이 비면 바로 흐름
        await EventuallyWithin(() => host.Snapshot.Operation == DiagnosisOperation.None, "두 번째 흐름 끝", TimeSpan.FromSeconds(10));
        Assert.Equal(2, actions.Calls.Count);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task FlushFailureStillRechecksWithAMessage()
    {
        var (host, actions, _) = NewHost(Load("steady", """{ "flushDns": "timeout" }"""));
        Assert.Equal(DiagnosisRequest.Started, host.RequestFlush());
        await EventuallyWithin(() => host.Snapshot.Operation == DiagnosisOperation.None && host.Snapshot.Last is not null, "흐름 끝", TimeSpan.FromSeconds(10));
        var last = host.Snapshot.Last!;
        Assert.False(last.Flush!.Ok);
        Assert.Equal("timeout", last.Flush.Code);
        Assert.Contains("10초", DiagnosisTexts.FlushMessage(last.Flush.Ok, last.Flush.Code));
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task MeasurementIdIsTakenWhenTheDiagnosisStarts()
    {
        string? running = "20261113T042000Z_aaaaaa";
        var (host, _, store) = NewHost(Load("steady", """{ "delayMs": { "tcp": 300 } }"""), () => running);
        host.StartDiagnosis();
        running = null;                                                      // 진단 중 측정이 끝남
        await EventuallyWithin(() => host.Snapshot.Last is not null, "결과", TimeSpan.FromSeconds(10));
        Assert.Equal("20261113T042000Z_aaaaaa", host.Snapshot.Last!.MeasurementId);
        var file = Assert.Single(Directory.GetFiles(store.Folder, "diagnosis-*.json"));
        Assert.Equal("20261113T042000Z_aaaaaa", DiagnosisRecord.Parse(File.ReadAllText(file)).MeasurementId);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task ShutdownRejectsRequestsAndNeverPublishesOrSavesALateResult()
    {
        var (host, actions, store) = NewHost(Load("steady", """{ "delayMs": { "adapter": 400, "https": 400 } }"""));
        int changes = 0;
        host.StartDiagnosis();
        host.RequestFlush();                                                 // 예약된 흐름은 버려진다
        host.Changed += () => Interlocked.Increment(ref changes);
        await host.ShutdownAsync(TimeSpan.FromSeconds(2));
        int after = changes;
        await Task.Delay(800, Ct);
        Assert.Equal(DiagnosisRequest.Closed, host.StartDiagnosis());
        Assert.Equal(DiagnosisRequest.Closed, host.RequestFlush());
        Assert.Equal(DiagnosisRequest.Closed, host.CheckSite("shop.example"));
        Assert.Null(host.Snapshot.Last);
        Assert.Empty(actions.Calls);
        Assert.Equal(after, changes);
        Assert.Empty(Directory.Exists(store.Folder) ? Directory.GetFiles(store.Folder) : []);
    }

    [Fact]
    public async Task SiteCheckValidatesRunsAlongsideAndCompareUsesTheLastResult()
    {
        var (host, _, _) = NewHost(Load("steady"));
        Assert.Equal(DiagnosisRequest.Invalid, host.CheckSite("https://shop.example/path"));
        Assert.Equal("not-domain", host.Snapshot.Site.InputError);
        host.StartDiagnosis();
        Assert.Equal(DiagnosisRequest.Started, host.CheckSite("shop.example"));   // 진단과 함께 돈다
        await EventuallyWithin(() => host.Snapshot.Site.Result is not null && host.Snapshot.Last is not null, "둘 다 끝", TimeSpan.FromSeconds(10));
        Assert.Null(host.Snapshot.Last!.Previous);                         // 처음
        host.StartDiagnosis();
        await EventuallyWithin(() => host.Snapshot.Last!.Previous is not null && host.Snapshot.Operation == DiagnosisOperation.None, "두 번째", TimeSpan.FromSeconds(10));
        Assert.Empty(host.Snapshot.Last!.Changes!);
        Assert.Equal("지난 검사와 같습니다.", DiagnosisView.Compare(host.Snapshot.Last!, 0)!.Title);
        await host.ShutdownAsync();
    }

    // ---------------- 진단 기록 ----------------

    [Fact]
    public async Task SameSecondSavesNeverOverwriteAndBrokenOrTempFilesAreSkipped()
    {
        var store = new DiagnosisStore(dir.Sub("store"));
        var r = await Run(Frozen(Load("steady"), 10));
        var names = Enumerable.Range(0, 3).Select(_ => Path.GetFileName(store.Save(r, "test", null).File!)).ToArray();
        var stem = Path.GetFileNameWithoutExtension(DiagnosisRecord.FileName(r.StartedAt));
        Assert.Equal([stem + ".json", stem + "-2.json", stem + "-3.json"], names);
        File.WriteAllText(Path.Combine(store.Folder, "diagnosis-20991231T000000Z.json"), "{ broken");   // 더 늦은 손상 기록
        File.WriteAllText(Path.Combine(store.Folder, ".diagnosis-left.tmp"), "{}");
        var (latest, skipped) = store.Latest();
        Assert.Equal(1, skipped);
        Assert.Equal(r.StartedAt, latest!.StartedAt);
        Assert.DoesNotContain(Directory.GetFiles(store.Folder), f => f.EndsWith(".tmp", StringComparison.Ordinal) && !f.EndsWith("left.tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveFailureLeavesNoTempFileAndReportsACode()
    {
        var blocked = Path.Combine(dir.Path, "is-a-file");
        File.WriteAllText(blocked, "x");
        var save = new DiagnosisStore(blocked).Save(await Run(Frozen(Load("steady"), 10)), "test", null);
        Assert.Null(save.File);
        Assert.NotNull(save.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp", SearchOption.AllDirectories));
    }

    // ---------------- 폴더 허용 ----------------

    [Fact]
    public void FolderPolicyAllowsOnlyExistingFoldersInsideTheRoots()
    {
        var root = dir.Sub("records");
        var child = Directory.CreateDirectory(Path.Combine(root, "20261113T042000Z_aaaaaa")).FullName;
        var sibling = dir.Sub("records-x");
        Assert.Null(FolderPolicy.Check(root, [root]));
        Assert.Null(FolderPolicy.Check(child, [root]));
        Assert.Null(FolderPolicy.Check(child + Path.DirectorySeparatorChar, [root + Path.DirectorySeparatorChar]));
        Assert.Equal("not-allowed", FolderPolicy.Check(sibling, [root]));                        // 이름 앞부분만 같은 형제
        Assert.Equal("not-allowed", FolderPolicy.Check(Path.Combine(child, "..", "..", "records-x"), [root]));
        Assert.Equal("missing", FolderPolicy.Check(Path.Combine(root, "none"), [root]));
        Assert.Equal("not-allowed", FolderPolicy.Check("", [root]));
    }

    // ---------------- 문구 ----------------

    [Fact]
    public async Task CopyTextHasNoAddressesDomainsOrTargetNames()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scenarios"), "*.json"))
        {
            var s = Scenario.Load(file);
            foreach (long round in new long[] { 5, 100 })
            {
                var text = QuickDiagnosis.CopyText(await Run(Frozen(s, round)));
                foreach (var t in s.Targets)
                {
                    Assert.DoesNotContain(t.Address, text);
                    // "공유기"는 엔진의 고정 결론 문장에도 쓰는 일반 단어라 대상 이름 검사에서 뺀다
                    if (t.Name is { } n && n != "공유기") Assert.DoesNotContain(n, text);
                }
                Assert.DoesNotContain("example", text);
            }
        }
    }

    [Fact]
    public void EveryKnownCodeHasPlainWordsAndHeadsAreDistinct()
    {
        string[] codes =
        [
            "apipa", "no-ipv4", "no-gateway", "no-route", "adapter-down", "interface-not-found", "adapter-unmeasured", "timeout", "cancelled", "not-run",
            "now-unknown", "log-timeout", "log-unsupported", "log-denied", "no-address", "tls", "dns", "TimedOut", "demo-fail", "demo-error",
            "scenario-undefined", "scenario-error", "ProbeError", "ProbeError_10051", "dns-HostNotFound", "socket-ConnectionRefused", "http-503",
            "http-InvalidResponse", "exit-1", "SocketException", "DestinationHostUnreachable",
        ];
        Assert.All(codes, c => Assert.DoesNotContain("기타 사유", DiagnosisTexts.Reason(c)));
        Assert.StartsWith("기타 사유", DiagnosisTexts.Reason("what-is-this"));
        var heads = Enumerable.Range(1, 12).Select(n => DiagnosisTexts.Head(n).Head).ToArray();
        Assert.Equal(12, heads.Distinct().Count());
        foreach (var input in new[] { "", "https://a.example", "a.example:8080", "192.0.2.1", "nodot", "bad_label.example", "a.123", new string('a', 254) + ".example" })
        {
            var (ascii, error) = SiteCheck.Validate(input);
            Assert.Null(ascii);
            Assert.DoesNotContain("형식이 아닙니다", DiagnosisTexts.InputError(error!));
        }
    }

    [Fact]
    public async Task ScopeKeepsPartialSuccessAndGatewaySilenceVisible()
    {
        // 결론 3(TCP 정상·확인 + DNS 실패)은 문장에 TCP 일부 성공을 말하지 않는다: 관측 범위 줄이 보인다
        var r = await Run(Frozen(Load("steady", """{ "tcp": ["ok", "fail"], "dns": ["fail", "fail"], "gateway": "fail" }"""), 10));
        Assert.Equal(3, r.Main.Number);
        var view = DiagnosisView.Conclusion(r);
        Assert.Equal("이름 해석(DNS)이 되지 않음", view.Head);
        Assert.Contains(view.Scope, l => l.Contains("외부 TCP 443") && l.Contains("일부만 성공"));
        Assert.Contains(view.Scope, l => l.Contains("Ping을 막는 장비일 수 있음"));
        var tcp = DiagnosisView.Check(r[CheckId.Tcp], Fast(Load("steady")));
        Assert.Equal("198.51.100.10:443", tcp.Targets[0].Shown);           // 펼친 상세에만 (복사에는 없음)
    }
}
