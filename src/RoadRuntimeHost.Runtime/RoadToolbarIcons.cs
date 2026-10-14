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
            if (!RoadIconRaster.Counts(lanes, out backward, out forward)) return;
            // Mirror-equivalent base topologies share the same small atlas.
            if (backward > forward) { int swap = backward; backward = forward; forward = swap; }
            if (_failed) return;
            if (_atlas == null)
                try { Build(); } catch { _failed = true; throw; }
            info.m_Atlas = _atlas; info.m_Thumbnail = RoadIconRaster.Name(backward, forward);
        }

        private void Build()
        {
            byte[] overlay = ReadOverlay();
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
                    byte[] rgba = RoadIconRaster.Render(backward, forward, overlay);
                    string name = RoadIconRaster.Name(backward, forward);
                    Color32[] tile = ToUnity(rgba);
                    Texture2D image = new Texture2D(128, 128, TextureFormat.ARGB32, false);
                    image.name = name; image.SetPixels32(tile); image.Apply();
                    images.Add(image);
                    int column = index % 4, row = index / 4;
                    for (int y = 0; y < 128; ++y)
                        Array.Copy(tile, y * 128, pixels, (row * 128 + y) * 512 + column * 128, 128);
                    Rect region = new Rect(column * 0.25f, row * 0.25f, 0.25f, 0.25f);
                    foreach (string state in RoadIconRaster.States)
                        atlas.AddSprite(new UITextureAtlas.SpriteInfo { name = name + state, texture = image, region = region });
                    ++index;
                }
            packed.SetPixels32(pixels); packed.Apply();
            material = new Material(UIView.GetAView().defaultAtlas.material);
            material.mainTexture = packed; atlas.material = material;
            _atlas = atlas;
            DiagnosticLog.Info("SUCCESS", "road_toolbar_icons_ready", "Shared base-road icons were generated",
                "topologies", index.ToString(), "overlay", overlay == null ? "none" : "icons/overlay.png");
            } catch {
                foreach (Texture2D image in images) UnityEngine.Object.Destroy(image);
                if (material != null) UnityEngine.Object.Destroy(material);
                UnityEngine.Object.Destroy(packed); UnityEngine.Object.Destroy(atlas);
                throw;
            }
        }

        private byte[] ReadOverlay()
        {
            string path = Path.Combine(_root, "icons/overlay.png");
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Toolbar overlay PNG exceeds 1 MiB");
            byte[] encoded = File.ReadAllBytes(path);
            RoadIconRaster.RequireOverlayPng(encoded);
            Texture2D image = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            try
            {
                if (!image.LoadImage(encoded) || image.width != 128 || image.height != 128)
                    throw new InvalidDataException("Toolbar overlay must be a 128x128 PNG");
                Color32[] pixels = image.GetPixels32();
                byte[] rgba = new byte[128 * 128 * 4];
                for (int y = 0; y < 128; ++y)
                    for (int x = 0; x < 128; ++x)
                    {
                        Color32 color = pixels[(127 - y) * 128 + x];
                        int p = (y * 128 + x) * 4;
                        rgba[p] = color.r; rgba[p + 1] = color.g; rgba[p + 2] = color.b; rgba[p + 3] = color.a;
                    }
                return rgba;
            }
            finally { UnityEngine.Object.Destroy(image); }
        }

        internal static Color32[] ToUnity(byte[] rgba)
        {
            Color32[] pixels = new Color32[128 * 128];
            for (int y = 0; y < 128; ++y)
                for (int x = 0; x < 128; ++x)
                {
                    int p = (y * 128 + x) * 4;
                    pixels[(127 - y) * 128 + x] = new Color32(rgba[p], rgba[p + 1], rgba[p + 2], rgba[p + 3]);
                }
            return pixels;
        }
    }
}
