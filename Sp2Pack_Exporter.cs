// ============================================================
//  SP2 Pack — exporter
//
//  Writes the scanned geometry to OBJ + MTL + PNG so the in-game
//  loader plugin can rebuild it.
//
//  Output layout (inside the chosen folder):
//      <name>.obj      world-space triangles, one usemtl per group
//      scene.mtl       material definitions (Kd / d / map_Kd)
//      tex/*.png       textures
//
//  All vertices are written first, then faces. When "merge by
//  material" is on the faces are reordered so each material is a
//  single contiguous block, which cuts the sub-mesh count the
//  loader has to build.
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sp2Pack
{
    public class Sp2ExportResult
    {
        public string outDir;
        public int files;
        public int vertices;
        public int triangles;
        public int materials;
        public int textures;
        public long objBytes;
        public long mtlBytes;
        public long texBytes;
        public readonly List<string> notes = new List<string>();
    }

    public static class Sp2Exporter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Sp2ExportResult Export(Sp2Scan scan, Sp2Config cfg, string outDir, string objName,
            Action<float, string> progress = null)
        {
            var result = new Sp2ExportResult { outDir = outDir };
            if (scan == null) return result;

            Directory.CreateDirectory(outDir);
            string texDir = Path.Combine(outDir, "tex");

            // Start clean so a renamed module does not leave stale obj/mtl/textures behind
            // (those leftovers used to get packaged too, doubling the zip size).
            CleanOutput(outDir, texDir);
            Directory.CreateDirectory(texDir);

            Sp2Baker.ClearCache();

            var mats = new MaterialTable(texDir, cfg, result);
            var objPath = Path.Combine(outDir, objName + ".obj");

            // UV is written only if every source mesh actually has it.
            bool globalUV = cfg.exportUV && AllHaveUV(scan, cfg);

            // Triangle soup, resolved into world vertices + a material index.
            var triMat = new List<int>(1 << 16);
            var triA = new List<int>(1 << 16);
            var triB = new List<int>(1 << 16);
            var triC = new List<int>(1 << 16);
            var matNames = new List<string>();
            var matIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            int baseIndex = 1; // OBJ indices are 1-based

            using (var w = new StreamWriter(objPath, false, new UTF8Encoding(false)))
            {
                w.Write("# SP2 Pack export\n");
                w.Write("mtllib scene.mtl\n");

                var tris = new List<int>(1024);

                for (int i = 0; i < scan.entries.Count; i++)
                {
                    if (progress != null && (i & 15) == 0)
                        progress(i / (float)Mathf.Max(1, scan.entries.Count), scan.entries[i].go != null ? scan.entries[i].go.name : "mesh");

                    var e = scan.entries[i];
                    var mesh = ResolveMesh(e);
                    if (mesh == null) continue;

                    var verts = mesh.vertices;
                    var uvs = mesh.uv;
                    bool hasUV = globalUV && uvs != null && uvs.Length == verts.Length;
                    var m = e.matrix;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        var p = m.MultiplyPoint3x4(verts[v]);
                        w.Write("v "); w.Write(F(p.x, cfg)); w.Write(' ');
                        w.Write(F(p.y, cfg)); w.Write(' '); w.Write(F(p.z, cfg)); w.Write('\n');
                    }
                    if (hasUV)
                    {
                        for (int v = 0; v < uvs.Length; v++)
                        {
                            w.Write("vt "); w.Write(F(uvs[v].x, cfg)); w.Write(' ');
                            w.Write(F(uvs[v].y, cfg)); w.Write('\n');
                        }
                    }

                    int subCount = Mathf.Max(1, mesh.subMeshCount);
                    for (int sub = 0; sub < subCount; sub++)
                    {
                        Material mat = (e.materials != null && sub < e.materials.Length) ? e.materials[sub] : null;
                        string name = mats.Register(mat);
                        int mi = MatIndex(name, matNames, matIndex);

                        mesh.GetTriangles(tris, sub);
                        for (int t = 0; t + 2 < tris.Count; t += 3)
                        {
                            triMat.Add(mi);
                            triA.Add(baseIndex + tris[t]);
                            triB.Add(baseIndex + tris[t + 1]);
                            triC.Add(baseIndex + tris[t + 2]);
                        }
                    }

                    baseIndex += verts.Length;
                    result.vertices += verts.Length;
                    result.files++;

                    if (mesh != e.mesh) UnityEngine.Object.DestroyImmediate(mesh);
                }

                // Terrain
                for (int i = 0; i < scan.terrains.Count; i++)
                    baseIndex = WriteTerrain(w, scan.terrains[i], cfg, mats, baseIndex, globalUV,
                        triMat, triA, triB, triC, matNames, matIndex, result);

                // Faces
                WriteFaces(w, cfg.mergeByMaterial, triMat, triA, triB, triC, matNames, globalUV);
                triMat.Clear(); triA.Clear(); triB.Clear(); triC.Clear();
            }

            result.triangles = triA.Count + triMat.Count; // informational; real count set in WriteFaces
            result.triangles = _lastTriangleCount;

            mats.WriteMtl(Path.Combine(outDir, "scene.mtl"));

            var fi = new FileInfo(objPath);
            result.objBytes = fi.Exists ? fi.Length : 0;
            result.mtlBytes = mats.MtlBytes;
            result.materials = mats.Count;
            result.textures = mats.TextureCount;

            return result;
        }

        static int _lastTriangleCount;

        /// <summary>Removes previous export output (obj / mtl / tex) so the folder always mirrors the latest run.</summary>
        static void CleanOutput(string outDir, string texDir)
        {
            try
            {
                if (Directory.Exists(outDir))
                {
                    foreach (var f in Directory.GetFiles(outDir, "*.obj")) File.Delete(f);
                    foreach (var f in Directory.GetFiles(outDir, "*.mtl")) File.Delete(f);
                }
                if (Directory.Exists(texDir)) Directory.Delete(texDir, true);
            }
            catch { }
        }

        // ------------------------------------------------------------ faces

        static void WriteFaces(StreamWriter w, bool merge,
            List<int> triMat, List<int> triA, List<int> triB, List<int> triC,
            List<string> matNames, bool uv)
        {
            int count = triMat.Count;

            if (merge)
            {
                var order = new List<int>();
                var buckets = new Dictionary<int, List<int>>();
                for (int i = 0; i < count; i++)
                {
                    int mi = triMat[i];
                    if (!buckets.TryGetValue(mi, out var list))
                    {
                        list = new List<int>();
                        buckets[mi] = list;
                        order.Add(mi);
                    }
                    list.Add(i);
                }

                for (int o = 0; o < order.Count; o++)
                {
                    int mi = order[o];
                    w.Write("usemtl "); w.Write(matNames[mi]); w.Write('\n');
                    var list = buckets[mi];
                    for (int k = 0; k < list.Count; k++)
                    {
                        int i = list[k];
                        w.Write("f "); Face(w, triA[i], triB[i], triC[i], uv); w.Write('\n');
                    }
                }
            }
            else
            {
                int last = int.MinValue;
                for (int i = 0; i < count; i++)
                {
                    int mi = triMat[i];
                    if (mi != last)
                    {
                        w.Write("usemtl "); w.Write(matNames[mi]); w.Write('\n');
                        last = mi;
                    }
                    w.Write("f "); Face(w, triA[i], triB[i], triC[i], uv); w.Write('\n');
                }
            }

            _lastTriangleCount = count;
        }

        static int MatIndex(string name, List<string> names, Dictionary<string, int> index)
        {
            if (index.TryGetValue(name, out var i)) return i;
            i = names.Count;
            names.Add(name);
            index[name] = i;
            return i;
        }

        // ------------------------------------------------------------ terrain

        static int WriteTerrain(StreamWriter w, Terrain terrain, Sp2Config cfg, MaterialTable mats,
            int baseIndex, bool uv,
            List<int> triMat, List<int> triA, List<int> triB, List<int> triC,
            List<string> matNames, Dictionary<string, int> matIndex, Sp2ExportResult result)
        {
            var data = terrain.terrainData;
            if (data == null) return baseIndex;

            int res = data.heightmapResolution;
            if (res < 2) return baseIndex;

            // Cap the sampled resolution so a huge terrain cannot explode the OBJ.
            const int MaxSamples = 129;
            int stride = Mathf.Max(1, Mathf.CeilToInt(res / (float)MaxSamples));
            int nx = (res + stride - 1) / stride;

            var heights = data.GetHeights(0, 0, res, res);
            var size = data.size;
            var pos = terrain.transform.position;

            string name = mats.Register(terrain.materialTemplate);
            int mi = MatIndex(name, matNames, matIndex);

            for (int y = 0; y < res; y += stride)
            {
                for (int x = 0; x < res; x += stride)
                {
                    float fx = (float)x / (res - 1);
                    float fz = (float)y / (res - 1);
                    w.Write("v ");
                    w.Write(F(pos.x + fx * size.x, cfg)); w.Write(' ');
                    w.Write(F(pos.y + heights[y, x] * size.y, cfg)); w.Write(' ');
                    w.Write(F(pos.z + fz * size.z, cfg)); w.Write('\n');

                    if (uv)
                    {
                        w.Write("vt "); w.Write(F(fx, cfg)); w.Write(' ');
                        w.Write(F(fz, cfg)); w.Write('\n');
                    }
                }
            }

            for (int y = 0; y < nx - 1; y++)
            {
                for (int x = 0; x < nx - 1; x++)
                {
                    int i0 = baseIndex + y * nx + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + nx;
                    int i3 = i2 + 1;
                    triMat.Add(mi); triA.Add(i0); triB.Add(i2); triC.Add(i1);
                    triMat.Add(mi); triA.Add(i1); triB.Add(i2); triC.Add(i3);
                }
            }

            result.vertices += nx * nx;
            result.files++;
            result.notes.Add("Terrain exported at 1/" + stride + " heightmap resolution (flat material).");
            return baseIndex + nx * nx;
        }

        // ------------------------------------------------------------ helpers

        static bool AllHaveUV(Sp2Scan scan, Sp2Config cfg)
        {
            for (int i = 0; i < scan.entries.Count; i++)
            {
                var m = scan.entries[i].mesh;
                if (m == null) continue;
                var uv = m.uv;
                if (uv == null || uv.Length != m.vertexCount) return false;
            }
            return true;
        }

        static Mesh ResolveMesh(Sp2Entry e)
        {
            if (!e.skinned) return e.mesh;

            var smr = e.go != null ? e.go.GetComponent<SkinnedMeshRenderer>() : null;
            if (smr == null) return e.mesh;
            try
            {
                var baked = new Mesh();
                smr.BakeMesh(baked);
                return baked;
            }
            catch
            {
                return e.mesh;
            }
        }

        static void Face(StreamWriter w, int a, int b, int c, bool uv)
        {
            if (uv)
            {
                w.Write(a); w.Write('/'); w.Write(a); w.Write(' ');
                w.Write(b); w.Write('/'); w.Write(b); w.Write(' ');
                w.Write(c); w.Write('/'); w.Write(c);
            }
            else
            {
                w.Write(a); w.Write(' '); w.Write(b); w.Write(' '); w.Write(c);
            }
        }

        static string F(float v, Sp2Config cfg)
        {
            int dec = Mathf.Clamp(cfg.vertexDecimals, 3, 6);
            return v.ToString("0." + new string('#', dec), Inv);
        }

        // ============================================================
        //  Material table: dedupes materials, writes MTL, exports PNGs
        // ============================================================

        class MaterialTable
        {
            readonly string _texDir;
            readonly Sp2Config _cfg;
            readonly Sp2ExportResult _result;
            readonly Dictionary<int, string> _byId = new Dictionary<int, string>();
            readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly StringBuilder _mtl = new StringBuilder();
            readonly Dictionary<int, string> _texFiles = new Dictionary<int, string>();
            readonly HashSet<string> _usedTexFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public int Count => _byId.Count;
            public int TextureCount => _texFiles.Count;
            public long MtlBytes { get; private set; }

            public MaterialTable(string texDir, Sp2Config cfg, Sp2ExportResult result)
            {
                _texDir = texDir;
                _cfg = cfg;
                _result = result;
            }

            public string Register(Material mat)
            {
                if (mat == null) return Fallback();

                int id = mat.GetInstanceID();
                if (_byId.TryGetValue(id, out var existing)) return existing;

                string name = Unique(mat.name);
                _byId[id] = name;

                _mtl.Append("newmtl ").Append(name).Append('\n');

                Color c = ColorOf(mat);
                _mtl.Append("Kd ").Append(N(c.r)).Append(' ').Append(N(c.g)).Append(' ').Append(N(c.b)).Append('\n');
                if (c.a < 0.999f)
                {
                    _mtl.Append("d ").Append(N(c.a)).Append('\n');
                    _mtl.Append("Tr ").Append(N(1f - c.a)).Append('\n');
                }
                _mtl.Append("Ka 0 0 0\nKs 0 0 0\nillum 2\n");

                var tex = MainTexture(mat);
                if (tex != null)
                {
                    string file = TextureFile(tex, name);
                    if (file != null) _mtl.Append("map_Kd ").Append(file).Append('\n');
                }
                _mtl.Append('\n');
                return name;
            }

            string _fallback;
            string Fallback()
            {
                if (_fallback != null) return _fallback;
                _fallback = Unique("default");
                _mtl.Append("newmtl ").Append(_fallback).Append('\n');
                _mtl.Append("Kd 0.7 0.7 0.7\nKa 0 0 0\nKs 0 0 0\nillum 2\n\n");
                return _fallback;
            }

            string TextureFile(Texture tex, string matName)
            {
                int id = tex.GetInstanceID();
                if (_texFiles.TryGetValue(id, out var known)) return known;

                string file = UniqueTex(matName + "_" + Safe(tex.name));
                string path = Path.Combine(_texDir, file);
                long bytes = Sp2Baker.WritePng(tex, _cfg, path);
                if (bytes <= 0)
                {
                    _result.notes.Add("Skipped unreadable texture: " + tex.name);
                    _texFiles[id] = null;
                    return null;
                }
                _texFiles[id] = file;
                _result.texBytes += bytes;
                return "tex/" + file;
            }

            public void WriteMtl(string path)
            {
                File.WriteAllText(path, "# SP2 Pack materials\n" + _mtl, new UTF8Encoding(false));
                MtlBytes = new FileInfo(path).Length;
            }

            static Color ColorOf(Material m)
            {
                if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
                if (m.HasProperty("_Color")) return m.GetColor("_Color");
                return Color.white;
            }

            static Texture MainTexture(Material m)
            {
                if (m.HasProperty("_BaseMap"))
                {
                    var t = m.GetTexture("_BaseMap");
                    if (t != null) return t;
                }
                if (m.HasProperty("_MainTex"))
                {
                    var t = m.GetTexture("_MainTex");
                    if (t != null) return t;
                }
                return m.mainTexture;
            }

            string Unique(string raw)
            {
                string b = Safe(string.IsNullOrEmpty(raw) ? "material" : raw);
                if (_usedNames.Add(b)) return b;
                int n = 2;
                while (!_usedNames.Add(b + "_" + n)) n++;
                return b + "_" + n;
            }

            int _texSeq;
            string UniqueTex(string raw)
            {
                string candidate = Safe(raw) + ".png";
                while (_usedTexFiles.Contains(candidate))
                    candidate = Safe(raw) + "_" + (++_texSeq) + ".png";
                _usedTexFiles.Add(candidate);
                return candidate;
            }

            static string N(float v) => v.ToString("0.####", Inv);

            static string Safe(string raw)
            {
                var sb = new StringBuilder(raw.Length);
                foreach (char ch in raw)
                    sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
                var s = sb.ToString().Trim('_');
                return s.Length == 0 ? "x" : s;
            }
        }
    }
}
