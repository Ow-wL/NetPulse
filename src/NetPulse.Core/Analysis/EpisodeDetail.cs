using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;

namespace NetPulse.Core.Analysis;

/// <summary>상세 표의 한 칸: 대상 하나의 그 회차 응답.</summary>
public enum CellKind { Success, Lost, Error, Missing }

public sealed record DetailCell(string TargetId, CellKind Kind, double? RttMs);

/// <summary>
/// 상세 표의 한 줄. 회차 줄이면 <see cref="Round"/>가 있고, 공백 줄이면 null이며 칸이 없다 (공백을 정상으로 그리지 않는다).
/// </summary>
public sealed record DetailRow(long? Round, DateTimeOffset Wall, long AtMs, bool InEpisode, bool Gap, IReadOnlyList<DetailCell> Cells);

/// <summary>구간 시작 회차의 대상 경로 (유형만 공개 가능, 인터페이스 번호는 화면에만).</summary>
public sealed record RouteKindAt(string TargetId, string? Kind, bool Ok, int? InterfaceIndex);

/// <summary>
/// 구간 상세 (M4 설계 B4, M7 설계 3.3).
/// </summary>
/// <param name="Rows">앞 회차 · 구간 회차(길면 앞뒤 3개) · 뒤 회차, 사이의 공백 줄 포함, 시간순</param>
/// <param name="OmittedRounds">구간 가운데에서 생략한 회차 수 (0이면 생략 없음)</param>
/// <param name="OmitAfter">생략이 들어갈 자리: 이 회차 줄 다음</param>
/// <param name="Links">[시작 − 3초, 끝 + 3초]와 겹치는 링크 다운 (최대 8)</param>
/// <param name="MoreLinks">8건을 넘어 뺀 수</param>
/// <param name="Marks">같은 범위의 증상 표시</param>
/// <param name="EvidenceDamaged">기록의 이벤트 줄 손상이 있어 근거 일부가 빠졌을 수 있음</param>
public sealed record EpisodeDetailResult(
    Episode Episode, IReadOnlyList<DetailRow> Rows, int OmittedRounds, long? OmitAfter,
    bool NothingBefore, bool NothingAfter,
    IReadOnlyList<RouteKindAt> Routes, IReadOnlyList<DownSpan> Links, int MoreLinks,
    IReadOnlyList<SymptomMark> Marks, bool EvidenceDamaged);

/// <summary>구간 상세 계산. 순수 함수.</summary>
public static class EpisodeDetail
{
    public const int Around = 2, KeepEachEnd = 3, LongerThan = 9, LinkMax = 8;
    public const long NearMs = 3000;

    /// <summary>기록 하나에서 여러 끊김의 상세를 구할 때 한 번만 만드는 것 (정렬한 회차, 경로 문맥, 증상 표시). 리포트가 상세 10개를 만들 때 쓴다.</summary>
    public sealed record Shared(RoundData[] Rounds, JudgeContext Context, IReadOnlyList<SymptomMark> Marks);

    public static Shared Prepare(MeasurementRecord record, AnalysisResult analysis) =>
        Prepare(record, analysis, record.Rounds.OrderBy(r => r.Round).ToArray(), SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks);

    /// <summary>
    /// 호출한 쪽이 이미 만든 회차 정렬(<c>Round</c> 순 안정 정렬)과 증상 표시를 그대로 쓴다. 리포트 문서 만들기가 같은 것을 두 번 만들지 않게 (M8 게이트 bench).
    /// </summary>
    public static Shared Prepare(MeasurementRecord record, AnalysisResult analysis, RoundData[] orderedRounds, IReadOnlyList<SymptomMark> marks)
    {
        var header = record.Header ?? throw new ArgumentException("머리가 없는 기록입니다.", nameof(record));
        return new Shared(orderedRounds, JudgeContext.From(header, record.Rounds, record.Events, analysis.Links), marks);
    }

