using System.Globalization;
using NetPulse.Core.Analysis;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Report;

/// <summary>리포트를 만들 수 없는 이유 (M8 설계 4.2 오류 상태).</summary>
public enum ReportError { Running, Unreadable, Unsupported }

public sealed class ReportException(ReportError error) : Exception("리포트를 만들 수 없습니다: " + error)
{
    public ReportError Error { get; } = error;
}

/// <summary>리포트 입력 (M8 설계 3.1). 진단 기록은 그 측정 ID의 것만 쓴다.</summary>
public sealed record ReportInput(
    string Key, string Stamp, MeasurementRecord Record, AnalysisResult Analysis, RecordLifecycle Lifecycle,
    IReadOnlyList<StoredDiagnosis> Diagnoses, ReportOptions Options, DateTimeOffset CreatedAt, string AppVersion);

/// <summary>
/// 기록 + 분석 + 진단 기록 + 옵션 → 문서 (M8 설계 3.1). 순수 함수. 판정·등급·통계는 분석 결과 그대로 옮기고 다시 정하지 않는다.
/// </summary>
public static class ReportBuilder
{
    /// <summary>끊김 목록 상한 (설계 5절 [1-10], 구현 중 결정 7).</summary>
    public const int ListLimit = 300;
    /// <summary>끊김 목록에서 혼합 끊김의 하위 구간 줄 상한.</summary>
    public const int ListPartLimit = 10;
    /// <summary>끊김 상세의 하위 구간 상한 (구현 중 결정 3).</summary>
    public const int DetailPartLimit = 50;

    public static ReportDocument Build(ReportInput input)
    {
        var record = input.Record;
        if (input.Lifecycle == RecordLifecycle.Running) throw new ReportException(ReportError.Running);
        if (record.Status == RecordStatus.Unsupported) throw new ReportException(ReportError.Unsupported);
        if (record.Status == RecordStatus.Unreadable || record.Header is null) throw new ReportException(ReportError.Unreadable);
        return new Builder(input).Build();
    }

    private sealed class Builder(ReportInput input)
    {
        private readonly MeasurementRecord record = input.Record;
        private readonly AnalysisResult a = input.Analysis;
        private readonly RecordHeader header = input.Record.Header!;
        private readonly ReportOptions options = input.Options.Normalized();
        private Redactor red = null!;
        private Target[] targets = [];
        private RoundData[] rounds = [];
        private Dictionary<long, DateTimeOffset> wallOf = [];
        private long lastRound;
        private IReadOnlyList<SymptomMark> marks = [];
        private int ignoredMarks;

        private string Clock(DateTimeOffset wall) => ReportTexts.Clock(wall, header.StartedAt, header.UtcOffsetMinutes);
        private string ClockOf(long round) => wallOf.TryGetValue(round, out var w) ? Clock(w) : "알 수 없음";

