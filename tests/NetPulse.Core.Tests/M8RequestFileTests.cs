using System.Text;
using NetPulse.Core.Report;
using static NetPulse.Core.Tests.M8Records;

namespace NetPulse.Core.Tests;

/// <summary>생성·내보내기 스냅숏과 파일 쓰기 (M8 설계 3.6·3.7, 7.1 ReportRequest·ReportFile).</summary>
public sealed class M8RequestFileTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static ReportDocument Doc(ReportRequest r) => ReportBuilder.Build(Input(Typical(), r.Options) with { Key = r.Key, Stamp = r.Stamp });

    [Fact]
    public void AnOlderCompletionIsIgnoredAndOnlyTheLatestCanBeExported()
    {
        var q = new ReportRequests();
        var first = q.Begin("k", "s1", new ReportOptions(Masked: false));
        var second = q.Begin("k", "s1", new ReportOptions(Masked: false, IncludeMemos: true));
        Assert.Equal(ReportState.Building, q.State);
        Assert.Null(q.ForExport());
        // 완료 순서 뒤집기: 나중 요청이 먼저 끝나고, 앞 요청이 늦게 끝남
        Assert.True(q.Complete(second, Doc(second)));
        Assert.False(q.Complete(first, Doc(first)));
        Assert.Equal(ReportState.Ready, q.State);
        Assert.True(q.ForExport()!.Options.IncludeMemos);
        Assert.False(q.Fail(first, "늦은 실패"));
        Assert.Equal(ReportState.Ready, q.State);
    }

    [Fact]
    public void TurningMaskingOnDropsTheUnmaskedDocumentAtOnce()
    {
        var q = new ReportRequests();
        var open = q.Begin("k", "s1", new ReportOptions(Masked: false));
        q.Complete(open, Doc(open));
        Assert.False(q.Published!.Options.Masked);
        var masked = q.Begin("k", "s1", new ReportOptions(Masked: true));
        Assert.Null(q.Published);          // 그 순간 버림: 미리 보기는 "만드는 중"
        Assert.Null(q.ForExport());
        Assert.False(q.Complete(open, Doc(open)));   // 늦게 도착한 가리지 않은 문서도 게시되지 않음
        Assert.Null(q.ForExport());
        Assert.True(q.Complete(masked, Doc(masked)));
        Assert.True(q.ForExport()!.Options.Masked);
    }

    [Fact]
    public void WhileBuildingTheOldDocumentStaysInThePreviewButCannotBeExported()
    {
        var q = new ReportRequests();
        var a = q.Begin("k", "s1", new ReportOptions());
        q.Complete(a, Doc(a));
        q.Begin("k", "s1", new ReportOptions(EpisodeDetails: 2));
        Assert.NotNull(q.Published);
        Assert.Equal(ReportState.Building, q.State);
        Assert.Null(q.ForExport());
    }

    [Fact]
    public void AChangedStampMakesThePublishedDocumentStale()
    {
        var q = new ReportRequests();
        var r = q.Begin("k", "s1", new ReportOptions());
        q.Complete(r, Doc(r));
        Assert.False(q.StampChanged("other", "s2"));
        Assert.False(q.StampChanged("k", "s1"));
        Assert.True(q.StampChanged("k", "s2"));
        Assert.Equal(ReportState.Stale, q.State);
        Assert.Null(q.ForExport());
        var again = q.Begin("k", "s2", new ReportOptions());
        q.Complete(again, Doc(again));
        Assert.Equal(ReportState.Ready, q.State);
        Assert.NotNull(q.ForExport());
    }

    [Fact]
    public async Task AFailedBuildCannotBeExportedAndASlowEarlierBuildDoesNotWin()
    {
        var q = new ReportRequests();
        var gate = new TaskCompletionSource();
        var slow = q.Begin("k", "s1", new ReportOptions());
        var slowRun = q.RunAsync(slow, r => { gate.Task.Wait(TestContext.Current.CancellationToken); return Doc(r); });
        var fast = q.Begin("k", "s1", new ReportOptions(EpisodeDetails: 1));
        Assert.True(await q.RunAsync(fast, Doc));
        gate.SetResult();
        Assert.False(await slowRun);
        Assert.Equal(1, q.ForExport()!.Options.EpisodeDetails);

        var broken = q.Begin("k", "s1", new ReportOptions());
        Assert.False(await q.RunAsync(broken, _ => throw new ReportException(ReportError.Unreadable)));
        Assert.Equal(ReportState.Failed, q.State);
        Assert.Null(q.ForExport());
    }

    // ---------------- 파일 ----------------

    private string[] Temps(string folder) => Directory.GetFiles(folder, ReportFile.TempPrefix + "*");

    [Fact]
    public void AWrittenFileIsTheWholeHtmlAndNoTempFileRemains()
    {
        var folder = dir.Sub("ok");
        var path = Path.Combine(folder, "NetPulse-리포트.html");
        var r = ReportFile.Write(path, "<p>한글</p>", overwrite: false);
        Assert.True(r.Ok);
        Assert.Equal("<p>한글</p>", File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal(Encoding.UTF8.GetByteCount("<p>한글</p>"), r.Bytes);
        Assert.Empty(Temps(folder));
        var exists = ReportFile.Write(path, "새 글", overwrite: false);
        Assert.Equal(ReportWriteError.Exists, exists.Error);
        Assert.Equal("<p>한글</p>", File.ReadAllText(path, Encoding.UTF8));
        Assert.True(ReportFile.Write(path, "바꾼 글", overwrite: true).Ok);
        Assert.Equal("바꾼 글", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Temps(folder));
    }

    [Theory]
    [InlineData(ReportFault.Write)]
    [InlineData(ReportFault.Move)]
    public void FailuresLeaveNoTempFileAndKeepTheExistingFile(ReportFault fault)
    {
        var folder = dir.Sub("fail-" + fault);
        var path = Path.Combine(folder, "r.html");
        File.WriteAllText(path, "원래 파일");
        var r = ReportFile.Write(path, "<p>새 리포트</p>", overwrite: true, fault);
        Assert.Equal(ReportWriteError.Failed, r.Error);
        Assert.Null(r.Path);
        Assert.Equal("원래 파일", File.ReadAllText(path));
        Assert.Empty(Temps(folder));

        var fresh = Path.Combine(folder, "new.html");
        Assert.False(ReportFile.Write(fresh, "<p>x</p>", overwrite: false, fault).Ok);
        Assert.False(File.Exists(fresh));
        Assert.Empty(Temps(folder));
    }

    [Fact]
    public void AReplaceFailureKeepsTheOriginalFile()
    {
        var folder = dir.Sub("locked");
        var path = Path.Combine(folder, "r.html");
        File.WriteAllText(path, "원래 파일");
        ReportWriteResult r;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))   // 바꾸기가 실패하게 열어 둔다
            r = ReportFile.Write(path, "<p>새 리포트</p>", overwrite: true);
        Assert.False(r.Ok);
        Assert.Equal("원래 파일", File.ReadAllText(path));
        Assert.Empty(Temps(folder));
    }

    [Fact]
    public void AMissingFolderFailsWithoutThrowing()
    {
        var path = Path.Combine(dir.Path, "no-such-folder", "r.html");
        var r = ReportFile.Write(path, "<p>x</p>", overwrite: false);
        Assert.Equal(ReportWriteError.Failed, r.Error);
    }
}
