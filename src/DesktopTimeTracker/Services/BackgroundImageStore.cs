using System.IO;
using System.Security.Cryptography;
using DesktopTimeTracker.Models;

namespace DesktopTimeTracker.Services;

/// <summary>
/// 背景图片文件的存储管理：
/// - 默认背景（anime_bg.png）统一放在专门文件夹 DefaultFolder\Backgrounds\ 下
/// - 导入的图片也放这里，文件名 = 显示名（可随时在资源管理器里重命名，程序自动识别）
/// - 提供 导入 / 重命名 / 删除 / 文件夹同步 能力
/// </summary>
public static class BackgroundImageStore
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    public static string BackgroundsDirectory(string storageFolder) => Path.Combine(storageFolder, "Backgrounds");

    /// <summary>确保默认 anime_bg.png 存在（首次启动从内置 anime_bg.png 复制过来）。</summary>
    public static void EnsureDefault(string storageFolder)
    {
        string dir = BackgroundsDirectory(storageFolder);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "anime_bg.png");
        if (File.Exists(target)) return;

        // 从程序集资源提取（pack URI 打到 exe 里）
        try
        {
            var asm = typeof(BackgroundImageStore).Assembly;
            // csproj 用 Resource Include 打进程序集，默认命名空间 = 程序集名 + 路径
            // 但打包到 Assets/anime_bg.png，所以资源名一般是 "DesktopTimeTracker.Assets.anime_bg.png"
            string[] names = asm.GetManifestResourceNames();
            string? name = names.FirstOrDefault(n => n.EndsWith("anime_bg.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return; // 开发环境可能文件没打进去
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return;
            using var fs = File.Create(target);
            stream.CopyTo(fs);
        }
        catch { /* 忽略：如果拷失败就没有默认背景 */ }
    }

    public static BackgroundImage Import(string sourcePath, string storageFolder)
    {
        if (!BackgroundImage.IsSupportedExtension(sourcePath))
            throw new InvalidDataException("仅支持 .png / .jpg / .jpeg / .webp 格式。");

        var fileInfo = new FileInfo(sourcePath);
        if (fileInfo.Length < 8 || fileInfo.Length > 15 * 1024 * 1024) // 15 MB 上限
            throw new InvalidDataException("文件过大（最大 15 MB）或为空。");

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        var image = new BackgroundImage(Guid.NewGuid().ToString("N"), stem);
        string destination = UniqueDestination(storageFolder, image, Path.GetExtension(sourcePath));
        image = image with { FileName = Path.GetFileName(destination) };

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourcePath, destination, overwrite: false);
        return image;
    }

    public static void Delete(string storageFolder, BackgroundImage image)
    {
        if (image.Id == BackgroundImage.DefaultId) return;
        if (image.Id == BackgroundImage.NoneId) return;
        string path = Path.Combine(storageFolder, image.RelativePath!);
        if (File.Exists(path)) File.Delete(path);
    }

    public static BackgroundImage Rename(string storageFolder, BackgroundImage image, string newName)
    {
        string stem = SanitizeName(newName);
        if (stem.Length == 0) throw new InvalidDataException("新名称不能为空。");

        string oldPath = Path.Combine(storageFolder, image.RelativePath!);
        string ext = Path.GetExtension(image.FileNameOrDefault);
        string newFileName = UniqueFileName(storageFolder, stem, ext, image.FileNameOrDefault);
        string newPath = Path.Combine(BackgroundsDirectory(storageFolder), newFileName);

        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            File.Move(oldPath, newPath);

        return image with { Name = newName.Trim(), FileName = newFileName };
    }

    /// <summary>
    /// 扫描 Backgrounds 文件夹：登记新文件、识别手动改名，并处理默认背景。
    /// </summary>
    public static IReadOnlyList<BackgroundImage> SyncFromFolder(string storageFolder, IReadOnlyList<BackgroundImage> known)
    {
        string dir = BackgroundsDirectory(storageFolder);

        var result = new List<BackgroundImage>
        {
            // "无背景"永远放最前
            BackgroundImage.None
        };

        try { Directory.CreateDirectory(dir); }
        catch { /* 目录创建失败也返回可用的最小列表 */ return result; }

        try { EnsureDefault(storageFolder); } catch { }

        // 默认背景 anime_bg.png
        try
        {
            string defaultFile = Path.Combine(dir, "anime_bg.png");
            if (File.Exists(defaultFile)) result.Add(BackgroundImage.Default);
        }
        catch { }

        // 已登记的导入背景：按 FileNameOrDefault 对号入座
        var claimedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = known.Where(b => b.Id != BackgroundImage.DefaultId && b.Id != BackgroundImage.NoneId).ToList();
        var orphans = new List<BackgroundImage>();

        foreach (var image in imported)
        {
            try
            {
                string path = Path.Combine(storageFolder, image.RelativePath!);
                if (File.Exists(path))
                {
                    claimedStems.Add(Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
                    result.Add(image.FileName is null ? image with { FileName = image.FileNameOrDefault } : image);
                }
                else
                {
                    orphans.Add(image);
                }
            }
            catch { orphans.Add(image); }
        }

        // 未登记的图片文件
        List<string> unclaimed;
        try
        {
            unclaimed = Directory.EnumerateFiles(dir)
                .Where(f => Extensions.Contains(Path.GetExtension(f)))
                .Where(f => !string.Equals(Path.GetFileName(f), "anime_bg.png", StringComparison.OrdinalIgnoreCase))
                .Where(f => !claimedStems.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // 目录异常（被删、权限、损坏）→ 只返回已知的
            unclaimed = [];
        }

        int pairCount = Math.Min(orphans.Count, unclaimed.Count);
        for (int i = 0; i < pairCount; i++)
        {
            var orphan = orphans[i];
            string stem = unclaimed[i];
            string ext = Path.GetExtension(Path.Combine(dir, orphan.FileNameOrDefault));
            string fileName = stem + ext;
            claimedStems.Add(stem.ToLowerInvariant());
            result.Add(orphan with { Name = stem, FileName = fileName });
        }

        foreach (string stem in unclaimed.Skip(pairCount))
        {
            claimedStems.Add(stem.ToLowerInvariant());
            string id = StableId(stem);
            if (result.Any(b => b.Id == id) || known.Any(b => b.Id == id)) continue;
            string ext = Path.GetExtension(known.FirstOrDefault(b => b.FileName != null && Path.GetFileNameWithoutExtension(b.FileName).Equals(stem, StringComparison.OrdinalIgnoreCase))?.FileNameOrDefault ?? ".png");
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            result.Add(new BackgroundImage(id, stem, stem + ext));
        }

        // 顺序：None → 默认背景 → 其他（按名称）
        var ordered = result
            .OrderBy(b => b.Id == BackgroundImage.NoneId ? 0 : b.Id == BackgroundImage.DefaultId ? 1 : 2)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ordered;
    }

    private static string StableId(string stem)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("bgimage:" + stem.ToLowerInvariant()));
        return new Guid(hash).ToString("N");
    }

    private static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.').Trim();
        // 字符宽度限制：汉字算 2，英文算 1，上限 20（≈10 个汉字）
        return ClampByWidth(name, 20);
    }

    /// <summary>按字符宽度截断（汉字/全角=2，英文/半角=1）。</summary>
    internal static string ClampByWidth(string name, int maxWidth)
    {
        int width = 0;
        int cut = name.Length;
        for (int i = 0; i < name.Length; i++)
        {
            width += name[i] > 127 ? 2 : 1;
            if (width > maxWidth) { cut = i; break; }
        }
        return cut < name.Length ? name[..cut] : name;
    }

    private static string UniqueFileName(string storageFolder, string stem, string ext, string? currentFileName)
    {
        string dir = BackgroundsDirectory(storageFolder);
        string candidate = stem + ext;
        string current = string.IsNullOrWhiteSpace(currentFileName) ? "" : currentFileName;

        if (!File.Exists(Path.Combine(dir, candidate)) ||
            string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            return candidate;

        for (int n = 2; ; n++)
        {
            candidate = $"{stem} ({n}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
    }

    private static string UniqueDestination(string storageFolder, BackgroundImage image, string ext)
    {
        string dir = BackgroundsDirectory(storageFolder);
        Directory.CreateDirectory(dir);
        string stem = Path.GetFileNameWithoutExtension(image.FileNameOrDefault);
        string candidate = Path.Combine(dir, stem + ext);

        if (!File.Exists(candidate)) return candidate;
        for (int n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
