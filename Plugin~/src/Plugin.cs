// =====================================================================
// Universal World Model Loader 7.0.0
//  - MTL parsing (map_Kd diffuse texture)
//  - Multiple submeshes, one material each
//  - OBJ usemtl material switching
//  - Falls back to a same-named .png when there is no MTL
//  - Keeps chunking, async, frame slicing and delayed GC
// =====================================================================

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UniversalWorldModelLoader
{
    [BepInPlugin("com.sp2.worldmodelloader", "Universal World Model Loader", "7.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static ManualLogSource Log;
        public static ConfigFile Cfg;

        public static ConfigEntry<string> CfgRefTerrain;
        public static ConfigEntry<int> CfgAxisMode;
        public static ConfigEntry<bool> CfgFlipWinding;
        public static ConfigEntry<float> CfgScale;
        public static ConfigEntry<float> CfgOffsetX;
        public static ConfigEntry<float> CfgOffsetY;
        public static ConfigEntry<float> CfgOffsetZ;

        public static ConfigEntry<int> CfgColliderMode;
        public static ConfigEntry<float> CfgColliderSizeScale;
        public static ConfigEntry<float> CfgColliderOffsetY;

        public static ConfigEntry<int> CfgMaxConcurrentParse;
        public static ConfigEntry<int> CfgSpawnPerFrame;
        public static ConfigEntry<int> CfgMaxMeshVerticesPerFrame;
        public static ConfigEntry<int> CfgSmoothTailCount;
        public static ConfigEntry<int> CfgActivatePerFrame;
        public static ConfigEntry<int> CfgDelayGCSeconds;
        public static ConfigEntry<int> CfgChunkTriangles;

        public static ConfigEntry<int> CfgRailLayer;

        public static ConfigEntry<bool> CfgEnableTextures;
        public static ConfigEntry<bool> CfgEnableMtl;
        public static ConfigEntry<float> CfgTintR;
        public static ConfigEntry<float> CfgTintG;
        public static ConfigEntry<float> CfgTintB;
        public static ConfigEntry<float> CfgTintA;

        public static ConfigEntry<KeyCode> CfgKeyLoad;
        public static ConfigEntry<KeyCode> CfgKeyUnload;
        public static ConfigEntry<KeyCode> CfgKeyGui;

        public static ConfigEntry<bool> CfgShowWireframe;
        public static ConfigEntry<float> CfgWireR;
        public static ConfigEntry<float> CfgWireG;
        public static ConfigEntry<float> CfgWireB;
        public static ConfigEntry<float> CfgWireAlpha;

        public const string OBJ_SUBDIR = "UniversalWorldModelLoader/Obj";

        public GameObject Root;
        static Shader _pickedShader;
        static string _shaderName = "?";
        static Material _solidMat;
        static Material WireMat;
        public static readonly List<GameObject> WireObjects = new List<GameObject>();

        static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();
        static readonly Dictionary<string, Material> _matCache = new Dictionary<string, Material>();
        static readonly Dictionary<string, Material> _mtlMatCache = new Dictionary<string, Material>();

        static Type _tFlightScene;
        static PropertyInfo _pFlightSceneInstance;
        static PropertyInfo _pCameraManagerMainCamera;

        public static string StatusMsg = "";
        public static Terrain RefTerrain;
        public static int LastLoadedCount = 0;
        public static int LastFailedCount = 0;
        public static int LastPendingCount = 0;
        public static string CurrentPhase = "Idle";

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Cfg = Config;
            BindConfigs();

            var go = new GameObject("UniversalWorldModelLoader");
            GameObject.DontDestroyOnLoad(go);
            go.AddComponent<LoaderGui>();
            go.AddComponent<FrameSpikeLogger>();

            Log.LogInfo("Universal World Model Loader 7.0.0 loaded");
        }

        void BindConfigs()
        {
            CfgRefTerrain = Cfg.Bind("Placement", "RefTerrain", "SPN_1_2", "Reference terrain name");
            CfgAxisMode = Cfg.Bind("Placement", "AxisMode", 6, "Axis conversion 0-9");
            CfgFlipWinding = Cfg.Bind("Placement", "FlipWinding", true, "Flip winding");
            CfgScale = Cfg.Bind("Placement", "Scale", 1f, "Scale");
            CfgOffsetX = Cfg.Bind("Placement", "OffsetX", 0f, "X offset");
            CfgOffsetY = Cfg.Bind("Placement", "OffsetY", 740f, "Y offset");
            CfgOffsetZ = Cfg.Bind("Placement", "OffsetZ", 0f, "Z offset");

            CfgColliderMode = Cfg.Bind("Collision", "Mode", 4, "0=none 1=convex 2=box 3=precise AABB 4=non-convex precise");
            CfgColliderSizeScale = Cfg.Bind("Collision", "SizeScale", 1f, "Size scale");
            CfgColliderOffsetY = Cfg.Bind("Collision", "OffsetY", 0f, "Y offset");

            CfgMaxConcurrentParse = Cfg.Bind("Performance", "MaxConcurrentParse", 4, "Background parse concurrency");
            CfgSpawnPerFrame = Cfg.Bind("Performance", "SpawnPerFrame", 3, "Meshes built per frame");
            CfgMaxMeshVerticesPerFrame = Cfg.Bind("Performance", "MaxMeshVerticesPerFrame", 200000, "Max vertices per frame");
            CfgSmoothTailCount = Cfg.Bind("Performance", "SmoothTailCount", 5, "Smooth tail threshold");
            CfgActivatePerFrame = Cfg.Bind("Performance", "ActivatePerFrame", 1, "Activations per frame");
            CfgDelayGCSeconds = Cfg.Bind("Performance", "DelayGCSeconds", 3, "Delayed GC seconds");
            CfgChunkTriangles = Cfg.Bind("Performance", "ChunkTriangles", 8000,
                "Max triangles per chunk (0 = no chunking)");

            CfgRailLayer = Cfg.Bind("Performance", "RailLayer", 20, "Layer");

            CfgEnableTextures = Cfg.Bind("Texture", "EnableTextures", true, "Enable textures");
            CfgEnableMtl = Cfg.Bind("Texture", "EnableMtl", true, "Parse MTL files (map_Kd)");
            CfgTintR = Cfg.Bind("Texture", "TintR", 1f, "R");
            CfgTintG = Cfg.Bind("Texture", "TintG", 1f, "G");
            CfgTintB = Cfg.Bind("Texture", "TintB", 1f, "B");
            CfgTintA = Cfg.Bind("Texture", "TintA", 1f, "A");

            CfgKeyLoad = Cfg.Bind("Key", "Load", KeyCode.F9, "Load");
            CfgKeyUnload = Cfg.Bind("Key", "Unload", KeyCode.F10, "Unload");
            CfgKeyGui = Cfg.Bind("Key", "Gui", KeyCode.F7, "GUI");

            CfgShowWireframe = Cfg.Bind("Debug", "ShowWireframe", true, "Wireframe");
            CfgWireR = Cfg.Bind("Debug", "WireR", 0f, "R");
            CfgWireG = Cfg.Bind("Debug", "WireG", 1f, "G");
            CfgWireB = Cfg.Bind("Debug", "WireB", 0f, "B");
            CfgWireAlpha = Cfg.Bind("Debug", "WireAlpha", 0.35f, "Alpha");
        }

        void Update()
        {
            if (Input.GetKeyDown(CfgKeyLoad.Value)) LoadAll();
            if (Input.GetKeyDown(CfgKeyUnload.Value)) Unload();
            RefreshWireframe();
        }

        public void LoadAll()
        {
            Unload();
            StatusMsg = "";
            LastLoadedCount = 0;
            LastFailedCount = 0;
            LastPendingCount = 0;
            CurrentPhase = "Initialising";

            RefTerrain = FindReferenceTerrain();
            if (RefTerrain == null)
            {
                StatusMsg = "Terrain not found " + CfgRefTerrain.Value;
                CurrentPhase = "Error";
                return;
            }

            Root = new GameObject("WorldModelRoot");
            Root.transform.SetParent(RefTerrain.transform, false);
            Root.transform.localPosition = new Vector3(
                CfgOffsetX.Value, CfgOffsetY.Value, CfgOffsetZ.Value);
            Root.transform.localRotation = Quaternion.identity;
            Root.transform.localScale = Vector3.one * CfgScale.Value;

            StatusMsg = "Attached to " + RefTerrain.name;

            var shader = GetShader();
            if (shader == null) { Log.LogError("Shader not found"); CurrentPhase = "Error"; return; }

            Log.LogInfo("=== Load ===");
            Log.LogInfo("  Shader         = " + _shaderName);
            Log.LogInfo("  RefTerrain     = " + RefTerrain.name);
            Log.LogInfo("  ChunkTriangles = " + CfgChunkTriangles.Value);
            Log.LogInfo("  EnableMtl      = " + CfgEnableMtl.Value);

            string dir = Path.Combine(Paths.PluginPath, OBJ_SUBDIR);
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return; }

            var files = Directory.GetFiles(dir, "*.obj");
            if (files.Length == 0) { Log.LogWarning("No .obj files in the Obj folder: " + dir); return; }

            var loader = Root.AddComponent<AsyncLoader>();
            loader.Begin(files);
        }

        public void Unload()
        {
            if (Root != null)
            {
                UnityEngine.Object.Destroy(Root);
                Root = null;
                Log.LogInfo("Unloaded");
            }
            WireObjects.Clear();
            StatusMsg = "";
            CurrentPhase = "Idle";
        }

        // ==================== Materials ====================

        public static Texture2D LoadTextureFromFile(string fullPath)
        {
            if (!CfgEnableTextures.Value) return null;
            if (!File.Exists(fullPath)) return null;
            if (_texCache.TryGetValue(fullPath, out var c)) return c;

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (tex.LoadImage(bytes))
                {
                    tex.name = Path.GetFileNameWithoutExtension(fullPath);
                    tex.wrapMode = TextureWrapMode.Repeat;
                    tex.filterMode = FilterMode.Bilinear;
                    _texCache[fullPath] = tex;
                    return tex;
                }
                UnityEngine.Object.Destroy(tex);
            }
            catch (Exception e)
            {
                Log.LogWarning("Texture failed " + fullPath + ": " + e.Message);
            }
            _texCache[fullPath] = null;
            return null;
        }

        // Fall back to a same-named .png/.jpg when there is no MTL
        public static Texture2D LoadTextureForObj(string objPath)
        {
            if (!CfgEnableTextures.Value) return null;
            string dir = Path.GetDirectoryName(objPath);
            string name = Path.GetFileNameWithoutExtension(objPath);
            string[] exts = { ".png", ".PNG", ".jpg", ".JPG", ".jpeg", ".JPEG" };
            foreach (var ext in exts)
            {
                string p = Path.Combine(dir, name + ext);
                if (File.Exists(p)) return LoadTextureFromFile(p);
            }
            return null;
        }

        static Shader GetShader()
        {
            if (_pickedShader != null) return _pickedShader;
            string[] candidates = new string[]
            {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Universal Render Pipeline/Unlit",
                "Sprites/Default", "Unlit/Color", "Standard",
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                var s = Shader.Find(candidates[i]);
                if (s != null) { _pickedShader = s; _shaderName = candidates[i]; return s; }
            }
            return null;
        }

        static void ApplyBaseColor(Material mat, Color c)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        }

        static void ApplyCommon(Material mat)
        {
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", 0f);
            if (mat.HasProperty("_DoubleSidedEnable")) mat.SetFloat("_DoubleSidedEnable", 1f);
        }

        // Shared solid material used when there is no texture
        public static Material GetSolidMat()
        {
            if (_solidMat == null)
            {
                var sh = GetShader();
                if (sh == null) return null;
                _solidMat = new Material(sh);
                Color c = new Color(CfgTintR.Value, CfgTintG.Value, CfgTintB.Value, CfgTintA.Value);
                ApplyBaseColor(_solidMat, c);
                ApplyCommon(_solidMat);
            }
            return _solidMat;
        }

        // Per-obj "same-named texture" material
        public static Material GetMaterialFor(string objPath)
        {
            Texture2D tex = LoadTextureForObj(objPath);
            if (tex == null) return GetSolidMat();

            if (_matCache.TryGetValue(objPath, out var m)) return m;
            var sh = GetShader();
            if (sh == null) return null;
            var mat = new Material(sh);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            Color tint = new Color(CfgTintR.Value, CfgTintG.Value, CfgTintB.Value, CfgTintA.Value);
            ApplyBaseColor(mat, tint);
            ApplyCommon(mat);
            _matCache[objPath] = mat;
            return mat;
        }


        // Robustly resolve a texture path from an MTL (handles .\, absolute paths, case, recursive search)
        public static string ResolveTexturePath(string mtlPath, string mapPath)
        {
            if (string.IsNullOrEmpty(mapPath)) return null;

            string mtlDir = Path.GetDirectoryName(mtlPath);
            if (string.IsNullOrEmpty(mtlDir)) mtlDir = ".";

            // Normalise: strip quotes and spaces, turn \ into /
            string raw = mapPath.Trim().Trim('"').Trim();
            string cleaned = raw.Replace('\\', '/');

            // Drop the leading "./"
            while (cleaned.StartsWith("./")) cleaned = cleaned.Substring(2);
            while (cleaned.StartsWith("../"))
            {
                cleaned = cleaned.Substring(3);
                mtlDir = Path.GetDirectoryName(mtlDir);
                if (string.IsNullOrEmpty(mtlDir)) mtlDir = ".";
            }

            // Try 1: relative to the mtl folder, normalised with GetFullPath
            try
            {
                string c1 = Path.GetFullPath(Path.Combine(mtlDir, cleaned));
                if (File.Exists(c1)) return c1;
            }
            catch { }

            // Try 2: just the file name, directly in the mtl folder
            try
            {
                string fn = Path.GetFileName(cleaned);
                string c2 = Path.GetFullPath(Path.Combine(mtlDir, fn));
                if (File.Exists(c2)) return c2;
            }
            catch { }

            // Try 3: recursive file-name search under the mtl folder (case-insensitive)
            try
            {
                string fn = Path.GetFileName(cleaned);
                if (!string.IsNullOrEmpty(fn))
                {
                    var hits = Directory.GetFiles(mtlDir, fn, SearchOption.AllDirectories);
                    if (hits != null && hits.Length > 0) return hits[0];
                }
            }
            catch { }

            // Try 4: recursive search by extension-less name (handles case and extension mismatch)
            try
            {
                string stem = Path.GetFileNameWithoutExtension(cleaned);
                if (!string.IsNullOrEmpty(stem))
                {
                    var all = Directory.GetFiles(mtlDir, "*.*", SearchOption.AllDirectories);
                    for (int i = 0; i < all.Length; i++)
                    {
                        string candStem = Path.GetFileNameWithoutExtension(all[i]);
                        if (string.Equals(candStem, stem, StringComparison.OrdinalIgnoreCase))
                            return all[i];
                    }
                }
            }
            catch { }

            // Try 5: whole-disk search (last resort, slow, only when mtlDir is empty)
            // omitted

            return null;
        }


        // Create a Material from an MTL definition (cached by mtlPath::matName)
        public static Material GetMtlMaterial(string mtlPath, MtlMaterialDef def)
        {
            string key = mtlPath + "::" + (def != null ? def.Name : "?");
            if (_mtlMatCache.TryGetValue(key, out var cached)) return cached;

            var sh = GetShader();
            if (sh == null) return null;
            var mat = new Material(sh);

            // Texture (relative to the mtl folder)
            Texture2D tex = null;
            if (def != null && !string.IsNullOrEmpty(def.DiffuseMap))
            {
                string resolved = ResolveTexturePath(mtlPath, def.DiffuseMap);
                if (resolved != null)
                {
                    tex = LoadTextureFromFile(resolved);
                    if (tex == null)
                        Log.LogWarning("Path resolved but loading failed: " + resolved);
                }
                else
                {
                    Log.LogWarning("MTL texture not found: '" + def.DiffuseMap + "' (mtl: " + Path.GetFileName(mtlPath) + ")");
                }
            }

            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }

            // Colour = MTL Kd x global tint
            Vector3 kd = def != null ? def.Diffuse : Vector3.one;
            float alpha = def != null ? def.Alpha : 1f;
            Color c = new Color(
                kd.x * CfgTintR.Value,
                kd.y * CfgTintG.Value,
                kd.z * CfgTintB.Value,
                alpha * CfgTintA.Value);
            ApplyBaseColor(mat, c);
            ApplyCommon(mat);

            // Transparency handling
            if (alpha < 0.999f)
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = 3000;
            }

            _mtlMatCache[key] = mat;
            return mat;
        }

        // Main entry: pick a material from the obj path, mtl list and material name
        public static Material GetMaterialForSubmesh(
            string objPath, string[] mtlFilePaths, string matName)
        {
            // Empty name, "__default__" or no mtl -> fall back to the same-named png
            bool isDefault = string.IsNullOrEmpty(matName) || matName == "__default__";
            if (!CfgEnableMtl.Value || isDefault || mtlFilePaths == null || mtlFilePaths.Length == 0)
                return GetMaterialFor(objPath);

            // Look for this material in each mtl
            for (int i = 0; i < mtlFilePaths.Length; i++)
            {
                var defs = MtlLoader.Load(mtlFilePaths[i]);
                if (defs == null) continue;
                MtlMaterialDef def;
                if (defs.TryGetValue(matName, out def))
                    return GetMtlMaterial(mtlFilePaths[i], def);
            }

            // Not found -> fall back
            return GetMaterialFor(objPath);
        }

        public static void RefreshMaterials()
        {
            foreach (var kv in _matCache) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _matCache.Clear();
            foreach (var kv in _mtlMatCache) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _mtlMatCache.Clear();
            foreach (var kv in _texCache) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _texCache.Clear();
            MtlLoader.ClearCache();
            if (_solidMat != null) { UnityEngine.Object.Destroy(_solidMat); _solidMat = null; }
        }

        // ==================== Wireframe ====================

        public static void RefreshWireframe()
        {
            bool show = CfgShowWireframe.Value;
            Color c = new Color(CfgWireR.Value, CfgWireG.Value, CfgWireB.Value, CfgWireAlpha.Value);
            if (WireMat != null)
            {
                if (WireMat.HasProperty("_BaseColor")) WireMat.SetColor("_BaseColor", c);
                if (WireMat.HasProperty("_Color")) WireMat.SetColor("_Color", c);
            }
            for (int i = WireObjects.Count - 1; i >= 0; i--)
            {
                if (WireObjects[i] == null) { WireObjects.RemoveAt(i); continue; }
                if (WireObjects[i].activeSelf != show) WireObjects[i].SetActive(show);
            }
        }

        static void EnsureWireResources()
        {
            if (WireMat == null)
            {
                var sh = GetShader();
                if (sh == null) return;
                WireMat = new Material(sh);
                Color c = new Color(CfgWireR.Value, CfgWireG.Value, CfgWireB.Value, CfgWireAlpha.Value);
                if (WireMat.HasProperty("_Surface")) WireMat.SetFloat("_Surface", 1f);
                if (WireMat.HasProperty("_SrcBlend")) WireMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (WireMat.HasProperty("_DstBlend")) WireMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (WireMat.HasProperty("_ZWrite")) WireMat.SetFloat("_ZWrite", 0f);
                ApplyBaseColor(WireMat, c);
                ApplyCommon(WireMat);
                WireMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                WireMat.renderQueue = 3000;
            }
        }

        public static GameObject CreateWireFor(Transform parent, BoxCollider bc)
        {
            EnsureWireResources();
            if (WireMat == null) return null;

            GameObject wire = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wire.name = "Wire_" + parent.name;
            var autoCol = wire.GetComponent<Collider>();
            if (autoCol != null) UnityEngine.Object.Destroy(autoCol);

            wire.transform.SetParent(parent, false);
            wire.transform.localPosition = bc.center;
            wire.transform.localRotation = Quaternion.identity;
            wire.transform.localScale = bc.size;
            wire.layer = parent.gameObject.layer;

            var mr = wire.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = WireMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.allowOcclusionWhenDynamic = false;
            }
            wire.SetActive(CfgShowWireframe.Value);
            WireObjects.Add(wire);
            return wire;
        }

        // ==================== Utilities ====================

        public static Terrain FindReferenceTerrain()
        {
            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return null;
            string want = CfgRefTerrain.Value;
            foreach (var t in terrains)
                if (t != null && t.name == want) return t;
            return null;
        }

        public static string[] ListTerrains()
        {
            var terrains = Terrain.activeTerrains;
            if (terrains == null) return new string[0];
            var list = new List<string>();
            foreach (var t in terrains)
                if (t != null) list.Add(t.name);
            return list.ToArray();
        }

        static Type FindType(string fullName)
        {
            var t = AccessTools.TypeByName(fullName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        public static Component GetFlightScene()
        {
            if (_pFlightSceneInstance == null)
            {
                _tFlightScene = FindType("Assets.Scripts.Flight.FlightSceneScript");
                if (_tFlightScene != null)
                    _pFlightSceneInstance = _tFlightScene.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
            }
            if (_pFlightSceneInstance == null) return null;
            try { return _pFlightSceneInstance.GetValue(null) as Component; }
            catch { return null; }
        }

        public static Camera FindActiveCamera()
        {
            if (_tFlightScene == null)
                _tFlightScene = FindType("Assets.Scripts.Flight.FlightSceneScript");
            if (_pCameraManagerMainCamera == null && _tFlightScene != null)
            {
                var tCamMgr = FindType("Assets.Scripts.Flight.Cameras.CameraManagerScript");
                if (tCamMgr != null)
                    _pCameraManagerMainCamera = tCamMgr.GetProperty("MainCamera",
                        BindingFlags.Public | BindingFlags.Instance);
            }
            if (_tFlightScene != null && _pCameraManagerMainCamera != null)
            {
                try
                {
                    var fs = GetFlightScene();
                    if (fs != null)
                    {
                        var csP = _tFlightScene.GetProperty("CameraScript",
                            BindingFlags.Public | BindingFlags.Instance);
                        var cs = csP != null ? csP.GetValue(fs) : null;
                        if (cs != null)
                        {
                            var mc = _pCameraManagerMainCamera.GetValue(cs) as Camera;
                            if (mc != null && mc.isActiveAndEnabled) return mc;
                        }
                    }
                }
                catch { }
            }
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled) return main;
            return null;
        }

        public static string AxisName(int m)
        {
            switch (m)
            {
                case 0: return "( x,  y,  z)";
                case 1: return "( x,  z,  y)";
                case 2: return "( x,  z, -y)";
                case 3: return "( x, -z,  y)";
                case 4: return "( x, -y,  z)";
                case 5: return "( x, -y, -z)";
                case 6: return "( x,  y, -z)";
                case 7: return "(-x,  y,  z)";
                case 8: return "(-x,  y, -z)";
                case 9: return "(-x, -y, -z)";
                default: return "?";
            }
        }

        public static string ColliderName(int m)
        {
            switch (m)
            {
                case 0: return "No collision";
                case 1: return "Convex mesh collider";
                case 2: return "Axis-aligned box";
                case 3: return "Precise AABB box";
                case 4: return "Non-convex precise mesh collider";
                default: return "?";
            }
        }
    }

    // =================================================================
    // MTL material definition
    // =================================================================
    public class MtlMaterialDef
    {
        public string Name;
        public Vector3 Diffuse = Vector3.one;
        public float Alpha = 1f;
        public string DiffuseMap;
        public string NormalMap;
    }

    // =================================================================
    // MTL loader (cached + thread-safe)
    // =================================================================
    public static class MtlLoader
    {
        static readonly Dictionary<string, Dictionary<string, MtlMaterialDef>> _cache
            = new Dictionary<string, Dictionary<string, MtlMaterialDef>>(StringComparer.OrdinalIgnoreCase);
        static readonly object _lock = new object();

        public static void ClearCache()
        {
            lock (_lock) _cache.Clear();
        }

        public static Dictionary<string, MtlMaterialDef> Load(string mtlPath)
        {
            lock (_lock)
            {
                Dictionary<string, MtlMaterialDef> c;
                if (_cache.TryGetValue(mtlPath, out c)) return c;
            }

            var result = new Dictionary<string, MtlMaterialDef>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(mtlPath))
                {
                    MtlMaterialDef cur = null;
                    string[] lines = File.ReadAllLines(mtlPath);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line[0] == '#') continue;

                        string[] p = line.Split(new char[] { ' ', '\t' },
                            StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length == 0) continue;
                        string key = p[0].ToLowerInvariant();

                        switch (key)
                        {
                            case "newmtl":
                                cur = new MtlMaterialDef { Name = Rest(p, 1) };
                                result[cur.Name] = cur;
                                break;

                            case "kd":
                                if (cur != null && p.Length >= 4)
                                {
                                    float r, g, b;
                                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
                                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out g);
                                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
                                    cur.Diffuse = new Vector3(r, g, b);
                                }
                                break;

                            case "d":
                                if (cur != null && p.Length >= 2)
                                {
                                    float a;
                                    if (float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                                        cur.Alpha = a;
                                }
                                break;

                            case "tr":
                                if (cur != null && p.Length >= 2)
                                {
                                    float a;
                                    if (float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                                        cur.Alpha = 1f - a;
                                }
                                break;

                            case "map_kd":
                                if (cur != null && p.Length >= 2)
                                {
                                    cur.DiffuseMap = RestTrimmed(p, 1);
                                    Plugin.Log.LogInfo("  MTL " + cur.Name + " map_Kd = " + cur.DiffuseMap);
                                }
                                break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("MTL parse failed " + mtlPath + ": " + e.Message);
            }

            lock (_lock) _cache[mtlPath] = result;
            return result;
        }

        static string Rest(string[] p, int start)
        {
            if (start >= p.Length) return "";
            if (start == p.Length - 1) return p[start];
            return string.Join(" ", p, start, p.Length - start);
        }

        // Handle map_Kd with optional arguments (e.g. -s 1 1 1): take only the trailing path
        static string RestTrimmed(string[] p, int start)
        {
            // Simple strategy: scan from start for the last token that looks like a file name
            for (int i = p.Length - 1; i >= start; i--)
            {
                string t = p[i];
                if (t.IndexOf('.') >= 0 || t.IndexOf('/') >= 0 || t.IndexOf('\\') >= 0)
                    return t.Trim('"');
            }
            // Otherwise: join everything after start
            return Rest(p, start).Trim('"');
        }
    }

    // =================================================================
    // One chunk of data
    // =================================================================
    public class ParsedChunk
    {
        public string ObjPath;
        public string ChunkName;
        public Vector3[] Verts;
        public Vector2[] UVs;
        public bool HasUV;

        // Multiple submeshes: each chunk may hold several material groups
        public int[] SubmeshTris;            // Concatenated triangles of all submeshes
        public int[] SubmeshStarts;          // Start offset of each submesh; length = submeshCount + 1
        public string[] SubmeshMaterialNames; // Material name of each submesh (the OBJ usemtl)

        public string[] MtlFilePaths;        // Absolute paths of the mtl files referenced by the obj
    }

    // =================================================================
    // Async loader
    // =================================================================
    public class AsyncLoader : MonoBehaviour
    {
        readonly ConcurrentQueue<ParsedChunk> _ready = new ConcurrentQueue<ParsedChunk>();
        readonly Queue<GameObject> _pendingActivate = new Queue<GameObject>();

        enum Phase { Idle, Meshes, Activating, Done }
        Phase _phase = Phase.Idle;

        string[] _files;
        int _nextFileIndex;
        int _pendingFiles;
        int _spawned;
        int _failed;
        int _withTex;
        int _noTex;
        int _verticesTotal;
        int _activated;
        int _generation;
        float _startTime;
        float _lastLogTime;

        public void Begin(string[] files)
        {
            _files = files;
            _nextFileIndex = 0;
            _pendingFiles = 0;
            _spawned = 0;
            _failed = 0;
            _withTex = 0;
            _noTex = 0;
            _verticesTotal = 0;
            _activated = 0;
            _generation++;
            _startTime = Time.realtimeSinceStartup;
            _phase = Phase.Meshes;

            Plugin.CurrentPhase = "Building meshes";
            Plugin.Log.LogInfo("Loading " + files.Length + " OBJ files (with chunking + MTL)");
        }

        void Update()
        {
            if (_phase == Phase.Idle || _phase == Phase.Done) return;

            int maxConcurrent = Mathf.Max(1, Plugin.CfgMaxConcurrentParse.Value);
            while (_nextFileIndex < _files.Length && _pendingFiles < maxConcurrent)
            {
                string path = _files[_nextFileIndex++];
                Interlocked.Increment(ref _pendingFiles);
                int gen = _generation;
                Task.Run(() => ParseInBackground(path, gen));
            }

            if (_phase == Phase.Meshes) DoMeshPhase();
            else if (_phase == Phase.Activating) DoActivatePhase();

            if (Time.realtimeSinceStartup - _lastLogTime > 0.5f)
            {
                _lastLogTime = Time.realtimeSinceStartup;
                Plugin.LastPendingCount = _pendingFiles;

                if (_phase == Phase.Meshes)
                    Plugin.StatusMsg = string.Format("Building meshes: {0} chunks (pendingFiles {1})",
                        _spawned, _pendingFiles);
                else if (_phase == Phase.Activating)
                    Plugin.StatusMsg = string.Format("Activating {0}/{1}", _activated, _spawned);
            }
        }

        void DoMeshPhase()
        {
            int remaining = _files.Length - _spawned - _failed;
            int spawnBudget = Mathf.Max(1, Plugin.CfgSpawnPerFrame.Value);
            int smoothTail = Plugin.CfgSmoothTailCount.Value;
            if (smoothTail > 0 && remaining <= smoothTail) spawnBudget = 1;
            int vertBudget = Mathf.Max(1000, Plugin.CfgMaxMeshVerticesPerFrame.Value);

            while (spawnBudget > 0 && vertBudget > 0 && _ready.TryDequeue(out var chunk))
            {
                int vcount = chunk.Verts != null ? chunk.Verts.Length : 0;
                try
                {
                    var go = SpawnChunk(chunk);
                    _spawned++;
                    _verticesTotal += vcount;
                    _pendingActivate.Enqueue(go);
                    spawnBudget--;
                    vertBudget -= vcount;
                }
                catch (Exception e)
                {
                    _failed++;
                    Plugin.Log.LogError("Spawn failed " + chunk.ChunkName + ": " + e.Message);
                }
            }

            if (_nextFileIndex >= _files.Length && Volatile.Read(ref _pendingFiles) == 0 && _ready.IsEmpty)
            {
                _phase = Phase.Activating;
                Plugin.CurrentPhase = "Activating";
                Plugin.Log.LogInfo(string.Format(
                    "Mesh phase complete ({0} chunks); activating frame by frame ({1} per frame)",
                    _spawned, Plugin.CfgActivatePerFrame.Value));
            }
        }

        void DoActivatePhase()
        {
            int perFrame = Mathf.Max(1, Plugin.CfgActivatePerFrame.Value);
            int n = 0;
            while (n < perFrame && _pendingActivate.Count > 0)
            {
                var go = _pendingActivate.Dequeue();
                if (go != null) go.SetActive(true);
                _activated++;
                n++;
            }
            if (_pendingActivate.Count == 0) Finish();
        }

        void ParseInBackground(string path, int gen)
        {
            try
            {
                int chunkTris = Plugin.CfgChunkTriangles.Value;
                List<ParsedChunk> chunks = ObjParser.ParseToChunks(path,
                    Plugin.CfgAxisMode.Value,
                    Plugin.CfgFlipWinding.Value,
                    chunkTris);

                if (gen != _generation) return;
                foreach (var c in chunks) _ready.Enqueue(c);
            }
            catch (Exception e)
            {
                if (gen == _generation)
                    Plugin.Log.LogWarning("Parse failed " + path + ": " + e.Message);
            }
            finally
            {
                if (gen == _generation)
                    Interlocked.Decrement(ref _pendingFiles);
            }
        }

        GameObject SpawnChunk(ParsedChunk c)
        {
            if (c.Verts == null || c.Verts.Length == 0)
                throw new Exception("empty chunk");

            var mesh = new Mesh();
            mesh.name = c.ChunkName;
            if (c.Verts.Length > 65000)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(c.Verts);
            if (c.HasUV && c.UVs != null && c.UVs.Length == c.Verts.Length)
                mesh.SetUVs(0, c.UVs);

            int numSub = c.SubmeshMaterialNames != null ? c.SubmeshMaterialNames.Length : 0;
            if (numSub <= 0)
            {
                // Should not happen; safety net
                mesh.SetTriangles(c.SubmeshTris, 0);
                numSub = 1;
            }
            else
            {
                mesh.subMeshCount = numSub;
                for (int i = 0; i < numSub; i++)
                {
                    int s = c.SubmeshStarts[i];
                    int e = c.SubmeshStarts[i + 1];
                    int count = e - s;
                    if (count <= 0)
                    {
                        mesh.SetTriangles(new int[0], i);
                        continue;
                    }
                    int[] slice = new int[count];
                    Array.Copy(c.SubmeshTris, s, slice, 0, count);
                    mesh.SetTriangles(slice, i);
                }
            }

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            GameObject go = new GameObject(c.ChunkName);
            go.transform.SetParent(transform, false);
            SetLayerRecursive(go.transform, Plugin.CfgRailLayer.Value);
            go.SetActive(false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            // Multiple materials
            var mats = new Material[numSub];
            bool hasAnyTex = false;
            for (int i = 0; i < numSub; i++)
            {
                string matName = (c.SubmeshMaterialNames != null && i < c.SubmeshMaterialNames.Length)
                    ? c.SubmeshMaterialNames[i] : null;
                var m = Plugin.GetMaterialForSubmesh(c.ObjPath, c.MtlFilePaths, matName);
                mats[i] = m;
                if (m != null)
                {
                    bool hasTex = (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null)
                               || (m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null);
                    if (hasTex) hasAnyTex = true;
                }
            }
            if (hasAnyTex) _withTex++; else _noTex++;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            AddCollider(go, mesh);
            return go;
        }

        void AddCollider(GameObject go, Mesh mesh)
        {
            int mode = Plugin.CfgColliderMode.Value;
            if (mode == 1)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = true;
            }
            else if (mode == 2 || mode == 3)
            {
                var bc = go.AddComponent<BoxCollider>();
                Vector3 ctr = mesh.bounds.center;
                ctr.y += Plugin.CfgColliderOffsetY.Value;
                bc.center = ctr;
                float s = Plugin.CfgColliderSizeScale.Value;
                bc.size = new Vector3(
                    mesh.bounds.size.x * s,
                    mesh.bounds.size.y * s,
                    mesh.bounds.size.z * s);
                Plugin.CreateWireFor(go.transform, bc);
            }
            else if (mode == 4)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = false;
                mc.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                                  | MeshColliderCookingOptions.EnableMeshCleaning
                                  | MeshColliderCookingOptions.WeldColocatedVertices;
            }
        }

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t) SetLayerRecursive(c, layer);
        }

        void Finish()
        {
            float elapsed = Time.realtimeSinceStartup - _startTime;
            Plugin.LastLoadedCount = _spawned;
            Plugin.LastFailedCount = _failed;
            Plugin.LastPendingCount = 0;
            Plugin.CurrentPhase = "Done";
            _phase = Phase.Done;

            Plugin.Log.LogInfo(string.Format(
                "All done: {0} chunks, {1} failed, textured {2}, untextured {3}, vertices {4}, elapsed {5:F2}s",
                _spawned, _failed, _withTex, _noTex, _verticesTotal, elapsed));

            Plugin.StatusMsg = string.Format("Done: {0} chunks", _spawned);

            float delay = Mathf.Max(0f, Plugin.CfgDelayGCSeconds.Value);
            if (delay <= 0f) DoGc(0);
            else StartCoroutine(DelayedGc(delay));

            _files = null;
        }

        IEnumerator DelayedGc(float d)
        {
            yield return new WaitForSecondsRealtime(d);
            Plugin.Log.LogInfo("[GC] Gen0");
            DoGc(0);
            yield return new WaitForSecondsRealtime(2f);
            Plugin.Log.LogInfo("[GC] Gen1");
            DoGc(1);
            yield return new WaitForSecondsRealtime(2f);
            Plugin.Log.LogInfo("[GC] Gen2");
            DoGc(2);
        }

        void DoGc(int gen)
        {
            try { GC.Collect(gen, GCCollectionMode.Optimized); } catch { }
        }

        void OnDestroy()
        {
            _generation++;
            _phase = Phase.Idle;
        }
    }

    // =================================================================
    // Frame time monitor
    // =================================================================
    public class FrameSpikeLogger : MonoBehaviour
    {
        float _maxFrameThisWindow;
        float _windowStart;
        int _spikeCount;
        const float WINDOW = 2f;
        const float SPIKE_THRESHOLD = 0.030f;

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > _maxFrameThisWindow) _maxFrameThisWindow = dt;

            if (Time.unscaledTime - _windowStart > WINDOW)
            {
                if (_maxFrameThisWindow > SPIKE_THRESHOLD)
                {
                    _spikeCount++;
                    Plugin.Log.LogWarning(string.Format(
                        "[Spike #{0}] last {1:F1}s max frame {2:F1}ms  phase: {3}",
                        _spikeCount, WINDOW, _maxFrameThisWindow * 1000f, Plugin.CurrentPhase));
                }
                _maxFrameThisWindow = 0f;
                _windowStart = Time.unscaledTime;
            }
        }
    }

    // =================================================================
    // OBJ parser (MTL + chunking + multiple submeshes)
    // =================================================================
    public static class ObjParser
    {
        static Vector3 ConvertAxis(float x, float y, float z, int mode)
        {
            switch (mode)
            {
                case 0: return new Vector3(x, y, z);
                case 1: return new Vector3(x, z, y);
                case 2: return new Vector3(x, z, -y);
                case 3: return new Vector3(x, -z, y);
                case 4: return new Vector3(x, -y, z);
                case 5: return new Vector3(x, -y, -z);
                case 6: return new Vector3(x, y, -z);
                case 7: return new Vector3(-x, y, z);
                case 8: return new Vector3(-x, y, -z);
                case 9: return new Vector3(-x, -y, -z);
                default: return new Vector3(x, y, z);
            }
        }

        static void ParseFaceToken(string token, CultureInfo ci, out int vIdx, out int vtIdx)
        {
            vIdx = -1; vtIdx = -1;
            int s1 = token.IndexOf('/');
            if (s1 < 0) { vIdx = int.Parse(token, ci); return; }
            vIdx = int.Parse(token.Substring(0, s1), ci);
            int s2 = token.IndexOf('/', s1 + 1);
            if (s2 < 0)
            {
                string vtStr = token.Substring(s1 + 1);
                if (vtStr.Length > 0) vtIdx = int.Parse(vtStr, ci);
            }
            else if (s2 > s1 + 1)
            {
                string vtStr = token.Substring(s1 + 1, s2 - s1 - 1);
                if (vtStr.Length > 0) vtIdx = int.Parse(vtStr, ci);
            }
        }

        public static List<ParsedChunk> ParseToChunks(
            string path, int axisMode, bool flipWinding, int maxTrisPerChunk)
        {
            string baseName = Path.GetFileNameWithoutExtension(path);
            string objDir = Path.GetDirectoryName(path);

            var positions = new List<Vector3>(8192);
            var texcoords = new List<Vector2>(8192);
            string[] lines = File.ReadAllLines(path);
            CultureInfo ci = CultureInfo.InvariantCulture;
            bool anyUV = false;

            // Triangles (keeping the mtl index)
            var triA = new List<int>(16384);
            var triB = new List<int>(16384);
            var triC = new List<int>(16384);
            var triTA = new List<int>(16384);
            var triTB = new List<int>(16384);
            var triTC = new List<int>(16384);
            var triMtl = new List<int>(16384);

            // MTL name pool
            var mtlNames = new List<string>();
            var mtlNameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            mtlNames.Add("__default__");
            mtlNameToIdx["__default__"] = 0;
            int curMtl = 0;

            // Referenced MTL files
            var mtlFiles = new List<string>();
            var mtlFileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string key = line.Substring(0, sp).ToLowerInvariant();
                string rest = line.Substring(sp + 1).Trim();

                if (key == "v")
                {
                    string[] p = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3)
                        positions.Add(ConvertAxis(
                            float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci), axisMode));
                }
                else if (key == "vt")
                {
                    string[] p = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 2)
                        texcoords.Add(new Vector2(float.Parse(p[0], ci), float.Parse(p[1], ci)));
                    else if (p.Length == 1)
                        texcoords.Add(new Vector2(float.Parse(p[0], ci), 0f));
                }
                else if (key == "mtllib")
                {
                    if (Plugin.CfgEnableMtl.Value && objDir != null)
                    {
                        string[] parts = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < parts.Length; i++)
                        {
                            string fname = parts[i].Trim('"');
                            string abs = Path.GetFullPath(Path.Combine(objDir, fname));
                            if (mtlFileSet.Add(abs)) mtlFiles.Add(abs);
                        }
                    }
                }
                else if (key == "usemtl")
                {
                    string name = rest.Trim('"');
                    if (name.Length == 0) name = "__default__";

                    int idx;
                    if (!mtlNameToIdx.TryGetValue(name, out idx))
                    {
                        idx = mtlNames.Count;
                        mtlNameToIdx[name] = idx;
                        mtlNames.Add(name);
                    }
                    curMtl = idx;
                }
                else if (key == "f")
                {
                    string[] p = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 3) continue;

                    int n = p.Length;
                    int[] vIdx = new int[n];
                    int[] vtIdx = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        int v, vt;
                        ParseFaceToken(p[i], ci, out v, out vt);
                        if (v < 0) v = positions.Count + v; else v = v - 1;
                        if (vt > 0) vt = vt - 1;
                        else if (vt < 0) vt = texcoords.Count + vt;
                        vIdx[i] = v;
                        vtIdx[i] = vt;
                        if (vt >= 0 && vt < texcoords.Count) anyUV = true;
                    }

                    for (int i = 1; i < n - 1; i++)
                    {
                        int a, b, c, ta, tb, tc;
                        if (flipWinding)
                        {
                            a = vIdx[0]; b = vIdx[i + 1]; c = vIdx[i];
                            ta = vtIdx[0]; tb = vtIdx[i + 1]; tc = vtIdx[i];
                        }
                        else
                        {
                            a = vIdx[0]; b = vIdx[i]; c = vIdx[i + 1];
                            ta = vtIdx[0]; tb = vtIdx[i]; tc = vtIdx[i + 1];
                        }
                        triA.Add(a); triB.Add(b); triC.Add(c);
                        triTA.Add(ta); triTB.Add(tb); triTC.Add(tc);
                        triMtl.Add(curMtl);
                    }
                }
            }

            var result = new List<ParsedChunk>();
            int totalTris = triA.Count;
            if (totalTris == 0)
            {
                Plugin.Log.LogWarning("Parsed empty: " + path);
                return result;
            }

            int chunkSize = maxTrisPerChunk <= 0 ? totalTris : maxTrisPerChunk;
            int numChunks = (totalTris + chunkSize - 1) / chunkSize;

            string[] mtlPathsArr = mtlFiles.ToArray();

            for (int cIdx = 0; cIdx < numChunks; cIdx++)
            {
                int startTri = cIdx * chunkSize;
                int endTri = Math.Min(startTri + chunkSize, totalTris);
                int triCount = endTri - startTri;

                // Unique material indices used by this chunk
                var localMtl = new List<int>();
                var seen = new HashSet<int>();
                for (int t = startTri; t < endTri; t++)
                {
                    if (seen.Add(triMtl[t])) localMtl.Add(triMtl[t]);
                }
                localMtl.Sort();

                int numSub = localMtl.Count;
                var mtlToSub = new Dictionary<int, int>(numSub);
                for (int i = 0; i < numSub; i++) mtlToSub[localMtl[i]] = i;

                // Bucket by submesh
                var subTriLists = new List<int>[numSub];
                for (int i = 0; i < numSub; i++) subTriLists[i] = new List<int>(triCount);
                for (int t = startTri; t < endTri; t++)
                    subTriLists[mtlToSub[triMtl[t]]].Add(t);

                // Build the output
                var outVerts = new List<Vector3>(triCount * 3);
                var outUVs = new List<Vector2>(triCount * 3);
                var subTris = new List<int>(triCount * 3);
                var subStarts = new int[numSub + 1];
                var subNames = new string[numSub];

                for (int s = 0; s < numSub; s++)
                {
                    subStarts[s] = subTris.Count;
                    subNames[s] = mtlNames[localMtl[s]];
                    var lst = subTriLists[s];
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int t = lst[k];
                        int baseIdx = outVerts.Count;
                        outVerts.Add(positions[triA[t]]); outUVs.Add(SafeUV(texcoords, triTA[t]));
                        outVerts.Add(positions[triB[t]]); outUVs.Add(SafeUV(texcoords, triTB[t]));
                        outVerts.Add(positions[triC[t]]); outUVs.Add(SafeUV(texcoords, triTC[t]));
                        subTris.Add(baseIdx);
                        subTris.Add(baseIdx + 1);
                        subTris.Add(baseIdx + 2);
                    }
                }
                subStarts[numSub] = subTris.Count;

                result.Add(new ParsedChunk
                {
                    ObjPath = path,
                    ChunkName = baseName + "_c" + cIdx.ToString("D3"),
                    Verts = outVerts.ToArray(),
                    UVs = anyUV ? outUVs.ToArray() : null,
                    HasUV = anyUV,
                    SubmeshTris = subTris.ToArray(),
                    SubmeshStarts = subStarts,
                    SubmeshMaterialNames = subNames,
                    MtlFilePaths = mtlPathsArr,
                });
            }

            return result;
        }

        static Vector2 SafeUV(List<Vector2> uvs, int idx)
        {
            if (idx >= 0 && idx < uvs.Count) return uvs[idx];
            return Vector2.zero;
        }
    }

    // =================================================================
    // GUI
    // =================================================================
    public class LoaderGui : MonoBehaviour
    {
        bool _visible = false;
        Rect _rect = new Rect(20f, 20f, 660f, 1080f);
        int _id = 0x5A3B21;
        string[] _terrainList;
        Vector2 _terrainScroll;
        Vector2 _mainScroll;

        string _refTerrainBuf;
        string _offsetXBuf, _offsetYBuf, _offsetZBuf;

        void Update()
        {
            if (Input.GetKeyDown(Plugin.CfgKeyGui.Value)) _visible = !_visible;
        }

        void OnGUI()
        {
            if (!_visible) return;
            _rect = GUILayout.Window(_id, _rect, DrawWindow, "Universal World Model Loader 7.0.0");
        }

        void DrawWindow(int id)
        {
            _mainScroll = GUILayout.BeginScrollView(_mainScroll);

            GUILayout.Label("=== Status ===");
            GUILayout.Label("Phase:      " + Plugin.CurrentPhase);
            GUILayout.Label("Status:     " + Plugin.StatusMsg);
            GUILayout.Label("Loaded:     " + Plugin.LastLoadedCount + " chunks, " + Plugin.LastFailedCount + " failed");
            GUILayout.Label("Pending:    " + Plugin.LastPendingCount);
            GUILayout.Label("Root:       " + (Plugin.Instance.Root != null ? "yes" : "no"));

            var cam = Plugin.FindActiveCamera();
            GUILayout.Label("Cam: " + (cam != null ? cam.transform.position.ToString() : "none"));

            GUILayout.Space(8);

            GUILayout.Label("=== Textures / Materials ===");
            bool tex = Plugin.CfgEnableTextures.Value;
            bool newTex = GUILayout.Toggle(tex, " Enable textures");
            if (newTex != tex)
            {
                Plugin.CfgEnableTextures.Value = newTex;
                Plugin.RefreshMaterials();
                Plugin.Instance.LoadAll();
            }
            bool mtl = Plugin.CfgEnableMtl.Value;
            bool newMtl = GUILayout.Toggle(mtl, " Parse MTL (map_Kd)");
            if (newMtl != mtl)
            {
                Plugin.CfgEnableMtl.Value = newMtl;
                Plugin.RefreshMaterials();
                Plugin.Instance.LoadAll();
            }
            if (GUILayout.Button("Refresh materials and reload"))
            {
                Plugin.RefreshMaterials();
                Plugin.Instance.LoadAll();
            }

            GUILayout.Space(8);

            GUILayout.Label("=== Performance ===");
            GUILayout.Label("Chunk triangles: " + Plugin.CfgChunkTriangles.Value);
            float ck = GUILayout.HorizontalSlider(Plugin.CfgChunkTriangles.Value, 500f, 50000f);
            Plugin.CfgChunkTriangles.Value = Mathf.RoundToInt(ck);

            GUILayout.Label("Spawn per frame: " + Plugin.CfgSpawnPerFrame.Value);
            float sf = GUILayout.HorizontalSlider(Plugin.CfgSpawnPerFrame.Value, 1f, 50f);
            Plugin.CfgSpawnPerFrame.Value = Mathf.RoundToInt(sf);

            GUILayout.Label("Vertices per frame: " + Plugin.CfgMaxMeshVerticesPerFrame.Value);
            float mv = GUILayout.HorizontalSlider(Plugin.CfgMaxMeshVerticesPerFrame.Value, 5000f, 2000000f);
            Plugin.CfgMaxMeshVerticesPerFrame.Value = Mathf.RoundToInt(mv);

            GUILayout.Label("Activate per frame: " + Plugin.CfgActivatePerFrame.Value);
            float apf = GUILayout.HorizontalSlider(Plugin.CfgActivatePerFrame.Value, 1f, 20f);
            Plugin.CfgActivatePerFrame.Value = Mathf.RoundToInt(apf);

            GUILayout.Label("Concurrency: " + Plugin.CfgMaxConcurrentParse.Value);
            float mc = GUILayout.HorizontalSlider(Plugin.CfgMaxConcurrentParse.Value, 1f, 16f);
            Plugin.CfgMaxConcurrentParse.Value = Mathf.RoundToInt(mc);

            GUILayout.Label("Delayed GC: " + Plugin.CfgDelayGCSeconds.Value + " s");
            float dgc = GUILayout.HorizontalSlider(Plugin.CfgDelayGCSeconds.Value, 0f, 10f);
            Plugin.CfgDelayGCSeconds.Value = Mathf.RoundToInt(dgc);

            GUILayout.Space(8);

            GUILayout.Label("=== Winding ===");
            bool fw = Plugin.CfgFlipWinding.Value;
            bool newFw = GUILayout.Toggle(fw, " Flip winding");
            if (newFw != fw)
            {
                Plugin.CfgFlipWinding.Value = newFw;
                Plugin.Instance.LoadAll();
            }

            GUILayout.Space(8);

            GUILayout.Label("=== Placement ===");
            GUILayout.BeginHorizontal();
            GUILayout.Label("RefTerrain:", GUILayout.Width(80));
            if (_refTerrainBuf == null) _refTerrainBuf = Plugin.CfgRefTerrain.Value;
            _refTerrainBuf = GUILayout.TextField(_refTerrainBuf, GUILayout.Width(180));
            GUILayout.EndHorizontal();

            GUILayout.Label("AxisMode: " + Plugin.CfgAxisMode.Value);
            GUILayout.BeginHorizontal();
            for (int i = 0; i <= 9; i++)
            {
                if (i == 5) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                bool cur = (Plugin.CfgAxisMode.Value == i);
                string lbl = (cur ? "●" : "○") + i;
                if (GUILayout.Button(lbl, GUILayout.Width(42)))
                    Plugin.CfgAxisMode.Value = i;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Scale: " + Plugin.CfgScale.Value.ToString("F4"));
            Plugin.CfgScale.Value = GUILayout.HorizontalSlider(Plugin.CfgScale.Value, 0.0001f, 100f);

            GUILayout.Label("Offset X / Y / Z");
            GUILayout.BeginHorizontal();
            if (_offsetXBuf == null) _offsetXBuf = Plugin.CfgOffsetX.Value.ToString("F2");
            if (_offsetYBuf == null) _offsetYBuf = Plugin.CfgOffsetY.Value.ToString("F2");
            if (_offsetZBuf == null) _offsetZBuf = Plugin.CfgOffsetZ.Value.ToString("F2");
            GUILayout.Label("X:", GUILayout.Width(20));
            _offsetXBuf = GUILayout.TextField(_offsetXBuf, GUILayout.Width(80));
            GUILayout.Label("Y:", GUILayout.Width(20));
            _offsetYBuf = GUILayout.TextField(_offsetYBuf, GUILayout.Width(80));
            GUILayout.Label("Z:", GUILayout.Width(20));
            _offsetZBuf = GUILayout.TextField(_offsetZBuf, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            GUILayout.Label("=== Collision ===");
            GUILayout.Label("Mode: " + Plugin.CfgColliderMode.Value + " -> " + Plugin.ColliderName(Plugin.CfgColliderMode.Value));
            GUILayout.BeginHorizontal();
            for (int i = 0; i <= 4; i++)
            {
                bool cur = (Plugin.CfgColliderMode.Value == i);
                string lbl = (cur ? "●" : "○") + i;
                if (GUILayout.Button(lbl, GUILayout.Width(35)))
                    Plugin.CfgColliderMode.Value = i;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            GUILayout.Label("=== Actions ===");
            if (GUILayout.Button("Apply and reload", GUILayout.Height(36)))
            {
                float v;
                if (float.TryParse(_offsetXBuf, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    Plugin.CfgOffsetX.Value = v;
                if (float.TryParse(_offsetYBuf, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    Plugin.CfgOffsetY.Value = v;
                if (float.TryParse(_offsetZBuf, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    Plugin.CfgOffsetZ.Value = v;
                if (!string.IsNullOrEmpty(_refTerrainBuf))
                    Plugin.CfgRefTerrain.Value = _refTerrainBuf;
                Plugin.Instance.LoadAll();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload (F9)")) Plugin.Instance.LoadAll();
            if (GUILayout.Button("Unload (F10)")) Plugin.Instance.Unload();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            if (GUILayout.Button("Log all terrains"))
            {
                _terrainList = Plugin.ListTerrains();
                Plugin.Log.LogInfo("Terrain list (" + _terrainList.Length + "):");
                foreach (var n in _terrainList) Plugin.Log.LogInfo("  " + n);
            }
            if (_terrainList != null && _terrainList.Length > 0)
            {
                _terrainScroll = GUILayout.BeginScrollView(_terrainScroll, GUILayout.Height(80));
                foreach (var n in _terrainList)
                {
                    if (GUILayout.Button(n))
                    {
                        _refTerrainBuf = n;
                        Plugin.CfgRefTerrain.Value = n;
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(6);
            GUILayout.Label("Config file: " + Plugin.Cfg.ConfigFilePath);

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }
    }
}