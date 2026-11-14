using System.IO;

namespace DesktopTimeTracker.Models;

/// <summary>背景图片条目，存放文件名（用户可在文件夹里直接改名）。</summary>
public sealed record BackgroundImage(string Id, string Name, string? FileName = null)
{
    /// <summary>使用程序内置的 anime_bg.png（或用户目录里的同文件名）。</summary>
    public const string DefaultId = "default";

    /// <summary>不显示任何背景（纯色底）。</summary>
    public const string NoneId = "none";

    public static BackgroundImage Default { get; } = new(DefaultId, "默认背景", "anime_bg.png");
    public static BackgroundImage None { get; } = new(NoneId, "无背景");

    public static bool IsImportedId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);

    public static bool IsSupportedExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>实际文件名（如 "海滩.png"）；旧数据回退为 Id.png。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FileNameOrDefault
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FileName)) return FileName;
            return Id == DefaultId ? "anime_bg.png" : $"{Id}.png";
        }
    }

    /// <summary>放在 DefaultFolder\Backgrounds\ 下。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RelativePath => Id == NoneId ? null : $"Backgrounds/{FileNameOrDefault}";

    public override string ToString() => Name;
}

/// <summary>背景的显示模式：位置 + 拉伸方式。</summary>
public enum BackgroundMode
{
    CornerTopRight = 0,   // 右上角落（原默认）：Uniform，不铺满
    CornerBottomLeft = 1, // 左下角落：Uniform，不铺满
    FillCover = 2,        // 铺满不变形：UniformToFill（会裁剪）
    FillUniform = 3,      // 铺满不变形：Uniform（可能留白）
}
