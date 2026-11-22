using System.Text.RegularExpressions;
using NetPulse.Core.Demo;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Evidence;
using NetPulse.Core.Live;

namespace NetPulse.Core.Tests;

/// <summary>M6 G4 1라운드 부분 a의 지적 (종료 직렬화, 임시 파일, 부분 덮어쓰기, DNS 설명, 회차 시점, 사유 코드).</summary>
public sealed class M6ReviewR1Tests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 13, 20, 0, TimeSpan.FromHours(9));
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private static Scenario Load(string name, string? diagnosis = null)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json"));
        if (diagnosis is not null) text = text.Replace("\"version\": 1,", "\"version\": 1, \"diagnosis\": " + diagnosis + ",");
        return Scenario.Parse(text);
    }

    private static DemoWorld Frozen(Scenario s, long round)
    {
        var tb = new DemoTimeBase(Start, 1);
        tb.Freeze(TimeSpan.FromSeconds(round));
        return new DemoWorld(s, tb);
    }

    private static DiagnosisSettings Fast(Scenario s) => DemoDiagnosticProbes.Settings(s) with
    {
        AdapterTimeout = TimeSpan.FromMilliseconds(300), GatewayTimeout = TimeSpan.FromMilliseconds(300), TcpTimeout = TimeSpan.FromMilliseconds(300),
        DnsTimeout = TimeSpan.FromMilliseconds(300), HttpsTimeout = TimeSpan.FromMilliseconds(300), PortalTimeout = TimeSpan.FromMilliseconds(300),
        LinkTimeout = TimeSpan.FromMilliseconds(300),
    };

    [Fact]
    public async Task ShutdownBetweenTheCheckAndTheCommandNeverStartsTheFlushOrPublishes()
    {
        var s = Load("steady");
        var actions = new DemoSystemActions(s);
        var store = new DiagnosisStore(dir.Sub("d"));
        var host = new DiagnosisHost(new DiagnosisWorld(new DemoDiagnosticProbes(Frozen(s, 10)), Fast(s), actions), store, () => null, () => Start, "test");
        int published = 0;
        host.Changed += () => Interlocked.Increment(ref published);
        Task? shutdown = null;
        // 단계 게시 직후, 명령을 부르기 직전에 종료가 들어온다
        host.BeforeFlushForTest = () => { shutdown = host.ShutdownAsync(); return Task.CompletedTask; };
        Assert.Equal(DiagnosisRequest.Started, host.RequestFlush());
        await Eventually(() => shutdown is not null, "종료 끼어들기");
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        int after = Volatile.Read(ref published);
        await Task.Delay(300, Ct);
        Assert.DoesNotContain("flushDns", actions.Calls);              // 명령을 시작하지 않았다
        Assert.Equal(after, Volatile.Read(ref published));            // 종료 뒤 게시 없음
        Assert.Equal(DiagnosisRequest.Closed, host.StartDiagnosis());
    }

    [Fact]
    public async Task WriteFailureAfterTheTempFileExistsLeavesNoTempFile()
    {
        var s = Load("steady");
        var folder = dir.Sub("w");
        var store = new DiagnosisStore(folder) { WriteFailForTest = () => throw new IOException("디스크 가득 (시험)") };
        var result = await QuickDiagnosis.RunAsync(new DemoDiagnosticProbes(Frozen(s, 10)), Fast(s), () => Start, Ct);
        var save = store.Save(result, "t", null);
        Assert.Null(save.File);
        Assert.Equal("io-error", save.Error);
        Assert.Empty(Directory.GetFiles(folder));   // .tmp 없음
    }

    [Fact]
    public async Task PartialOverridesKeepTheTimelineForFieldsTheyDoNotName()
    {
        // 링크가 내려가는 회차를 찾는다 (link-drop 시나리오)
        var s = Load("link-drop", """{ "link": { "log": "denied" }, "adapter": { "dns": false } }""");
        long down = Enumerable.Range(0, 3600).First(r => s.LinkDownAt(r));
        var probes = new DemoDiagnosticProbes(Frozen(s, down));
        var link = await probes.LinkAsync(Ct);
        Assert.Equal(LinkState.Down, link.Now);                        // now는 적지 않았으므로 시간축의 다운
        Assert.Equal(LinkCapability.Denied, link.Log);
        var adapter = await probes.AdapterAsync(Ct);
        Assert.False(adapter.AdapterUp);                              // up도 시간축 (링크 다운)
        Assert.False(adapter.HasDns);                                 // 적은 필드만 바뀜
    }

    /// <summary>M12 노트북 중간 확인: 시나리오가 HTTPS 제한 시간을 정할 수 있다 (시험용, 1~60초). 없으면 기본값.</summary>
    [Fact]
    public void ScenarioCanSetTheHttpsTimeLimit()
    {
        Assert.Equal(new DiagnosisSettings().HttpsTimeout, DemoDiagnosticProbes.Settings(Load("outage-long")).HttpsTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), DemoDiagnosticProbes.Settings(Load("outage-long", """{ "httpsTimeoutMs": 15000 }""")).HttpsTimeout);
        Assert.Throws<ScenarioException>(() => Load("outage-long", """{ "httpsTimeoutMs": 999 }"""));
        Assert.Throws<ScenarioException>(() => Load("outage-long", """{ "httpsTimeoutMs": 60001 }"""));
    }

    [Fact]
    public async Task ProbesUseTheRoundAtTheCallNotAfterTheDelay()
    {
        var s = Load("outage-long", """{ "delayMs": { "tcp": 400 } }""");
        // 외부가 응답하던 회차에서 호출하고, 지연 동안 끊김 회차로 넘어가게 시간축을 돌린다
        var tb = new DemoTimeBase(Start, 1);
        long fine = Enumerable.Range(1, 3000).First(r => s.Targets.Where(t => t.Role == "external").All(t => new DemoWorld(s, Frozen(s, 0).TimeBase).Resolve(t.Id, r).Outcome == NetPulse.Core.Measurement.ProbeOutcome.Success)
            && new DemoWorld(s, Frozen(s, 0).TimeBase).Resolve(s.Targets.First(t => t.Role == "external").Id, r + 1).Outcome != NetPulse.Core.Measurement.ProbeOutcome.Success);
        tb.Freeze(TimeSpan.FromSeconds(fine));
        var world = new DemoWorld(s, tb);
        var probes = new DemoDiagnosticProbes(world);
        var ext = s.Targets.First(t => t.Role == "external");
        var call = probes.TcpAsync(System.Net.IPAddress.Parse(ext.Address), 443, Ct);
        tb.Freeze(TimeSpan.FromSeconds(fine + 1));                    // 지연 도중 다음 회차로
        var result = await call;
        Assert.Equal(TargetOutcome.Ok, result.Outcome);              // 호출한 그 회차의 답
    }

    [Fact]
    public void AdapterWithoutDnsIsDescribedAsSuch()
    {
        var c = new CheckResult(CheckId.Adapter, CheckJudgment.Normal, false, RunState.Run, null, [], TimeSpan.Zero,
            new Dictionary<string, string> { ["dns"] = "none" });
        Assert.Contains("DNS 서버 설정이 없습니다", DiagnosisTexts.Explain(c));
        var with = c with { Facts = new Dictionary<string, string> { ["dns"] = "present" } };
        Assert.Contains("DNS 설정이 있습니다", DiagnosisTexts.Explain(with));
    }

    /// <summary>제품 코드가 만드는 사유 코드(문자열 상수)를 소스에서 모아 모두 쉬운 말이 있는지 본다.</summary>
    [Fact]
    public void EveryProducedReasonCodeHasPlainWords()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NetPulse.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var files = new[] { "src/NetPulse.Platform", "src/NetPulse.Core/Diagnostics", "src/NetPulse.Core/Live" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root!.FullName, d), "*.cs"))
            .Where(f => !f.EndsWith("DiagnosisTexts.cs", StringComparison.Ordinal) && !f.EndsWith("SiteCheck.cs", StringComparison.Ordinal));
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\"([A-Za-z][A-Za-z0-9]*(?:-[a-z0-9]+)+|[A-Za-z][A-Za-z0-9]*_)\""))
                codes.Add(m.Groups[1].Value.EndsWith('_') ? m.Groups[1].Value + "5" : m.Groups[1].Value);
        // 사유 코드가 아닌 상수 (경로 이름·형식 이름 등)
        codes.ExceptWith(["hyper-v", "demo-records", "name-exhausted", "invalid-path", "disk-full", "io-error", "fallback-root", "ms-settings", "settings-network", "settings-wifi", "settings-proxy"]);
        var unknown = codes.Where(c => DiagnosisTexts.Reason(c).StartsWith("기타 사유", StringComparison.Ordinal)).ToArray();
        Assert.True(unknown.Length == 0, "쉬운 말이 없는 사유 코드: " + string.Join(", ", unknown));
    }

    private static async Task Eventually(Func<bool> done, string what)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException(what);
            await Task.Delay(20, Ct);
        }
    }
}
