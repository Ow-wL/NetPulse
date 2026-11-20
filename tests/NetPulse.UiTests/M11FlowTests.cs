using System.IO;
using System.Text.Json;

namespace NetPulse.UiTests;

/// <summary>M11 설계 B4: 동의를 포함해 첫 측정까지의 사용자 조작 수 (N14 "세 단계 안", M10에서 동의 포함으로 정함).</summary>
public sealed class M11FlowTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    [Fact]
    public void FirstMeasurementIncludingConsentTakesAtMostThreeActions()
    {
        // 동의 기록도 프로필도 없는 첫 실행. 시작 인수(--start, --screen) 없이 사용자 조작만 센다
        int actions = 0;
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady");
        app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "terms", "사용 조건 화면");
        app.Invoke("terms.accept");
        actions++;
        app.FollowNextWindow();
        app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "firstrun", "첫 실행 안내");
        app.WaitDiag(d => d.GetProperty("screenDiag").ValueKind == JsonValueKind.Object && d.GetProperty("screenDiag").GetProperty("startEnabled").GetBoolean(), b => b, "시작 켜짐");
        app.Invoke("firstrun.start");
        actions++;
        app.WaitDiag(d => (M5Fixtures.Str(d, "screen"), M5Fixtures.Str(d, "host")), s => s == ("measure", "Measuring"), "측정 시작");
        File.WriteAllText(Path.Combine(G5Dir, "flow-first-measurement-with-consent.json"), JsonSerializer.Serialize(new { actions, steps = new[] { "terms.accept", "firstrun.start" } }));
        Assert.True(actions <= 3, $"동의 포함 {actions}단계");
        Assert.Equal(0, app.Close());
    }
}
