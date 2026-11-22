using System.IO;
using System.Text.Json;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M12 설계 A: 측정 끝 타임라인 선 빠짐 (M11 시각 검토 C3). 끝난 스냅숏을 타임라인 배치 전(폭 0)에 받으면 열 1개로 계산한 장면이
/// 새 스냅숏 없이 남았다. 시험 옵션 <c>--test-finish-before-layout</c>로 그 조건을 만들고(우연한 재현에 기대지 않음),
/// 배치 뒤 장면 계산에 쓴 열 수가 실제 폭을 따라가는지 본다. 고치기 전 실행에서는 끝 상태 단언이 실패해야 한다.
/// </summary>
public sealed class M12TimelineTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static JsonElement State(JsonElement d) => d.GetProperty("screenDiag").GetProperty("timelineState");
    private static JsonElement Check(JsonElement d) => d.GetProperty("screenDiag").GetProperty("timelineCheck");
    private static int? Int(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Number ? e.GetProperty(name).GetInt32() : null;

    private static bool Matches(JsonElement d)
    {
        var t = State(d);
        double width = t.GetProperty("width").GetDouble();
        return t.GetProperty("hasFrame").GetBoolean() && width > 0 && Int(t, "frameColumns") == (int)Math.Round(width);
    }

    [Fact]
    public void FinishedMeasurementReceivedBeforeLayoutIsRecomputedForTheRealWidth()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "profile:outage-long");
        using var app = AppDriver.LaunchWith(userData, [.. M5Fixtures.Args("--demo outage-long --demo-speed 600 --start --test-finish-before-layout"), "--freeze-motion"]).OwnUserData();
        app.WaitDiag(d => M5Fixtures.Str(d, "screen") == "measure" && d.GetProperty("finished").ValueKind == JsonValueKind.Object
            && d.GetProperty("screenDiag").ValueKind == JsonValueKind.Object, b => b, "끝난 측정의 측정 화면");

        // 조건 성립: 끝난 스냅숏을 폭 0에서 받았다 (장면 계산과 무관하게 센 값)
        var first = app.WaitDiag(d => d, d => State(d).GetProperty("width").GetDouble() > 0, "타임라인 배치");
        var check = Check(first);
        Assert.True(check.GetProperty("receivedFinishedBeforeLayout").GetBoolean(), "끝난 스냅숏을 배치 전에 받지 않았다 (시험 조건 불성립)");
        Assert.Equal(0, check.GetProperty("widthAtReceive").GetDouble());

        // 끝 상태: 새 스냅숏 없이 장면이 실제 폭으로 다시 계산된다. 고치기 전에는 열 1개 장면이 남아 시간 초과로 실패한다
        JsonElement settled;
        try { settled = app.WaitDiag(d => d, Matches, "장면 열 수 = 타임라인 폭"); }
        finally
        {
            var last = app.Diag();
            File.WriteAllText(Path.Combine(G5Dir, "m12-timeline-finish-before-layout.json"),
                JsonSerializer.Serialize(new { state = State(last), check = Check(last) }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.Equal(0, Check(settled).GetProperty("snapshotsAfterLayout").GetInt32());

        // 끝난 측정에서 창 크기를 바꾸면 장면도 새 폭을 따른다
        double before = State(settled).GetProperty("width").GetDouble();
        app.ResizeWindow(Size.WindowWidth - 160, Size.WindowHeight);   // 줄이기 (화면 밖으로 나가지 않게)
        app.WaitDiag(d => d, d => Math.Abs(State(d).GetProperty("width").GetDouble() - before) >= 1 && Matches(d), "창 크기 변경 뒤 장면 열 수 = 새 폭");
        Assert.Equal(0, app.Close());
    }
}
