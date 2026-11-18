using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Analysis;
using NetPulse.Core.Demo;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Evidence;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Report;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

/// <summary>
/// M8 시험용 기록 (설계 7.1). 이름·사업자에 식별 값(주소·전화·이메일·UNC)을 넣어 가린 문서에 원본 값이 남는지 본다.
/// 주소는 문서용 주소만 쓴다.
/// </summary>
internal static class M8Records
{
    public static readonly DateTimeOffset W0 = new(2026, 11, 13, 4, 20, 0, TimeSpan.Zero);
    public const string Id = "20261113T042000Z_k3x9qa";
    public const string Tail = "k3x9qa";

    public static readonly Target Router = new("gw-home", "192.0.2.254 우리집 공유기 010-1234-5678", IPAddress.Parse("192.0.2.254"), TargetRole.Local, null, ProbeMethod.Icmp);
    public static readonly Target ExtA = new("isp-a1", "회사 VPN 게이트웨이 user@example.com", IPAddress.Parse("198.51.100.7"), TargetRole.External, "예시 통신사 가나", ProbeMethod.Tcp(443));
    public static readonly Target ExtB = new("isp-b2", @"\\fileserver\share 서버", IPAddress.Parse("203.0.113.9"), TargetRole.External, "Example Telecom B", ProbeMethod.Icmp);
    public static readonly Target[] Three = [Router, ExtA, ExtB];

    public static RecordHeader Header(int rounds = 600, IReadOnlyList<Target>? targets = null) =>
        new(Id, 0, "1.0", W0, 540, targets ?? Three, 1000, 800, rounds * 1000L, "primary", ExpectedConnection.Wired);

    /// <summary>대상마다 한 글자씩: o 성공, x 손실, e 오류, _ 결과 없음.</summary>
    public static List<RoundData> Rounds(string router, string a, string b, double rtt = 12)
    {
        var list = new List<RoundData>();
        for (int r = 0; r < router.Length; r++)
        {
            var results = new List<TargetResult>();
            foreach (var (id, p) in new[] { (Router.Id, router), (ExtA.Id, a), (ExtB.Id, b) })
                switch (p[r])
                {
                    case 'o': results.Add(new(id, ProbeOutcome.Success, rtt + (r % 5), null)); break;
                    case 'x': results.Add(new(id, ProbeOutcome.Lost, null, null)); break;
                    case 'e': results.Add(new(id, ProbeOutcome.Error, null, @"D:\np-test-logs\probe.log")); break;
                }
            long at = r * 1000L;
            list.Add(new RoundData(r, at, at + 3, W0.AddMilliseconds(at), 0, false, results, at + 40));
        }
        return list;
    }

    public static string Ok(int n) => new('o', n);
    public static string Lost(int n) => new('x', n);

    /// <summary>외부 둘이 함께 n회차마다 1회차 손실 (끊김이 많은 기록).</summary>
    public static string Every(int length, int period) => string.Concat(Enumerable.Range(0, length).Select(i => i % period == period - 1 ? 'x' : 'o'));

    public static IEnumerable<JsonObject> Routes(string kind = "wired") => Three.Select(t => Ev(Id, "route", 0, 0, W0, new JsonObject
    {
        ["target"] = t.Id, ["status"] = "ok", ["interfaceIndex"] = 7, ["kind"] = kind,
    }));

    public static JsonObject Ev(string id, string type, long? round, long atMs, DateTimeOffset wall, JsonObject fields) =>
        JsonNode.Parse(RecordSchema.EventLine(id, type, round, atMs, wall, fields))!.AsObject();

    public static JsonObject Mark(long atMs, string? memo, string mark = "m1")
    {
        var fields = new JsonObject { ["mark"] = mark };
        if (memo is not null) fields["memo"] = memo;
        return Ev(Id, SymptomMarks.MarkType, atMs / 1000, atMs, W0.AddMilliseconds(atMs), fields);
    }

    public static RecordSummary Summary(int rounds, EndReason end = EndReason.Completed) =>
        new(Id, end, W0.AddSeconds(rounds), rounds, rounds, 0, 0, 0, rounds - 1, false, null, 0, rounds * 1000L);

