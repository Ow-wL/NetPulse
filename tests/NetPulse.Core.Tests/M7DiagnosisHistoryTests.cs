using NetPulse.Core.Demo;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;

namespace NetPulse.Core.Tests;

/// <summary>진단 기록 목록과 지난 진단 다시 보기 (M7 설계 3.6 [1-5]).</summary>
public sealed class M7DiagnosisHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 13, 20, 0, TimeSpan.FromHours(9));
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "np-m7d-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static Scenario Load(string diagnosis)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json"));
        return Scenario.Parse(text.Replace("\"version\": 1,", "\"version\": 1, \"diagnosis\": " + diagnosis + ","));
    }

    private static async Task<DiagnosisResult> Run(string diagnosis, DateTimeOffset at)
    {
        var s = Load(diagnosis);
        var tb = new DemoTimeBase(Start, 1);
        tb.Freeze(TimeSpan.FromSeconds(10));
        var settings = DemoDiagnosticProbes.Settings(s) with
        {
            AdapterTimeout = TimeSpan.FromMilliseconds(300), GatewayTimeout = TimeSpan.FromMilliseconds(300), TcpTimeout = TimeSpan.FromMilliseconds(300),
            DnsTimeout = TimeSpan.FromMilliseconds(300), HttpsTimeout = TimeSpan.FromMilliseconds(300), PortalTimeout = TimeSpan.FromMilliseconds(300),
            LinkTimeout = TimeSpan.FromMilliseconds(300),
        };
        return await QuickDiagnosis.RunAsync(new DemoDiagnosticProbes(new DemoWorld(s, tb)), settings, () => at, Ct);
    }

    private const string Ok = """{"gateway": "ok", "tcp": ["ok", "ok"], "dns": ["ok", "ok"], "https": ["ok", "ok"], "link": {"now": "up", "log": "supported", "recentDown": false}}""";
    private const string Dns = """{"gateway": "ok", "tcp": ["ok", "ok"], "dns": ["fail", "fail"], "https": ["fail", "fail"], "link": {"now": "up", "log": "supported", "recentDown": false}}""";

    [Fact]
    public async Task ListIsNewestFirstAndCountsUnreadableRecords()
    {
        var store = new DiagnosisStore(dir);
        store.Save(await Run(Ok, Start), "t", null);
        store.Save(await Run(Dns, Start.AddMinutes(5)), "t", "m-1");
        store.Save(await Run(Ok, Start.AddMinutes(5)), "t", null);   // 같은 초 → -2 접미사, 더 최근으로 본다
        File.WriteAllText(Path.Combine(dir, "diagnosis-20261113T000000Z-9.json"), "{ 망가짐");
        var (items, skipped) = store.List();
        Assert.Equal(3, items.Count);
        Assert.Equal(1, skipped);
        Assert.Equal([11, 3, 11], items.Select(i => i.Stored.MainConclusion));
        Assert.EndsWith("-2.json", items[0].File);
        Assert.Equal("m-1", items[1].Stored.MeasurementId);
        Assert.True(StoredDiagnosisView.Row(items[1].Stored).DuringMeasurement);
        var latest = store.Latest().Latest!;   // 다시 읽어 배열 참조가 달라 값으로 비교
        Assert.Equal((items[0].Stored.StartedAt, items[0].Stored.MainConclusion, items[0].Stored.MeasurementId), (latest.StartedAt, latest.MainConclusion, latest.MeasurementId));
    }

    [Fact]
    public async Task StoredViewUsesTheSavedConclusionAndExplainsOnlyWhenItStillMatches()
    {
        var store = new DiagnosisStore(dir);
        var save = store.Save(await Run(Dns, Start), "t", null);
        var stored = DiagnosisRecord.Parse(File.ReadAllText(save.File!));
        var v = StoredDiagnosisView.From(stored);
        Assert.Equal(3, v.Number);
        Assert.Equal(DiagnosisTexts.Head(3).Head, v.Head);
        Assert.NotNull(v.Found);
        Assert.NotEmpty(v.Steps);
        Assert.Null(v.Mismatch);
        Assert.All(v.Checks.SelectMany(c => c.Targets), t => Assert.Null(t.Shown));   // 지금 설정의 주소를 붙이지 않음
        Assert.All(v.Checks.SelectMany(c => c.Targets), t => Assert.StartsWith("대상 ", t.Label));

        // 저장된 번호와 다시 낸 결론이 다르면: 저장 번호가 주, 설명·해볼 것은 붙이지 않는다
        var altered = File.ReadAllText(save.File!).Replace("\"conclusion\": 3", "\"conclusion\": 6");
        var other = StoredDiagnosisView.From(DiagnosisRecord.Parse(altered));
        Assert.Equal(6, other.Number);
        Assert.Equal(DiagnosisTexts.Head(6).Head, other.Head);
        Assert.Null(other.Found);
        Assert.Empty(other.Steps);
        Assert.Equal(StoredDiagnosisView.MismatchText, other.Mismatch);
    }
}
