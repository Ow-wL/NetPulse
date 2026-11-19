using NetPulse.Core.Live;
using NetPulse.Design.Settings;

namespace NetPulse.App.Resident;

/// <summary>설정 저장 경계(<see cref="SettingsStore"/>)의 records 묶음을 Core 옮기기 서비스에 잇는다 (M9 설계 B1·B3).</summary>
public sealed class RecordFolderSettings(SettingsStore store) : IRecordFolderSettings
{
    public bool Writable => store.RecordsWritable;

    public RecordFolders Read()
    {
        var r = store.Current.Records;
        return new RecordFolders(r.Folder, r.ExtraFolders, r.Move is { } m ? new MoveLog(m.Kind, m.From, m.To, m.Ids, m.Hashes, m.Done, m.Step) : null);
    }

    public bool Save(RecordFolders next)
    {
        if (!store.RecordsWritable) return false;
        var journal = next.Move is { } m ? new MoveJournal(m.Kind, m.From, m.To, m.Ids, m.Hashes, MoveJournal.StepMoving, m.Done) : null;
        var result = store.Update(f => f with { Records = new RecordsSettings(next.Folder, next.Extra, journal) });
        return result == SettingsSaveResult.Saved && store.Current.Records.Folder == next.Folder;
    }
}

/// <summary>시작 때 기록 위치 상태 (설정 카드가 보인다).</summary>
/// <param name="Interrupted">지난 실행의 옮기기가 중간에 끊겼다</param>
/// <param name="JournalProblem">일지를 확인하지 못했다 (자동 동작 없음, records 저장 금지)</param>
public sealed record RecordStartup(bool Interrupted, string? JournalProblem);

/// <summary>설정 화면이 쓰는 상주·기록 위치 도구 (M9).</summary>
public sealed record ResidentServices(RecordMoveService Moves, RecordWorkGate WorkGate, RecordStartup RecordStartup);

/// <summary>
/// 시험 주입 <c>--move-fail</c> (M9 설계 7.2-11): cross(다른 볼륨처럼 복사 경로), copy:&lt;id&gt;(그 기록 복사 실패),
/// delete:&lt;id&gt;(원본 삭제 실패), kill-after:&lt;n&gt;(n개 옮긴 뒤 프로세스 강제 종료).
/// </summary>
public sealed class FaultMoveOps(string spec) : NetPulse.Core.Records.DiskMoveOps
{
    private string Arg => spec[(spec.IndexOf(':') + 1)..];
    public override bool SameVolume(string a, string b) => spec is not ("cross" or "hold" or "hold-long") && !spec.StartsWith("copy:", StringComparison.Ordinal) && !spec.StartsWith("delete:", StringComparison.Ordinal) && base.SameVolume(a, b);
    public override void CopyFile(string from, string to)
    {
        if (spec == "hold") Thread.Sleep(NetPulse.Design.Tokens.Waits.MoveHoldTest);
        if (spec == "hold-long") Thread.Sleep(NetPulse.Design.Tokens.Waits.MoveHoldLongTest);   // 옮기는 중 장면: 파일마다 멈춤 (멈추기로 풀린다)
        if (spec.StartsWith("copy:", StringComparison.Ordinal) && from.Contains(Arg, StringComparison.Ordinal)) throw new IOException("시험: 복사 실패");
        base.CopyFile(from, to);
    }
    public override void DeleteDirectory(string path)
    {
        if (spec.StartsWith("delete:", StringComparison.Ordinal) && Path.GetFileName(path).StartsWith(Arg, StringComparison.Ordinal)) throw new IOException("시험: 삭제 실패");
        base.DeleteDirectory(path);
    }
    public override void RenameDirectory(string from, string to)
    {
        if (spec.StartsWith("delete:", StringComparison.Ordinal) && Path.GetFileName(from).StartsWith(Arg, StringComparison.Ordinal)
            && !Path.GetFileName(from).EndsWith(NetPulse.Core.Records.RecordMover.MovingSuffix, StringComparison.Ordinal)) throw new IOException("시험: 이름 바꾸기 실패");
        base.RenameDirectory(from, to);
    }

    public static void Configure(RecordMoveService moves, string spec)
    {
        moves.Ops = new FaultMoveOps(spec);
        if (spec.StartsWith("kill-after:", StringComparison.Ordinal) && int.TryParse(spec[11..], out var n))
            moves.AfterRecordForTest = done => { if (done >= n) System.Diagnostics.Process.GetCurrentProcess().Kill(); };
    }
}