    public static MeasurementRecord Record(List<RoundData> rounds, IEnumerable<JsonObject>? events = null, bool summary = true,
        IReadOnlyList<GapSpan>? gaps = null, DamageCounts? damage = null, IReadOnlyList<Target>? targets = null)
    {
        var header = Header(rounds.Count, targets);
        var s = summary ? Summary(rounds.Count) : null;
        return new MeasurementRecord(damage is { Total: > 0 } ? RecordStatus.Damaged : RecordStatus.Ok, null, header, rounds, gaps ?? [],
            (events ?? Routes()).ToArray(), s, s?.EndReason ?? EndReason.Interrupted, damage ?? new DamageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0), [], null, null);
    }

    public static AnalysisResult Analyze(MeasurementRecord r) => MeasurementAnalysis.Analyze(r.Header!, r.Rounds, r.Gaps, r.Events, r.Summary);

    public static ReportInput Input(MeasurementRecord r, ReportOptions? options = null, AnalysisResult? a = null,
        RecordLifecycle life = RecordLifecycle.Ended, IReadOnlyList<StoredDiagnosis>? diagnoses = null) =>
        new("key-1", "stamp-1", r, a ?? Analyze(r), life, diagnoses ?? [], options ?? new ReportOptions(), W0.AddHours(1), "test");

    public static ReportDocument Build(MeasurementRecord r, ReportOptions? options = null, AnalysisResult? a = null,
        RecordLifecycle life = RecordLifecycle.Ended, IReadOnlyList<StoredDiagnosis>? diagnoses = null) =>
        ReportBuilder.Build(Input(r, options, a, life, diagnoses));

    /// <summary>흔한 기록: 10분, 외부 둘 함께 끊긴 5회차 하나 + 혼합 끊김 하나 + 메모.</summary>
    public static MeasurementRecord Typical(string memo = "회의 중 끊김")
    {
        string a = Ok(100) + Lost(5) + Ok(195) + Lost(3) + Lost(2) + Ok(295);
        string b = Ok(100) + Lost(5) + Ok(195) + Lost(3) + Ok(2) + Ok(295);
        return Record(Rounds(Ok(600), a, b), Routes().Append(Mark(102_000, memo)));
    }

    public static StoredDiagnosis Diagnosis(DateTimeOffset at, string? measurementId = Id, int main = 1) =>
        new(DiagnosisReadStatus.Ok, null, at, at.AddSeconds(8), "1.0", measurementId,
            Enum.GetValues<CheckId>().Select(c => new StoredCheck(c, CheckJudgment.Normal, false, RunState.Run, null, 100, [])).ToArray(), main, []);

    /// <summary>링크 근거 네 상태 + 보조 사유 (원문 사유 코드에 경로를 넣어 둔다).</summary>
    public static LinkEvidence FourStates() => new(
        [
            new LinkSummary("a1", LinkSource.Direct, LinkDisplay.Down, LinkCapability.Supported, null, 0.97, false, []),
            new LinkSummary("a1", LinkSource.Log, LinkDisplay.Denied, LinkCapability.Denied, "log-denied", null, false, ["log-denied"]),
            new LinkSummary("a2", LinkSource.Direct, LinkDisplay.Unsupported, LinkCapability.Supported, null, 0.5, true, ["partial", "overflow"]),
            new LinkSummary("a2", LinkSource.Log, LinkDisplay.ObservedNone, LinkCapability.Supported, @"D:\np-test-logs\driver.etl", null, false, [@"D:\np-test-logs\driver.etl"]),
        ],
        [new DownSpan("a1", LinkSource.Direct, 100_000, 104_000, true)],
        new Dictionary<string, AdapterAt> { ["a1"] = new("a1", 7, "wired", false), ["a2"] = new("a2", 12, "wireless", false) }, 0, 0);

    /// <summary>
    /// 데모 시나리오 하나를 제품 경로(세션·기록·읽기)로 돌린 기록 (M5 시험과 같은 방식). 빠르게: 처리 속도 진행.
    /// </summary>
    public static async Task<MeasurementRecord> Demo(TempDir dir, string scenario, int speed = 600)
    {
        var s = Scenario.Load(Path.Combine(AppContext.BaseDirectory, "scenarios", scenario + ".json"));
        var world = new DemoWorld(s, new DemoTimeBase(TestSupport.Start, speed));
        var root = new DiskRecordRoot(dir.Sub(scenario + "-p"), "primary");
        var fallback = new DiskRecordRoot(dir.Sub(scenario + "-f"), "fallback");
        MeasurementSession? session = null;
        var run = world.NewRun(root, fallback, new DemoRunRules(), () => session is null || session.Engine.State.Rounds - session.Status.Observed < 20);
        var profile = world.Detect().Proposal ?? throw new InvalidOperationException("제안이 없는 시나리오: " + scenario);
        session = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = run.Clock, ProbeFor = run.ProbeFor, Primary = run.Primary, Fallback = run.Fallback,
            Routes = run.Network, Links = run.Network, Power = run.Power, LinkLogSettle = TimeSpan.Zero,
        });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        return RecordReader.Read(RecordReader.FindSegments(session.Id, [root.Path!, fallback.Path!]));
    }
}
