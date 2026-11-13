using System.Net;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

/// <summary>기록 쓰기 보장과 읽기 손상 처리 (M3 설계 6.5, 6.7).</summary>
public sealed class RecordTests
{
    private const string Id = "20261113T000000Z_abc123";
    private static readonly TargetResult[] NoResults = [];
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static RecordHeader Header(int segment = 0, string id = Id) => new(id, segment, "0.5.0", W0, 540,
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Tcp(443)),
        ], 1000, 800, 600_000, "primary");

    private static RoundData Round(long r, ProbeOutcome a = ProbeOutcome.Success, ProbeOutcome b = ProbeOutcome.Success) =>
        new(r, r * 1000, r * 1000 + 3, W0.AddSeconds(r), 0, false,
        [
            new TargetResult("a", a, a == ProbeOutcome.Success ? 1.5 : null, a == ProbeOutcome.Error ? "E" : null),
            new TargetResult("b", b, b == ProbeOutcome.Success ? 20.25 : null, b == ProbeOutcome.Error ? "E" : null),
        ]);

    private static string Line(long r) => RecordSchema.RoundLine(Id, Round(r));

    private static string Seg(TempDir dir, string name = Id, int segment = 0, IEnumerable<string>? lines = null, string? tail = null)
    {
        var folder = dir.Sub(name);
        TempDir.Write(Path.Combine(folder, "measurement.json"), RecordSchema.HeaderJson(Header(segment)));
        if (lines is not null) TempDir.Write(Path.Combine(folder, "rounds.jsonl"), string.Concat(lines.Select(l => l + "\n")) + (tail ?? ""));
        return folder;
    }

    // ---------------- 쓰기 ----------------

    [Fact]
    public void PartialWriteIsRolledBackSoRetryLeavesNoDamage()
    {
        using var dir = new TempDir();
        var faults = new Queue<Fault>([Fault.None, Fault.PartialWrite, Fault.None, Fault.None]);
        var folder = new FaultyFolder(new DiskRecordFolder(dir.Path, "primary"), _ => faults.Count > 0 ? faults.Dequeue() : Fault.None);
        using (var w = new LineAppender(folder, "rounds"))
        {
            Assert.True(w.TryAppend(Line(0), out _));
            Assert.False(w.TryAppend(Line(1), out var failure));
            Assert.IsType<IOException>(failure);
            Assert.True(w.TryAppend(Line(1), out _));
            Assert.True(w.TryAppend(Line(2), out _));
        }
        TempDir.Write(Path.Combine(dir.Path, "measurement.json"), RecordSchema.HeaderJson(Header()));
        var rec = RecordReader.Read([dir.Path]);
        Assert.Equal(RecordStatus.Ok, rec.Status);
        Assert.Equal([0L, 1, 2], rec.Rounds.Select(r => r.Round));
    }

    [Fact]
    public void FlushFailureIsRolledBack()
    {
        using var dir = new TempDir();
        var faults = new Queue<Fault>([Fault.FlushFails, Fault.None]);
        var folder = new FaultyFolder(new DiskRecordFolder(dir.Path, "primary"), _ => faults.Count > 0 ? faults.Dequeue() : Fault.None);
        using (var w = new LineAppender(folder, "rounds"))
        {
            Assert.False(w.TryAppend(Line(0), out _));
            Assert.True(w.TryAppend(Line(0), out _));
        }
        Assert.Single(File.ReadAllLines(Path.Combine(dir.Path, "rounds.jsonl")));
    }

    [Fact]
    public void ReportedFailureThatWasWrittenBecomesOneDuplicate()
    {
        using var dir = new TempDir();
        var faults = new Queue<Fault>([Fault.FlushFailsButWritten, Fault.None, Fault.None]);
        var folder = new FaultyFolder(new DiskRecordFolder(dir.Path, "primary"), _ => faults.Count > 0 ? faults.Dequeue() : Fault.None);
        using (var w = new LineAppender(folder, "rounds"))
        {
            Assert.False(w.TryAppend(Line(0), out _));
            Assert.True(w.TryAppend(Line(0), out _));
            Assert.True(w.TryAppend(Line(1), out _));
        }
        TempDir.Write(Path.Combine(dir.Path, "measurement.json"), RecordSchema.HeaderJson(Header()));
        var rec = RecordReader.Read([dir.Path]);
        Assert.Equal(1, rec.Damage.Duplicate);
        Assert.Equal([0L, 1], rec.Rounds.Select(r => r.Round));
    }

    [Fact]
    public void FailedRollbackMovesToNextPartFile()
    {
        using var dir = new TempDir();
        var faults = new Queue<Fault>([Fault.None, Fault.RollbackFails, Fault.None, Fault.None]);
        var folder = new FaultyFolder(new DiskRecordFolder(dir.Path, "primary"), _ => faults.Count > 0 ? faults.Dequeue() : Fault.None);
        using (var w = new LineAppender(folder, "rounds"))
        {
            Assert.True(w.TryAppend(Line(0), out _));
            Assert.False(w.TryAppend(Line(1), out _));
            Assert.Equal(2, w.Part);
            Assert.True(w.TryAppend(Line(1), out _));
            Assert.True(w.TryAppend(Line(2), out _));
        }
        Assert.True(File.Exists(Path.Combine(dir.Path, "rounds-2.jsonl")));
        TempDir.Write(Path.Combine(dir.Path, "measurement.json"), RecordSchema.HeaderJson(Header()));
        var rec = RecordReader.Read([dir.Path]);
        Assert.Equal([0L, 1, 2], rec.Rounds.Select(r => r.Round));
        Assert.Equal(1, rec.Damage.TruncatedTail);   // 버린 첫 파일 끝의 반쪽 줄
    }

    // ---------------- 읽기 ----------------

    [Fact]
    public void RoundTripKeepsEverything()
    {
        using var dir = new TempDir();
        var rounds = new[] { Round(0), Round(1, ProbeOutcome.Lost, ProbeOutcome.Error), Round(2) with { Skipped = 3, Gap = true } };
        var folder = Seg(dir, lines: rounds.Select(r => RecordSchema.RoundLine(Id, r)));
        var summary = new RecordSummary(Id, EndReason.Stopped, W0.AddMinutes(1), 3, 3, 0, 0, 0, 2, false, null);
        TempDir.Write(Path.Combine(folder, "summary.json"), RecordSchema.SummaryJson(summary));
        var rec = RecordReader.Read([folder]);
        Assert.Equal(RecordStatus.Ok, rec.Status);
        Assert.Equal(EndReason.Stopped, rec.EndReason);
        Assert.Null(rec.MissingNotice);
        Assert.Equal(summary, rec.Summary);
        Assert.Equal(Header().Targets, rec.Header!.Targets);
        Assert.Equal(rounds.Length, rec.Rounds.Count);
        for (int i = 0; i < rounds.Length; i++)
        {
            Assert.Equal(rounds[i] with { Results = NoResults }, rec.Rounds[i] with { Results = NoResults });
            Assert.Equal(rounds[i].Results, rec.Rounds[i].Results);
        }
    }

    [Fact]
    public void DamagedLinesAnywhereAreSkippedAndCounted()
    {
        using var dir = new TempDir();
        var lines = new[]
        {
            Line(0),
            "{ 망가진 줄",                                                                // 중간 형식 오류
            Line(1).Replace(Id, "다른측정"),                                             // 다른 측정 Id
            Line(2).Replace("\"t\":\"b\"", "\"t\":\"zz\""),                              // 모르는 대상
            Line(3).Replace("\"s\":\"ok\",\"ms\":1.5", "\"s\":\"lost\",\"ms\":1.5"),    // 손실인데 지연 있음
            Line(4).Replace("\"s\":\"ok\",\"ms\":1.5", "\"s\":\"maybe\",\"ms\":1.5"),   // 모르는 결과값
            Line(6),
            Line(5),                                                                    // 역전
            Line(6),                                                                    // 중복
            Line(7),
        };
        var folder = Seg(dir, lines: lines, tail: Line(8)[..20]);   // 잘린 끝
        var rec = RecordReader.Read([folder]);
        Assert.Equal(RecordStatus.Damaged, rec.Status);
        var d = rec.Damage;
        Assert.Equal((1, 1, 1, 1, 1), (d.Malformed, d.WrongId, d.UnknownTarget, d.BadCombination, d.UnknownValue));
        Assert.Equal((1, 1, 1), (d.OutOfOrder, d.Duplicate, d.TruncatedTail));
        Assert.Equal([0L, 6, 7], rec.Rounds.Select(r => r.Round));
        Assert.Equal(EndReason.Interrupted, rec.EndReason);   // 요약 없음
    }

    [Theory]
    [InlineData("\"s\":\"error\"", false)]            // 오류인데 사유 없음
    [InlineData("\"s\":\"error\",\"e\":\"\"", false)]  // 빈 사유
    [InlineData("\"s\":\"ok\"", false)]               // 성공인데 지연 없음
    [InlineData("\"s\":\"ok\",\"ms\":-1", false)]     // 음수 지연
    [InlineData("\"s\":\"error\",\"e\":\"X\"", true)]
    public void ResultCombinations(string resultA, bool valid)
    {
        var line = $$"""{"id":"{{Id}}","r":0,"due":0,"at":1,"wall":"2026-11-13T00:00:00.000Z","results":[{"t":"a",{{resultA}}}]}""";
        var (round, problem) = RecordSchema.ParseRound(line, Id, new HashSet<string> { "a", "b" });
        Assert.Equal(valid, round is not null);
        if (!valid) Assert.Equal(RecordSchema.LineProblem.BadCombination, problem);
        else Assert.Equal((0L, false), (round!.Skipped, round.Gap));   // 없는 필드의 기본값
    }

    [Fact]
    public void SummaryMismatchShowsMissingNotice()
    {
        // 마지막 회차 번호는 같고 중간 회차만 손상: 요약 관측 4, 원본 유효 3
        using var dir = new TempDir();
        var folder = Seg(dir, lines: [Line(0), "{x", Line(2), Line(3)]);
        TempDir.Write(Path.Combine(folder, "summary.json"),
            RecordSchema.SummaryJson(new RecordSummary(Id, EndReason.Completed, W0, 4, 4, 0, 0, 0, 3, false, null)));
        var rec = RecordReader.Read([folder]);
        Assert.Equal(EndReason.Completed, rec.EndReason);
        Assert.Contains("관측 4회차 중 원본 3회차", rec.MissingNotice);
        Assert.Equal(3, rec.Stats!.Rounds);   // 통계는 원본으로 다시 계산
    }

    [Fact]
    public void DamagedSummaryMeansInterrupted()
    {
        using var dir = new TempDir();
        var folder = Seg(dir, lines: [Line(0)]);
        TempDir.Write(Path.Combine(folder, "summary.json"), "{ 망가진");
        var rec = RecordReader.Read([folder]);
        Assert.Equal(EndReason.Interrupted, rec.EndReason);
        Assert.Null(rec.Summary);
        Assert.Equal(1, rec.Stats!.Rounds);
    }

    [Fact]
    public void EventDamageIsSeparateFromRoundStats()
    {
        using var dir = new TempDir();
        var folder = Seg(dir, lines: [Line(0), Line(1)]);
        TempDir.Write(Path.Combine(folder, "events.jsonl"),
            RecordSchema.EventLine(Id, "clock", 1, 1000, W0, new() { ["deltaMs"] = 9000 }) + "\n{망가진\n" + RecordSchema.EventLine("남의것", "clock", 1, 1, W0) + "\n");
        var rec = RecordReader.Read([folder]);
        Assert.Equal(2, rec.Damage.DamagedEvents);
        Assert.Equal(0, rec.Damage.Total);
        Assert.Single(rec.Events);
        Assert.Equal(2, rec.Stats!.Rounds);
    }

    [Fact]
    public void GapEventsAreReadBack()
    {
        using var dir = new TempDir();
        var folder = Seg(dir, lines: [Line(0), Line(1)]);
        var fields = new System.Text.Json.Nodes.JsonObject
        {
            ["reason"] = "suspend", ["fromMs"] = 500, ["toMs"] = 900, ["fromWall"] = RecordSchema.Iso(W0), ["toWall"] = RecordSchema.Iso(W0.AddHours(1)),
            ["lastRoundBefore"] = 0, ["firstRoundAfter"] = 1,
        };
        TempDir.Write(Path.Combine(folder, "events.jsonl"), RecordSchema.EventLine(Id, "gap", 1, 900, W0.AddHours(1), fields) + "\n");
        var gap = Assert.Single(RecordReader.Read([folder]).Gaps);
        Assert.Equal(new GapSpan(GapReason.Suspend, 500, 900, W0, W0.AddHours(1), 0, 1), gap);
    }

    [Fact]
    public void UnsupportedAndUnreadableHeaders()
    {
        using var dir = new TempDir();
        var a = dir.Sub("a");
        TempDir.Write(Path.Combine(a, "measurement.json"), RecordSchema.HeaderJson(Header()).Replace("\"version\": 1", "\"version\": 2"));
        Assert.Equal(RecordStatus.Unsupported, RecordReader.Read([a]).Status);
        var b = dir.Sub("b");
        TempDir.Write(Path.Combine(b, "measurement.json"), RecordSchema.HeaderJson(Header()).Replace("netpulse.measurement", "other.format"));
        Assert.Equal(RecordStatus.Unsupported, RecordReader.Read([b]).Status);
        var c = dir.Sub("c");
        TempDir.Write(Path.Combine(c, "measurement.json"), "{ \"format\": \"netpulse.measurement\", \"version\": 1 }");
        Assert.Equal(RecordStatus.Unreadable, RecordReader.Read([c]).Status);
        Assert.Equal(RecordStatus.Unreadable, RecordReader.Read([dir.Sub("d")]).Status);
    }

    [Fact]
    public void SegmentsAreMergedByIdAndMissingOnesAreFlagged()
    {
        using var primary = new TempDir();
        using var fallback = new TempDir();
        Seg(primary, lines: [Line(0), Line(1)]);
        var s1 = Seg(fallback, segment: 1, lines: [Line(1), Line(2)]);   // 옮기기 직전 회차가 양쪽에 있을 수 있다
        TempDir.Write(Path.Combine(s1, "summary.json"), RecordSchema.SummaryJson(new RecordSummary(Id, EndReason.Stopped, W0, 3, 3, 0, 0, 0, 2, false, null)));
        var segments = RecordReader.FindSegments(Id, [primary.Path, fallback.Path]);
        Assert.Equal(2, segments.Count);
        var rec = RecordReader.Read(segments);
        Assert.Equal([0L, 1, 2], rec.Rounds.Select(r => r.Round));
        Assert.Equal(1, rec.Damage.Duplicate);
        Assert.Equal(EndReason.Stopped, rec.EndReason);
        Assert.Empty(rec.MissingSegments);

        // 이전 위치에 닿지 못하면 누락 조각
        var only = RecordReader.Read(RecordReader.FindSegments(Id, [fallback.Path]));
        Assert.Equal([0], only.MissingSegments);
        Assert.Contains("찾지 못한 조각 1개", only.MissingNotice);
        Assert.Equal(RecordStatus.Damaged, only.Status);
    }

    [Fact]
    public void MixedMeasurementsAreRefused()
    {
        using var dir = new TempDir();
        var a = Seg(dir, name: "a");
        var b = dir.Sub("b");
        TempDir.Write(Path.Combine(b, "measurement.json"), RecordSchema.HeaderJson(Header(1, "다른측정")));
        Assert.Equal(RecordStatus.Unreadable, RecordReader.Read([a, b]).Status);
    }

    [Fact]
    public void DiskFullOnSameVolumeDoesNotRelocate()
    {
        var full = new IOException("가득", unchecked((int)0x80070070));
        var denied = new UnauthorizedAccessException();
        var c1 = new FaultyRoot(new DiskRecordRoot("x", "primary"), "C:\\");
        var c2 = new FaultyRoot(new DiskRecordRoot("y", "fallback"), "C:\\");
        var d = new FaultyRoot(new DiskRecordRoot("z", "fallback"), "D:\\");
        Assert.True(RecordLocation.IsDiskFull(full));
        Assert.False(RecordLocation.ShouldRelocate(full, c1, c2));
        Assert.True(RecordLocation.ShouldRelocate(full, c1, d));
        Assert.True(RecordLocation.ShouldRelocate(denied, c1, c2));
        Assert.False(RecordLocation.ShouldRelocate(denied, c2, c2));   // 이미 대체 위치
    }

    [Fact]
    public void NewIdLooksLikeFolderName()
    {
        var id = RecordLocation.NewId(new DateTimeOffset(2026, 11, 13, 9, 30, 5, TimeSpan.FromHours(9)));
        Assert.Matches("^20261113T003005Z_[a-z0-9]{6}$", id);
    }
}
