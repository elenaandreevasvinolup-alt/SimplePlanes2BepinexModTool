// ============================================================
//  SP2 Pack — texture baker
//
//  Turns any Unity texture into a plain PNG we can ship:
//  makes it readable, optionally rescales it and optionally
//  quantises colours to shrink the file. Results are cached so
//  the same texture is never processed twice.
// ============================================================

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sp2Pack
{
    public static class Sp2Baker
    {
        static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        public static void ClearCache()
        {
            foreach (var kv in _cache)
                if (kv.Value != null) Object.DestroyImmediate(kv.Value);
            _cache.Clear();
        }

        /// <summary>Produces a readable, optionally scaled/quantised copy of the source texture.</summary>
        public static Texture2D Bake(Texture src, Sp2Config cfg)
        {
            if (src == null) return null;

            string key = src.GetInstanceID() + ":" + cfg.texMaxSize + ":" + cfg.quantizeTextures;
            if (_cache.TryGetValue(key, out var cached) && cached != null) return cached;

            Texture2D result = null;
            try
            {
                result = MakeReadable(src);

                if (cfg.texMaxSize > 0)
                {
                    int longest = Mathf.Max(result.width, result.height);
                    if (longest > cfg.texMaxSize)
                    {
                        float k = (float)cfg.texMaxSize / longest;
                        int w = Mathf.Max(1, Mathf.RoundToInt(result.width * k));
                        int h = Mathf.Max(1, Mathf.RoundToInt(result.height * k));
                        result = Resize(result, w, h);
                    }
                }

                if (cfg.quantizeTextures)
                    Quantize(result, 6); // 6 levels per channel ≈ 216 colours
            }
            catch
            {
                if (result != null) { Object.DestroyImmediate(result); result = null; }
            }

            _cache[key] = result;
            return result;
        }

        /// <summary>Bakes and writes a PNG. Returns the byte size written (0 on failure).</summary>
        public static long WritePng(Texture src, Sp2Config cfg, string path)
        {
            var baked = Bake(src, cfg);
            if (baked == null) return 0;

            byte[] png = baked.EncodeToPNG();
            if (png == null) return 0;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
            return png.Length;
        }

        // ------------------------------------------------------------ helpers

        static Texture2D MakeReadable(Texture tex)
        {
            int w = Mathf.Max(1, tex.width);
            int h = Mathf.Max(1, tex.height);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var t2 = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t2.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t2.Apply();
                return t2;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static Texture2D Resize(Texture2D src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var t2 = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t2.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t2.Apply();
                Object.DestroyImmediate(src);
                return t2;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Posterises colour channels into <paramref name="levels"/> steps; alpha is kept.</summary>
        static void Quantize(Texture2D tex, int levels)
        {
            if (levels < 2) return;
            var px = tex.GetPixels32();
            float step = 255f / (levels - 1);
            for (int i = 0; i < px.Length; i++)
            {
                px[i].r = (byte)Mathf.RoundToInt(Mathf.Round(px[i].r / step) * step);
                px[i].g = (byte)Mathf.RoundToInt(Mathf.Round(px[i].g / step) * step);
                px[i].b = (byte)Mathf.RoundToInt(Mathf.Round(px[i].b / step) * step);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
        }
    }
}
