namespace NetPulse.Core.Report;

/// <summary>
/// 글 조각의 출처 (M8 설계 3.3 안전망). 생성 문구는 앱이 정한 글과 기록에서 나온 숫자·시각(식별 정보 아님),
/// 기록은 기록의 글 필드(대상 이름·주소·사업자·어댑터 별칭·측정 ID), 메모는 사용자가 적은 증상 메모.
/// </summary>
public enum TextOrigin { Generated, Record, Memo }

/// <summary>글 조각 하나. HTML에서는 텍스트 노드 하나가 된다.</summary>
public sealed record ReportRun(string Text, TextOrigin Origin = TextOrigin.Generated)
{
    public static ReportRun Gen(string text) => new(text, TextOrigin.Generated);
    public static ReportRun Rec(string text) => new(text, TextOrigin.Record);
    public static ReportRun Memo(string text) => new(text, TextOrigin.Memo);
}

public enum ParagraphKind { Normal, Lead, Note, Warning }

public abstract record ReportBlock;

/// <summary>문단. 조각마다 HTML 요소 하나 (글 순서 대조가 조각 단위로 되게).</summary>
public sealed record ReportParagraph(IReadOnlyList<ReportRun> Runs, ParagraphKind Kind = ParagraphKind.Normal) : ReportBlock
{
    public ReportParagraph(string text, ParagraphKind kind = ParagraphKind.Normal) : this([ReportRun.Gen(text)], kind) { }
}

/// <summary>절 안의 소제목.</summary>
public sealed record ReportHeading(ReportRun Text) : ReportBlock;

/// <summary>이름-값 한 쌍. <see cref="Field"/>는 시험이 값을 뽑는 고정 이름 (HTML <c>data-field</c>).</summary>
public sealed record ReportFact(string Label, ReportRun Value, string Field);

public sealed record ReportFacts(IReadOnlyList<ReportFact> Items) : ReportBlock;

public sealed record ReportCell(ReportRun Value, string? Field = null);

public enum RowKind
{
    Normal,
    /// <summary>위 줄에 딸린 줄 (혼합 끊김의 하위 구간)</summary>
    Sub,
    /// <summary>표 폭 전체의 안내 한 칸 ("나머지 n개")</summary>
    Note,
}

public sealed record ReportRow(IReadOnlyList<ReportCell> Cells, RowKind Kind = RowKind.Normal);

/// <summary>표. 열 머리는 대상 이름처럼 기록에서 올 수 있어 조각으로 둔다.</summary>
public sealed record ReportTable(string Caption, IReadOnlyList<ReportRun> Columns, IReadOnlyList<ReportRow> Rows) : ReportBlock;

/// <summary>타임라인 그림 (그림 데이터는 미리 보기와 HTML이 같이 쓴다).</summary>
public sealed record ReportFigure(TimelineFigure Figure) : ReportBlock;

public sealed record ReportSection(string Id, string Title, IReadOnlyList<ReportBlock> Blocks);

/// <summary>리포트 옵션 (M8 설계 3.3·4.1). 가리기 기본 켬, 메모 기본 뺌, 끊김 상세 0~10 (기본 5).</summary>
public sealed record ReportOptions(bool Masked = true, bool IncludeMemos = false, bool IncludeDiagnoses = true, int EpisodeDetails = 5)
{
    public const int MaxEpisodeDetails = 10;
    public ReportOptions Normalized() => this with { EpisodeDetails = Math.Clamp(EpisodeDetails, 0, MaxEpisodeDetails) };
}

/// <summary>
/// 리포트 문서 (M8 설계 2·3.1). 불변. 미리 보기와 내보낼 HTML이 이 하나에서 나온다 (같은 글 순서, 같은 그림 데이터).
/// </summary>
/// <param name="SourceKey">기록 열쇠 (RecordLibrary)</param>
/// <param name="Stamp">만들 때의 기록 도장. 바뀌면 이 문서는 오래됨</param>
public sealed record ReportDocument(
    string Title, string FileName, ReportOptions Options, string SourceKey, string Stamp, DateTimeOffset CreatedAt,
    IReadOnlyList<ReportRun> Lead, IReadOnlyList<ReportSection> Sections)
{
    /// <summary>안전망 (만든 기록의 원본 식별 값). 그리지 않는다.</summary>
    public Redactor? Guard { get; init; }

    /// <summary>
    /// 본문의 보이는 글 조각을 문서 순서대로 (머리 제목 → 머리 문단 → 절마다 제목·블록). HTML 본문의 텍스트 노드 순서와 같다.
    /// 그림은 제목·설명·눈금·범례 순 (<see cref="TimelineFigure.Texts"/>).
    /// </summary>
    public IEnumerable<ReportRun> Texts()
    {
        yield return ReportRun.Gen(Title);
        foreach (var r in Lead) yield return r;
        foreach (var s in Sections)
        {
            yield return ReportRun.Gen(s.Title);
            foreach (var b in s.Blocks)
                foreach (var r in BlockTexts(b)) yield return r;
        }
    }

    public static IEnumerable<ReportRun> BlockTexts(ReportBlock b)
    {
        switch (b)
        {
            case ReportParagraph p:
                foreach (var r in p.Runs) yield return r;
                break;
            case ReportHeading h:
                yield return h.Text;
                break;
            case ReportFacts f:
                foreach (var x in f.Items) { yield return ReportRun.Gen(x.Label); yield return x.Value; }
                break;
            case ReportTable t:
                yield return ReportRun.Gen(t.Caption);
                foreach (var c in t.Columns) yield return c;
                foreach (var row in t.Rows)
                    foreach (var cell in row.Cells) yield return cell.Value;
                break;
            case ReportFigure g:
                foreach (var r in g.Figure.Texts()) yield return r;
                break;
        }
    }

    /// <summary>값 칸 (고정 필드 이름, 값) 문서 순서대로. G3·G2 대조용.</summary>
    public IEnumerable<(string Field, string Value)> Fields()
    {
        foreach (var s in Sections)
            foreach (var b in s.Blocks)
                switch (b)
                {
                    case ReportFacts f:
                        foreach (var x in f.Items) yield return (x.Field, x.Value.Text);
                        break;
                    case ReportTable t:
                        foreach (var row in t.Rows)
                            foreach (var cell in row.Cells)
                                if (cell.Field is { } field) yield return (field, cell.Value.Text);
                        break;
                }
    }
}
