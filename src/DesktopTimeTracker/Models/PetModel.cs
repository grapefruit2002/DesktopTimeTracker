namespace DesktopTimeTracker.Models;

public sealed record PetModel(string Id, string Name, string? FileName = null)
{
    public const string DefaultId = "default";
    public static PetModel Default { get; } = new(DefaultId, "默认人物（原模型）");

    public static bool IsImportedId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);

    /// <summary>导入模型的实际文件名（如 "我的模型.vrm"）；旧数据回退为 Id.vrm。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FileNameOrDefault => string.IsNullOrWhiteSpace(FileName) ? $"{Id}.vrm" : FileName;

    /// <summary>统一放在 DefaultFolder\Models\ 下，不再把默认人物单独丢在根目录。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RelativePath => IsImportedId(Id) ? $"Models/{FileNameOrDefault}" : "Models/avatar.vrm";

    public override string ToString() => Name;
}
