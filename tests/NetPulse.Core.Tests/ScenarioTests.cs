using NetPulse.Core.Demo;
using NetPulse.Core.Measurement;

namespace NetPulse.Core.Tests;

public sealed class ScenarioTests
{
    private const string Valid = """
        {
          "version": 1, "name": "t", "seed": 11, "durationRounds": 600,
          "targets": [
            { "id": "gateway", "role": "local", "address": "192.0.2.1" },
            { "id": "ext-a", "role": "external", "address": "198.51.100.10" }
          ],
          "segments": [
            { "from": 0, "to": 600, "target": "*", "rtt": { "base": 18, "jitter": 6 } },
            { "from": 120, "to": 126, "target": "ext-a", "result": "lost" },
            { "from": 300, "to": 302, "target": "gateway", "result": "error" }
          ]
        }
        """;

    private static string With(string from, string to) => Valid.Replace(from, to);

    [Fact]
    public void ParsesValidScenario()
    {
        var s = Scenario.Parse(Valid);
        Assert.Equal(600, s.RoundCount);
        Assert.Equal(["gateway", "ext-a"], s.ProbeTargets.Select(t => t.Id));
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 2")]
    [InlineData("192.0.2.1\"", "1.2.3.4\"")]                      // 문서용 주소가 아님
    [InlineData("\"target\": \"gateway\"", "\"target\": \"nope\"")] // 없는 대상
    [InlineData("\"result\": \"lost\"", "\"result\": \"slow\"")]
    [InlineData("\"id\": \"ext-a\"", "\"id\": \"gateway\"")]          // id 중복
    [InlineData("\"role\": \"local\"", "\"role\": \"middle\"")]
    [InlineData("\"from\": 120, \"to\": 126", "\"from\": 126, \"to\": 126")]
    [InlineData("\"durationRounds\": 600", "\"durationRounds\": 0")]
    [InlineData("\"seed\": 11", "\"seed\": 11, \"unknown\": 1")]   // 모르는 항목
    public void RejectsInvalidScenario(string from, string to) =>
        Assert.Throws<ScenarioException>(() => Scenario.Parse(With(from, to)));

    [Fact]
    public void RejectsSegmentWithBothRttAndResult() =>
        Assert.Throws<ScenarioException>(() => Scenario.Parse(With("\"result\": \"lost\"", "\"result\": \"lost\", \"rtt\": { \"base\": 1 }")));

    [Fact]
    public void RoundCountFallsBackToLargestSegmentEnd()
    {
        // 배열의 마지막 구간이 아니라 to의 최댓값을 쓴다
        var s = Scenario.Parse(With("\"durationRounds\": 600,", ""));
        Assert.Equal(600, s.RoundCount);
    }

    [Fact]
    public void ResolvesBoundariesOverlapsAndGaps()
    {
        var s = Scenario.Parse(With("\"from\": 0, \"to\": 600, \"target\": \"*\"", "\"from\": 10, \"to\": 600, \"target\": \"*\""));
        var probe = new DemoProbe(s);
        Assert.Equal(ProbeOutcome.Error, probe.Resolve("ext-a", 9).Outcome);            // 구간 밖 → 측정 오류
        Assert.Equal("scenario-undefined", probe.Resolve("ext-a", 9).Status);
        Assert.Equal(ProbeOutcome.Success, probe.Resolve("ext-a", 10).Outcome);         // from 포함
        Assert.Equal(ProbeOutcome.Success, probe.Resolve("ext-a", 119).Outcome);
        Assert.Equal(ProbeOutcome.Lost, probe.Resolve("ext-a", 120).Outcome);           // 뒤에 적힌 구간이 이긴다
        Assert.Equal(ProbeOutcome.Lost, probe.Resolve("ext-a", 125).Outcome);
        Assert.Equal(ProbeOutcome.Success, probe.Resolve("ext-a", 126).Outcome);        // to 미포함
        Assert.Equal(ProbeOutcome.Success, probe.Resolve("gateway", 120).Outcome);      // 다른 대상은 영향 없음
        Assert.Equal(ProbeOutcome.Error, probe.Resolve("gateway", 300).Outcome);
        Assert.Equal(ProbeOutcome.Error, probe.Resolve("ext-a", 600).Outcome);          // 범위 밖
        // 흔들림은 이웃한 회차끼리도 다르다 (회차 끝자리만 다를 때 같은 값이 나오지 않게, M5)
        var values = Enumerable.Range(10, 100).Select(r => probe.Resolve("ext-a", r)).Where(x => x.Outcome == ProbeOutcome.Success).Select(x => x.RttMs).ToArray();
        int same = values.Zip(values.Skip(1)).Count(p => p.First == p.Second);
        Assert.True(same < values.Length / 5, $"이웃 회차 같은 값 {same}/{values.Length}");
    }

    [Fact]
    public void JitterIsDeterministicAndIndependentOfCallOrder()
    {
        var a = new DemoProbe(Scenario.Parse(Valid));
        var b = new DemoProbe(Scenario.Parse(Valid));
        var forward = Enumerable.Range(0, 50).Select(r => (a.Resolve("gateway", r), a.Resolve("ext-a", r))).ToArray();
        var backward = Enumerable.Range(0, 50).Reverse().Select(r => (b.Resolve("ext-a", r), b.Resolve("gateway", r))).Reverse().ToArray();
        Assert.Equal(forward.Select(x => x.Item1), backward.Select(x => x.Item2));
        Assert.Equal(forward.Select(x => x.Item2), backward.Select(x => x.Item1));
    }

    [Fact]
    public void JitterStaysWithinBoundsAndChangesWithSeed()
    {
        var s11 = new DemoProbe(Scenario.Parse(Valid));
        var s12 = new DemoProbe(Scenario.Parse(With("\"seed\": 11", "\"seed\": 12")));
        var values = Enumerable.Range(0, 100).Select(r => s11.Resolve("gateway", r).RttMs!.Value).ToArray();
        Assert.All(values, v => Assert.InRange(v, 12, 24));
        Assert.NotEqual(values, Enumerable.Range(0, 100).Select(r => s12.Resolve("gateway", r).RttMs!.Value));
    }

    [Fact]
    public void ShippedScenariosAreValid()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "scenarios");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files) Scenario.Load(file);
    }
}
