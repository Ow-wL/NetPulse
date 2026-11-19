using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace NetPulse.UiTests;

/// <summary>
/// M9에서 코드로만 고친 경우의 UI 시험 (M10 설계 D3 [1-8]). 시험 전용 멈춤 지점(<c>--memo-hold</c>, <c>--records-apply-hold</c>)으로
/// 실제 경쟁 순서를 만들고, 진단 값 <c>holds</c>로 멈춘 것을 확인한 뒤 숨은 단추 <c>diag.release</c>로 푼다.
/// </summary>
public sealed class M9GapTests
{
    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");

    private static int Hold(JsonElement d, string name, string what) =>
        d.TryGetProperty("holds", out var h) && h.ValueKind == JsonValueKind.Object && h.TryGetProperty(name, out var x) ? x.GetProperty(what).GetInt32() : 0;

    private static void WaitHeld(AppDriver app, string name) => app.WaitDiag(d => Hold(d, name, "entered"), n => n >= 1, "멈춤 " + name);

    private static string Focused(AppDriver app) => app.WaitDiag(d => d.GetProperty("focused").ToString(), _ => true, "초점");

    private static string Value(AppDriver app, string id) => ((ValuePattern)app.Find(id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    private static string MemoState(AppDriver app, int n) => app.Exists($"records.mark.{n}.state") ? app.Text($"records.mark.{n}.state") : "";

    private static AppDriver OpenRecords(string userData, string prep, params string[] extra)
    {
        M5Fixtures.Prepare(userData, prep);
        var app = AppDriver.LaunchWith(userData, ["--demo", "steady", "--screen", "records", "--freeze-motion", .. extra]);
        app.WaitDiag(d => Sd(d).ValueKind == JsonValueKind.Object && Sd(d).TryGetProperty("listed", out var l) && l.GetBoolean() && Sd(d).GetProperty("analyzing").GetInt32() == 0, b => b, "기록 목록 분석");
        return app;
    }

    private static void WaitDetail(AppDriver app) =>
        app.WaitDiag(d => Sd(d).TryGetProperty("detailComplete", out var c) && c.GetBoolean(), b => b, "기록 상세");

    /// <summary>M7 이월 2: 메모 A의 저장 완료(목록 갱신·다시 열기 포함)가 칸 B를 고치던 초점과 글을 빼앗지 않는다.</summary>
    [Fact]
    public void SavingOneMemoKeepsFocusAndTextInTheOtherMemo()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = OpenRecords(userData, "rec-two-memos", "--memo-hold", "1");
            app.Invoke("records.row.0");
            WaitDetail(app);
            app.Invoke("records.mark.0.edit");
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text"), e => e, "메모 A 칸");
            app.SetText("records.mark.0.text", "A 새 글");
            app.Invoke("records.mark.0.save");
            WaitHeld(app, "memo");                                                  // A는 쓰기를 마치고 화면 반영 직전에 멈춤
            app.Invoke("records.mark.1.edit");
            AppDriver.WaitFor(() => app.Exists("records.mark.1.text"), e => e, "메모 B 칸");
            app.Find("records.mark.1.text").SetFocus();
            app.SetText("records.mark.1.text", "B 쓰는 중");
            app.WaitDiag(d => d.GetProperty("focused").ToString(), f => f == "records.mark.1.text", "B 칸 초점");
            app.Invoke("diag.release");
            AppDriver.WaitFor(() => MemoState(app, 0), s => s == "저장했습니다", "A 저장 완료");
            // 저장 완료 뒤 목록 갱신·다시 열기로 생기는 다시 그리기까지 기다린다
            app.WaitDiag(d => Sd(d).GetProperty("detail").GetProperty("memos").EnumerateArray().Any(m => m.GetString() == "A 새 글"), b => b, "다시 연 상세에 A");
            Thread.Sleep(500);
            Assert.Equal("records.mark.1.text", Focused(app));
            Assert.Equal("B 쓰는 중", Value(app, "records.mark.1.text"));
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>M7 이월 3: 저장 시작 뒤 사용자가 다른 곳으로 스크롤했으면 완료가 그 칸으로 끌어오지 않는다.</summary>
    [Fact]
    public void SaveCompletionDoesNotScrollBackAfterTheUserScrolledAway()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using var app = OpenRecords(userData, "rec-two-memos", "--memo-hold", "1");
            app.Invoke("records.row.0");
            WaitDetail(app);
            app.Invoke("records.mark.0.edit");
            AppDriver.WaitFor(() => app.Exists("records.mark.0.text"), e => e, "메모 칸");
            app.SetText("records.mark.0.text", "스크롤 시험 글");
            app.Find("records.mark.0.text").SetFocus();
            app.PressEnter("records.mark.0.text");                                  // 초점이 그 칸에 있는 채 저장
            WaitHeld(app, "memo");
            var scroll = (ScrollPattern)app.Find("records.scroll").GetCurrentPattern(ScrollPattern.Pattern);
            // 저장 시작의 "저장하는 중…" 보이기 스크롤이 끝난 뒤에 옮긴다: 진행 중인 보이기가 시험의 스크롤을 덮어썼다 (UI 시험 1차)
            double last = double.NaN;
            AppDriver.WaitFor(() => { double now = scroll.Current.VerticalScrollPercent; bool still = now == last; last = now; Thread.Sleep(200); return still; }, s => s, "스크롤 멈춤");
            double before = scroll.Current.VerticalScrollPercent;
            double target = before < 50 ? 100 : 0;
            AppDriver.WaitFor(() =>
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, target);
                Thread.Sleep(300);
                return scroll.Current.VerticalScrollPercent;
            }, v => Math.Abs(v - target) < 1, "다른 곳으로 스크롤");
            app.Invoke("diag.release");
            AppDriver.WaitFor(() => MemoState(app, 0), s => s == "저장했습니다", "저장 완료");
            Thread.Sleep(700);
            Assert.True(Math.Abs(scroll.Current.VerticalScrollPercent - target) < 1, $"스크롤이 {scroll.Current.VerticalScrollPercent}%로 끌려갔다");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>숨기기 직전에 시작한 상세 열기가 숨긴 뒤 끝나면 그리지 않고, 다시 열면 그 상세가 보인다 (G4 M9 2라운드 b3).</summary>
    [Fact]
    public void ADetailFinishingWhileHiddenIsNotDrawnUntilReopened()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        M9ResidentTests.HintAlreadyShown(userData);
        try
        {
            using var app = OpenRecords(userData, "rec-memo", "--start", "--demo-speed", "20", "--close-to-tray", "on", "--records-apply-hold");
            var list = app.WaitDiag(x => x, x => Sd(x).TryGetProperty("rows", out var rows) && rows.EnumerateArray().Any(r => r.GetProperty("life").GetString() != "Running" && !r.GetProperty("analyzing").GetBoolean()), "끝난 기록 줄");
            var row = Sd(list).GetProperty("rows").EnumerateArray().First(r => r.GetProperty("life").GetString() != "Running");
            string id = row.GetProperty("Id").GetString()!;
            app.Invoke($"records.row.{row.GetProperty("n").GetInt32()}");
            WaitHeld(app, "records.apply");
            Assert.False(Sd(app.Diag()).GetProperty("detailOpen").GetProperty("cached").GetBoolean());   // 캐시를 타지 않은 열기
            app.RequestClose();
            M9ResidentTests.WaitHidden(app);
            // 숨기면 시험 멈춤이 풀려 옛 완료가 숨긴 동안 반영된다. 숨긴 창의 진단 값은 읽을 수 없어 앱 기록으로 본다 (UI 시험 1차)
            var applied = AppDriver.WaitFor(() => app.ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "recordsHiddenApply"),
                e => e.ValueKind == JsonValueKind.Object, "숨긴 동안 옛 완료 반영").GetProperty("data");
            Assert.False(app.WindowVisible);
            Assert.Equal(applied.GetProperty("rendersAtSuspend").GetInt32(), applied.GetProperty("renders").GetInt32());   // 숨긴 동안 그리지 않았다
            Assert.Equal(0, applied.GetProperty("frameHooks").GetInt32());                                              // 프레임 구독도 새로 만들지 않았다
            app.PostTray(AppDriver.NinSelect);
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "다시 보임");
            WaitDetail(app);
            Assert.Equal(id, Sd(app.Diag()).GetProperty("detail").GetProperty("Id").GetString());
            Assert.Equal((0, 0), app.Exit());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>상세를 여는 도중 기록 옮기기가 끝나도 "여는 중"에 머물지 않고 새 위치에서 같은 측정이 열린다 (G4 M9 1라운드 b4).</summary>
    [Fact]
    public void MovingRecordsWhileADetailIsOpeningOpensItFromTheNewPlace()
    {
        var userData = AppDriver.NewUserData();
        var dest = userData + "-dest";
        try
        {
            using var app = OpenRecords(userData, "rec-move", "--pick-folder", dest, "--records-apply-hold");
            var first = Sd(app.Diag()).GetProperty("rows").EnumerateArray().First();
            string id = first.GetProperty("Id").GetString()!;
            app.Invoke($"records.row.{first.GetProperty("n").GetInt32()}");
            WaitHeld(app, "records.apply");                                         // 옛 위치에서 읽기를 마치고 작업 표를 놓은 뒤 멈춤
            app.Select("nav.settings");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "settings", "설정");
            app.Invoke("settings.records.change");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "옮기기 확인");
            app.Invoke("dialog.confirm");
            app.WaitDiag(d => Sd(d).GetProperty("records").GetProperty("state").GetString(), s => s is "Done" or "Failed" or "Partial", "옮기기 끝");
            Assert.Equal("Done", Sd(app.Diag()).GetProperty("records").GetProperty("state").GetString());
            app.Select("nav.records");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "records", "기록");
            // 옮기기 완료가 상세를 새 위치로 다시 이었다 (아직 여는 중이었다)
            Assert.Contains(app.ReadLog(), e => e.GetProperty("kind").GetString() == "recordsRelinked" && e.GetProperty("data").GetProperty("loading").GetBoolean());
            int discarded = Sd(app.Diag()).GetProperty("detailOpen").GetProperty("discarded").GetInt32();
            app.Invoke("diag.release");                                             // 옛 위치의 열기 완료가 이제 도착
            // 옛 완료가 화면 반영 단계에서 버려질 때까지 기다린 뒤 본다 (새 상세가 먼저 끝나 있어도 옛 완료 처리 뒤의 상태를 본다, G4 M10 1라운드 b3)
            app.WaitDiag(x => Sd(x).GetProperty("detailOpen").GetProperty("discarded").GetInt32(), n => n > discarded, "옛 완료 버림");
            WaitDetail(app);
            Thread.Sleep(500);
            var d = Sd(app.Diag());
            Assert.False(d.GetProperty("detailLoading").GetBoolean());
            Assert.Equal(id, d.GetProperty("detail").GetProperty("Id").GetString());
            Assert.True(d.GetProperty("detailListed").GetBoolean(), "보이는 상세가 지금 목록(새 위치)의 출처");
            Assert.True(Directory.EnumerateDirectories(dest).Any(f => Path.GetFileName(f).StartsWith(id, StringComparison.Ordinal)), "새 위치에 그 측정");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); AppDriver.DeleteUserData(dest); }
    }

    /// <summary>숨겼다 다시 열면 메모 칸의 선택 범위가 돌아온다 (G4 M9 1라운드 b6).</summary>
    [Fact]
    public void ReopeningRestoresTheMemoSelection()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "rec-memo");
        M5Fixtures.SaveProfile(userData, "steady");
        M9ResidentTests.HintAlreadyShown(userData);
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--screen", "records", "--close-to-tray", "on");
            var list = app.WaitDiag(x => x, x => Sd(x).ValueKind == JsonValueKind.Object && Sd(x).TryGetProperty("rows", out var rows) && rows.EnumerateArray().Any(r => r.GetProperty("life").GetString() != "Running" && !r.GetProperty("analyzing").GetBoolean()), "끝난 기록 줄");
            var row = Sd(list).GetProperty("rows").EnumerateArray().First(r => r.GetProperty("life").GetString() != "Running").GetProperty("n").GetInt32();
            app.Invoke($"records.row.{row}");
            WaitDetail(app);
            app.Invoke("records.mark.0.edit");
            app.SetText("records.mark.0.text", "가나다라마바사");
            var box = app.Find("records.mark.0.text");
            box.SetFocus();
            var text = (TextPattern)box.GetCurrentPattern(TextPattern.Pattern);
            var range = text.DocumentRange.Clone();
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 2);
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -2);
            range.Select();
            AppDriver.WaitFor(() => text.GetSelection().FirstOrDefault()?.GetText(-1), s => s == "다라마", "선택 범위");
            app.RequestClose();
            M9ResidentTests.WaitHidden(app);
            app.PostTray(AppDriver.NinSelect);
            AppDriver.WaitFor(() => app.WindowVisible, b => b, "다시 보임");
            app.WaitDiag(x => !M9ResidentTests.HiddenNow(x) && x.GetProperty("focused").ToString() == "records.mark.0.text", b => b, "같은 칸 초점");
            var again = (TextPattern)app.Find("records.mark.0.text").GetCurrentPattern(TextPattern.Pattern);
            Assert.Equal("다라마", AppDriver.WaitFor(() => again.GetSelection().FirstOrDefault()?.GetText(-1), s => s == "다라마", "선택 복원"));
            Assert.Equal((0, 0), app.Exit());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
