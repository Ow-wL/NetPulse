using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>회차 줄 판정 (M7 UI 시험 1차: JsonDocument로 바꾼 뒤에도 같은 판정, 예외로 새던 줄은 깨진 줄로).</summary>
public sealed class RoundLineTests
{
    private static readonly HashSet<string> Targets = ["a", "b"];

    [Theory]
    [InlineData("{\"id\":\"m\",\"id\":\"m\",\"r\":1,\"due\":0,\"at\":0,\"wall\":\"2026-11-15T00:00:00+09:00\",\"results\":[]}")]       // 같은 이름 속성: 예전에는 ArgumentException이 새어 기록 전체를 못 읽을 수 있었다
    [InlineData("{\"id\":\"m\",\"r\":1,\"due\":0,\"at\":0,\"results\":[]}")]    // wall 없음: 예전에는 ArgumentNullException
    [InlineData("{\"id\":\"m\",\"r\":1,\"due\":0,\"at\":0,\"wall\":\"2026-11-15T00:00:00+09:00\",\"results\":[{\"t\":\"a\",\"t\":\"a\",\"s\":\"lost\"}]}")]
    [InlineData("[1]")]
    [InlineData("{")]
    public void BrokenLinesAreCountedAsMalformedInsteadOfThrowing(string line) =>
        Assert.Equal(RecordSchema.LineProblem.Malformed, RecordSchema.ParseRound(line, "m", Targets).Problem);

    [Fact]
    public void AResultWithoutATargetIsAnUnknownTargetAsBefore() =>
        Assert.Equal(RecordSchema.LineProblem.UnknownTarget, RecordSchema.ParseRound("{\"id\":\"m\",\"r\":1,\"due\":0,\"at\":0,\"wall\":\"2026-11-15T00:00:00+09:00\",\"results\":[{\"s\":\"lost\"}]}", "m", Targets).Problem);

    [Fact]
    public void AGoodLineKeepsEveryValueAndNullCountsAsAbsent()
    {
        var (r, p) = RecordSchema.ParseRound("{\"id\":\"m\",\"r\":1,\"due\":0,\"at\":5,\"wall\":\"2026-11-15T00:00:00+09:00\",\"gap\":null,\"results\":[{\"t\":\"a\",\"s\":\"ok\",\"ms\":1.5},{\"t\":\"b\",\"s\":\"error\",\"e\":\"x\"}]}", "m", Targets);
        Assert.Equal(RecordSchema.LineProblem.None, p);
        Assert.NotNull(r);
        Assert.Equal((1L, 0L, 5L, false, 0L), (r.Round, r.DueMs, r.AtMs, r.Gap, r.Skipped));
        Assert.Equal(1.5, r.Results[0].RttMs);
        Assert.Equal("x", r.Results[1].Error);
    }
}
