using System.IO;
using System.Text.Json;
using DesktopTimeTracker.Models;

namespace DesktopTimeTracker.Services;

public static class PetModelStore
{
    public static PetModel Import(string sourcePath, string storageFolder)
    {
        if (!string.Equals(Path.GetExtension(sourcePath), ".vrm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .vrm 人形模型文件。");

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Validate(source);
        source.Position = 0;

        var model = new PetModel(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(sourcePath));
        string destination = Path.Combine(storageFolder, model.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            source.CopyTo(target);
            target.Flush(true);
        }
        catch
        {
            target.Dispose();
            File.Delete(destination);
            throw;
        }
        return model;
    }

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
