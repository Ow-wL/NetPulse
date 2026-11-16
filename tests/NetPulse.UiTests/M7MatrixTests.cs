using System.IO;
using System.Text.Json;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M7 캡처 매트릭스와 배치 검사 (설계 7.4). 장면 목록은 m7-scenes.txt, 실행 방식은 M5 매트릭스와 같다.
/// </summary>
public sealed class M7MatrixTests
{
    public static TheoryData<string, string, string> Matrix()
    {
        var d = new TheoryData<string, string, string>();
        var only = Environment.GetEnvironmentVariable("NETPULSE_SCENES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var s in M5MatrixTests.Scenes("m7-scenes.txt").Where(s => only is not { Length: > 0 } || only.Contains(s.Id)))
            foreach (var t in new[] { "dark", "light" })
                foreach (var scale in new[] { "1", "1.5" })
                    d.Add(s.Id, t, scale);
        return d;
    }

    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    [Theory, MemberData(nameof(Matrix))]
    public void SceneLooksRightAndFits(string id, string theme, string scale)
    {
        var scene = M5MatrixTests.Scenes("m7-scenes.txt").Single(s => s.Id == id);
        using var app = M5MatrixTests.Open(scene, "--theme", theme, "--ui-scale", scale);
        if (!id.Contains("confirm", StringComparison.Ordinal)) app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        string name = $"m7-{id}-{theme}-{(scale == "1" ? "100" : "150")}";
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        var diag = app.Diag();
        File.WriteAllText(Path.Combine(G5Dir, "matrix-" + name + ".json"), JsonSerializer.Serialize(
            new { name, scene = id, theme, scale, file = Path.GetFileName(path), w = shot.Width, h = shot.Height, layoutProblems = problems, diag },
            new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Assert.Equal(0, app.Close());
    }
}