        public ReportDocument Build()
        {
            red = new Redactor(header, LinkEvidenceAdapters.From(a), options.Masked);
            targets = header.Targets.Where(t => t.Enabled).ToArray();
            rounds = record.Rounds.OrderBy(r => r.Round).ToArray();
            wallOf = new Dictionary<long, DateTimeOffset>(rounds.Length);
            foreach (var r in rounds) wallOf.TryAdd(r.Round, r.Wall);
            lastRound = rounds.Length == 0 ? -1 : rounds[^1].Round;
            (marks, ignoredMarks) = SymptomMarks.FromEvents(record.Events, record.NoteLines);

            var parts = new List<(string Id, IReadOnlyList<ReportBlock> Blocks)>
            {
                ("summary", Summary()),
                ("timeline", Timeline()),
                ("episodes", Episodes()),
                ("evidence", Evidence()),
            };
            if (options.EpisodeDetails > 0 && a.Episodes.Count > 0) parts.Add(("details", Details()));
            parts.Add(("marks", Marks()));
            if (options.IncludeDiagnoses) parts.Add(("diagnoses", Diagnoses()));
            parts.Add(("conditions", Conditions()));

            var sections = parts.Select((p, i) => new ReportSection(p.Id, ReportTexts.SectionTitle(i + 1, p.Id), p.Blocks)).ToArray();
            string title = ReportTexts.Title + " · " + header.StartedAt.ToOffset(TimeSpan.FromMinutes(header.UtcOffsetMinutes)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            int locals = targets.Count(t => t.Role == TargetRole.Local), externals = targets.Length - locals;
            ReportRun[] lead =
            [
                ReportRun.Gen(ReportTexts.Intro),
                ReportRun.Gen(ReportTexts.Method(locals, externals, header.IntervalMs, header.TimeoutMs)),
                ReportRun.Gen(ReportTexts.PrintHint),
            ];
            return new ReportDocument(title, ReportTexts.FileName(header.StartedAt, header.UtcOffsetMinutes), options, input.Key, input.Stamp,
                input.CreatedAt, lead, sections) { Guard = red };
        }

        // ---------------- 1 요약 ----------------

        private IReadOnlyList<ReportBlock> Summary()
        {
            var c = a.Coverage;
            var facts = new List<ReportFact>
            {
                new("등급", ReportRun.Gen(Sentences.GradeName(a.Grade.Grade)), "summary.grade"),
                new("결론", ReportRun.Gen(a.Grade.Sentence), "summary.sentence"),
                new("시작", ReportRun.Gen(ReportTexts.DateTime(header.StartedAt, header.UtcOffsetMinutes)), "summary.start"),
            };
            long? endMs = record.Summary?.EndMs ?? rounds.Select(r => (long?)(r.EndMs ?? r.AtMs)).LastOrDefault();
            if (record.Summary is { } s)
                facts.Add(new("끝", ReportRun.Gen(ReportTexts.DateTime(s.EndedAt, header.UtcOffsetMinutes)), "summary.end"));
            else
                facts.Add(new("끝", ReportRun.Gen(rounds.Length == 0 ? "끝 기록 없음"
                    : "끝 기록 없음 · 마지막 관측 " + ReportTexts.DateTime(rounds[^1].Wall, header.UtcOffsetMinutes)), "summary.end"));
            facts.Add(new("길이", ReportRun.Gen(endMs is long ms ? (record.Summary is null ? "마지막 관측까지 " : "") + TimelineModel.Seconds(ms) : "알 수 없음"), "summary.duration"));
            facts.Add(new("끝 사유", ReportRun.Gen(RecordView.EndText(input.Lifecycle, record.Summary?.EndReason ?? record.EndReason)), "summary.endReason"));
            if (RecordView.SaveText(record.Summary) is { } save) facts.Add(new("저장 상태", ReportRun.Gen(save), "summary.save"));
            facts.Add(new("관측 충족", ReportRun.Gen(Coverage(c)), "summary.coverage"));
            facts.Add(new("끊김", ReportRun.Gen(ReportTexts.EpisodeCount(a.Episodes.Count, c, a.Grade.Grade)), "summary.episodes"));
            if (a.Episodes.Count > 0)
            {
                var longest = a.Episodes.OrderByDescending(e => e.MinDurationMs).ThenBy(e => e.FirstRound).First();
                facts.Add(new("가장 긴 끊김", ReportRun.Gen(EpisodeDuration(longest)), "summary.longest"));
            }
            var blocks = new List<ReportBlock> { new ReportFacts(facts) };
            foreach (var n in a.Grade.Notes) blocks.Add(new ReportParagraph(RecordView.NoteText(n), ParagraphKind.Note));
            blocks.Add(new ReportParagraph(ReportTexts.Scope));
            blocks.Add(options.Masked
                ? new ReportParagraph(ReportTexts.MaskedNote, ParagraphKind.Note)
                : new ReportParagraph(ReportTexts.UnmaskedNote, ParagraphKind.Warning));
            if (options.IncludeMemos && marks.Any(m => m.Memo is not null))
                blocks.Add(new ReportParagraph(options.Masked ? ReportTexts.MemoNote : "증상 메모가 적은 그대로 들어 있습니다.", ParagraphKind.Note));
            return blocks;
        }

        private static string Coverage(Coverage c) => c.ExpectedRounds <= 0 ? "판단할 수 있는 관측이 없습니다."
            : $"판단할 수 있는 관측 {c.JudgeableRounds}회차 / 관측해야 했던 {c.ExpectedRounds}회차 ({LiveSummary.Percent((double)c.JudgeableRounds / c.ExpectedRounds)})";

        private string EpisodeDuration(Episode e) =>
            ReportTexts.Duration(e.MinDurationMs, e.MaxDurationMs, e.Parts.Sum(p => p.Count), e.LastRound == lastRound, input.Lifecycle);

        private string PartDuration(SubRun p) =>
            ReportTexts.Duration(p.MinDurationMs, p.MaxDurationMs, p.Count, p.LastRound == lastRound, input.Lifecycle);

        // ---------------- 2 타임라인 ----------------

        private IReadOnlyList<ReportBlock> Timeline()
        {
            long interval = header.IntervalMs;
            long end = Math.Max(record.Summary?.EndMs ?? 0, rounds.Length == 0 ? interval : rounds[^1].DueMs + interval);
            var bands = TimelineModel.Bands(rounds, targets, record.Gaps, interval);
            var downs = a.Links.Downs.Select(d => new LinkDownView(d.Adapter, d.Source, d.FromMs, d.ToMs, false)).ToArray();
            var frame = TimelineModel.Frame(rounds, targets, record.Gaps, marks, downs, bands, 0, end, TimelineFigure.Columns, interval, "전체");
            var figure = TimelineFigure.Build(frame, bands, downs, marks, header.StartedAt, TimeSpan.FromMinutes(header.UtcOffsetMinutes));
            return [new ReportFigure(figure)];
        }

        // ---------------- 3 끊김 목록 ----------------

        private IReadOnlyList<ReportBlock> Episodes()
        {
            var blocks = new List<ReportBlock>();
            if (a.Episodes.Count == 0)
            {
                blocks.Add(new ReportParagraph(ReportTexts.EmptyEpisodes(a.Coverage, a.Grade.Grade)));
                return blocks;
            }
            var stats = a.Stats.Where(s => s.Verdict != Verdict.Normal && s.Count > 0).ToArray();
            blocks.Add(new ReportTable("판정별 통계 (하위 구간 기준, 최소 길이)", Cols("판정", "횟수", "가장 긴 것", "평균", "끝을 모름"),
                stats.Select(s => new ReportRow([
                    new(ReportRun.Gen(RecordView.VerdictName(s.Verdict)), "verdict.name"),
                    new(ReportRun.Gen($"{s.Count}번"), "verdict.count"),
                    new(ReportRun.Gen($"{TimelineModel.Seconds(s.LongestMinMs)} 이상"), "verdict.longest"),
                    new(ReportRun.Gen($"{TimelineModel.Seconds((long)Math.Round(s.AverageMinMs))} 이상"), "verdict.average"),
                    new(ReportRun.Gen($"{s.OpenEnded}번"), "verdict.open"),
                ])).ToArray()));
            int partCount = stats.Sum(s => s.Count);
            if (partCount != a.Episodes.Count)
                blocks.Add(new ReportParagraph($"끊김 {a.Episodes.Count}번 가운데 판정이 바뀐 구간이 있어, 판정별로 나눠 세면 {partCount}개입니다.", ParagraphKind.Note));

            var indexed = a.Episodes.Select((e, i) => (Episode: e, Index: i)).ToArray();
            var shown = indexed;
            string caption = "끊김 목록";
            if (indexed.Length > ListLimit)
            {
                shown = indexed.OrderByDescending(x => x.Episode.MinDurationMs).ThenBy(x => x.Episode.FirstRound).Take(ListLimit)
                    .OrderBy(x => x.Episode.FirstRound).ToArray();
                caption = ReportTexts.ListCapTitle(ListLimit, indexed.Length);
                blocks.Add(new ReportHeading(ReportRun.Gen(caption)));
                blocks.Add(new ReportParagraph(ReportTexts.ListCapNote(indexed.Length, ListLimit), ParagraphKind.Note));
            }
            var rows = new List<ReportRow>();
            foreach (var (e, i) in shown)
            {
                bool mixed = e.Parts.Count > 1;
                rows.Add(new ReportRow([
                    new(ReportRun.Gen($"{i + 1}"), "episode.index"),
                    new(ReportRun.Gen(ClockOf(e.FirstRound)), "episode.start"),
                    new(ReportRun.Gen(EpisodeDuration(e)), "episode.duration"),
                    new(ReportRun.Gen(mixed ? $"여러 판정 (하위 구간 {e.Parts.Count}개)" : RecordView.VerdictName(e.Parts[0].Verdict)), "episode.verdict"),
                    new(ReportRun.Gen(mixed ? "하위 구간마다 아래 줄에" : ReportTexts.Candidate(e.Parts[0].Candidate)), "episode.candidate"),
                ]));
                if (!mixed) continue;
                foreach (var (p, k) in e.Parts.Take(ListPartLimit).Select((p, k) => (p, k)))
                    rows.Add(PartRow(p, k, "part"));
                if (e.Parts.Count > ListPartLimit)
                    rows.Add(new ReportRow([new(ReportRun.Gen(ReportTexts.MoreParts(e.Parts.Count - ListPartLimit)), "part.more")], RowKind.Note));
            }
            blocks.Add(new ReportTable(caption, Cols("번호", "시작", "길이", "판정", "구간 후보"), rows));
            return blocks;
        }

        private ReportRow PartRow(SubRun p, int k, string prefix) => new([
            new(ReportRun.Gen($"하위 구간 {k + 1}")),
            new(ReportRun.Gen(ClockOf(p.FirstRound)), prefix + ".start"),
            new(ReportRun.Gen(PartDuration(p)), prefix + ".duration"),
            new(ReportRun.Gen(RecordView.VerdictName(p.Verdict)), prefix + ".verdict"),
            new(ReportRun.Gen(ReportTexts.Candidate(p.Candidate)), prefix + ".candidate"),
        ], RowKind.Sub);

        // ---------------- 4 관측 근거 ----------------

        private IReadOnlyList<ReportBlock> Evidence()
        {
            var blocks = new List<ReportBlock>();
            var stats = record.Stats ?? StatsCalculator.Compute(rounds, targets.Select(t => t.Id).ToArray(), record.Gaps);
            blocks.Add(new ReportTable("대상별 응답", Cols("대상", "역할", "방식", "유효 응답", "응답 없음", "손실률", "평균 지연", "최대 지연", "최대 연속 응답 없음", "검사 오류"),
                targets.Select(t =>
                {
                    var s = stats.Targets.FirstOrDefault(x => x.TargetId == t.Id);
                    int valid = s is null ? 0 : s.Success + s.Lost;
                    return new ReportRow([
                        new(red.TargetName(t), "target.name"),
                        new(ReportRun.Gen(Role(t)), "target.role"),
                        new(ReportRun.Gen(RecordView.MethodText(t.Method)), "target.method"),
                        new(ReportRun.Gen(s is null ? "값 없음" : $"{valid}번"), "target.valid"),
                        new(ReportRun.Gen(s is null ? "값 없음" : $"{s.Lost}번"), "target.lost"),
                        new(ReportRun.Gen(s?.LossRate is double r ? LiveSummary.Percent(r) : "값 없음"), "target.lossRate"),
                        new(ReportRun.Gen(s?.AvgRttMs is double avg ? PathMapModel.Ms(avg) : "값 없음"), "target.avg"),
                        new(ReportRun.Gen(s?.MaxRttMs is double max ? PathMapModel.Ms(max) : "값 없음"), "target.max"),
                        new(ReportRun.Gen(s is null || valid == 0 ? "값 없음" : $"{s.MaxConsecutiveLoss}번"), "target.consecutive"),
                        new(ReportRun.Gen(s is null ? "값 없음" : $"{s.Error}번"), "target.error"),
                    ]);
                }).ToArray()));
            blocks.Add(new ReportParagraph("검사 오류는 응답 없음과 구별하고 손실률의 분모에서 뺍니다.", ParagraphKind.Note));

            blocks.Add(new ReportHeading(ReportRun.Gen("링크 근거")));
            if (a.Links.Summaries.Count == 0) blocks.Add(new ReportParagraph([ReportRun.Gen(ReportTexts.NoLinkEvidence)]));
            else
                blocks.Add(new ReportTable("어댑터별 링크 근거", Cols("어댑터", "출처", "상태", "보조 사유"),
                    a.Links.Summaries.Select(s => new ReportRow([
                        new(red.Adapter(s.Adapter, a.Links.Adapters.TryGetValue(s.Adapter, out var at) ? at.InterfaceIndex : null), "link.adapter"),
                        new(ReportRun.Gen(ReportTexts.LinkSource(s.Source)), "link.source"),
                        new(ReportRun.Gen(ReportTexts.LinkState(s.Display)), "link.state"),
                        new(ReportRun.Gen(ReportTexts.LinkNotes(s)), "link.notes"),
                    ])).ToArray()));

            var ctx = JudgeContext.From(header, rounds, record.Events, a.Links);
            blocks.Add(new ReportTable("경로 유형 (대상마다)", Cols("대상", "시작 시점 경로", "경로 유형이 바뀐 횟수"),
                targets.Select(t =>
                {
                    var kinds = ctx.Routes.TryGetValue(t.Id, out var list) ? list.Select(x => x.Route.Ok ? x.Route.Kind ?? "unknown" : "none").ToArray() : [];
                    int changes = 0;
                    for (int i = 1; i < kinds.Length; i++) if (kinds[i] != kinds[i - 1]) changes++;
                    string first = kinds.Length == 0 ? "기록 없음" : kinds[0] == "none" ? "경로 없음" : ReportTexts.RouteKind(kinds[0] == "unknown" ? null : kinds[0]);
                    return new ReportRow([
                        new(red.TargetName(t), "route.target"),
                        new(ReportRun.Gen(first), "route.start"),
                        new(ReportRun.Gen($"{changes}번"), "route.changes"),
                    ]);
                }).ToArray()));

            blocks.Add(new ReportHeading(ReportRun.Gen("지연 추세 (5분 구간 중앙값)")));
            blocks.Add(new ReportParagraph(RecordView.TrendNote, ParagraphKind.Note));
            var trend = targets.Select(t => (Target: t, Base: a.Latency.FirstOrDefault(l => l.TargetId == t.Id))).ToArray();
            var windows = trend.SelectMany(x => x.Base?.MedianMs is null ? [] : x.Base.WindowMedians.Select(w => w.FromMs)).Distinct().Order().ToArray();
            if (windows.Length == 0) blocks.Add(new ReportParagraph("기준선을 만들 만큼 응답이 없어 추세를 그리지 않았습니다."));
            else
            {
                var columns = new List<ReportRun> { ReportRun.Gen("구간 시작") };
                columns.AddRange(trend.Select(x => red.TargetName(x.Target)));
                blocks.Add(new ReportTable("지연 추세", columns, windows.Select(w =>
                {
                    var cells = new List<ReportCell> { new(ReportRun.Gen(Clock(header.StartedAt + TimeSpan.FromMilliseconds(w))), "trend.start") };
                    foreach (var (_, b) in trend)
                    {
                        string text = b?.MedianMs is null ? "기준 없음"
                            : b.WindowMedians.Where(m => m.FromMs == w).Select(m => PathMapModel.Ms(m.MedianMs)).FirstOrDefault() ?? "값 없음";
                        cells.Add(new ReportCell(ReportRun.Gen(text), "trend.value"));
                    }
                    return new ReportRow(cells);
                }).ToArray()));
            }
            return blocks;
        }

        private static string Role(Target t) => t.Role == TargetRole.Local ? "로컬" : "외부";

        // ---------------- 5 끊김 상세 ----------------

        private IReadOnlyList<ReportBlock> Details()
        {
            var blocks = new List<ReportBlock>
            {
                new ReportParagraph($"길이가 긴 끊김 {Math.Min(options.EpisodeDetails, a.Episodes.Count)}개의 앞뒤 회차 응답과 근거입니다.", ParagraphKind.Note),
            };
            var top = a.Episodes.Select((e, i) => (Episode: e, Index: i)).OrderByDescending(x => x.Episode.MinDurationMs).ThenBy(x => x.Episode.FirstRound)
                .Take(options.EpisodeDetails).ToArray();
            var names = targets.Select(red.TargetName).ToArray();
            foreach (var (e, i) in top)
            {
                blocks.Add(new ReportHeading(ReportRun.Gen($"끊김 {i + 1} · {ClockOf(e.FirstRound)} · {EpisodeDuration(e)}")));
                if (e.Parts.Count > 1)
                {
                    var rows = e.Parts.Take(DetailPartLimit).Select((p, k) => PartRow(p, k, "detail.part")).ToList();
                    if (e.Parts.Count > DetailPartLimit)
                        rows.Add(new ReportRow([new(ReportRun.Gen(ReportTexts.MoreParts(e.Parts.Count - DetailPartLimit)), "detail.part.more")], RowKind.Note));
                    blocks.Add(new ReportTable("하위 구간", Cols("하위 구간", "시작", "길이", "판정", "구간 후보"), rows));
                }
                else
                    blocks.Add(new ReportParagraph($"판정: {RecordView.VerdictName(e.Parts[0].Verdict)} · {ReportTexts.Candidate(e.Parts[0].Candidate)}"));

                var d = EpisodeDetail.For(record, a, e);
                var columns = new List<ReportRun> { ReportRun.Gen("시각"), ReportRun.Gen("위치") };
                columns.AddRange(names);
                var detailRows = new List<ReportRow>();
                foreach (var r in d.Rows)
                {
                    if (r.Round is null) { detailRows.Add(new ReportRow([new(ReportRun.Gen("관측 공백 (절전·멈춤): 이 사이는 관측하지 못했습니다"))], RowKind.Note)); continue; }
                    var cells = new List<ReportCell>
                    {
                        new(ReportRun.Gen(Clock(r.Wall)), "detail.time"),
                        new(ReportRun.Gen(r.InEpisode ? "끊김 안" : r.Round < e.FirstRound ? "끊김 앞" : "끊김 뒤"), "detail.place"),
                    };
                    if (r.Gap) cells.AddRange(targets.Select(_ => new ReportCell(ReportRun.Gen("관측 없음"), "detail.cell")));
                    else cells.AddRange(r.Cells.Select(c => new ReportCell(ReportRun.Gen(RecordView.CellText(c)), "detail.cell")));
                    detailRows.Add(new ReportRow(cells, r.InEpisode ? RowKind.Normal : RowKind.Sub));
                    if (d.OmitAfter == r.Round && d.OmittedRounds > 0)
                        detailRows.Add(new ReportRow([new(ReportRun.Gen($"가운데 {d.OmittedRounds}회차는 생략했습니다"), "detail.omitted")], RowKind.Note));
                }
                if (d.NothingBefore) detailRows.Insert(0, new ReportRow([new(ReportRun.Gen("앞 회차 없음 (측정 시작)"))], RowKind.Note));
                if (d.NothingAfter) detailRows.Add(new ReportRow([new(ReportRun.Gen("뒤 회차 없음 (기록 끝)"))], RowKind.Note));
                blocks.Add(new ReportTable("앞뒤 2회차의 대상별 응답", columns, detailRows));

                var routeRuns = new List<ReportRun> { ReportRun.Gen("시작 시점 경로: ") };
                for (int k = 0; k < targets.Length; k++)
                {
                    var route = d.Routes.FirstOrDefault(x => x.TargetId == targets[k].Id);
                    if (k > 0) routeRuns.Add(ReportRun.Gen(", "));
                    routeRuns.Add(names[k]);
                    routeRuns.Add(ReportRun.Gen(" " + (route is null || !route.Ok ? "경로 없음" : ReportTexts.RouteKind(route.Kind))));
                }
                blocks.Add(new ReportParagraph(routeRuns));

                if (d.Links.Count == 0) blocks.Add(new ReportParagraph("앞뒤 3초 안의 링크 끊김 관측: 없음"));
                else
                {
                    var linkRuns = new List<ReportRun> { ReportRun.Gen($"앞뒤 3초 안의 링크 끊김 관측: {d.Links.Count + d.MoreLinks}건 (") };
                    var seen = d.Links.Select(x => x.Adapter).Distinct().ToArray();
                    for (int k = 0; k < seen.Length; k++)
                    {
                        if (k > 0) linkRuns.Add(ReportRun.Gen(", "));
                        linkRuns.Add(red.Adapter(seen[k], a.Links.Adapters.TryGetValue(seen[k], out var at) ? at.InterfaceIndex : null));
                    }
                    linkRuns.Add(ReportRun.Gen(")"));
                    blocks.Add(new ReportParagraph(linkRuns));
                }
                blocks.Add(new ReportParagraph($"앞뒤 3초 안의 증상 표시: {(d.Marks.Count == 0 ? "없음" : $"{d.Marks.Count}개")}"));
                if (d.EvidenceDamaged) blocks.Add(new ReportParagraph("근거 기록 일부를 읽지 못해 빠진 근거가 있을 수 있습니다.", ParagraphKind.Warning));
            }
            return blocks;
        }

        // ---------------- 6 증상 표시와 메모 ----------------

        private IReadOnlyList<ReportBlock> Marks()
        {
            if (marks.Count == 0) return [new ReportParagraph("증상 표시가 없습니다.")];
            var blocks = new List<ReportBlock>();
            var ordered = marks.OrderBy(m => m.AtMs).ToArray();
            if (options.IncludeMemos)
                blocks.Add(new ReportTable("증상 표시와 메모", Cols("표시 시각", "메모"), ordered.Select(m => new ReportRow([
                    new(ReportRun.Gen(Clock(m.Wall)), "mark.time"),
                    new(m.Memo is null ? ReportRun.Gen("메모 없음") : ReportRun.Memo(options.Masked ? red.MaskMemo(m.Memo) : m.Memo), "mark.memo"),
                ])).ToArray()));
            else
            {
                blocks.Add(new ReportTable("증상 표시", Cols("표시 시각"), ordered.Select(m => new ReportRow([new(ReportRun.Gen(Clock(m.Wall)), "mark.time")])).ToArray()));
                blocks.Add(new ReportParagraph("메모는 넣지 않았습니다 (리포트 옵션).", ParagraphKind.Note));
            }
            return blocks;
        }

        // ---------------- 7 측정 중 진단 ----------------

        private IReadOnlyList<ReportBlock> Diagnoses()
        {
            var list = input.Diagnoses.Where(s => s.Status == DiagnosisReadStatus.Ok && s.MeasurementId == header.Id).OrderBy(s => s.StartedAt).ToArray();
            if (list.Length == 0) return [new ReportParagraph("이 측정 중에 한 빠른 진단이 없습니다.")];
            var blocks = new List<ReportBlock>
            {
                new ReportParagraph("저장된 결론과 검사 판정입니다. 진단 기록에는 대상 주소가 없어 대상은 순번으로만 다룹니다.", ParagraphKind.Note),
            };
            foreach (var s in list)
            {
                var v = StoredDiagnosisView.From(s);
                blocks.Add(new ReportHeading(ReportRun.Gen("진단 · " + Clock(s.StartedAt))));
                var facts = new List<ReportFact> { new("결론", ReportRun.Gen(v.Head), "diagnosis.head") };
                if (v.Also.Count > 0) facts.Add(new("다른 가능성", ReportRun.Gen(string.Join(" · ", v.Also)), "diagnosis.also"));
                blocks.Add(new ReportFacts(facts));
                if (v.Mismatch is { } mismatch) blocks.Add(new ReportParagraph(mismatch, ParagraphKind.Note));
                blocks.Add(new ReportTable("검사 판정", Cols("검사", "판정"), v.Checks.Select(c => new ReportRow([
                    new(ReportRun.Gen(c.Name), "diagnosis.check.name"),
                    new(ReportRun.Gen(c.JudgmentText), "diagnosis.check.judgment"),
                ])).ToArray()));
            }
            return blocks;
        }

        // ---------------- 8 측정 조건 ----------------

        private IReadOnlyList<ReportBlock> Conditions()
        {
            var cols = new List<string> { "순서", "대상", "역할", "방식", "포트" };
            if (!options.Masked) cols.Add("주소");
            cols.Add("사업자");
            var blocks = new List<ReportBlock>
            {
                new ReportTable("대상 구성", Cols([.. cols]), targets.Select((t, i) =>
                {
                    var cells = new List<ReportCell>
                    {
                        new(ReportRun.Gen($"{i + 1}"), "condition.target.order"),
                        new(red.TargetName(t), "condition.target.name"),
                        new(ReportRun.Gen(Role(t)), "condition.target.role"),
                        new(ReportRun.Gen(t.Method.Kind == ProbeMethodKind.Icmp ? "Ping" : "TCP"), "condition.target.method"),
                        new(ReportRun.Gen(t.Method.Kind == ProbeMethodKind.Tcp ? t.Method.Port.ToString(CultureInfo.InvariantCulture) : "없음"), "condition.target.port"),
                    };
                    if (red.Address(t) is { } address) cells.Add(new(address, "condition.target.address"));
                    cells.Add(new(red.Provider(t), "condition.target.provider"));
                    return new ReportRow(cells);
                }).ToArray()),
            };
            var damage = RecordView.DamageNotices(record, ignoredMarks);
            blocks.Add(new ReportFacts(
            [
                new("측정 ID", red.Id(), "condition.id"),
                new("측정 주기", ReportRun.Gen(ReportTexts.Sec(header.IntervalMs) + "초"), "condition.interval"),
                new("응답 제한시간", ReportRun.Gen(ReportTexts.Sec(header.TimeoutMs) + "초"), "condition.timeout"),
                new("예상 연결", ReportRun.Gen(RecordView.ExpectedText(header.Expected)), "condition.expected"),
                new("계획한 길이", ReportRun.Gen(TimelineModel.Seconds(header.PlannedMs)), "condition.planned"),
                new("기록 상태", ReportRun.Gen(damage.Count == 0 ? "손상 없음" : $"알림 {damage.Count}건"), "condition.damage"),
                new("측정한 앱", ReportRun.Gen("NetPulse " + header.AppVersion), "condition.appVersion"),
                new("분석기 버전", ReportRun.Gen(a.AnalyzerVersion.ToString(CultureInfo.InvariantCulture)), "condition.analyzer"),
                new("리포트를 만든 앱", ReportRun.Gen("NetPulse " + input.AppVersion), "condition.reportApp"),
                new("만든 시각", ReportRun.Gen(ReportTexts.DateTime(input.CreatedAt, header.UtcOffsetMinutes)), "condition.created"),
                new("가리기", ReportRun.Gen(options.Masked ? "켬 (통신사에 보낼 때)" : "끔 (내가 볼 때)"), "condition.masking"),
            ]));
            foreach (var n in damage) blocks.Add(new ReportParagraph(n, ParagraphKind.Warning));
            return blocks;
        }

        private static IReadOnlyList<ReportRun> Cols(params string[] names) => names.Select(ReportRun.Gen).ToArray();
    }
}
