using System.Drawing;
using System.Text.Json;

namespace NetPulse.UiTests;

/// <summary>
/// 캡처 전 상태 기반 대기의 순수 판정 (M12 설계 G1-B). 앱 없이 돈다 (실행 파일·NETPULSE_EXE가 필요 없다, <see cref="AppDriverExitTests"/>와 같음).
/// </summary>
public sealed class SettleTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void ADiagWithoutSettleIsNotReportedAndNotSettled()
    {
        // 사용 조건 창처럼 settle 값이 없는 진단: 앱이 알리지 않았다 → 최대 대기까지 기다린다
        var r = Settle.Read(Json("""{"screen":"terms"}"""));
        Assert.False(r.Present);
        Assert.False(r.Settled);
    }

    [Fact]
    public void SettledComesOnlyFromTheAppFlag()
    {
        var yes = Settle.Read(Json("""{"settle":{"pendingWork":0,"finiteMotions":0,"generation":3,"sceneGeneration":1,"laidOut":true,"timelineOk":true,"settled":true}}"""));
        var no = Settle.Read(Json("""{"settle":{"pendingWork":1,"finiteMotions":0,"generation":3,"sceneGeneration":1,"laidOut":true,"timelineOk":true,"settled":false}}"""));
        var odd = Settle.Read(Json("""{"settle":{"settled":"true"}}"""));   // 형식이 다르면 끝나지 않은 것으로
        Assert.True(yes.Present && yes.Settled);
        Assert.True(no.Present && !no.Settled);
        Assert.False(odd.Settled);
    }

    [Fact]
    public void TheKeyChangesWhenTheSceneGenerationChanges()
    {
        // 두 캡처 앞뒤로 같은지 보는 값: 장면 세대가 바뀌면 달라야 한다
        var a = Settle.Read(Json("""{"settle":{"generation":3,"sceneGeneration":1,"settled":true}}"""));
        var b = Settle.Read(Json("""{"settle":{"generation":4,"sceneGeneration":1,"settled":true}}"""));
        var c = Settle.Read(Json("""{"settle":{"generation":3,"sceneGeneration":1,"settled":true}}"""));
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(a.Key, c.Key);
    }

    [Fact]
    public void SameImageNeedsEveryPixelAndTheSameSize()
    {
        using var a = Filled(40, 30, Color.FromArgb(255, 10, 20, 30));
        using var b = Filled(40, 30, Color.FromArgb(255, 10, 20, 30));
        using var onePixel = Filled(40, 30, Color.FromArgb(255, 10, 20, 30));
        onePixel.SetPixel(39, 29, Color.FromArgb(255, 10, 20, 31));
        using var smaller = Filled(40, 29, Color.FromArgb(255, 10, 20, 30));
        Assert.True(Settle.SameImage(a, b));
        Assert.False(Settle.SameImage(a, onePixel));
        Assert.False(Settle.SameImage(a, smaller));
    }

    private static Bitmap Filled(int w, int h, Color c)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }
}