    public static EpisodeDetailResult For(MeasurementRecord record, AnalysisResult analysis, Episode episode, Shared? shared = null)
    {
        var header = record.Header ?? throw new ArgumentException("머리가 없는 기록입니다.", nameof(record));
        var rounds = shared?.Rounds ?? record.Rounds.OrderBy(r => r.Round).ToArray();
        var targets = header.Targets.Where(t => t.Enabled).Select(t => t.Id).ToArray();

        int first = Array.FindIndex(rounds, r => r.Round >= episode.FirstRound);
        int last = Array.FindLastIndex(rounds, r => r.Round <= episode.LastRound);
        if (first < 0 || last < first) throw new ArgumentException("기록에 없는 구간입니다.", nameof(episode));

        var inside = rounds[first..(last + 1)];
        var shown = new List<RoundData>();
        int omitted = 0;
        long? omitAfter = null;
        if (inside.Length > LongerThan)
        {
            shown.AddRange(inside.Take(KeepEachEnd));
            omitted = inside.Length - KeepEachEnd * 2;
            omitAfter = inside[KeepEachEnd - 1].Round;
            shown.AddRange(inside.Skip(inside.Length - KeepEachEnd));
        }
        else shown.AddRange(inside);

        var before = rounds[Math.Max(0, first - Around)..first];
        var after = rounds[(last + 1)..Math.Min(rounds.Length, last + 1 + Around)];
        var all = before.Concat(shown).Concat(after).ToArray();

        DetailRow Row(RoundData r) => new(r.Round, r.Wall, r.AtMs, r.Round >= episode.FirstRound && r.Round <= episode.LastRound, r.Gap,
            r.Gap ? [] : targets.Select(id => Cell(r, id)).ToArray());

        // 보이는 회차들 사이에 든 공백 구간을 줄로 끼운다
        var rows = new List<DetailRow>();
        for (int i = 0; i < all.Length; i++)
        {
            if (i > 0)
                foreach (var g in record.Gaps.Where(g => g.FromMs >= all[i - 1].AtMs && g.FromMs < all[i].AtMs))
                    rows.Add(new DetailRow(null, g.FromWall, g.FromMs, false, true, []));
            rows.Add(Row(all[i]));
        }

        long startMs = inside[0].AtMs, endMs = inside[^1].AtMs;
        var near = analysis.Links.Downs.Where(d => d.ToMs >= startMs - NearMs && d.FromMs <= endMs + NearMs).OrderBy(d => d.FromMs).ToArray();

        var ctx = shared?.Context ?? JudgeContext.From(header, record.Rounds, record.Events, analysis.Links);
        var routes = targets.Select(id => ctx.RouteOf(id, episode.FirstRound) is { } r
            ? new RouteKindAt(id, r.Kind, r.Ok, r.InterfaceIndex)
            : new RouteKindAt(id, null, false, null)).ToArray();

        var marks = (shared?.Marks ?? SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks)
            .Where(m => m.AtMs >= startMs - NearMs && m.AtMs <= endMs + NearMs).OrderBy(m => m.AtMs).ToArray();

        return new EpisodeDetailResult(episode, rows, omitted, omitAfter, before.Length == 0, after.Length == 0,
            routes, near.Take(LinkMax).ToArray(), Math.Max(0, near.Length - LinkMax), marks, record.Damage.DamagedEvents > 0);
    }

    private static DetailCell Cell(RoundData r, string targetId)
    {
        var x = r.Results.FirstOrDefault(t => t.TargetId == targetId);
        if (x is null) return new DetailCell(targetId, CellKind.Missing, null);
        return x.Outcome switch
        {
            ProbeOutcome.Success => new DetailCell(targetId, CellKind.Success, x.RttMs),
            ProbeOutcome.Lost => new DetailCell(targetId, CellKind.Lost, null),
            _ => new DetailCell(targetId, CellKind.Error, null),
        };
    }
}
