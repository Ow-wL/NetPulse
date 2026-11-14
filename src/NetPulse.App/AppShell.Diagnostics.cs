using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using NetPulse.App.Design;
using NetPulse.App.Screens;
using NetPulse.Core.Live;
using NetPulse.Design.Tokens;

namespace NetPulse.App;

/// <summary>
/// 진단 값과 자체 시험 (M5 설계 7.4). diag.state의 이름(JSON)으로 화면·측정 상태를 내보낸다 (UI 시험이 읽는다).
/// --selftest layout: 지금 화면의 잘림·겹침·글자 잘림. --bench: 측정 화면 프레임·분석 시간 (설계 5절).
/// </summary>
public sealed partial class AppShell
{
    private void QueueDiag()
    {
        if (diagQueued || closeAllowed) return;
        diagQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            diagQueued = false;
            if (closeAllowed) return;
            try { RenderProfile.Time("shell.diag", WriteDiag); }
            catch (Exception ex) { log.Write("diagFailed", new { type = ex.GetType().Name, message = ex.Message, at = ex.StackTrace?.Split(Environment.NewLine).Take(4) }); }
        });
    }

    private void WriteDiag()
    {
        var mode = ui.Effects.Mode;
        var s = host.Snapshot;
        var focused = root.XamlRoot is { } xr ? FocusManager.GetFocusedElement(xr) as DependencyObject : null;
        var state = new
        {
            screen = screens.CurrentId,
            host = s.State.ToString(),
            world = host.World.Kind,
            held = host.World is DemoAppWorld dw && dw.World.TimeBase.Held,
            session = s.SessionId,
            rounds = s.Rounds.Count,
            elapsedMs = s.ElapsedMs,
            marks = s.Marks.Select(m => new { m.Mark, m.AtMs, m.Memo, state = m.State.ToString() }),
            map = new { candidate = s.Map.Candidate.ToString(), note = s.Map.CandidateNote, text = s.Map.AccessibleText },
            analysis = s.AnalysisAt is { } a ? new { a.Seq, a.ElapsedMs, a.Rounds, a.Events, a.DurationMs, grade = s.Analysis?.Grade.Grade.ToString() } : null,
            finished = s.Finished is { } f ? new { f.Result.Id, end = f.Result.EndReason.ToString(), grade = f.Analysis?.Grade.Grade.ToString(), outages = f.Analysis?.Episodes.Count, f.Result.Unsaved } : null,
            startError = s.StartError,
            profile = s.Profile?.Name,
            loadStatus = host.LoadStatus.ToString(),
            recent = new { listed = recent.Listed, items = recent.Items.Select(i => new { i.Entry.Id, i.Analyzing, grade = i.Grade?.ToString(), i.Outages, i.Error, life = i.Entry.Lifecycle.ToString() }) },
            screenDiag = screens.Current?.Diag(),
            outage = observation.Current != NetPulse.Core.Presentation.ObservationKind.None,
            observation = observation.Current.ToString(),
            theme = mode.Theme.ToString().ToLowerInvariant(),
            material = mode.Material.ToString().ToLowerInvariant(),
            glassAcrylic = ui.Glass.IsAcrylic,
            lightsVisible = mode.LightsVisible,
            lightsMoving = mode.LightsMoving,
            problem = ui.Lights.Problem,
            aurora = ui.Lights.AuroraOn,
            reduceMotion = ui.Motion.Reduced,
            frozen = ui.Motion.Frozen,
            simulated = options.AnySimulation,
            active = ui.Motion.ActiveByKind.ToDictionary(k => k.Key.ToString().ToLowerInvariant(), k => k.Value),
            created = ui.Motion.CreatedByKind.ToDictionary(k => k.Key.ToString().ToLowerInvariant(), k => k.Value),
            dialogOpen = dialog.IsOpen,
            toasts = toasts.Count,
            focused = focused is null ? null : AutomationProperties.GetAutomationId(focused),
            uiScale = options.UiScale,
            reasons = mode.Reasons,
            transitions = ui.Theme.Transitions,
            inTransition = ui.Theme.InTransition,
            fallbacks = ui.Theme.Fallbacks,
            snapshotLayers = ui.Theme.SnapshotLayersInTree,
            lowPerformance = ui.Pacer.Low,
        };
        diag.Text = JsonSerializer.Serialize(state);
    }

    private void RunSelfTest()
    {
        try
        {
            switch (options.SelfTest)
            {
                case "layout": log.Write("selftest", new { kind = "layout", screen = screens.CurrentId, report = LayoutProbe.Report(root, diag, [toasts]) }); break;
                default: log.Write("selftest", new { kind = options.SelfTest, error = "unknown" }); break;
            }
        }
        catch (Exception ex) { log.Write("selftest", new { kind = options.SelfTest, error = ex.GetType().Name, message = ex.Message }); }
    }

    /// <summary>
    /// 측정 화면 bench (M5 설계 5절): 채우기(재지 않음) → 1배속 전환 뒤 준비 → idle-live → zoom-full → pan → nav.
    /// 잠정 분석 시간은 준비를 뺀 구간에서 5초마다의 분석 기록을 모은다.
    /// </summary>
    private async void RunBench()
    {
        try
        {
            if (screens.CurrentId != "measure") Navigate("measure", null);
            // 채우기: 시간축이 1배속이 될 때까지 (데모 규칙의 배속 전환 회차)
            while (host.World is DemoAppWorld demo && demo.World.TimeBase.Speed > NetPulse.Core.Demo.DemoTimeBase.VirtualAbove)
                await Task.Delay(MotionTokens.Slow);
            await Task.Delay(BenchTokens.Warmup);
            var analyses = new Dictionary<long, double>();
            void Sample() { if (host.Snapshot.AnalysisAt is { } a) analyses[a.Seq] = a.DurationMs; }
            host.Changed += Sample;
            var segments = new List<object>();
            var profile = new List<object>();
            async Task Segment(string name, Func<Task> body)
            {
                RenderProfile.Take();
                RenderProfile.On = true;
                int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
                var pauseBefore = GC.GetTotalPauseDuration();
                ui.Pacer.BeginSegment();
                await body();
                segments.Add(ui.Pacer.EndSegment(name));
                RenderProfile.On = false;
                var gc = new
                {
                    gen0 = GC.CollectionCount(0) - gcBefore[0], gen1 = GC.CollectionCount(1) - gcBefore[1], gen2 = GC.CollectionCount(2) - gcBefore[2],
                    pauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds,
                    lastPausesMs = GC.GetGCMemoryInfo(GCKind.Any).PauseDurations.ToArray().Select(d => d.TotalMilliseconds),
                };
                profile.Add(new { segment = name, parts = RenderProfile.Take(), gc });
            }
            var measure = screens.Get("measure") as MeasureScreen;
            await Segment("idle-live", () => Task.Delay(BenchTokens.IdleLive));
            await Segment("zoom-full", async () => { measure?.Command(Design.Controls.TimelineCommand.ShowAll); await Task.Delay(BenchTokens.ZoomFull); });
            await Segment("pan", async () =>
            {
                measure?.Command(Design.Controls.TimelineCommand.ZoomIn);
                for (int i = 0; i < BenchTokens.PanCount; i++) { measure?.Command(Design.Controls.TimelineCommand.PanLeft); await Task.Delay(BenchTokens.PanStep); }
                measure?.Command(Design.Controls.TimelineCommand.ToNow);
            });
            var navSteps = new List<object>();
            await Segment("nav", async () =>
            {
                for (int i = 0; i < BenchTokens.NavCount; i++)
                {
                    int at = ui.Pacer.SegmentSamples;
                    string to = i % 2 == 0 ? "home" : "measure";
                    Navigate(to, null);
                    await Task.Delay(BenchTokens.NavStep);
                    navSteps.Add(new { to, maxMs = ui.Pacer.SegmentMaxFrom(at) });
                }
            });
            // 진단 구간 (M6 설계 5절): 첫 진입 → 진단 한 번(진행 → 결과) → 홈 ↔ 진단 6번
            await Segment("diag-enter", async () => { Navigate("diagnose", null); await Task.Delay(BenchTokens.DiagEnter); });
            await Segment("diag-run", async () =>
            {
                (screens.Get("diagnose") as DiagnoseScreen)?.Start();
                var limit = DateTime.UtcNow + BenchTokens.DiagRunLimit;
                while (DateTime.UtcNow < limit && !(diagnosis.Snapshot is { Operation: Core.Live.DiagnosisOperation.None, Last: not null }))
                    await Task.Delay(MotionTokens.Fast);
                await Task.Delay(BenchTokens.DiagSettle);
            });
            await Segment("diag-reenter", async () =>
            {
                for (int i = 0; i < BenchTokens.DiagReenterCount; i++)
                {
                    int at = ui.Pacer.SegmentSamples;
                    string to = i % 2 == 0 ? "home" : "diagnose";
                    Navigate(to, null);
                    await Task.Delay(BenchTokens.NavStep);
                    navSteps.Add(new { to, maxMs = ui.Pacer.SegmentMaxFrom(at), segment = "diag-reenter" });
                }
            });
            host.Changed -= Sample;
            var durations = analyses.Values.OrderBy(v => v).ToArray();
            double P95(double[] v) => v.Length == 0 ? 0 : v[Math.Min(v.Length - 1, (int)Math.Ceiling(v.Length * 0.95) - 1)];
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long privateMb = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);
            log.Write("bench", new
            {
                kind = "measure", segments, profile, navSteps, rounds = host.Snapshot.Rounds.Count,
                analysis = new { samples = durations.Length, p95Ms = P95(durations), maxMs = durations.LastOrDefault() },
                baselineMs = ui.Pacer.Baseline, privateMb,
            });
        }
        catch (Exception ex) { log.Write("bench", new { kind = "measure", error = ex.GetType().Name, message = ex.Message }); }
        BeginClose();
    }

    /// <summary>
    /// 기록 화면 bench (M7 설계 5절, 구현 중 결정 3·6): 시험이 미리 만든 기록 40개(12시간 2개 포함)로
    /// records-enter → records-open(12시간, 캐시 없음) → records-timeline → records-switch(다른 12시간 → 짧은 기록)
    /// → records-compare(12시간 둘) → records-nav-during-analysis → records-reenter → records-after-memo.
    /// 구간마다 프레임과 끝 시점 전용 메모리, 실행 전체의 최고 전용 메모리(0.5초 표본)를 남긴다. 분석 시간은 따로.
    /// </summary>
    private async void RunRecordsBench()
    {
        long peakMb = 0;
        var sampler = DispatcherQueue.CreateTimer();
        sampler.Interval = BenchTokens.MemorySample;
        sampler.Tick += (_, _) => peakMb = Math.Max(peakMb, PrivateMb());
        try
        {
            sampler.Start();
            await Task.Delay(BenchTokens.Warmup);
            var library = recent.Library;
            var segments = new List<object>();
            var memory = new List<object>();
            var timings = new List<object>();
            async Task Segment(string name, Func<Task> body)
            {
                ui.Pacer.BeginSegment();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await body();
                segments.Add(ui.Pacer.EndSegment(name));
                timings.Add(new { segment = name, ms = watch.ElapsedMilliseconds });
                memory.Add(new { segment = name, privateMb = PrivateMb(), held = library.HeldCount });
            }
            async Task Until(Func<bool> done)
            {
                var limit = DateTime.UtcNow + BenchTokens.RecordsOpenLimit;
                while (DateTime.UtcNow < limit && !done()) await Task.Delay(MotionTokens.Fast);
            }
            RecordsScreen Screen() => (RecordsScreen)screens.Get("records");

            await Segment("records-enter", async () => { Navigate("records", null); await Task.Delay(BenchTokens.RecordsEnter); });
            await library.AnalyzedAsync(library.Items.Select(i => i.Key).ToArray());
            var longs = library.Items.Where(i => i.Entry.PlannedMs >= 12 * 3_600_000L).Select(i => i.Key).ToArray();
            var shorts = library.Items.Where(i => i.Entry.PlannedMs < 12 * 3_600_000L && i.Summary.Error is null).Select(i => i.Key).ToArray();
            if (longs.Length < 2 || shorts.Length < 2) throw new InvalidOperationException($"bench 준비가 모자랍니다 (12시간 {longs.Length}, 짧은 기록 {shorts.Length})");

            // 목록 분석이 끝난 뒤 보유분을 비워 "캐시 없는 열기"로 시작한다
            library.SetPinned([]);
            await Segment("records-open", async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Screen().Show(longs[0]);
                await Until(() => Screen().DetailReady);
                timings.Add(new { segment = "records-open-analysis", ms = watch.ElapsedMilliseconds });
                await Task.Delay(BenchTokens.RecordsSettle);
            });
            await Segment("records-timeline", async () =>
            {
                Screen().Timeline(Design.Controls.TimelineCommand.ShowAll);
                await Task.Delay(BenchTokens.PanStep);
                Screen().Timeline(Design.Controls.TimelineCommand.ZoomIn);
                for (int i = 0; i < BenchTokens.PanCount; i++) { Screen().Timeline(Design.Controls.TimelineCommand.PanLeft); await Task.Delay(BenchTokens.PanStep); }
            });
            await Segment("records-switch", async () =>
            {
                Screen().Show(null);
                await Task.Delay(BenchTokens.NavStep);
                Screen().Show(longs[1]);
                await Until(() => Screen().DetailReady);
                Screen().Show(shorts[0]);
                await Until(() => Screen().DetailReady);
                await Task.Delay(BenchTokens.RecordsSettle);
            });
            await Segment("records-compare", async () =>
            {
                _ = Screen().Compare(longs[0], longs[1]);
                await Until(() => Screen().CompareReady);
                await Task.Delay(BenchTokens.RecordsSettle);
            });
            await Segment("records-nav-during-analysis", async () =>
            {
                Screen().Show(shorts[1]);
                for (int i = 0; i < BenchTokens.RecordsReenterCount; i++)
                {
                    Navigate(i % 2 == 0 ? "home" : "records", null);
                    await Task.Delay(BenchTokens.NavStep);
                }
            });
            await Segment("records-reenter", async () =>
            {
                for (int i = 0; i < BenchTokens.RecordsReenterCount; i++)
                {
                    Navigate(i % 2 == 0 ? "home" : "records", null);
                    await Task.Delay(BenchTokens.NavStep);
                }
            });
            await Segment("records-after-memo", async () =>
            {
                var item = library.Items.First(i => i.Key == shorts[0]);
                var opened = await library.OpenAsync(item.Key);
                var mark = opened is null ? null : Core.Records.SymptomMarks.FromEvents(opened.Record.Events, opened.Record.NoteLines).Marks.FirstOrDefault();
                if (opened is not null && mark is not null)
                {
                    await memos.SaveAsync(item.Key, item.Entry.Segments, opened.Record.Header!.Id, mark.Mark, mark.AtMs, "bench 메모");
                    await library.RefreshAsync(host.RunningId);
                    Screen().Show(item.Key);
                    await Until(() => Screen().DetailReady);
                }
                await Task.Delay(BenchTokens.RecordsSettle);
            });
            sampler.Stop();
            log.Write("bench", new { kind = "records", segments, timings, memory, peakPrivateMb = Math.Max(peakMb, PrivateMb()), records = library.Items.Count, baselineMs = ui.Pacer.Baseline });
        }
        catch (Exception ex)
        {
            sampler.Stop();
            log.Write("bench", new { kind = "records", error = ex.GetType().Name, message = ex.Message });
        }
        BeginClose();
    }

    private static long PrivateMb() => System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);
}
