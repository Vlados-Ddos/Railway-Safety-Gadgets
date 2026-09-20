using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace RailwaySafetyGadgets
{
    internal static class ModelAssets
    {
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, GameObject> Templates = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, Mesh> PreviewMeshes = new Dictionary<string, Mesh>();
        private static readonly Dictionary<string, Vector3> Sizes = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();
        private static readonly Dictionary<Material, Texture2D[]> SplitMasks = new Dictionary<Material, Texture2D[]>();
        internal static Texture2D SplitEmission(Material material, bool red, bool yellow)
        {
            Texture2D[] masks;
            if (!SplitMasks.TryGetValue(material, out masks)) return Texture2D.blackTexture;
            return masks[(red ? 1 : 0) | (yellow ? 2 : 0)];
        }
        internal static Vector3 Vector(JToken v) { return new Vector3((float)v[0], (float)v[1], (float)v[2]); }
        internal static GameObject Create(string kind, float scale, Transform parent)
        {
            GameObject template;
            if (!Templates.TryGetValue(kind, out template) || template == null)
            {
                template = Load(kind);
                Templates[kind] = template;
            }
            GameObject root = UnityEngine.Object.Instantiate(template, parent, false);
            root.name = kind + "_model";
            root.transform.localScale = Vector3.one * scale;
            root.SetActive(true);
            return root;
        }

        internal static Vector3 Size(string kind)
        {
            Vector3 size;
            if (!Sizes.TryGetValue(kind, out size))
                Sizes[kind] = size = Vector(JObject.Parse(File.ReadAllText(Path.Combine(Main.ModPath, "Assets", kind, "model.json")))["size"]);
            return size;
        }

        internal static Vector3 ExportCenter(Vector3 size) { return new Vector3(0, size.y / 2, size.z / 2); }

        internal static void CenterModel(GameObject visual, Vector3 size)
        {
            // The item, collider and installed gadget all use a centred origin.
            // Source vertices, material slots and proportions remain unchanged.
            visual.transform.localPosition = -ExportCenter(size);
        }

        internal static GameObject CreatePreview(string kind, float scale, Transform parent, bool visible)
        {
            Mesh preview;
            if (!PreviewMeshes.TryGetValue(kind, out preview))
            {
                GameObject template;
                if (!Templates.TryGetValue(kind, out template)) Templates[kind] = template = Load(kind);
                var parts = new List<CombineInstance>();
                int expectedIndices = 0;
                var center = ExportCenter(Size(kind));
                foreach (var filter in template.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh source = filter.sharedMesh;
                    Matrix4x4 matrix = Matrix4x4.Translate(-center) * template.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    for (int sub = 0; sub < source.subMeshCount; sub++)
                    {
                        if (source.GetIndexCount(sub) == 0) continue;
                        expectedIndices += (int)source.GetIndexCount(sub);
                        parts.Add(new CombineInstance { mesh = source, subMeshIndex = sub, transform = matrix });
                    }
                }
                // Native DrawHighlight draws submesh 0; ItemPlacer supplies only
                // one material. Feed BOTH native previews every source surface.
                preview = new Mesh { name = "RSG_" + kind + "_placement", indexFormat = IndexFormat.UInt32 };
                preview.CombineMeshes(parts.ToArray(), true, true);
                preview.RecalculateBounds();
                if (preview.subMeshCount != 1 || preview.GetIndexCount(0) != expectedIndices ||
                    preview.bounds.center.sqrMagnitude > 1e-10f || (preview.bounds.size - Size(kind)).sqrMagnitude > 1e-10f)
                    throw new InvalidDataException("Incomplete or uncentred placement preview: " + kind);
                PreviewMeshes[kind] = preview;
            }
            var root = new GameObject("RSG_" + kind + "_placement_preview");
            root.transform.SetParent(parent, false);
            // MeshFilter stays at local zero: native ItemPlacer uses its position
            // in the final drop-height calculation.
            var geometry = new GameObject("PlacementGeometry"); geometry.transform.SetParent(root.transform, false);
            geometry.transform.localScale = Vector3.one * scale;
            geometry.AddComponent<MeshFilter>().sharedMesh = preview;
            if (visible) geometry.AddComponent<MeshRenderer>().sharedMaterial = Templates[kind].GetComponentInChildren<Renderer>(true).sharedMaterial;
            return root;
        }

        private static GameObject Load(string kind)
        {
            string dir = Path.Combine(Main.ModPath, "Assets", kind);
            var data = JObject.Parse(File.ReadAllText(Path.Combine(dir, "model.json")));
            var materialData = (JArray)data["materials"];
            var materials = new Material[materialData.Count];
            Shader shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("Unity Standard shader is unavailable");
            for (int i = 0; i < materials.Length; i++)
            {
                var md = materialData[i]; var tx = md["textures"];
                var mat = new Material(shader) { name = (string)md["name"] };
                var c = md["color"];
                mat.color = new Color((float)c[0], (float)c[1], (float)c[2], (float)md["alpha"]);
                mat.SetFloat("_Metallic", (float)md["metallic"]);
                mat.SetFloat("_Glossiness", 1 - (float)md["roughness"]);
                Map(mat, "_MainTex", dir, (string)tx["albedo"], false);
                if (tx["normalUnity"] != null)
                {
                    Map(mat, "_BumpMap", dir, (string)tx["normalUnity"], true);
                    mat.EnableKeyword("_NORMALMAP");
                }
                if (tx["metallicSmoothness"] != null)
                {
                    Map(mat, "_MetallicGlossMap", dir, (string)tx["metallicSmoothness"], true);
                    mat.SetFloat("_GlossMapScale", 1);
                    mat.EnableKeyword("_METALLICGLOSSMAP");
                }
                Map(mat, "_OcclusionMap", dir, (string)tx["ao"], true);
                // Per-renderer property blocks drive existing physical meshes.
                Map(mat, "_EmissionMap", dir, (string)tx["emission"], false);
                if (tx["emissionRed"] != null && tx["emissionYellow"] != null)
                    SplitMasks[mat] = new[] { Texture2D.blackTexture,
                        Texture(Path.Combine(dir, (string)tx["emissionRed"]), false),
                        Texture(Path.Combine(dir, (string)tx["emissionYellow"]), false),
                        Texture(Path.Combine(dir, (string)tx["emission"]), false) };
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", Color.black);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                if ((float)md["alpha"] < 1)
                {
                    mat.SetFloat("_Mode", 3);
                    mat.SetInt("_SrcBlend", (int)BlendMode.One);
                    mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = 3000;
                    mat.SetOverrideTag("RenderType", "Transparent");
                }
                materials[i] = mat;
            }
            var root = new GameObject("RSG_asset_" + kind);
            root.SetActive(false); root.transform.SetParent(Main.Staging.transform, false);
            using (var reader = new BinaryReader(File.OpenRead(Path.Combine(dir, "model.rsgm")), Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RSGM" || reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported model format");
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                    var node = new GameObject(name);
                    node.transform.SetParent(root.transform, false);
                    node.transform.localPosition = ReadVector(reader);
                    int n = reader.ReadInt32();
                    var vertices = new Vector3[n]; var normals = new Vector3[n]; var uv = new Vector2[n];
                    for (int v = 0; v < n; v++) { vertices[v] = ReadVector(reader); normals[v] = ReadVector(reader); uv[v] = new Vector2(reader.ReadSingle(), reader.ReadSingle()); }
                    var mesh = new Mesh { name = name, indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                    mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv;
                    int sub = reader.ReadInt32(); mesh.subMeshCount = sub;
                    var slots = new Material[sub];
                    for (int s = 0; s < sub; s++)
                    {
                        slots[s] = materials[reader.ReadInt32()];
                        int len = reader.ReadInt32(); var indices = new int[len];
                        for (int t = 0; t < len; t++) indices[t] = reader.ReadInt32();
                        mesh.SetTriangles(indices, s, false);
                    }
                    mesh.RecalculateBounds(); mesh.RecalculateTangents();
                    node.AddComponent<MeshFilter>().sharedMesh = mesh;
                    node.AddComponent<MeshRenderer>().sharedMaterials = slots;
                    mesh.UploadMeshData(false);
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Model has trailing data");
            }
            return root;
        }

        private static Vector3 ReadVector(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        private static void Map(Material m, string slot, string dir, string file, bool linear)
        {
            if (file == null) return;
            m.SetTexture(slot, Texture(Path.Combine(dir, file), linear));
        }
        private static Texture2D Texture(string path, bool linear)
        {
            string key = path + linear;
            Texture2D texture;
            if (Textures.TryGetValue(key, out texture)) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = Path.GetFileName(path), anisoLevel = 4 };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) throw new InvalidDataException(path);
            texture.wrapMode = TextureWrapMode.Repeat;
            Textures[key] = texture;
            return texture;
        }
        internal static Sprite Icon(string kind, bool dropped)
        {
            string path = Path.Combine(Main.ModPath, "Assets", kind, dropped ? "icon_dropped.png" : "icon.png");
            Sprite icon;
            if (Icons.TryGetValue(path, out icon)) return icon;
            // Standard icon lettering needs prefiltered mip levels in small slots.
            // Ghost outlines retain their original non-mipmapped edge treatment.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, !dropped, false) { name = kind + (dropped ? "_icon_ghost" : "_icon"), filterMode = dropped ? FilterMode.Bilinear : FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path), false)) throw new InvalidDataException(path);
            tex.Apply(!dropped, true);
            icon = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
            icon.name = tex.name;
            Icons[path] = icon;
            return icon;
        }
    }
}
