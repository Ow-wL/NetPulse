using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;

namespace NetPulse.Core.Tests;

/// <summary>"○ 이상" 하한 길이는 올리지 않는다 (M11 G4 1라운드: 59.6초가 "1분 이상"이 되면 최소 길이보다 크게 말한다).</summary>
public sealed class M11DurationTests
{
    [Theory]
    [InlineData(0, "0초")]
    [InlineData(59_600, "59초")]
    [InlineData(60_000, "1분")]
    [InlineData(65_999, "1분 5초")]
    [InlineData(3_599_600, "59분 59초")]
    [InlineData(3_600_000, "1시간")]
    [InlineData(3_659_999, "1시간")]
    [InlineData(3_719_000, "1시간 1분")]
    public void LowerBoundIsNeverRoundedUp(long ms, string expected) => Assert.Equal(expected, TimelineModel.AtLeast(ms));

    [Fact]
    public void PlainDurationKeepsRounding() => Assert.Equal("1분", TimelineModel.Seconds(59_600));

    [Theory]
    [InlineData("outage-long", 59_600, "인터넷이 59초 이상 끊긴 것으로 보입니다.")]
    [InlineData("outage-long", 3_599_600, "인터넷이 59분 59초 이상 끊긴 것으로 보입니다.")]
    [InlineData("outage", 754_400, "인터넷 연결이 2번 끊긴 것으로 보입니다 (최장 12분 34초 이상).")]
    public void ConclusionSentenceUsesTheLowerBound(string reason, long longestMinMs, string expected)
    {
        VerdictStats[] stats = [new(Verdict.ExternalOutage, 2, longestMinMs, longestMinMs, 0), new(Verdict.LinkDown, 0, 0, 0, 0)];
        var coverage = new Coverage(100, 100, 100, 0, 0, 0, 0, 0);
        Assert.Equal(expected, Sentences.For(Grade.Bad, reason, stats, coverage));
    }
}
