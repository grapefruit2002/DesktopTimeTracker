using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopTimeTracker.Models;

namespace DesktopTimeTracker.Services;

/// <summary>
/// 桌宠模型文件的存储管理：
/// - 模型统一存放在专门文件夹 <c>%APPDATA%\DesktopTimeTracker\Models\</c> 下
/// - 文件名使用模型的可读名称（可随时在资源管理器里重命名，程序会自动识别）
/// - 提供 导入 / 重命名 / 文件夹同步 三个能力
/// </summary>
public static class PetModelStore
{
    private static readonly string[] ModelExtensions = [".vrm"];

    public static string ModelsDirectory(string storageFolder) => Path.Combine(storageFolder, "Models");

    public static PetModel Import(string sourcePath, string storageFolder)
    {
        if (!string.Equals(Path.GetExtension(sourcePath), ".vrm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .vrm 人形模型文件。");

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Validate(source);
        source.Position = 0;

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        var model = new PetModel(Guid.NewGuid().ToString("N"), stem);
        string destination = UniqueDestination(storageFolder, model);
        model = model with { FileName = Path.GetFileName(destination) };

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(target);
            target.Flush(true);
        }
        return model;
    }

    /// <summary>重命名模型：显示名称与物理文件一起改名。</summary>
    public static PetModel Rename(string storageFolder, PetModel model, string newName)
    {
        string stem = SanitizeName(newName);
        if (stem.Length == 0) throw new InvalidDataException("新名称不能为空。");

        string oldPath = Path.Combine(storageFolder, model.RelativePath);
        string newFileName = UniqueFileName(storageFolder, stem, model.FileNameOrDefault);
        string newPath = Path.Combine(ModelsDirectory(storageFolder), newFileName);

        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            File.Move(oldPath, newPath);

        return model with { Name = newName.Trim(), FileName = newFileName };
    }

