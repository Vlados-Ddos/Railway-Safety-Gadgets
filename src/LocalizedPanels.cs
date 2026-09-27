using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    // ModelAssets owns one shared material set per model, including item,
    // mounted, shelf and future instances. Change only its albedo reference.
    internal static class LocalizedPanels
    {
        private sealed class Binding
        {
            internal Material Material;
            internal Texture Original;
            internal string Russian, English;
            internal int Width, Height;
        }
        private static readonly List<Binding> Bindings = new List<Binding>();
        // Missing/corrupt resources are cached as null, avoiding repeated I/O/logs.
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();

        internal static void Register(Material material, string directory)
        {
            string manifest = Path.Combine(directory, "panel-localization.json");
            if (!File.Exists(manifest)) return; // legacy model pack remains usable
            try
            {
                var data = JObject.Parse(File.ReadAllText(manifest));
                if ((string)data["material"] != material.name) return;
                foreach (var entry in Bindings) if (entry.Material == material) return;
                var binding = new Binding { Material = material, Original = material.mainTexture,
                    Russian = Resource(directory, (string)data["ru"]), English = Resource(directory, (string)data["en"]),
                    Width = (int)data["width"], Height = (int)data["height"] };
                if (binding.Width < 1 || binding.Height < 1 || binding.Width > 4096 || binding.Height > 4096)
                    throw new InvalidDataException("Invalid localized panel dimensions");
                Bindings.Add(binding);
                Apply(binding, Texts.Russian);
            }
            catch (Exception ex) { Main.ErrorOnce("localized-panel-manifest-" + directory, ex); }
        }

        private static string Resource(string directory, string file)
        {
            if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid localized panel resource name");
            return Path.Combine(directory, file);
        }

        private static Texture2D Load(string path, int width, int height)
        {
            Texture2D cached;
            if (Textures.TryGetValue(path, out cached)) return cached;
            Texture2D texture = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false)
                { name = Path.GetFileName(path), anisoLevel = 4, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat };
                if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != width || texture.height != height)
                    throw new InvalidDataException("Invalid localized panel image: " + path);
                // Precompute mip levels and release the CPU copy. No per-object
                // texture/material clone and no Update/late-frame language polling.
                texture.Apply(true, true);
            }
            catch (Exception ex)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = null;
                Main.ErrorOnce("localized-panel-texture-" + path, ex);
            }
            Textures[path] = texture;
            return texture;
        }

        private static void Apply(Binding binding, bool russian)
        {
            Texture selected = russian ? Load(binding.Russian, binding.Width, binding.Height) : null;
            if (selected == null) selected = Load(binding.English, binding.Width, binding.Height);
            if (selected == null) selected = binding.Original;
            if (binding.Material.mainTexture != selected) binding.Material.mainTexture = selected;
        }

        internal static void Refresh(bool russian)
        {
            for (int i = Bindings.Count - 1; i >= 0; i--)
            {
                var binding = Bindings[i];
                if (binding.Material == null) { Bindings.RemoveAt(i); continue; }
                Apply(binding, russian);
            }
        }
    }
}
