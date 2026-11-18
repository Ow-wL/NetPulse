using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPulse.Design.Settings;

public enum ThemePreference { Dark, Light, System }

public enum SettingsLoadStatus { Loaded, Missing, Corrupt, UnknownVersion, Migrated }

/// <summary>화면 설정 (M2 설계 5.1). 설정 파일의 <c>look</c> 묶음.</summary>
public sealed record AppSettings(ThemePreference Theme, bool Aurora, bool ReduceEffects, bool ReduceMotion)
{
    /// <summary>첫 실행: 다크, 오로라 끔, 효과·움직임 줄이기 끔.</summary>
    public static readonly AppSettings Default = new(ThemePreference.Dark, false, false, false);
}

/// <summary>상주와 알림 (M9 설계 A·B2). 설정 파일의 <c>resident</c> 묶음.</summary>
public sealed record ResidentSettings(bool CloseToTray, bool NotifyDropout, bool NotifyRecovered, bool NotifyFinished, bool TrayHintShown)
{
    public static readonly ResidentSettings Default = new(true, true, false, true, false);
}

/// <summary>
/// 기록 옮기기 일지 (M9 설계 B3). 칸별 복구를 하지 않는다: 한 덩어리로 검증하고 맞지 않으면 고치지 않고 보존한다 [2-8].
/// <see cref="Hashes"/>는 측정 Id → 잠근 뒤 판정한 내용 해시(조각 폴더들의 파일 목록·크기·SHA-256을 합친 값).
/// </summary>
public sealed record MoveJournal(string Kind, string From, string To, IReadOnlyList<string> Ids, IReadOnlyDictionary<string, string> Hashes,
    string Step, IReadOnlyList<string> Done)
{
    public const string KindNew = "new", KindRetry = "retry", KindDefault = "default";
    public const string StepMoving = "moving";
}

/// <summary>기록 위치 (M9 설계 B3). <see cref="Folder"/>가 null이면 기본 위치(사용자 폴더의 records).</summary>
public sealed record RecordsSettings(string? Folder, IReadOnlyList<string> ExtraFolders, MoveJournal? Move)
{
    public static readonly RecordsSettings Default = new(null, [], null);
}

/// <summary>진단 기본값 (M9 설계 B4). null이면 Core 기본값. 값의 뜻(IPv4·호스트 이름)은 Core가 검사한다.</summary>
public sealed record DiagnosisPrefs(IReadOnlyList<string>? TcpTargets, IReadOnlyList<string>? Domains)
{
    public static readonly DiagnosisPrefs Default = new(null, null);
}

/// <summary>설정 파일 2판의 내용 전체.</summary>
public sealed record SettingsFile(AppSettings Look, ResidentSettings Resident, RecordsSettings Records, DiagnosisPrefs Diagnosis)
{
    public static readonly SettingsFile Default = new(AppSettings.Default, ResidentSettings.Default, RecordsSettings.Default, DiagnosisPrefs.Default);
}

/// <summary>읽은 결과. <see cref="Repaired"/>는 기본값으로 바꾼 칸 이름(값은 남기지 않는다).</summary>
/// <see cref="RawRecords"/>는 읽은 <c>records</c> 묶음의 원문: 일지 검증이 실패해 그 묶음을 저장하지 않을 때 원문 그대로 다시 쓴다.
public sealed record SettingsLoad(SettingsFile Settings, SettingsLoadStatus Status, string? Detail, IReadOnlyList<string> Repaired, string? RawRecords);

