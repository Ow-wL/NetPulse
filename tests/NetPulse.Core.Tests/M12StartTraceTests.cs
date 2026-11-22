using NetPulse.Core.Live;

namespace NetPulse.Core.Tests;

/// <summary>M12 설계 B2: 시작 단계 기록의 해석. 정상 종료 표시가 없는 실행만 "끊김", 마지막 단계는 도달 지점.</summary>
public sealed class M12StartTraceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static string L(string run, string step, int sec, int? code = null) => StartTrace.Line(run, step, T0.AddSeconds(sec), code);

    [Fact]
    public void PreviousRunWithoutExitIsInterruptedAtItsLastReachedStep()
    {
        string[] lines = [L("a", "main", 0), L("a", "app", 1), L("a", "exit", 9, 0), L("b", "main", 10), L("b", "app", 11), L("b", "shell", 12)];
        var previous = StartTrace.PreviousInterrupted([.. lines, L("c", "main", 20)], "c");
        Assert.NotNull(previous);
        Assert.Equal("b", previous!.Run);
        Assert.Equal("shell", previous.LastStep);
        Assert.Equal(T0.AddSeconds(12), previous.LastAt);
        Assert.Equal(["main", "app", "shell"], previous.Steps);
    }

    [Fact]
    public void PreviousRunThatExitedIsNotInterruptedWhateverTheCode()
    {
        string[] lines = [L("a", "main", 0), L("a", "exit", 5, 70), L("c", "main", 20)];
        Assert.Null(StartTrace.PreviousInterrupted(lines, "c"));
        var a = StartTrace.Runs(lines)[0];
        Assert.True(a.Exited);
        Assert.Equal(70, a.ExitCode);
    }

    [Fact]
    public void NoEarlierRunMeansNothingToReport()
    {
        Assert.Null(StartTrace.PreviousInterrupted([], "c"));
        Assert.Null(StartTrace.PreviousInterrupted([L("c", "main", 0)], "c"));
    }

    [Fact]
    public void BrokenAndForeignLinesAreSkipped()
    {
        // 쓰는 도중 끊긴 마지막 줄, 형식이 다른 줄
        string[] lines = [L("a", "main", 0), "{\"run\":\"a\",\"st", "not json", "{\"run\":1,\"step\":\"x\"}", "", L("c", "main", 9)];
        var previous = StartTrace.PreviousInterrupted(lines, "c");
        Assert.Equal("a", previous!.Run);
        Assert.Equal("main", previous.LastStep);
    }

    /// <summary>G4 M12 1라운드 3: 숫자지만 int가 아닌 종료 코드(범위 밖·소수)는 예외 없이 "코드 없음"으로 읽는다.</summary>
    [Fact]
    public void OutOfRangeExitCodesAreReadAsNoCode()
    {
        string[] lines =
        [
            """{"run":"a","step":"main","t":"2026-11-22T09:00:00Z"}""",
            """{"run":"a","step":"exit","t":"2026-11-22T09:00:01Z","code":2147483648}""",
            """{"run":"b","step":"exit","t":"2026-11-22T09:00:02Z","code":1.5}""",
            """{"run":"c","step":"exit","t":"2026-11-22T09:00:03Z","code":-1}""",
        ];
        var runs = StartTrace.Runs(lines);
        Assert.Equal([("a", true, (int?)null), ("b", true, null), ("c", true, -1)], runs.Select(r => (r.Run, r.Exited, r.ExitCode)));
    }

    [Fact]
    public void LinesCarryOnlyRunStepTimeAndCode()
    {
        var line = StartTrace.Line("r1", "exit", T0, 0);
        var o = System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject();
        Assert.Equal(["run", "step", "t", "code"], o.Select(p => p.Key).ToArray());
        Assert.DoesNotContain("code", StartTrace.Line("r1", "main", T0));
    }

    [Fact]
    public void TrimKeepsTheLastLinesOnlyWhenTheFileIsLarge()
    {
        var many = Enumerable.Range(0, StartTrace.KeepLines + 50).Select(i => L("r" + i, "main", i)).ToArray();
        Assert.Null(StartTrace.Trimmed(many, StartTrace.TrimBytes));
        var kept = StartTrace.Trimmed(many, StartTrace.TrimBytes + 1)!;
        Assert.Equal(StartTrace.KeepLines, kept.Count);
        Assert.Equal(many[^1], kept[^1]);
        Assert.Null(StartTrace.Trimmed(many.Take(10).ToArray(), StartTrace.TrimBytes + 1));
    }
}
