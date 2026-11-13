using System.Diagnostics;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Showcase;

/// <summary>
/// 프레임·자원 측정 (M2 설계 5.9, --bench). 캡처 시험과 따로 돈다.
/// 준비 전환 2회(버림) → 정지 5초 → 전환 6회(1.5초 간격) → 오로라 켜고 끄기 4회 → 정지 5초 → 전환을 더 해 20회·60회 때 메모리.
/// 메모리: 원형 전환(RenderTargetBitmap)을 거듭하면 처음 20회쯤 동안 전용 메모리가 크게 늘고 그 뒤 거의 늘지 않는다 (관측: 20회 +128 MB,
/// 60회 +168 MB, 120회 +160 MB). 그래서 판정은 20회→60회 사이 증가로 한다 (DECISIONS "M2 원형 전환 메모리").
/// 전환 1회 구간 = 요청부터 정리 끝 + 200 ms (찍기, 색 바꾸기, 클립 해제, 아크릴 복귀 포함).
/// 결과는 app-log.jsonl의 "bench" 기록. 끝나면 창을 닫는다.
/// </summary>
public sealed partial class ShowcaseWindow
{
    private static TimeSpan Idle => FrameBudget.BenchIdle;
    private static TimeSpan Settle => FrameBudget.BenchSettle;
    private static TimeSpan Gap => FrameBudget.BenchGap;

    private async void RunBench()
    {
        var segments = new List<FrameStats>();
        try
        {
            await Task.Delay(Idle);   // 첫 등장 모션이 끝난 뒤의 반복 모션 수를 기준으로
            int repeatsAtStart = ui.Motion.ActiveByKind[MotionKind.Repeat];
            int subscribersAtStart = ui.Effects.SubscriberCount;
            for (int i = 0; i < 2; i++) { await SwitchTheme(); await Task.Delay(Gap); }
            double memAt2 = PrivateMb();

            segments.Add(await Measure("idle-1", () => Task.Delay(Idle)));
            for (int i = 0; i < 6; i++)
            {
                segments.Add(await Measure("transition-" + i, async () => { await SwitchTheme(); await Task.Delay(Settle); }));
                await Task.Delay(Gap);
            }
            for (int i = 0; i < 4; i++)
                segments.Add(await Measure("aurora-" + i, async () =>
                {
                    ui.Effects.UpdateSettings(ui.Effects.Settings with { Aurora = !ui.Effects.Settings.Aurora }, persist: false);
                    await Task.Delay(MotionTokens.Slow * 3);
                }));
            segments.Add(await Measure("idle-2", () => Task.Delay(Idle)));
            for (int i = 0; i < 14; i++) { await SwitchTheme(); await Task.Delay(Settle); }
            await Task.Delay(Idle);
            double memAt22 = PrivateMb();   // 준비 2 + 측정 6 + 추가 14 = 전환 22회 뒤
            for (int i = 0; i < 40; i++) { await SwitchTheme(); await Task.Delay(Settle); }
            await Task.Delay(Idle);
            double memAt62 = PrivateMb();   // 전환 62회 뒤

            log.Write("bench", new
            {
                segments,
                baselineMs = ui.Pacer.Baseline,
                transitions = ui.Theme.Transitions,
                fallbacks = ui.Theme.Fallbacks,
                unclippedNewThemeFrames = ui.Theme.UnclippedNewThemeFrames,
                lastSnapshotMs = ui.Theme.LastSnapshotMs,
                memory = new { at2Mb = Math.Round(memAt2, 1), at22Mb = Math.Round(memAt22, 1), at62Mb = Math.Round(memAt62, 1), plateauGrowthMb = Math.Round(memAt62 - memAt22, 1) },
                leftovers = new
                {
                    snapshotLayers = ui.Theme.SnapshotLayersInTree,
                    repeatsAtStart,
                    repeatsAtEnd = ui.Motion.ActiveByKind[MotionKind.Repeat],
                    subscribersAtStart,
                    subscribersAtEnd = ui.Effects.SubscriberCount,
                },
            });
        }
        catch (Exception ex)
        {
            log.Write("bench", new { error = ex.GetType().Name, message = ex.Message, segments });
        }
        BeginClose();
    }

    private async Task<FrameStats> Measure(string name, Func<Task> body)
    {
        ui.Pacer.BeginSegment();
        await body();
        return ui.Pacer.EndSegment(name);
    }

    /// <summary>테마를 뒤집고 원형 전환이 끝날 때까지 기다린다.</summary>
    private async Task SwitchTheme()
    {
        int before = ui.Theme.Transitions + ui.Theme.Fallbacks;
        var next = ui.Effects.Mode.Theme == ThemeKind.Dark ? ThemePreference.Light : ThemePreference.Dark;
        SetRevealOrigin(themeChoice.Item(next == ThemePreference.Light ? "light" : "dark"));
        ui.Effects.UpdateSettings(ui.Effects.Settings with { Theme = next }, persist: false);
        var deadline = Stopwatch.StartNew();
        while ((ui.Theme.InTransition || ui.Theme.Transitions + ui.Theme.Fallbacks == before) && deadline.Elapsed < FrameBudget.BenchSwitchTimeout)
            await Task.Delay(MotionTokens.Fade);
    }

    private static double PrivateMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        return p.PrivateMemorySize64 / (1024.0 * 1024.0);
    }
}
