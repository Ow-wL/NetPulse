using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPulse.Core.Targets;

/// <summary>Invalid: 읽었지만 검증 오류가 있는 프로필이 있음 (원본은 옮겨 두고 쓰지 않는다).</summary>
public enum ProfileLoadStatus { Loaded, Missing, Unreadable, UnknownVersion, Invalid }

/// <summary>저장된 프로필 묶음.</summary>
public sealed record ProfileSet(IReadOnlyList<Profile> Profiles, string? LastUsedId);

/// <summary>
/// 프로필 저장 (M3 설계 3.3): 사용자 데이터 폴더의 profiles.json, 버전 1. 임시 파일에 쓴 뒤 바꾼다.
/// 읽지 못하면 파일을 지우지 않고 profiles.json.unreadable-UTC시각으로 옮겨 두고 상태를 돌려준다 (호출자가 탐지 제안으로 시작).
/// </summary>
public static class ProfileStore
{
    public const string FileName = "profiles.json";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TargetDto(string Id, string Name, string Address, TargetRole Role, string? Provider, string Method, bool Enabled);
    private sealed record ProfileDto(string Id, string Name, List<TargetDto> Targets, int TimeoutMs, int DurationMinutes, ExpectedConnection Expected);
    private sealed record FileDto(int Version, List<ProfileDto> Profiles, string? LastUsedId);

    public static (ProfileSet? Set, ProfileLoadStatus Status, string? MovedTo) Load(string folder, DateTimeOffset now)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return (null, ProfileLoadStatus.Missing, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != CurrentVersion)
                return (null, ProfileLoadStatus.UnknownVersion, MoveAside(path, now));
            var dto = doc.RootElement.Deserialize<FileDto>(Json) ?? throw new JsonException("빈 파일");
            var profiles = dto.Profiles.Select(ToModel).ToArray();
            // 저장 계약과 같은 검증: 오류가 있는 프로필이 섞였으면 원본을 보존(옮겨 둠)하고 쓰지 않는다
            if (profiles.Any(p => ProfileValidator.HasErrors(ProfileValidator.Validate(p))))
                return (null, ProfileLoadStatus.Invalid, MoveAside(path, now));
            return (new ProfileSet(profiles, dto.LastUsedId), ProfileLoadStatus.Loaded, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return (null, ProfileLoadStatus.Unreadable, MoveAside(path, now));
        }
    }

    /// <summary>저장한다. 검증 오류가 있는 프로필이 있으면 저장하지 않고 ArgumentException (M3 설계 3.2 "저장·시작 거부").</summary>
    public static void Save(string folder, ProfileSet set)
    {
        foreach (var p in set.Profiles)
        {
            var errors = ProfileValidator.Validate(p).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).ToArray();
            if (errors.Length > 0) throw new ArgumentException($"측정 구성 '{p.Id}' 오류: {string.Join(", ", errors)}", nameof(set));
        }
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        var dto = new FileDto(CurrentVersion, set.Profiles.Select(ToDto).ToList(), set.LastUsedId);
        File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static string? MoveAside(string path, DateTimeOffset now)
    {
        var target = path + ".unreadable-" + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        try { File.Move(path, target); return Path.GetFileName(target); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static ProfileDto ToDto(Profile p) => new(p.Id, p.Name,
        p.Targets.Select(t => new TargetDto(t.Id, t.Name, t.Address.ToString(), t.Role, t.Provider, t.Method.ToString(), t.Enabled)).ToList(),
        (int)p.Timeout.TotalMilliseconds, (int)p.Duration.TotalMinutes, p.Expected);

    private static Profile ToModel(ProfileDto d) => new(d.Id, d.Name,
        d.Targets.Select(t => new Target(t.Id, t.Name, IPAddress.Parse(t.Address), t.Role, t.Provider, ParseMethod(t.Method), t.Enabled)).ToArray(),
        TimeSpan.FromMilliseconds(d.TimeoutMs), TimeSpan.FromMinutes(d.DurationMinutes), d.Expected);

    public static ProbeMethod ParseMethod(string text)
    {
        if (text == "icmp") return ProbeMethod.Icmp;
        if (text.StartsWith("tcp:", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            return ProbeMethod.Tcp(port);
        throw new FormatException("알 수 없는 측정 방식: " + text);
    }
}
