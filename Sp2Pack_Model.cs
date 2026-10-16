// ============================================================
//  SP2 Pack — dependency scan, statistics and size estimation
//
//  Scanning is cooperative: Sp2ScanJob.Step() runs for a small
//  millisecond budget per editor frame and resumes next frame.
//  Both component collection and processing are time-sliced, so
//  even a huge scene never blocks the editor. Heavy lookups
//  (triangle counts, a material's texture list) are cached.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Sp2Pack
{
    public class Sp2Entry
    {
        public Mesh mesh;
        public Matrix4x4 matrix;
        public Material[] materials;
        public GameObject go;
        public bool skinned;
        public int triangles;
        public int vertices;
    }

    public class Sp2Scan
    {
        public readonly List<Sp2Entry> entries = new List<Sp2Entry>();
        public readonly List<Terrain> terrains = new List<Terrain>();

        public readonly List<Material> materials = new List<Material>();
        public readonly List<Texture> textures = new List<Texture>();

        public int totalVertices;
        public int totalTriangles;
        public int objects;

        public int skippedSmall;
        public int skippedHidden;
        public int skippedFar;

        public readonly List<string> warnings = new List<string>();

        public bool Empty => entries.Count == 0 && terrains.Count == 0;

        /// <summary>Total source pixels of all textures (for size estimation).</summary>
        public long TexturePixels()
        {
            long px = 0;
            for (int i = 0; i < textures.Count; i++)
            {
                var t = textures[i];
                if (t == null) continue;
                px += (long)t.width * t.height;
            }
            return px;
        }
    }

    public class Sp2Estimate
    {
        public long objBytes;
        public long mtlBytes;
        public long texBytes;
        public long pluginBytes;    // loader plugin dll
        public long frameworkBytes; // bundled BepInEx framework
        public long Total => objBytes + mtlBytes + texBytes + pluginBytes + frameworkBytes;
    }

    // ============================================================
    //  Resumable scan job
    // ============================================================

    public class Sp2ScanJob
    {
        enum Phase { Collect, Process, Finished }

        readonly Sp2Config _cfg;
        readonly GameObject[] _roots;
        readonly List<Component> _targets = new List<Component>();

        readonly HashSet<int> _seenMats = new HashSet<int>();
        readonly HashSet<int> _seenTex = new HashSet<int>();
        readonly HashSet<int> _seenTerrain = new HashSet<int>();

        Phase _phase;
        int _rootIndex;
        int _i;

        public readonly Sp2Scan Scan = new Sp2Scan();
        public bool Done => _phase == Phase.Finished;

        public float Progress
        {
            get
            {
                if (_phase == Phase.Finished) return 1f;
                if (_phase == Phase.Collect)
                {
                    float c = _roots == null || _roots.Length == 0 ? 1f : (float)_rootIndex / _roots.Length;
                    return c * 0.5f;
                }
                float p = _targets.Count == 0 ? 1f : (float)_i / _targets.Count;
                return 0.5f + p * 0.5f;
            }
        }

        public Sp2ScanJob(GameObject[] roots, Sp2Config cfg)
        {
            _cfg = cfg;
            _roots = roots ?? new GameObject[0];
            _phase = _roots.Length == 0 ? Phase.Finished : Phase.Collect;
            if (_phase == Phase.Finished) Warn();
        }

        /// <summary>Processes work until the millisecond budget runs out. Call from the editor update loop.</summary>
        public void Step(double budgetMs)
        {
            if (_phase == Phase.Finished) return;

            double start = EditorApplication.timeSinceStartup;

            while (true)
            {
                if (_phase == Phase.Collect)
                {
                    while (_rootIndex < _roots.Length)
                    {
                        Collect(_roots[_rootIndex++]);
                        if (Elapsed(start) >= budgetMs) return;
                    }
                    _phase = _targets.Count == 0 ? Phase.Finished : Phase.Process;
                    if (_phase == Phase.Finished) { Warn(); return; }
                }

                if (_phase == Phase.Process)
                {
                    while (_i < _targets.Count)
                    {
                        Process(_targets[_i++]);
                        if (Elapsed(start) >= budgetMs) return;
                    }
                    _phase = Phase.Finished;
                    Warn();
                    return;
                }

                return;
            }
        }

        /// <summary>Runs to completion, ignoring the budget. Use only for headless/export paths.</summary>
        public void RunToCompletion()
        {
            while (!Done) Step(100000.0);
        }

        static double Elapsed(double start) => (EditorApplication.timeSinceStartup - start) * 1000.0;

        void Collect(GameObject root)
        {
            if (root == null) return;
            _targets.AddRange(root.GetComponentsInChildren<MeshFilter>(_cfg.includeInactive));
            if (_cfg.includeSkinned)
                _targets.AddRange(root.GetComponentsInChildren<SkinnedMeshRenderer>(_cfg.includeInactive));
            if (_cfg.includeTerrain)
                _targets.AddRange(root.GetComponentsInChildren<Terrain>(_cfg.includeInactive));
        }

        void Process(Component c)
        {
            if (c is MeshFilter mf)
            {
                ProcessMesh(mf.sharedMesh, mf.GetComponent<MeshRenderer>(), mf.gameObject, mf.transform.localToWorldMatrix, false);
                return;
            }
            if (c is SkinnedMeshRenderer smr)
            {
                ProcessMesh(smr.sharedMesh, smr, smr.gameObject, smr.transform.localToWorldMatrix, true);
                return;
            }
            if (c is Terrain t)
            {
                if (t.terrainData != null && _seenTerrain.Add(t.GetInstanceID()))
                    Scan.terrains.Add(t);
            }
        }

        void ProcessMesh(Mesh mesh, Renderer renderer, GameObject go, Matrix4x4 matrix, bool skinned)
        {
            if (mesh == null || mesh.vertexCount == 0) return;

            if (_cfg.dropHidden && (go == null || (go.hideFlags & HideFlags.HideInHierarchy) != 0))
            { Scan.skippedHidden++; return; }
            if (renderer != null && !renderer.enabled) { Scan.skippedHidden++; return; }
            if (_cfg.maxDistance > 0f && go != null && go.transform.position.magnitude > _cfg.maxDistance)
            { Scan.skippedFar++; return; }

            int tris = Sp2Model.Triangles(mesh);
            if (tris < _cfg.minTriangles) { Scan.skippedSmall++; return; }

            var materials = renderer != null ? renderer.sharedMaterials : null;

            var entry = new Sp2Entry
            {
                mesh = mesh,
                matrix = matrix,
                materials = materials,
                go = go,
                skinned = skinned,
                triangles = tris,
                vertices = mesh.vertexCount,
            };
            Scan.entries.Add(entry);

            Scan.totalVertices += entry.vertices;
            Scan.totalTriangles += tris;
            Scan.objects++;

            if (materials != null)
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    var m = materials[i];
                    if (m == null) continue;
                    if (_seenMats.Add(m.GetInstanceID())) Scan.materials.Add(m);

                    var texs = Sp2Model.TexturesOf(m);
                    for (int k = 0; k < texs.Count; k++)
                    {
                        var tex = texs[k];
                        if (tex != null && _seenTex.Add(tex.GetInstanceID())) Scan.textures.Add(tex);
                    }
                }
            }
        }

        void Warn()
        {
            if (Scan.Empty)
                Scan.warnings.Add("Nothing to export: check the selection, the triangle threshold and the visibility filters.");
        }
    }

    public static class Sp2Model
    {
        // Size constants used by the estimator
        const long PluginBytes = 38 * 1024;      // SP2WorldModelLoader.dll
        const long FrameworkBytes = 639 * 1024;  // BepInEx win x64 zip

        // ---- caches (survive across scans; cleared on demand)
        static readonly Dictionary<int, int> _triCache = new Dictionary<int, int>();
        static readonly Dictionary<int, List<Texture>> _texCache = new Dictionary<int, List<Texture>>();

        public static void ClearCaches()
        {
            _triCache.Clear();
            _texCache.Clear();
            Sp2Baker.ClearCache();
        }

        /// <summary>Triangle count without allocating the index array (mesh.triangles copies every call).</summary>
        public static int Triangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int id = mesh.GetInstanceID();
            if (_triCache.TryGetValue(id, out var cached)) return cached;

            int indices = 0;
            try
            {
                int subs = Mathf.Max(1, mesh.subMeshCount);
                for (int s = 0; s < subs; s++) indices += (int)mesh.GetIndexCount(s);
            }
            catch { indices = 0; }

            int tris = indices / 3;
            _triCache[id] = tris;
            return tris;
        }

        /// <summary>Cached list of textures referenced by a material's shader properties.</summary>
        public static List<Texture> TexturesOf(Material m)
        {
            int id = m.GetInstanceID();
            if (_texCache.TryGetValue(id, out var cached)) return cached;

            var list = new List<Texture>();
            var shader = m.shader;
            if (shader != null)
            {
                int count;
                try { count = shader.GetPropertyCount(); }
                catch { count = 0; }

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                        var tex = m.GetTexture(shader.GetPropertyName(i));
                        if (tex != null) list.Add(tex);
                    }
                    catch { }
                }
            }
            _texCache[id] = list;
            return list;
        }

        public static Sp2ScanJob BeginScan(GameObject[] roots, Sp2Config cfg)
        {
            return new Sp2ScanJob(roots, cfg);
        }

        // ------------------------------------------------------------ estimate

        public static Sp2Estimate Estimate(Sp2Scan scan, Sp2Config cfg)
        {
            var est = new Sp2Estimate();
            if (scan == null) return est;

            int dec = Mathf.Clamp(cfg.vertexDecimals, 3, 6);
            long vLine = 2 + 3L * (dec + 3);
            long vtLine = cfg.exportUV ? (3 + 2L * 8) : 0;
            long fLine = 2 + 3L * 8;

            long obj = 0;
            for (int i = 0; i < scan.entries.Count; i++)
            {
                var e = scan.entries[i];
                obj += e.vertices * (vLine + vtLine);
                obj += e.triangles * fLine;
            }

            for (int i = 0; i < scan.terrains.Count; i++)
            {
                var d = scan.terrains[i].terrainData;
                if (d == null) continue;
                int res = Mathf.Clamp(d.heightmapResolution, 2, 257);
                long pts = (long)res * res;
                obj += pts * (vLine + vtLine);
                obj += (pts * 2) * fLine;
            }

            est.objBytes = obj;
            est.mtlBytes = Math.Max(512, scan.materials.Count * 120L);

            double bytesPerPixel = cfg.quantizeTextures ? 0.35 : 0.85;
            long texPixels = 0;
            for (int i = 0; i < scan.textures.Count; i++)
            {
                var t = scan.textures[i];
                if (t == null) continue;
                int w = t.width, h = t.height;
                if (cfg.texMaxSize > 0)
                {
                    int longest = Mathf.Max(w, h);
                    if (longest > cfg.texMaxSize)
                    {
                        float k = (float)cfg.texMaxSize / longest;
                        w = Mathf.Max(1, Mathf.RoundToInt(w * k));
                        h = Mathf.Max(1, Mathf.RoundToInt(h * k));
                    }
                }
                texPixels += (long)w * h;
            }
            est.texBytes = (long)(texPixels * bytesPerPixel);

            est.pluginBytes = PluginBytes;
            est.frameworkBytes = FrameworkBytes;
            return est;
        }

        // ------------------------------------------------------------ scene targets

        public static GameObject[] CurrentSelection()
        {
            return Selection.gameObjects;
        }

        public static GameObject[] SceneRoots()
        {
            var list = new List<GameObject>();
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return list.ToArray();
            scene.GetRootGameObjects(list);
            return list.ToArray();
        }
    }
}
