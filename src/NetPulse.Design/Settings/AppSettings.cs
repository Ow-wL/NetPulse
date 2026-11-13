using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPulse.Design.Settings;

public enum ThemePreference { Dark, Light, System }

public enum SettingsLoadStatus { Loaded, Missing, Corrupt, UnknownVersion }

/// <summary>
/// 화면 설정 (M2 설계 5.1). 사용자 데이터 폴더의 settings.json, 버전 1.
/// 읽지 못하면 기본값으로 시작하고 파일은 그대로 둔다 (사용자가 설정을 바꿀 때 새로 쓴다).
/// </summary>
public sealed record AppSettings(ThemePreference Theme, bool Aurora, bool ReduceEffects, bool ReduceMotion)
{
    public const int CurrentVersion = 1;
    public const string FileName = "settings.json";

    /// <summary>첫 실행: 다크, 오로라 끔, 효과·움직임 줄이기 끔.</summary>
    public static readonly AppSettings Default = new(ThemePreference.Dark, false, false, false);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record FileV1(int Version, ThemePreference Theme, bool Aurora, bool ReduceEffects, bool ReduceMotion);

    public static (AppSettings Settings, SettingsLoadStatus Status, string? Detail) Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return (Default, SettingsLoadStatus.Missing, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != CurrentVersion)
                return (Default, SettingsLoadStatus.UnknownVersion, v.ValueKind == JsonValueKind.Number ? "version " + v.GetRawText() : "version 없음");
            var f = doc.RootElement.Deserialize<FileV1>(Json) ?? throw new JsonException("빈 설정");
            if (!Enum.IsDefined(f.Theme)) throw new JsonException("알 수 없는 테마");
            return (new AppSettings(f.Theme, f.Aurora, f.ReduceEffects, f.ReduceMotion), SettingsLoadStatus.Loaded, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return (Default, SettingsLoadStatus.Corrupt, ex.GetType().Name);
        }
    }

    /// <summary>임시 파일에 쓴 뒤 바꾼다 (쓰다 끊겨도 이전 파일이 남는다).</summary>
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileV1(CurrentVersion, Theme, Aurora, ReduceEffects, ReduceMotion), Json));
        File.Move(temp, path, overwrite: true);
    }
}
