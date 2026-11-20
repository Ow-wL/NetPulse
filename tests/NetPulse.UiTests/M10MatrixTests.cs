using System.IO;
using System.Text.Json;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M10 G5 (설계 4절 상태표, 6절): 사용 조건 화면·정보 카드·라이선스와 고지 장면 × 테마 × 배율, 고대비·효과·움직임 줄이기,
/// 데모 캡처의 UIA 글에 실제 경로·사용자 이름이 없는지 (M9와 같은 검사).
/// </summary>
public sealed class M10MatrixTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static M5MatrixTests.Scene Scene(string id) => M5MatrixTests.Scenes("m10-scenes.txt").Single(s => s.Id == id);

    public static TheoryData<string, string, string> Matrix()
    {
        var d = new TheoryData<string, string, string>();
        var only = Environment.GetEnvironmentVariable("NETPULSE_SCENES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var s in M5MatrixTests.Scenes("m10-scenes.txt").Where(s => only is not { Length: > 0 } || only.Contains(s.Id)))
            foreach (var t in new[] { "dark", "light" })
                foreach (var scale in new[] { "1", "1.5" })
                    d.Add(s.Id, t, scale);
        return d;
    }

    private static void Shoot(string name, string sceneId, string[] extra, object meta)
    {
        using var app = M5MatrixTests.Open(Scene(sceneId), extra);
        if (!sceneId.StartsWith("terms", StringComparison.Ordinal) && !sceneId.StartsWith("about-legal", StringComparison.Ordinal)) app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        var privacy = M9MatrixTests.PrivateText(app.Window);
        Record("matrix-" + name, new { name, scene = sceneId, meta, file = Path.GetFileName(path), w = shot.Width, h = shot.Height, layoutProblems = problems, privacy, diag = app.Diag() });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(privacy.Count == 0, string.Join("\n", privacy));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Assert.Equal(0, app.Close());   // 사용 조건 화면에서 닫기 = 동의하지 않음(종료 코드 0)
    }

    [Theory, MemberData(nameof(Matrix))]
    public void SceneLooksRightAndFits(string id, string theme, string scale) =>
        Shoot($"m10-{id}-{theme}-{(scale == "1" ? "100" : "150")}", id, ["--theme", theme, "--ui-scale", scale], new { theme, scale });

    public static TheoryData<string, string, string> Extras() => new()
    {
        { "m10-terms-hc-aquatic", "terms", "--simulate-high-contrast aquatic" },
        { "m10-terms-hc-desert", "terms", "--simulate-high-contrast desert" },
        { "m10-about-legal-expanded-hc-aquatic", "about-legal-expanded", "--simulate-high-contrast aquatic" },
        { "m10-about-legal-expanded-hc-desert", "about-legal-expanded", "--simulate-high-contrast desert" },
        { "m10-terms-dark-reduced", "terms", "--theme dark --simulate-transparency-off" },
        { "m10-about-legal-expanded-light-reduced", "about-legal-expanded", "--theme light --simulate-transparency-off" },
        { "m10-terms-light-reducedmotion", "terms", "--theme light --simulate-animations-off" },
        { "m10-about-legal-expanded-dark-reducedmotion", "about-legal-expanded", "--theme dark --simulate-animations-off" },
    };

    [Theory, MemberData(nameof(Extras))]
    public void ExtraSceneRenders(string name, string sceneId, string extra) => Shoot(name, sceneId, extra.Split(' '), new { extra });
}
