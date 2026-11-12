namespace DesktopTimeTracker.Models;

public sealed record PetModel(string Id, string Name)
{
    public const string DefaultId = "default";
    public static PetModel Default { get; } = new(DefaultId, "默认人物（原模型）");

    public static bool IsImportedId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);

    [System.Text.Json.Serialization.JsonIgnore]
    public string RelativePath => IsImportedId(Id) ? $"Models/{Id}.vrm" : "avatar.vrm";

    public override string ToString() => Name;
}
