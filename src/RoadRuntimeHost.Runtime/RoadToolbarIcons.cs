using System;
using System.Collections.Generic;
using System.IO;
using ColossalFramework.UI;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class RoadToolbarIcons
    {
        private readonly string _root;
        private UITextureAtlas _atlas;
        private bool _failed;

        public RoadToolbarIcons(string root) { _root = root; }

        public void Apply(NetInfo info, LaneBundle[] lanes)
        {
            int backward, forward;
            if (!RoadIconAssets.Counts(lanes, out backward, out forward)) return;
            // Mirror-equivalent base topologies share the same small atlas.
            if (backward > forward) { int swap = backward; backward = forward; forward = swap; }
            if (_failed) return;
            if (_atlas == null)
                try { Build(); } catch { _failed = true; throw; }
            info.m_Atlas = _atlas; info.m_Thumbnail = RoadIconAssets.Name(backward, forward);
        }

        private void Build()
        {
            // Validate all files before allocating native textures. Python owns
            // the artwork; this adapter only packs those PNGs into a UI atlas.
            List<byte[]> encoded = new List<byte[]>();
            for (int b = 0; b <= 4; ++b)
                for (int f = Math.Max(1, b); f <= 4; ++f)
                    encoded.Add(RoadIconAssets.Read(_root, b, f));
            UITextureAtlas atlas = ScriptableObject.CreateInstance<UITextureAtlas>();
            atlas.name = "RoadRuntimeHost.BaseRoadIcons";
            Texture2D packed = new Texture2D(512, 512, TextureFormat.ARGB32, false);
            packed.name = atlas.name; packed.wrapMode = TextureWrapMode.Clamp;
            packed.filterMode = FilterMode.Bilinear;
            List<Texture2D> images = new List<Texture2D>();
            Material material = null;
            try {
            Color32[] pixels = new Color32[512 * 512];
            int index = 0;
            for (int backward = 0; backward <= 4; ++backward)
                for (int forward = Math.Max(1, backward); forward <= 4; ++forward)
                {
                    string name = RoadIconAssets.Name(backward, forward);
                    Texture2D image = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    images.Add(image);
                    if (!image.LoadImage(encoded[index]) || image.width != 128 || image.height != 128)
                        throw new InvalidDataException("Toolbar PNG could not be decoded: " + name);
                    image.name = name;
                    Color32[] tile = image.GetPixels32();
                    int column = index % 4, row = index / 4;
                    for (int y = 0; y < 128; ++y)
                        Array.Copy(tile, y * 128, pixels, (row * 128 + y) * 512 + column * 128, 128);
                    Rect region = new Rect(column * 0.25f, row * 0.25f, 0.25f, 0.25f);
                    foreach (string state in RoadIconAssets.States)
                        atlas.AddSprite(new UITextureAtlas.SpriteInfo { name = name + state, texture = image, region = region });
                    ++index;
                }
            packed.SetPixels32(pixels); packed.Apply();
            material = new Material(UIView.GetAView().defaultAtlas.material);
            material.mainTexture = packed; atlas.material = material;
            _atlas = atlas;
            DiagnosticLog.Info("SUCCESS", "road_toolbar_icons_ready", "Offline base-road PNGs were loaded",
                "topologies", index.ToString(), "source", "icons/*.png");
            } catch {
                foreach (Texture2D image in images) UnityEngine.Object.Destroy(image);
                if (material != null) UnityEngine.Object.Destroy(material);
                UnityEngine.Object.Destroy(packed); UnityEngine.Object.Destroy(atlas);
                throw;
            }
        }

    }
}