/// <summary>
/// 설정 파일 (M2 설계 5.1, M9 설계 B1). 사용자 데이터 폴더의 settings.json, 2판.
/// 1판은 <c>look</c>으로 옮긴다. 칸 하나가 잘못되면 그 칸만 기본값(진단 주소·도메인은 쌍 단위). 손상 파일은 기본값으로 시작하고
/// 원본을 한 번 <c>.corrupt</c>로 남긴다. 알 수 없는(더 새) 판이면 기본값으로 시작하고 저장하지 않는다(<see cref="SettingsStore"/>).
/// </summary>
public static class SettingsFormat
{
    public const int CurrentVersion = 2;
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    public static SettingsLoad Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return new(SettingsFile.Default, SettingsLoadStatus.Missing, null, [], null);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(SettingsFile.Default, SettingsLoadStatus.Corrupt, ex.GetType().Name, [], null); }
        JsonObject root;
        try { root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("객체 아님"); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            KeepCorrupt(path);
            return new(SettingsFile.Default, SettingsLoadStatus.Corrupt, ex.GetType().Name, [], null);
        }
        int? version = root["version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        var repaired = new List<string>();
        if (version == 1)
        {
            var look = ReadLook(root, "", repaired);
            return new(SettingsFile.Default with { Look = look }, SettingsLoadStatus.Migrated, "version 1", repaired, null);
        }
        if (version != CurrentVersion)
            return new(SettingsFile.Default, SettingsLoadStatus.UnknownVersion, version is null ? "version 없음" : "version " + version, [], null);
        var file = new SettingsFile(
            ReadLook(root["look"] as JsonObject ?? Missing(repaired, "look"), "look.", repaired),
            ReadResident(root["resident"] as JsonObject ?? Missing(repaired, "resident"), repaired),
            ReadRecords(root["records"] as JsonObject ?? Missing(repaired, "records"), repaired),
            ReadDiagnosis(root["diagnosis"] as JsonObject ?? Missing(repaired, "diagnosis"), repaired));
        return new(file, SettingsLoadStatus.Loaded, null, repaired, (root["records"] as JsonObject)?.ToJsonString());
    }

    /// <summary>파일 내용 (임시 파일에 쓰는 글).</summary>
    /// <param name="rawRecords">null이 아니면 <c>records</c> 묶음을 이 원문 그대로 쓴다 (일지 검증 실패, B3 흐름 9).</param>
    public static string Serialize(SettingsFile s, string? rawRecords = null)
    {
        var root = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["look"] = new JsonObject
            {
                ["theme"] = s.Look.Theme switch { ThemePreference.Light => "light", ThemePreference.System => "system", _ => "dark" },
                ["aurora"] = s.Look.Aurora, ["reduceEffects"] = s.Look.ReduceEffects, ["reduceMotion"] = s.Look.ReduceMotion,
            },
            ["resident"] = new JsonObject
            {
                ["closeToTray"] = s.Resident.CloseToTray, ["notifyDropout"] = s.Resident.NotifyDropout, ["notifyRecovered"] = s.Resident.NotifyRecovered,
                ["notifyFinished"] = s.Resident.NotifyFinished, ["trayHintShown"] = s.Resident.TrayHintShown,
            },
            ["records"] = rawRecords is not null ? JsonNode.Parse(rawRecords) : new JsonObject
            {
                ["folder"] = s.Records.Folder,
                ["extraFolders"] = new JsonArray(s.Records.ExtraFolders.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["move"] = s.Records.Move is { } m ? JournalNode(m) : null,
            },
            ["diagnosis"] = new JsonObject
            {
                ["tcpTargets"] = Strings(s.Diagnosis.TcpTargets),
                ["domains"] = Strings(s.Diagnosis.Domains),
            },
        };
        return root.ToJsonString(Write);
    }

    /// <summary>일지를 글로 (검증 실패한 일지를 원래 모양 그대로 다시 쓸 때도 쓴다).</summary>
    public static JsonObject JournalNode(MoveJournal m) => new()
    {
        ["kind"] = m.Kind, ["from"] = m.From, ["to"] = m.To,
        ["ids"] = new JsonArray(m.Ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        ["hashes"] = new JsonObject(m.Hashes.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
        ["step"] = m.Step,
        ["done"] = new JsonArray(m.Done.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
    };

    /// <summary>일지 글을 읽는다. 모양이 틀리면 null (뜻 검증은 Core가 한다).</summary>
    public static MoveJournal? ParseJournal(JsonObject? o)
    {
        if (o is null) return null;
        string? Str(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        List<string>? List(string k)
        {
            if (o[k] is not JsonArray a) return null;
            var r = new List<string>();
            foreach (var n in a) { if (n is JsonValue v && v.TryGetValue<string>(out var s)) r.Add(s); else return null; }
            return r;
        }
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o["hashes"] is not JsonObject h) return null;
        foreach (var (k, n) in h) { if (n is JsonValue v && v.TryGetValue<string>(out var s)) hashes[k] = s; else return null; }
        if (Str("kind") is not { } kind || Str("from") is not { } from || Str("to") is not { } to || Str("step") is not { } step
            || List("ids") is not { } ids || List("done") is not { } done) return null;
        return new MoveJournal(kind, from, to, ids, hashes, step, done);
    }

    private static void KeepCorrupt(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // 이미 있으면 처음 것을 둔다
    }

    private static JsonObject Missing(List<string> repaired, string name) { repaired.Add(name); return []; }

    private static JsonNode? Strings(IReadOnlyList<string>? list) => list is null ? null : new JsonArray(list.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private static bool Bool(JsonObject o, string key, bool fallback, string prefix, List<string> repaired)
    {
        if (o[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        repaired.Add(prefix + key);
        return fallback;
    }

    private static AppSettings ReadLook(JsonObject o, string prefix, List<string> repaired)
    {
        var d = AppSettings.Default;
        ThemePreference theme = d.Theme;
        if (o["theme"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t switch { "dark" => true, "light" => true, "system" => true, _ => false })
            theme = t switch { "light" => ThemePreference.Light, "system" => ThemePreference.System, _ => ThemePreference.Dark };
        else repaired.Add(prefix + "theme");
        return new AppSettings(theme, Bool(o, "aurora", d.Aurora, prefix, repaired), Bool(o, "reduceEffects", d.ReduceEffects, prefix, repaired),
            Bool(o, "reduceMotion", d.ReduceMotion, prefix, repaired));
    }

    private static ResidentSettings ReadResident(JsonObject o, List<string> repaired)
    {
        var d = ResidentSettings.Default;
        const string p = "resident.";
        return new ResidentSettings(Bool(o, "closeToTray", d.CloseToTray, p, repaired), Bool(o, "notifyDropout", d.NotifyDropout, p, repaired),
            Bool(o, "notifyRecovered", d.NotifyRecovered, p, repaired), Bool(o, "notifyFinished", d.NotifyFinished, p, repaired),
            Bool(o, "trayHintShown", d.TrayHintShown, p, repaired));
    }

    private static RecordsSettings ReadRecords(JsonObject o, List<string> repaired)
    {
        string? folder = null;
        if (o["folder"] is JsonValue fv)
        {
            if (fv.TryGetValue<string>(out var f) && IsAbsolute(f)) folder = f;
            else repaired.Add("records.folder");
        }
        var extra = new List<string>();
        if (o["extraFolders"] is JsonArray a)
        {
            foreach (var n in a)
            {
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && IsAbsolute(s)) { if (!extra.Contains(s, StringComparer.OrdinalIgnoreCase)) extra.Add(s); }
                else repaired.Add("records.extraFolders[]");
            }
        }
        else if (o["extraFolders"] is not null) repaired.Add("records.extraFolders");
        // 일지는 고치지 않는다: 모양이 맞으면 읽고, 아니면 null로 두되 원문은 RawRecords로 남는다 (Core가 검증해 경고·저장 금지)
        var move = o["move"] is JsonObject jo ? ParseJournal(jo) : null;
        if (o["move"] is JsonNode bad && move is null) repaired.Add("records.move");
        return new RecordsSettings(folder, extra, move);
    }

    private static DiagnosisPrefs ReadDiagnosis(JsonObject o, List<string> repaired)
    {
        IReadOnlyList<string>? Pair(string key)
        {
            if (o[key] is null) return null;
            if (o[key] is JsonArray a && a.Count == 2 && a.All(n => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0))
            {
                var list = a.Select(n => n!.GetValue<string>().Trim()).ToArray();
                if (!string.Equals(list[0], list[1], StringComparison.OrdinalIgnoreCase)) return list;
            }
            repaired.Add("diagnosis." + key);
            return null;
        }
        return new DiagnosisPrefs(Pair("tcpTargets"), Pair("domains"));
    }

    private static bool IsAbsolute(string p)
    {
        try { return Path.IsPathFullyQualified(p); }
        catch (ArgumentException) { return false; }
    }
}
