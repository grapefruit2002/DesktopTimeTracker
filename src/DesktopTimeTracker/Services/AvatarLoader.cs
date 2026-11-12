using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DesktopTimeTracker.Services;

/// <summary>
/// 极简 glTF/GLB（含 VRM）加载器：解析网格、材质贴图与节点变换，
/// 生成 WPF 的 Model3DGroup。骨架蒙皮不参与计算，直接渲染绑定姿势（T/A-pose）。
/// </summary>
internal static class AvatarLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>按优先级寻找 user 放置的模型文件。</summary>
    public static bool TryFindAvatar(out string path)
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTimeTracker");
        string baseDir = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(appData, "avatar.vrm"),
            Path.Combine(appData, "avatar.glb"),
            Path.Combine(appData, "avatar.gltf"),
            Path.Combine(baseDir, "Assets", "avatar.vrm"),
            Path.Combine(baseDir, "Assets", "avatar.glb"),
        ];

        foreach (string c in candidates)
        {
            if (File.Exists(c)) { path = c; return true; }
        }
        path = "";
        return false;
    }

    /// <summary>VRM 0.x 模型朝向 -Z，需要额外旋转 180° 才能面向相机。</summary>
    public static bool LastNeedsHalfTurn { get; private set; }

    /// <summary>贴图像素数据（用于按 alpha 镂空三角形，修复头发被渲染成白片的问题）。</summary>
    private sealed class TexData
    {
        public byte[]? Pixels;
        public int Width;
        public int Height;
        public bool HasAlpha;

        public byte AlphaAt(Point uv)
        {
            if (Pixels is null || Width <= 0 || Height <= 0) return 255;
            int x = (int)Math.Clamp(uv.X * (Width - 1), 0, Width - 1);
            int y = (int)Math.Clamp(uv.Y * (Height - 1), 0, Height - 1);
            int offset = (y * Width + x) * 4;
            return offset + 3 < Pixels.Length ? Pixels[offset + 3] : (byte)255;
        }
    }

    private static readonly Dictionary<int, TexData> TextureData = new();
    private static readonly Dictionary<Material, TexData> MaterialTexture = new();

    public static Model3DGroup? Load(string path, out Rect3D bounds)
    {
        bounds = Rect3D.Empty;
        LastNeedsHalfTurn = false;
        TextureData.Clear();
        MaterialTexture.Clear();
        try
        {
            byte[] data = File.ReadAllBytes(path);
            GltfDoc doc;
            byte[] bin;

            if (data.Length > 12 && data[0] == 'g' && data[1] == 'l' && data[2] == 'T')
            {
                (doc, bin) = ParseGlb(data);
            }
            else
            {
                doc = JsonSerializer.Deserialize<GltfDoc>(data, JsonOptions) ?? throw new InvalidDataException("无效的 glTF");
                bin = [];
            }

            if (doc.meshes is null || doc.nodes is null) return null;

            // VRM 0.x 的 extensionsUsed 里含 "VRM"，其朝向是 -Z
            LastNeedsHalfTurn = doc.extensionsUsed?.Contains("VRM") == true;

            var root = new Model3DGroup();
            var materialCache = new Dictionary<int, Material>();
            var textureCache = new Dictionary<int, ImageSource?>();

            var sceneNodes = doc.scenes is { Length: > 0 } scenes && doc.scene >= 0 && doc.scene < scenes.Length
                ? scenes[doc.scene].nodes ?? []
                : Enumerable.Range(0, doc.nodes.Length).ToArray();

            var boundsAcc = new Rect3D();

            foreach (int nodeIndex in sceneNodes)
                BuildNode(doc, bin, nodeIndex, Matrix3D.Identity, root, materialCache, textureCache, ref boundsAcc);

            if (boundsAcc.IsEmpty) return null;
            bounds = boundsAcc;
            return root;
        }
        catch (Exception ex)
        {
            PetLog.Write($"AvatarLoader 加载失败: {ex}");
            return null;
        }
    }

    private static (GltfDoc Doc, byte[] Bin) ParseGlb(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        br.ReadBytes(4);           // magic
        br.ReadUInt32();           // version
        br.ReadUInt32();           // length

        GltfDoc? doc = null;
        byte[] bin = [];

        while (ms.Position < ms.Length - 8)
        {
            uint chunkLength = br.ReadUInt32();
            uint chunkType = br.ReadUInt32();
            byte[] chunk = br.ReadBytes((int)chunkLength);

            if (chunkType == 0x4E4F534A) // 'JSON'
                doc = JsonSerializer.Deserialize<GltfDoc>(chunk, JsonOptions);
            else if (chunkType == 0x004E4942) // 'BIN'
                bin = chunk;
        }

        return (doc ?? throw new InvalidDataException("GLB 缺少 JSON 块"), bin);
    }

    private static void BuildNode(GltfDoc doc, byte[] bin, int nodeIndex, Matrix3D parent,
        Model3DGroup root, Dictionary<int, Material> materials, Dictionary<int, ImageSource?> textures, ref Rect3D bounds)
    {
        if (nodeIndex < 0 || nodeIndex >= doc.nodes!.Length) return;
        GltfNode node = doc.nodes[nodeIndex];

        Matrix3D local = NodeMatrix(node);
        Matrix3D world = local * parent;

        int meshIndex = node.mesh;
        if (meshIndex >= 0 && meshIndex < doc.meshes!.Length)
        {
            GltfMesh mesh = doc.meshes[meshIndex];
            if (mesh.primitives is not null)
            {
                foreach (GltfPrimitive prim in mesh.primitives)
                {
                    GeometryModel3D? model = BuildPrimitive(doc, bin, prim, materials, textures, ref bounds);
                    if (model is null) continue;
                    model.Transform = new MatrixTransform3D(world);
                    root.Children.Add(model);
                }
            }
        }

        if (node.children is not null)
            foreach (int child in node.children)
                BuildNode(doc, bin, child, world, root, materials, textures, ref bounds);
    }

    private static Matrix3D NodeMatrix(GltfNode node)
    {
        if (node.matrix is { Length: 16 } m)
        {
            // glTF 矩阵是列主序
            return new Matrix3D(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        double sx = node.scale is { Length: 3 } s ? s[0] : 1;
        double sy = node.scale is { Length: 3 } s2 ? s2[1] : 1;
        double sz = node.scale is { Length: 3 } s3 ? s3[2] : 1;
        double x = node.translation is { Length: 3 } t ? t[0] : 0;
        double y = node.translation is { Length: 3 } t2 ? t2[1] : 0;
        double z = node.translation is { Length: 3 } t3 ? t3[2] : 0;
        double qx = node.rotation is { Length: 4 } r ? r[0] : 0;
        double qy = node.rotation is { Length: 4 } r2 ? r2[1] : 0;
        double qz = node.rotation is { Length: 4 } r3 ? r3[2] : 0;
        double qw = node.rotation is { Length: 4 } r4 ? r4[3] : 1;

        var rot = new Quaternion(qx, qy, qz, qw);
        var mtx = Matrix3D.Identity;
        mtx.Rotate(rot);
        mtx.Scale(new Vector3D(sx, sy, sz));
        mtx.Translate(new Vector3D(x, y, z));
        return mtx;
    }

    private static GeometryModel3D? BuildPrimitive(GltfDoc doc, byte[] bin, GltfPrimitive prim,
        Dictionary<int, Material> materials, Dictionary<int, ImageSource?> textures, ref Rect3D bounds)
    {
        if (prim.attributes is null) return null;
        if (!prim.attributes.TryGetValue("POSITION", out int posIndex)) return null;

        float[]? positions = ReadFloats(doc, bin, posIndex, 3);
        if (positions is null) return null;

        float[]? normals = prim.attributes.TryGetValue("NORMAL", out int nIdx) ? ReadFloats(doc, bin, nIdx, 3) : null;
        float[]? uvs = prim.attributes.TryGetValue("TEXCOORD_0", out int tIdx) ? ReadFloats(doc, bin, tIdx, 2) : null;
        int[]? indices = prim.indices >= 0 ? ReadIndices(doc, bin, prim.indices) : null;

        var mesh = new MeshGeometry3D();
        for (int i = 0; i < positions.Length; i += 3)
        {
            var p = new Point3D(positions[i], positions[i + 1], positions[i + 2]);
            mesh.Positions.Add(p);
            if (bounds.IsEmpty) bounds = new Rect3D(p, new Size3D(0, 0, 0));
            else bounds.Union(p);
        }

        if (normals is not null)
            for (int i = 0; i + 2 < normals.Length; i += 3)
                mesh.Normals.Add(new Vector3D(normals[i], normals[i + 1], normals[i + 2]));

        if (uvs is not null)
            for (int i = 0; i + 1 < uvs.Length; i += 2)
                mesh.TextureCoordinates.Add(new Point(uvs[i], 1 - uvs[i + 1])); // glTF 与 WPF 的 V 轴相反

        if (indices is not null)
            foreach (int idx in indices) mesh.TriangleIndices.Add(idx);
        else
            for (int i = 0; i < mesh.Positions.Count; i++) mesh.TriangleIndices.Add(i);

        Material material = GetMaterial(doc, prim.material, materials, textures, bin);

        // 贴图带透明通道时（头发/裙摆等镂空面片），按 alpha 裁掉透明三角形
        if (MaterialTexture.TryGetValue(material, out TexData? tex) && tex.HasAlpha)
            CutoutTransparentTriangles(mesh, tex);

        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material
        };
    }

    /// <summary>丢弃完全透明的三角形（WPF 3D 不支持逐像素 alpha，只能做镂空）。</summary>
    private static void CutoutTransparentTriangles(MeshGeometry3D mesh, TexData tex)
    {
        var uvs = mesh.TextureCoordinates;
        if (uvs.Count == 0 || mesh.TriangleIndices.Count < 3) return;

        var kept = new System.Windows.Media.Int32Collection(mesh.TriangleIndices.Count);
        var indices = mesh.TriangleIndices;

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= uvs.Count || b >= uvs.Count || c >= uvs.Count) continue;

            byte maxAlpha = Math.Max(tex.AlphaAt(uvs[a]), Math.Max(tex.AlphaAt(uvs[b]), tex.AlphaAt(uvs[c])));
            if (maxAlpha >= 100)
            {
                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
            }
        }

        mesh.TriangleIndices = kept;
    }

    private static Material GetMaterial(GltfDoc doc, int index, Dictionary<int, Material> cache,
        Dictionary<int, ImageSource?> textures, byte[] bin)
    {
        if (index < 0 || doc.materials is null || index >= doc.materials.Length)
            return new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xEA, 0xDD, 0xD8)));
        if (cache.TryGetValue(index, out Material? cached)) return cached;

        GltfMaterial mat = doc.materials[index];
        float[] c = mat.pbrMetallicRoughness?.baseColorFactor ?? [1f, 1f, 1f, 1f];
        int texIndex = mat.pbrMetallicRoughness?.baseColorTexture?.index ?? -1;

        // VRM 0.x：MToon 材质不带 pbrMetallicRoughness，颜色/贴图在 extensions.VRM.materialProperties 里
        if (mat.extensions is not null)
        {
            if (mat.extensions.TryGetValue("VRM", out JsonElement vrm) &&
                vrm.TryGetProperty("materialProperties", out JsonElement props) &&
                props.ValueKind == JsonValueKind.Array && props.GetArrayLength() > 0)
            {
                JsonElement mp = props[0];
                if (mp.TryGetProperty("litColorFactor", out JsonElement lit) && lit.ValueKind == JsonValueKind.Array)
                    c = lit.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
                if (mp.TryGetProperty("textureIndex", out JsonElement ti) && ti.ValueKind == JsonValueKind.Number)
                    texIndex = ti.GetInt32();
            }

            // VRM 1.0：VRMC_materials_mtoon
            if (mat.extensions.TryGetValue("VRMC_materials_mtoon", out JsonElement mtoon))
            {
                if (mtoon.TryGetProperty("baseColorFactor", out JsonElement bc) && bc.ValueKind == JsonValueKind.Array)
                    c = bc.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
            }
        }

        var color = Color.FromRgb(
            (byte)Math.Clamp(c[0] * 255, 0, 255),
            (byte)Math.Clamp(c[1] * 255, 0, 255),
            (byte)Math.Clamp(c[2] * 255, 0, 255));

        Material result;
        ImageSource? texture = null;
        if (texIndex >= 0) texture = GetTexture(doc, texIndex, textures, bin);

        if (texture is not null)
        {
            var brush = new ImageBrush(texture) { Stretch = Stretch.Fill };
            brush.Freeze();
            result = new DiffuseMaterial(brush);

            // 记录材质对应的像素数据，供 alpha 镂空使用
            if (TextureData.TryGetValue(texIndex, out TexData? td)) MaterialTexture[result] = td;
        }
        else
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            result = new DiffuseMaterial(brush);
        }

        cache[index] = result;
        return result;
    }

    private static ImageSource? GetTexture(GltfDoc doc, int textureIndex,
        Dictionary<int, ImageSource?> cache, byte[] bin)
    {
        if (cache.TryGetValue(textureIndex, out ImageSource? cached)) return cached;
        cache[textureIndex] = null;

        if (doc.textures is null || textureIndex >= doc.textures.Length) return null;
        int imageIndex = doc.textures[textureIndex].source;
        if (doc.images is null || imageIndex < 0 || imageIndex >= doc.images.Length) return null;

        GltfImage image = doc.images[imageIndex];
        int bvIndex = image.bufferView;
        if (bvIndex < 0 || doc.bufferViews is null) return null;
        if (bvIndex >= doc.bufferViews.Length) return null;

        GltfBufferView view = doc.bufferViews[bvIndex];
        int offset = view.byteOffset;
        int length = view.byteLength;
        if (offset < 0 || length <= 0 || offset + length > bin.Length) return null;

        try
        {
            var ms = new MemoryStream(bin, offset, length);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            cache[textureIndex] = bmp;

            // 解码像素（BGRA32），供 alpha 镂空使用
            try
            {
                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth;
                int h = converted.PixelHeight;
                int stride = w * 4;
                var pixels = new byte[stride * h];
                converted.CopyPixels(pixels, stride, 0);

                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] < 250) { hasAlpha = true; break; }
                }

                TextureData[textureIndex] = new TexData
                {
                    Pixels = pixels,
                    Width = w,
                    Height = h,
                    HasAlpha = hasAlpha
                };
            }
            catch (Exception)
            {
                // 解码失败就不做镂空，不影响正常显示
            }

            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static float[]? ReadFloats(GltfDoc doc, byte[] bin, int accessorIndex, int components)
    {
        if (doc.accessors is null || accessorIndex < 0 || accessorIndex >= doc.accessors.Length) return null;
        GltfAccessor acc = doc.accessors[accessorIndex];
        int bv = acc.bufferView;
        if (doc.bufferViews is null || bv < 0 || bv >= doc.bufferViews.Length) return null;

        GltfBufferView view = doc.bufferViews[bv];
        int stride = view.byteStride > 0 ? view.byteStride : components * 4;
        int start = view.byteOffset + acc.byteOffset;

        var result = new float[acc.count * components];
        for (int i = 0; i < acc.count; i++)
        {
            int baseOffset = start + i * stride;
            for (int c = 0; c < components; c++)
            {
                int o = baseOffset + c * 4;
                if (o + 4 > bin.Length) return result;
                result[i * components + c] = BitConverter.ToSingle(bin, o);
            }
        }
        return result;
    }

    private static int[]? ReadIndices(GltfDoc doc, byte[] bin, int accessorIndex)
    {
        if (doc.accessors is null || accessorIndex < 0 || accessorIndex >= doc.accessors.Length) return null;
        GltfAccessor acc = doc.accessors[accessorIndex];
        int bv = acc.bufferView;
        if (doc.bufferViews is null || bv < 0 || bv >= doc.bufferViews.Length) return null;

        GltfBufferView view = doc.bufferViews[bv];
        int start = view.byteOffset + acc.byteOffset;
        int size = acc.componentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 4 };
        var result = new int[acc.count];

        for (int i = 0; i < acc.count; i++)
        {
            int o = start + i * size;
            if (o + size > bin.Length) break;
            result[i] = size switch
            {
                1 => bin[o],
                2 => BitConverter.ToUInt16(bin, o),
                _ => (int)BitConverter.ToUInt32(bin, o)
            };
        }
        return result;
    }

    // ---------------- glTF JSON 结构 ----------------

    private sealed class GltfDoc
    {
        public GltfScene[]? scenes { get; set; }
        public int scene { get; set; }
        public GltfNode[]? nodes { get; set; }
        public GltfMesh[]? meshes { get; set; }
        public GltfAccessor[]? accessors { get; set; }
        public GltfBufferView[]? bufferViews { get; set; }
        public GltfMaterial[]? materials { get; set; }
        public GltfTexture[]? textures { get; set; }
        public GltfImage[]? images { get; set; }
        public string[]? extensionsUsed { get; set; }
    }

    private sealed class GltfScene
    {
        public int[]? nodes { get; set; }
    }

    private sealed class GltfNode
    {
        public int[]? children { get; set; }
        public int mesh { get; set; } = -1;
        public float[]? matrix { get; set; }
        public float[]? translation { get; set; }
        public float[]? rotation { get; set; }
        public float[]? scale { get; set; }
        public string? name { get; set; }
    }

    private sealed class GltfMesh
    {
        public GltfPrimitive[]? primitives { get; set; }
    }

    private sealed class GltfPrimitive
    {
        public Dictionary<string, int>? attributes { get; set; }
        public int indices { get; set; } = -1;
        public int material { get; set; } = -1;
        public int mode { get; set; }
    }

    private sealed class GltfAccessor
    {
        public int bufferView { get; set; } = -1;
        public int byteOffset { get; set; }
        public int componentType { get; set; }
        public int count { get; set; }
        public string? type { get; set; }
    }

    private sealed class GltfBufferView
    {
        public int byteOffset { get; set; }
        public int byteLength { get; set; }
        public int byteStride { get; set; }
    }

    private sealed class GltfMaterial
    {
        public GltfPbr? pbrMetallicRoughness { get; set; }
        public bool doubleSided { get; set; }

        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? extensions { get; set; }
    }

    private sealed class GltfPbr
    {
        public float[]? baseColorFactor { get; set; }
        public GltfTextureInfo? baseColorTexture { get; set; }
    }

    private sealed class GltfTextureInfo
    {
        public int index { get; set; } = -1;
    }

    private sealed class GltfTexture
    {
        public int source { get; set; } = -1;
    }

    private sealed class GltfImage
    {
        public string? uri { get; set; }
        public int bufferView { get; set; } = -1;
        public string? mimeType { get; set; }
    }
}

internal static class PetLog
{
    public static void Write(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTimeTracker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PET] {message}\n");
        }
        catch (IOException) { }
    }
}
