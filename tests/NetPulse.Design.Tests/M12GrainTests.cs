using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

/// <summary>M12 설계 E: 흑요석 미세 결(시안 CSS 그대로)과 투명 효과 끔 표면의 물듦 제거.</summary>
public sealed class M12GrainTests
{
    [Fact]
    public void GrainMatchesTheMockupCss()
    {
        Assert.Equal((3.0, 0.6, 0.06), (Materials.Grain.Spacing, Materials.Grain.Radius, Materials.Grain.LayerOpacity));
        Assert.Equal(new Rgba(255, 255, 255, 0.8), Materials.Grain.Dot(ThemeKind.Dark));
        Assert.Equal(new Rgba(0, 0, 0, 0.7), Materials.Grain.Dot(ThemeKind.Light));
        Assert.Equal(0.048, Materials.GrainLayer(ThemeKind.Dark).MaxOpacity, 6);
        Assert.Equal(0.042, Materials.GrainLayer(ThemeKind.Light).MaxOpacity, 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void MaskHasOneDotPerCellWithTheDotArea(double scale)
    {
        // 30×30 DIP = 10×10 점. 덮임 합 ≈ 점 수 × π r² (물리 화소)
        var (w, h, s) = GrainMask.Size(30, 30, scale);
        Assert.Equal(scale, s);
        var mask = GrainMask.Build(w, h, s);
        double covered = mask.Sum(b => b / 255.0);
        double expected = 100 * Math.PI * Math.Pow(Materials.Grain.Radius * s, 2);
        Assert.InRange(covered, expected * 0.8, expected * 1.2);
        // 결정적
        Assert.Equal(mask, GrainMask.Build(w, h, s));
    }

    [Fact]
    public void LargeWindowsAreCappedInPixels()
    {
        var (w, h, s) = GrainMask.Size(3840, 2160, 2);
        Assert.True((long)w * h <= GrainMask.MaxPixels + w + h, $"{w}×{h}");
        Assert.True(s < 2);
        Assert.Equal((0, 0), (GrainMask.Size(0, 100, 1).Width, GrainMask.Size(0, 100, 1).Height));
    }

    [Fact]
    public void BgraIsPremultipliedDotColor()
    {
        var bgra = GrainMask.ToBgra([0, 255, 128], ThemeKind.Dark);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, bgra[..4]);
        Assert.Equal(new byte[] { 12, 12, 12, 12 }, bgra[4..8]);          // 255 × 0.048
        var light = GrainMask.ToBgra([255], ThemeKind.Light);
        Assert.Equal(new byte[] { 0, 0, 0, 11 }, light);                 // 검은 점 255 × 0.042
    }

    [Fact]
    public void BackgroundRangeIncludesTheGrainLayer()
    {
        var withGrain = BackgroundRange.For(ThemeKind.Dark, false, false);
        var lightsOnly = BackgroundRange.From(Palette.Dark.Bg, Materials.Lights(ThemeKind.Dark, false));
        Assert.Equal(lightsOnly.VertexCount * 2, withGrain.VertexCount);
        Assert.True(Contrast.Luminance(withGrain.High) >= Contrast.Luminance(lightsOnly.High));
    }

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void OpaqueSurfacesAreNeutralAndKeepTheirContrast(ThemeKind theme)
    {
        var ink = Palette.For(theme).Ink;
        foreach (var level in Enum.GetValues<GlassLevel>())
        {
            var s = Materials.OpaqueSurface(theme, level);
            Assert.True(s.R == s.G && s.G == s.B, $"{theme} {level} 무채색이 아님: {s}");
            Assert.True(Contrast.Ratio(ink, s) >= 4.5, $"{theme} {level} 대비 {Contrast.Ratio(ink, s):0.00}");
        }
    }

    [Fact]
    public void NeutralLikeNeverLowersContrast()
    {
        var ink = Palette.Dark.Ink;
        var purple = new Rgba(40, 30, 60);
        var gray = Materials.NeutralLike(purple, ink);
        Assert.True(Contrast.Ratio(ink, gray) >= Contrast.Ratio(ink, purple));
        Assert.True(Math.Abs(Contrast.Luminance(gray) - Contrast.Luminance(purple)) < 0.01);
    }
}
