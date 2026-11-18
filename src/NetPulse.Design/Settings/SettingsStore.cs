namespace NetPulse.Design.Settings;

public enum SettingsSaveResult { Saved, NotPersisted, Failed }

/// <summary>
/// 설정의 유일한 저장 경계 (M9 설계 B1 [2-7]). 최신 설정 전체를 가지고, 모든 변경을 잠금 안에서 <b>최신 값 위에</b> 묶음 단위로
/// 적용한 뒤 임시 파일 → 바꾸기 → 다시 읽어 확인으로 저장한다. 스레드 안전 (옮기기 작업자·세션 종료가 UI 밖에서 부른다).
/// <list type="bullet">
/// <item>알 수 없는(더 새) 판을 읽었으면 이 실행은 저장하지 않는다(<see cref="CanPersist"/> 거짓): 새 판 파일을 낡은 판으로 덮지 않는다.</item>
/// <item>옮기기 일지 검증이 실패하면(Core) <see cref="BlockRecords"/>: <c>records</c> 묶음은 바뀌지 않고 원문 그대로 다시 쓴다.</item>
/// </list>
/// </summary>
public sealed class SettingsStore
{
    private readonly object gate = new();
    private readonly string? folder;
    private readonly string? rawRecords;
    private SettingsFile current;
    private bool recordsBlocked;

    /// <param name="folder">null이면 저장하지 않는 저장소 (전시 화면 시험·단위 시험).</param>
    public SettingsStore(string? folder, SettingsLoad load)
    {
        this.folder = folder;
        current = load.Settings;
        Status = load.Status;
        rawRecords = load.RawRecords;
        CanPersist = folder is not null && load.Status != SettingsLoadStatus.UnknownVersion;
    }

    public SettingsLoadStatus Status { get; }

    /// <summary>이 실행에서 설정 파일을 쓸 수 있는가 (새 판이면 거짓).</summary>
    public bool CanPersist { get; }

    /// <summary><c>records</c> 묶음을 바꾸고 저장할 수 있는가 (기록 옮기기 등 파괴적 동작의 조건).</summary>
    public bool RecordsWritable { get { lock (gate) return CanPersist && !recordsBlocked; } }

    public SettingsFile Current { get { lock (gate) return current; } }

    /// <summary>바뀐 뒤 (잠금 밖, 부른 스레드에서).</summary>
    public event Action<SettingsFile>? Changed;

    /// <summary>일지를 검증하지 못했다: 이 실행에서 records 묶음을 바꾸지 않고 저장할 때 원문 그대로 쓴다 (M9 설계 B3 흐름 9).</summary>
    public void BlockRecords() { lock (gate) recordsBlocked = true; }

    /// <summary>
    /// 최신 값에 바꾸기를 적용하고 저장한다. 저장할 수 없는 실행이면 메모리에만 적용하고 <see cref="SettingsSaveResult.NotPersisted"/>.
    /// records 묶음이 막혀 있으면 그 묶음의 바뀜은 버린다.
    /// </summary>
    public SettingsSaveResult Update(Func<SettingsFile, SettingsFile> change) => Update(change, out _);

    public SettingsSaveResult Update(Func<SettingsFile, SettingsFile> change, out string? failure)
    {
        SettingsFile next;
        SettingsSaveResult result;
        failure = null;
        lock (gate)
        {
            next = change(current);
            if (recordsBlocked) next = next with { Records = current.Records };
            current = next;
            if (!CanPersist) result = SettingsSaveResult.NotPersisted;
            else
            {
                try
                {
                    Write(next);
                    result = SettingsSaveResult.Saved;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    failure = ex.GetType().Name;
                    result = SettingsSaveResult.Failed;
                }
            }
        }
        Changed?.Invoke(next);
        return result;
    }

    /// <summary>gate 안.</summary>
    private void Write(SettingsFile next)
    {
        Directory.CreateDirectory(folder!);
        var path = Path.Combine(folder!, SettingsFormat.FileName);
        var temp = path + ".tmp";
        var text = SettingsFormat.Serialize(next, recordsBlocked ? rawRecords : null);
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        // 다시 읽어 확인 (쓴 글 그대로인지). 다르면 저장 실패로 본다
        if (File.ReadAllText(path) != text) throw new InvalidDataException("설정 확인 실패");
    }
}
