namespace NetPulse.Core.Tests;

/// <summary>기록 줄 읽기 (M7 UI 시험 1차): 줄 단위 읽기가 예전 Split('\n') 결과와 같고, 끊긴 마지막 줄을 알린다.</summary>
public sealed class RecordLinesTests
{
    private static (string Line, bool Tail)[] Read(string text) => [.. NetPulse.Core.Records.RecordReader.Lines(new StringReader(text))];

    private static (string Line, bool Tail)[] BySplit(string text)
    {
        var lines = text.Split('\n');
        bool endsClean = text.Length == 0 || text.EndsWith('\n');
        return [.. lines.Select((l, i) => (l, i == lines.Length - 1 && !endsClean))];
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("a\nb\n")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\n\nb\n")]
    [InlineData("a\rb\n")]
    public void MatchesTheOldSplitAndMarksAnUnterminatedTail(string text) =>
        Assert.Equal(BySplit(text).Where(x => x.Item1.Length > 0), Read(text).Where(x => x.Line.Length > 0));

    [Fact]
    public void LinesLongerThanTheBufferAndCrossingItStayWhole()
    {
        var a = new string('x', 40_000);
        var b = new string('y', 16 * 1024 - 1);
        var lines = Read(a + "\n" + b + "\n" + "끝");
        Assert.Equal([(a, false), (b, false), ("끝", true)], lines);
    }
}