    /// <summary>
    /// 同步 Models 文件夹：扫描里面的 *.vrm，自动登记新文件，
    /// 并识别“在资源管理器里改名”的情况（旧文件消失 + 出现一个未登记的新文件）。
    /// 返回同步后的完整模型列表。
    /// </summary>
    public static IReadOnlyList<PetModel> SyncFromFolder(string storageFolder, IReadOnlyList<PetModel> known)
    {
        string modelsDir = ModelsDirectory(storageFolder);

        try { Directory.CreateDirectory(modelsDir); }
        catch { return []; }

        var imported = known.Where(m => m.Id != PetModel.DefaultId).ToList();
        var claimedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ① 已登记模型：按 FileNameOrDefault 对号入座
        var result = new List<PetModel>();
        var orphans = new List<PetModel>();
        foreach (var model in imported)
        {
            string path = Path.Combine(storageFolder, model.RelativePath);
            if (File.Exists(path))
            {
                claimedStems.Add(Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
                result.Add(model.FileName is null ? model with { FileName = model.FileNameOrDefault } : model);
            }
            else
            {
                orphans.Add(model); // 文件不见了（可能被手动改名）
            }
        }

        // ② 未登记的文件：优先配给“文件消失”的模型（识别改名），其余作为新模型登记
        List<string> unclaimed;
        try
        {
            unclaimed = Directory.EnumerateFiles(modelsDir, "*.vrm")
                .Where(f => !claimedStems.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            unclaimed = [];
        }

        int pairCount = Math.Min(orphans.Count, unclaimed.Count);
        for (int i = 0; i < pairCount; i++)
        {
            var orphan = orphans[i];
            string stem = unclaimed[i];
            claimedStems.Add(stem.ToLowerInvariant());
            result.Add(orphan with
            {
                Name = stem,
                FileName = stem + ".vrm"
            });
        }

        foreach (string stem in unclaimed.Skip(pairCount))
        {
            claimedStems.Add(stem.ToLowerInvariant());
            string id = StableId(stem);
            if (result.Any(m => m.Id == id) || known.Any(m => m.Id == id)) continue;
            result.Add(new PetModel(id, stem, stem + ".vrm"));
        }

        // 顺序：按名称排序，方便在设置里挑选
        return result
            .OrderBy(m => m.Id == PetModel.DefaultId ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>由名称生成稳定的 32 位模型 Id（重命名文件后 Id 保持不变）。</summary>
    private static string StableId(string stem)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("petmodel:" + stem.ToLowerInvariant()));
        return new Guid(hash).ToString("N");
    }

    private static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.').Trim();
        // 字符宽度限制：汉字算 2，英文算 1，上限 20（≈10 个汉字）
        return BackgroundImageStore.ClampByWidth(name, 20);
    }

    /// <summary>在 Models 文件夹里生成不重名的目标文件名。</summary>
    private static string UniqueFileName(string storageFolder, string stem, string? currentFileName)
    {
        string dir = ModelsDirectory(storageFolder);
        string candidate = stem + ".vrm";
        string current = string.IsNullOrWhiteSpace(currentFileName) ? "" : currentFileName;

        if (!File.Exists(Path.Combine(dir, candidate)) ||
            string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            return candidate;

        for (int n = 2; ; n++)
        {
            candidate = $"{stem} ({n}).vrm";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
    }

    /// <summary>同上，但用于导入时（目标文件一定不存在）。</summary>
    private static string UniqueDestination(string storageFolder, PetModel model)
    {
        string dir = ModelsDirectory(storageFolder);
        string candidate = Path.Combine(dir, model.FileNameOrDefault);
        Directory.CreateDirectory(dir);

        if (!File.Exists(candidate)) return candidate;
        string stem = Path.GetFileNameWithoutExtension(model.FileNameOrDefault);
        for (int n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{stem} ({n}).vrm");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ---------------- VRM 校验 ----------------

    private static void Validate(Stream stream)
    {
        if (stream.Length < 20 || stream.Length > 512L * 1024 * 1024)
            throw new InvalidDataException("模型文件为空、不完整或超过 512 MB。");

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2 || reader.ReadUInt32() != stream.Length)
            throw new InvalidDataException("不是有效的 VRM 二进制模型，或文件已损坏。");

        uint jsonLength = reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x4E4F534A || jsonLength == 0 || jsonLength % 4 != 0 ||
            jsonLength > 16 * 1024 * 1024 || jsonLength > stream.Length - stream.Position)
            throw new InvalidDataException("模型描述数据无效或过大。");

        try
        {
            using JsonDocument document = JsonDocument.Parse(reader.ReadBytes((int)jsonLength));
            ValidateMetadata(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("模型描述数据损坏，无法读取 VRM 人形骨骼。", ex);
        }

        bool hasBinary = false;
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8)
                throw new InvalidDataException("模型数据块不完整。");
            uint length = reader.ReadUInt32();
            uint type = reader.ReadUInt32();
            if (length % 4 != 0 || length > stream.Length - stream.Position || type == 0x4E4F534A ||
                (type == 0x004E4942 && hasBinary))
                throw new InvalidDataException("模型数据块已损坏。");
            if (type == 0x004E4942) hasBinary = true;
            stream.Seek(length, SeekOrigin.Current);
        }
        if (!hasBinary) throw new InvalidDataException("模型缺少内嵌网格数据。");
    }

    private static void ValidateMetadata(JsonElement root)
    {
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0" ||
            !root.TryGetProperty("extensions", out JsonElement extensions))
            throw new InvalidDataException("仅支持 VRM 0.x / 1.0 人形模型。");

        int nodeCount = root.GetProperty("nodes").GetArrayLength();
        var bones = new Dictionary<string, int>(StringComparer.Ordinal);
        if (extensions.TryGetProperty("VRMC_vrm", out JsonElement vrm1))
        {
            if (vrm1.GetProperty("specVersion").GetString() != "1.0")
                throw new InvalidDataException("仅支持正式版 VRM 1.0 或 VRM 0.x 模型。");
            foreach (JsonProperty bone in vrm1.GetProperty("humanoid").GetProperty("humanBones").EnumerateObject())
                bones[bone.Name] = bone.Value.GetProperty("node").GetInt32();
        }
        else if (extensions.TryGetProperty("VRM", out JsonElement vrm0))
        {
            foreach (JsonElement bone in vrm0.GetProperty("humanoid").GetProperty("humanBones").EnumerateArray())
                bones[bone.GetProperty("bone").GetString()!] = bone.GetProperty("node").GetInt32();
        }
        else
        {
            throw new InvalidDataException("文件不含 VRM 人形骨骼，普通 GLB / GLTF 不能直接使用。");
        }

        string[] required = ["hips", "spine", "head", "leftUpperArm", "leftLowerArm", "leftHand",
            "rightUpperArm", "rightLowerArm", "rightHand", "leftUpperLeg", "leftLowerLeg", "leftFoot",
            "rightUpperLeg", "rightLowerLeg", "rightFoot"];
        if (required.Any(name => !bones.TryGetValue(name, out int node) || node < 0 || node >= nodeCount) ||
            bones.Values.Any(node => node < 0 || node >= nodeCount))
            throw new InvalidDataException("模型缺少必要的人形骨骼，无法复用桌宠动作。");

        foreach (string collection in new[] { "buffers", "images" })
        {
            if (!root.TryGetProperty(collection, out JsonElement resources)) continue;
            foreach (JsonElement resource in resources.EnumerateArray())
                if (resource.TryGetProperty("uri", out JsonElement uri) &&
                    !(uri.GetString()?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ?? false))
                    throw new InvalidDataException("请导入贴图和网格全部内嵌的 VRM 文件，不支持外部文件或网络资源。");
        }
    }
}
