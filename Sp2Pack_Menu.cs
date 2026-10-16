// ============================================================
//  SP2 Pack — menu entries
// ============================================================

using System.IO;
using UnityEditor;
using UnityEngine;

namespace Sp2Pack
{
    public static class Sp2Menu
    {
        [MenuItem("Tools/SP2 Pack/Open Window %#p", false, 10)]
        static void OpenWindow()
        {
            Sp2Window.Open();
        }

        [MenuItem("Tools/SP2 Pack/Open Assets Folder", false, 30)]
        static void OpenAssets()
        {
            Directory.CreateDirectory(Sp2Window.AssetsDir);
            EditorUtility.RevealInFinder(Sp2Window.AssetsDir);
        }

        [MenuItem("Tools/SP2 Pack/Open Pack Output Folder", false, 31)]
        static void OpenDist()
        {
            Directory.CreateDirectory(Sp2Window.DistDir);
            EditorUtility.RevealInFinder(Sp2Window.DistDir);
        }

        /// <summary>
        /// Headless entry point for CI / command line:
        ///   Unity -batchmode -quit -projectPath &lt;project&gt; -executeMethod Sp2Pack.Sp2Menu.BatchExportScene
        /// </summary>
        public static void BatchExportScene()
        {
            var cfg = Sp2Config.Load();
            var job = Sp2Model.BeginScan(Sp2Model.SceneRoots(), cfg);
            job.RunToCompletion();
            var result = Sp2Exporter.Export(job.Scan, cfg, Sp2Window.AssetsDir, "airport");
            AssetDatabase.Refresh();
            Debug.Log($"[SP2 Pack] batch export: {result.files} meshes, {result.triangles} triangles -> {Sp2Window.Pretty(result.outDir)}");
        }
    }
}
