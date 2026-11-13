namespace NetPulse.Design.Tests;

public sealed class ContrastTests
{
    [Theory]
    [InlineData("#000000", "#ffffff", 21.0)]
    [InlineData("#ffffff", "#ffffff", 1.0)]
    [InlineData("#767676", "#ffffff", 4.54)]   // WCAG 예시: 흰 바탕에서 4.5:1을 넘는 가장 밝은 회색
    [InlineData("#777777", "#ffffff", 4.48)]
    public void RatioMatchesWcagReferenceValues(string a, string b, double expected) =>
        Assert.Equal(expected, Contrast.Ratio(Rgba.Hex(a), Rgba.Hex(b)), 2);

    [Fact]
    public void AlphaCompositingMixesChannels()
    {
        var half = Rgba.White(0.5).Over(Rgba.Hex("#000000"));
        Assert.Equal(new Rgba(128, 128, 128), half);
    }

    [Fact]
    public void WorstCasePicksTheLeastContrastingBackground()
    {
        // 반투명 흰 유리 위 검은 글자: 배경이 어두울수록 표면이 어두워져 대비가 낮다
        var worst = Contrast.Worst(Rgba.Hex("#111111"), Rgba.White(0.7), [Rgba.Hex("#ffffff"), Rgba.Hex("#000000"), Rgba.Hex("#808080")]);
        Assert.Equal(Rgba.Hex("#000000"), worst.Background);
        Assert.True(worst.Ratio < Contrast.Ratio(Rgba.Hex("#111111"), Rgba.Hex("#ffffff")));
    }

    [Fact]
    public void TranslucentBackgroundIsRejected() =>
        Assert.Throws<ArgumentException>(() => Rgba.White(0.5).Over(Rgba.Black(0.5)));
}
