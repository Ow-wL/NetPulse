using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using NetPulse.Core.Analysis;
using NetPulse.Core.Detection;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;

namespace NetPulse.Platform.Tests;

/// <summary>
/// M4 G3 대체: 화면 없는 통합 시나리오 (M4 설계 10절). 외부망·로그가 없거나 막힌 러너에서도 돈다:
/// 네트워크 성공은 판정하지 않고, 상태가 정의된 값인지와 근거가 파일까지 보존되는지를 본다. 이름·주소는 출력하지 않는다.
/// </summary>
public sealed class M4IntegrationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("np-g3m4-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static ITestOutputHelper? Out => TestContext.Current.TestOutputHelper;

    private static int LoopbackIndex()
    {
        var (index, error) = NetworkInfo.BestInterface(IPAddress.Loopback);
        Assert.True(index is not null, "루프백 경로 조회 실패: " + error);
        return index!.Value;
    }

    [Fact]
    public void IfEntryReadsLoopbackFieldsAtTheRightOffsets()
    {
        int index = LoopbackIndex();
        var (row, code) = IfEntry.Read(index);
        Assert.True(row is not null, "GetIfEntry2 실패: " + code);
        Assert.Equal(index, row!.Index);
        Assert.Equal(24, row.IfType);                    // IF_TYPE_SOFTWARE_LOOPBACK
        Assert.Equal(LinkState.Up, IfEntry.StateOf(row));
        Assert.Equal("loopback", IfEntry.KindOf(row));
        Assert.NotEqual(Guid.Empty, row.Guid);
    }

    [Fact]
    public void IfEntryMatchesNetworkInterfaceForTheRoutedAdapter()
    {
        var (route, _) = NetworkInfo.BestRoute(DetectionPlanner.RouteProbeAddress);
        if (route is null) { Out?.WriteLine("외부 경로 없음: 건너뜀 (러너 환경)"); return; }
        var (row, code) = IfEntry.Read(route.InterfaceIndex);
        Assert.True(row is not null, "GetIfEntry2 실패: " + code);
        var nic = NetworkInterface.GetAllNetworkInterfaces().First(n =>
        {
            try { return n.GetIPProperties().GetIPv4Properties()?.Index == route.InterfaceIndex; } catch (NetworkInformationException) { return false; }
        });
        // 위치가 맞는지: 설명·GUID가 .NET이 본 값과 같아야 한다 (값은 출력하지 않음)
        Assert.Equal(nic.Description, row!.Description);
        Assert.True(Guid.TryParse(nic.Id, out var g) && g == row.Guid, "GUID 위치 불일치");
        Assert.Equal(nic.OperationalStatus == OperationalStatus.Up, row.OperStatus == 1);
        Out?.WriteLine($"경로 어댑터: 유형 {IfEntry.KindOf(row)}, 하드웨어 {row.Hardware}, 상태 {IfEntry.StateOf(row)}");
    }

    [Fact]
    public void IfEntryReadCostIsRecorded()
    {
        int index = LoopbackIndex();
        var times = new List<double>();
        for (int i = 0; i < 300; i++)
        {
            var w = Stopwatch.StartNew();
            IfEntry.Read(index);
            times.Add(w.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        double p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
        Out?.WriteLine($"GetIfEntry2 p95 {p95:F3} ms, 최대 {times[^1]:F3} ms");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ifentry-cost.json"),
            new JsonObject { ["p95Ms"] = Math.Round(p95, 4), ["maxMs"] = Math.Round(times[^1], 4), ["count"] = times.Count }.ToJsonString());
        // 기준(1 ms)은 이 PC 실측 기록에만 적고 CI 합격 조건으로 쓰지 않는다 (M4 설계 10절). 비정상적으로 느린 것만 막는다.
        Assert.True(p95 < 50, $"p95 {p95:F3} ms");
    }

    [Fact]
    public async Task RealSessionKeepsLinkEvidenceThroughFiles()
    {
        var primary = new DiskRecordRoot(Path.Combine(root, "p"), "primary");
        var fallback = new DiskRecordRoot(Path.Combine(root, "f"), "fallback");
        var profile = new Profile("g3", "G3", [
            new Target("loopback", "루프백", IPAddress.Loopback, TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("doc", "문서용 주소", IPAddress.Parse("192.0.2.1"), TargetRole.External, "TEST-NET-1", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Any);
        var session = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = new SystemClock(), ProbeFor = t => ProbeFactory.For(t, profile.Timeout),
            Primary = primary, Fallback = fallback, Routes = new RouteObserver(), Power = new PowerEvents(),
            Links = new LinkObserver(), LinkLog = new LinkLogReader(), LinkLogSettle = TimeSpan.FromSeconds(1), AppVersion = "g3",
        });
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var result = await session.StopAsync();
        Assert.Equal(EndReason.Stopped, result.EndReason);
        Assert.False(result.SealFailed);

        var rec = RecordReader.Read(RecordReader.FindSegments(session.Id, [Path.Combine(root, "p"), Path.Combine(root, "f")]));
        Assert.Equal(RecordStatus.Ok, rec.Status);
        Assert.Equal(ExpectedConnection.Any, rec.Header!.Expected);
        Assert.All(rec.Rounds, r => Assert.NotNull(r.EndMs));
        Assert.Equal(0, rec.Summary!.EventsDropped);
        var links = LinkEvidenceBuilder.FromEvents(rec.Events);
        // 루프백 인터페이스의 직접 관측은 지원되고 관측 범위 90% 이상이어야 한다 (항상 unsupported를 내는 구현을 걸러냄)
        var loop = links.Adapters.Values.Single(a => a.Kind == "loopback");
        var direct = links.Summaries.Single(s => s.Adapter == loop.Alias && s.Source == LinkSource.Direct);
        Assert.Equal(LinkCapability.Supported, direct.Capability);
        Assert.True(direct.Coverage >= 0.9, "관측 범위 " + direct.Coverage);
        Assert.Equal(LinkDisplay.ObservedNone, direct.Display);
        // 로그 경로는 요약 줄이 있고 정의된 값 (러너에서 로그가 없거나 막힐 수 있음)
        Assert.Contains(links.Summaries, s => s.Source == LinkSource.Log);
        foreach (var s in links.Summaries) Out?.WriteLine($"{s.Source} {LinkEvidenceBuilder.Text(s.Display)} ({LinkEvidenceBuilder.Text(s.Capability)} {s.Code})");

        // 분석이 끝까지 돈다 (등급은 1분 미만이라 관측 부족)
        var analysis = MeasurementAnalysis.Analyze(rec);
        Assert.Equal(Grade.Insufficient, analysis.Grade.Grade);
    }

    [Fact]
    public void ThisPcLinkLogReadGivesDefinedState()
    {
        var (route, _) = NetworkInfo.BestRoute(DetectionPlanner.RouteProbeAddress);
        var adapters = new List<AdapterInfo>();
        if (route is not null && IfEntry.Read(route.InterfaceIndex).Row is { } row) adapters.Add(IfEntry.InfoOf(row));
        var now = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var result = new LinkLogReader().Read(now - TimeSpan.FromDays(30), now, adapters, TimeSpan.FromSeconds(20));
        Assert.True(Enum.IsDefined(result.Overall));
        Out?.WriteLine($"로그: {LinkEvidenceBuilder.Text(result.Overall)} {result.OverallCode}, {watch.ElapsedMilliseconds} ms, 대응 이벤트 {result.Events.Count(e => e.Match == LinkMatch.Linked)}, "
            + $"미대응 {result.Events.Count(e => e.Match != LinkMatch.Linked)}, 해석 실패 {result.Unparsed}, "
            + string.Join(", ", adapters.Select(a => $"{a.Kind} 어댑터 {(result.PerAdapter.TryGetValue(a.Key, out var c) ? LinkEvidenceBuilder.Text(c.Capability) + " " + c.Code : "없음")}")));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "linklog-30d.json"), new JsonObject
        {
            ["overall"] = LinkEvidenceBuilder.Text(result.Overall), ["linked"] = result.Events.Count(e => e.Match == LinkMatch.Linked),
            ["linkedDown"] = result.Events.Count(e => e.Match == LinkMatch.Linked && e.State == LinkState.Down),
            ["other"] = result.Events.Count(e => e.Match != LinkMatch.Linked), ["unparsed"] = result.Unparsed, ["ms"] = watch.ElapsedMilliseconds,
            ["adapterSupported"] = adapters.Count > 0 && result.PerAdapter.TryGetValue(adapters[0].Key, out var cc) && cc.Capability == LinkCapability.Supported,
        }.ToJsonString());
    }

    [Fact]
    public async Task QuickDiagnosisFinishesWithinLimitsAndDefinedStates()
    {
        using var probes = new DiagnosticProbes();
        var settings = new DiagnosisSettings();
        var watch = Stopwatch.StartNew();
        var result = await QuickDiagnosis.RunAsync(probes, settings, () => DateTimeOffset.Now, TestContext.Current.CancellationToken);
        // 제한 합계: 어댑터 2 + 게이트웨이 1 (순서대로), 나머지는 동시 (최대 4.5) → 여유 1초
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8.5), $"{watch.Elapsed.TotalSeconds:F1}초");
        Assert.Equal(7, result.Checks.Count);
        Assert.All(result.Checks, c => Assert.True(Enum.IsDefined(c.Judgment)));
        Assert.InRange(result.Main.Number, 1, 12);
        var text = QuickDiagnosis.CopyText(result);
        Assert.DoesNotContain("1.1.1.1", text);
        Assert.DoesNotContain("www.", text);
        foreach (var c in result.Checks) Out?.WriteLine($"{QuickDiagnosis.CheckName(c.Id)}: {QuickDiagnosis.JudgmentName(c.Judgment)} {c.Code} {(long)c.Elapsed.TotalMilliseconds} ms");
        Out?.WriteLine($"결론 {result.Main.Number}");
    }
}
