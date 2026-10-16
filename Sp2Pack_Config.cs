// ============================================================
//  SP2 Pack — export settings (persisted)
// ============================================================

using System;
using UnityEditor;
using UnityEngine;

namespace Sp2Pack
{
    [Serializable]
    public class Sp2Config
    {
        const string PrefKey = "Sp2Pack.Config.v1";

        // ---- target
        public string moduleName = "SP2 Airport";
        public bool includeTerrain = false;
        public bool includeInactive = true;
        public bool includeSkinned = true;

        // ---- optimisation
        public int minTriangles = 4;          // skip objects below this triangle count
        public bool mergeByMaterial = true;   // group by material to cut material switches
        public bool dropHidden = true;        // skip hidden / disabled renderers
        public float maxDistance = 0f;        // >0: keep only objects within this radius of the origin

        // ---- quality
        public int texMaxSize = 2048;         // longest texture edge cap; 0 = keep original
        public bool quantizeTextures = false; // palette-quantise textures to shrink the pack
        public int vertexDecimals = 5;        // vertex decimal places (3..6)
        public bool exportUV = true;

        public static Sp2Config Load()
        {
            try
            {
                string json = EditorPrefs.GetString(PrefKey, null);
                if (!string.IsNullOrEmpty(json))
                {
                    var c = JsonUtility.FromJson<Sp2Config>(json);
                    if (c != null) return c;
                }
            }
            catch { }
            return new Sp2Config();
        }

        public void Save()
        {
            try { EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(this)); }
            catch { }
        }
    }
}
