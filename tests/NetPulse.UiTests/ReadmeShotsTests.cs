using System.Drawing.Imaging;
using System.IO;

namespace NetPulse.UiTests;

/// <summary>
/// README 화면 캡처 (M10 설계 C [1-10]). 게이트 밖(<c>Kind=Docs</c>): <c>build/readme-shots.ps1</c>이 이 시험만 돌려
/// 비추적 폴더(artifacts/readme-shots/&lt;sha10&gt;)에 찍는다. 사람이 고른 것만 docs/images로 옮겨 커밋한다.
/// 데모 모드, 100%, 같은 창 크기, 창만 캡처(PrintWindow), 기존 G5 장면을 그대로 쓴다. 캡처마다 UIA 글에 실제 경로·이름이 없는지 본다.
/// </summary>
[Trait("Kind", "Docs")]
public sealed class ReadmeShotsTests
{
    public static TheoryData<string, string, string, string> Shots() => new()
    {
        { "home", "m5-scenes.txt", "home-measuring", "dark" },
        { "home-light", "m5-scenes.txt", "home-measuring", "light" },
        { "measure", "m5-scenes.txt", "measure-outage", "dark" },
        { "diagnose", "m6-scenes.txt", "diagnose-dns", "dark" },
        { "records", "m7-scenes.txt", "records-detail-outages", "dark" },
        { "report", "m8-scenes.txt", "report-preview", "dark" },
        { "settings", "m10-scenes.txt", "settings-about", "dark" },
        { "terms", "m10-scenes.txt", "terms", "dark" },
    };

    [Theory, MemberData(nameof(Shots))]
    public void Shoot(string name, string file, string sceneId, string theme)
    {
        var dir = Environment.GetEnvironmentVariable("NETPULSE_README_OUT") is { Length: > 0 } d ? d : Path.Combine(AppDriver.OutputDirectory, "readme");
        Directory.CreateDirectory(dir);
        var scene = M5MatrixTests.Scenes(file).Single(s => s.Id == sceneId);
        using var app = M5MatrixTests.Open(scene, "--theme", theme, "--ui-scale", "1");
        if (sceneId != "terms") app.CloseToasts();
        Thread.Sleep(900);
        var privacy = M9MatrixTests.PrivateText(app.Window);
        Assert.True(privacy.Count == 0, string.Join("\n", privacy));
        using var shot = app.CaptureBitmap();
        shot.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        Assert.Equal(0, app.Close());
    }
}
