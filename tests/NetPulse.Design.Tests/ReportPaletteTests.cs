using NetPulse.Core.Report;
using NetPulse.Design;
using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

/// <summary>리포트 문서 팔레트 = 디자인 토큰 라이트 팔레트 (M8 설계 4.1 [1-6], 7.1).</summary>
public sealed class ReportPaletteTests
{
    [Fact]
    public void TheDocumentPaletteIsTheLightPalette()
    {
        var doc = ReportPalette.Document;
        var light = Palette.Light;
        Assert.Equal(light.Bg, Rgba.Hex(doc.Bg));
        Assert.Equal(light.Ink, Rgba.Hex(doc.Ink));
        Assert.Equal(light.Muted, Rgba.Hex(doc.Muted));
        Assert.Equal(light.Accent, Rgba.Hex(doc.Accent));
        Assert.Equal(light.Ok, Rgba.Hex(doc.Ok));
        Assert.Equal(light.Warn, Rgba.Hex(doc.Warn));
        Assert.Equal(light.Bad, Rgba.Hex(doc.Bad));
        Assert.Equal(light.FocusOuter, Rgba.Hex(doc.Focus));
    }

    [Fact]
    public void DocumentTextIsReadableOnThePaper()
    {
        var doc = ReportPalette.Document;
        var paper = Rgba.Hex(doc.Surface);
        foreach (var (name, hex) in new[] { ("ink", doc.Ink), ("muted", doc.Muted), ("accent", doc.Accent), ("bad", doc.Bad), ("warn", doc.Warn) })
            Assert.True(Contrast.Ratio(Rgba.Hex(hex), paper) >= 4.5, name);
        Assert.True(Contrast.Ratio(Rgba.Hex(doc.Ink), Rgba.Hex(doc.BadSoft)) >= 4.5, "경고 띠 글자");
        Assert.True(Contrast.Ratio(Rgba.Hex(doc.Muted), Rgba.Hex(doc.Bg)) >= 4.5, "표 머리");
    }
}
