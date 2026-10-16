// ============================================================
//  SP2 Pack — main editor window
//
//  Tabs: Target / Optimise / Quality / Preview / Export / About.
//
//  Responsiveness / feedback rules:
//    * scanning is cooperative — Sp2ScanJob.Step() runs a few ms
//      per editor frame and resumes next frame;
//    * settings that only affect the estimate never rescan;
//    * the page is refreshed only when no control is dragged
//      (GUIUtility.hotControl == 0);
//    * exporting shows a progress bar and blocks re-entry;
//    * packaging runs as an asynchronous process polled from the
//      update loop, so the editor stays alive and the buttons
//      disable themselves while a package is being built.
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sp2Pack
{
    public class Sp2Window : EditorWindow
    {
        const string TabKey = "Sp2Pack.Window.Tab";
        const double ScanBudgetMs = 3.0;
        const double DebounceSeconds = 0.30;
        const double ClickCooldownSeconds = 1.0;

        enum TargetMode { Selection, SceneRoots }

        Sp2Config _cfg;
        TargetMode _targetMode = TargetMode.Selection;
        int _tab;

        GameObject[] _roots = new GameObject[0];
        Sp2Scan _scan;
        Sp2Estimate _est;
        Sp2ScanJob _job;
        double _rescanAt;
        bool _dirty;
        Sp2ExportResult _lastExport;

        // Busy / packaging state
        bool _busy;
        string _busyText = "";
        double _lastClick;
        Process _proc;
        double _procStart;
        readonly StringBuilder _procLog = new StringBuilder();
        string _packResult;
        bool _packFailed;

        VisualElement _root;
        VisualElement _body;
        VisualElement _page;
        Label _status;
        Button _exportBtn;
        Button _packageBtn;

        // ------------------------------------------------------------ open

        public static void Open()
        {
            var w = GetWindow<Sp2Window>();
            w.titleContent = new GUIContent("SP2 Pack");
            w.minSize = new Vector2(560, 560);
            w.Show();
        }

        public void CreateGUI()
        {
            _cfg = Sp2Config.Load();
            _tab = EditorPrefs.GetInt(TabKey, 0);
            BuildShell();
            EditorApplication.update += OnUpdate;
            RefreshRoots();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.Kill(); } catch { }
            }
            _proc = null;
        }

        void OnSelectionChange()
        {
            if (_targetMode != TargetMode.Selection) return;
            RefreshRoots();
        }

        // ------------------------------------------------------------ update loop

        void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;

            // Poll the packaging process.
            if (_proc != null)
            {
                if (_proc.HasExited)
                    FinishPackage();
                else if (_status != null)
                    _status.text = "Packaging… " + (now - _procStart).ToString("0.0") + "s";
                return;
            }

            if (_job != null && !_job.Done)
            {
                _job.Step(ScanBudgetMs);
                if (_status != null)
                    _status.text = "Scanning… " + Mathf.RoundToInt(_job.Progress * 100f) + "%";

                if (_job.Done) ApplyJob();
                return;
            }

            if (_rescanAt > 0 && now >= _rescanAt)
            {
                _rescanAt = 0;
                StartScan();
                return;
            }

            if (_dirty && GUIUtility.hotControl == 0)
            {
                _dirty = false;
                RefreshPage();
                UpdateStatus();
            }
        }

        void StartScan()
        {
            _job = Sp2Model.BeginScan(_roots, _cfg);
        }

        void ApplyJob()
        {
            _scan = _job.Scan;
            _job = null;
            _est = Sp2Model.Estimate(_scan, _cfg);

            if (GUIUtility.hotControl == 0) { RefreshPage(); UpdateStatus(); }
            else _dirty = true;
        }

        void RefreshRoots()
        {
            _roots = _targetMode == TargetMode.Selection
                ? Sp2Model.CurrentSelection()
                : Sp2Model.SceneRoots();
            RequestRescan();
            _dirty = true;
        }

        void RequestRescan()
        {
            _job = null; // discard any in-flight scan: the target or settings changed
            _rescanAt = EditorApplication.timeSinceStartup + DebounceSeconds;
        }

        /// <summary>Runs the scan synchronously with a progress bar; used by export, which needs full data.</summary>
        void EnsureScan()
        {
            if (_rescanAt > 0) { _rescanAt = 0; StartScan(); }
            if (_job == null) StartScan();

            try
            {
                while (!_job.Done)
                {
                    _job.Step(20.0);
                    EditorUtility.DisplayProgressBar("SP2 Pack", "Scanning the scene…", _job.Progress);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            ApplyJob();
        }

        void UpdateStatus()
        {
            if (_status == null) return;
            if (_busy) { _status.text = _busyText; return; }
            if (_scan == null) { _status.text = "Idle"; return; }
            _status.text = _scan.objects + " objects · " + Sp2Style.Count(_scan.totalTriangles) + " tris · "
                + _scan.materials.Count + " materials · " + _scan.textures.Count + " textures";
        }

        void SetBusy(bool busy, string text)
        {
            _busy = busy;
            _busyText = text ?? "";
            if (_exportBtn != null) _exportBtn.SetEnabled(!busy);
            if (_packageBtn != null) _packageBtn.SetEnabled(!busy);
            UpdateStatus();
        }

        /// <summary>Guards against double clicks: true means "ignore this click".</summary>
        bool GuardClick()
        {
            if (_busy) return true;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastClick < ClickCooldownSeconds) return true;
            _lastClick = now;
            return false;
        }

        // ------------------------------------------------------------ shell

        void BuildShell()
        {
            rootVisualElement.Clear();
            _root = Sp2Style.Root();
            rootVisualElement.Add(_root);

            var head = Sp2Style.Row();
            head.style.marginBottom = Sp2Style.Pad;
            head.style.justifyContent = Justify.SpaceBetween;

            var left = Sp2Style.Row();
            left.Add(Sp2Style.Pill("SP2 Pack", Sp2Style.Accent));
            left.Add(Sp2Style.Body(TargetDescription()));
            head.Add(left);

            var right = Sp2Style.Row();
            _status = Sp2Style.Body("");
            _status.style.color = Sp2Style.Muted;
            _status.style.marginRight = 10;
            right.Add(_status);
            right.Add(Sp2Style.Btn("Rescan", () =>
            {
                _rescanAt = 0;
                StartScan();
            }, primary: false));
            head.Add(right);

            _root.Add(head);

            _body = new VisualElement();
            _body.style.flexGrow = 1;
            _root.Add(_body);

            Rebuild();
        }

        void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            var tabs = Sp2Style.Row();
            tabs.style.marginBottom = Sp2Style.Pad;
            tabs.style.flexWrap = Wrap.Wrap;
            string[] names = { "Target", "Optimise", "Quality", "Preview", "Export", "About" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                tabs.Add(Sp2Style.Btn(names[i], () =>
                {
                    _tab = idx;
                    EditorPrefs.SetInt(TabKey, idx);
                    Rebuild();
                }, primary: _tab == i));
            }
            _body.Add(tabs);

            _page = new VisualElement();
            _page.style.flexGrow = 1;
            _body.Add(_page);

            BuildPage();
        }

        void RefreshPage()
        {
            if (_page == null) return;
            _page.Clear();
            BuildPage();
        }

        void BuildPage()
        {
            var scroll = Sp2Style.Scroll();
            _page.Add(scroll);

            switch (_tab)
            {
                case 0: BuildTarget(scroll); break;
                case 1: BuildOptimise(scroll); break;
                case 2: BuildQuality(scroll); break;
                case 3: BuildPreview(scroll); break;
                case 4: BuildExport(scroll); break;
                default: BuildAbout(scroll); break;
            }
        }

        // ------------------------------------------------------------ target

        string TargetDescription()
        {
            if (_roots == null || _roots.Length == 0) return "No target selected";
            if (_roots.Length == 1) return _roots[0] != null ? _roots[0].name : "—";
            return _roots.Length + " roots";
        }

        void BuildTarget(VisualElement host)
        {
            var src = Sp2Style.Card();
            src.Add(Sp2Style.Header("Source"));
            src.Add(Sp2Style.Caption("Pick what to export. Selection follows the scene selection; Scene Roots exports everything in the open scene."));

            var row = Sp2Style.Row();
            row.Add(Sp2Style.Btn("Use Selection", () =>
            {
                _targetMode = TargetMode.Selection;
                RefreshRoots();
                Rebuild();
            }, primary: _targetMode == TargetMode.Selection));
            row.Add(Sp2Style.Btn("Use Scene Roots", () =>
            {
                _targetMode = TargetMode.SceneRoots;
                RefreshRoots();
                Rebuild();
            }, primary: _targetMode == TargetMode.SceneRoots));
            src.Add(row);

            src.Add(Sp2Style.KV("Roots", _roots != null ? _roots.Length.ToString() : "0"));
            src.Add(Sp2Style.KV("Target", TargetDescription()));
            host.Add(src);

            if ((_roots == null || _roots.Length == 0) && _targetMode == TargetMode.Selection)
            {
                host.Add(Sp2Style.Notice("Nothing selected in the scene. Select the objects you want to export, or switch to Scene Roots.",
                    Sp2Style.Warn));
            }

            var inc = Sp2Style.Card();
            inc.Add(Sp2Style.Header("Include"));
            inc.Add(Sp2Style.Check("Terrain", _cfg.includeTerrain, v => { _cfg.includeTerrain = v; Changed(true); }));
            inc.Add(Sp2Style.Caption("Exports terrain as a decimated mesh. Most SimplePlanes 2 maps already have terrain, so leave this off unless the airport needs its own ground."));
            inc.Add(Sp2Style.Check("Inactive objects", _cfg.includeInactive, v => { _cfg.includeInactive = v; Changed(true); }));
            inc.Add(Sp2Style.Check("Skinned meshes", _cfg.includeSkinned, v => { _cfg.includeSkinned = v; Changed(true); }));
            host.Add(inc);
        }

        // ------------------------------------------------------------ optimise

        void BuildOptimise(VisualElement host)
        {
            var card = Sp2Style.Card();
            card.Add(Sp2Style.Header("Lighten"));
            card.Add(Sp2Style.Caption("Fewer and smaller objects mean a lighter pack and a faster load in game."));

            card.Add(Sp2Style.IntField("Min triangles", _cfg.minTriangles, v => { _cfg.minTriangles = Mathf.Max(0, v); Changed(true); }));
            card.Add(Sp2Style.Check("Skip hidden renderers", _cfg.dropHidden, v => { _cfg.dropHidden = v; Changed(true); }));
            card.Add(Sp2Style.Check("Merge by material", _cfg.mergeByMaterial, v => { _cfg.mergeByMaterial = v; Changed(false); }));
            card.Add(Sp2Style.Caption("Groups triangles by material so the loader creates the fewest sub-meshes."));
            card.Add(Sp2Style.FloatField("Max distance (m, 0 = off)", _cfg.maxDistance, v => { _cfg.maxDistance = Mathf.Max(0f, v); Changed(true); }));
            host.Add(card);

            var report = Sp2Style.Card();
            report.Add(Sp2Style.Header("Filters skipped"));
            if (_scan == null)
            {
                report.Add(Sp2Style.Body("Scanning…"));
            }
            else
            {
                report.Add(Sp2Style.KV("Too small", _scan.skippedSmall.ToString(),
                    _scan.skippedSmall > 0 ? Sp2Style.Warn : Sp2Style.Muted));
                report.Add(Sp2Style.KV("Hidden", _scan.skippedHidden.ToString(),
                    _scan.skippedHidden > 0 ? Sp2Style.Warn : Sp2Style.Muted));
                report.Add(Sp2Style.KV("Too far", _scan.skippedFar.ToString(),
                    _scan.skippedFar > 0 ? Sp2Style.Warn : Sp2Style.Muted));
            }
            host.Add(report);
        }

        // ------------------------------------------------------------ quality

        static readonly int[] TexSizes = { 0, 4096, 2048, 1024, 512 };
        static readonly string[] TexLabels = { "Original", "4096", "2048", "1024", "512" };

        void BuildQuality(VisualElement host)
        {
            var tex = Sp2Style.Card();
            tex.Add(Sp2Style.Header("Textures"));

            var choices = new List<string>(TexLabels);
            int idx = Mathf.Max(0, Array.IndexOf(TexSizes, _cfg.texMaxSize));
            tex.Add(Sp2Style.Dropdown("Max size (px)", choices, idx, i =>
            {
                _cfg.texMaxSize = TexSizes[Mathf.Clamp(i, 0, TexSizes.Length - 1)];
                Changed(false);
            }));
            tex.Add(Sp2Style.Caption("Longest edge cap. Halving the size quarters the pixel count, which is the biggest lever on pack size."));

            tex.Add(Sp2Style.Check("Quantise colours", _cfg.quantizeTextures, v => { _cfg.quantizeTextures = v; Changed(false); }));
            tex.Add(Sp2Style.Caption("Posterises textures to a small palette; strongly reduces PNG size at the cost of gradients."));
            host.Add(tex);

            var geo = Sp2Style.Card();
            geo.Add(Sp2Style.Header("Geometry"));
            geo.Add(Sp2Style.IntField("Vertex decimals (3-6)", _cfg.vertexDecimals, v => { _cfg.vertexDecimals = Mathf.Clamp(v, 3, 6); Changed(false); }));
            geo.Add(Sp2Style.Caption("Fewer decimals shrink the OBJ text a little; 5 is safe for metre scale."));
            geo.Add(Sp2Style.Check("Export UV", _cfg.exportUV, v => { _cfg.exportUV = v; Changed(false); }));
            host.Add(geo);

            var live = Sp2Style.Card();
            live.Add(Sp2Style.Header("Estimated pack size"));
            var size = _est != null ? _est.Total : 0;
            live.Add(Sp2Style.Value(Sp2Style.Bytes(size), Sp2Style.Accent));
            live.Add(Sp2Style.Caption("Updates live while you change the settings above."));
            host.Add(live);
        }

        // ------------------------------------------------------------ preview

        void BuildPreview(VisualElement host)
        {
            if (_scan == null)
            {
                host.Add(Sp2Style.Notice("Scanning the scene…", Sp2Style.Accent));
                return;
            }

            var stats = Sp2Style.Card();
            stats.Add(Sp2Style.Header("Statistics"));
            stats.Add(Sp2Style.KV("Objects", _scan.objects.ToString()));
            stats.Add(Sp2Style.KV("Triangles", Sp2Style.Count(_scan.totalTriangles)));
            stats.Add(Sp2Style.KV("Vertices", Sp2Style.Count(_scan.totalVertices)));
            stats.Add(Sp2Style.KV("Materials", _scan.materials.Count.ToString()));
            stats.Add(Sp2Style.KV("Textures", _scan.textures.Count.ToString()));
            stats.Add(Sp2Style.KV("Texture pixels", Sp2Style.Count(_scan.TexturePixels())));
            host.Add(stats);

            var est = _est ?? Sp2Model.Estimate(_scan, _cfg);
            var size = Sp2Style.Card();
            size.Add(Sp2Style.Header("Estimated pack size"));
            size.Add(Sp2Style.Value(Sp2Style.Bytes(est.Total), Sp2Style.Accent));
            Sp2Style.Space(4);

            long total = est.Total > 0 ? est.Total : 1;
            AddPart(size, "Geometry (obj)", est.objBytes, total, Sp2Style.Accent);
            AddPart(size, "Materials (mtl)", est.mtlBytes, total, Sp2Style.Violet);
            AddPart(size, "Textures", est.texBytes, total, Sp2Style.Ok);
            AddPart(size, "Loader plugin", est.pluginBytes, total, Sp2Style.Warn);
            AddPart(size, "BepInEx framework", est.frameworkBytes, total, Sp2Style.Muted);
            host.Add(size);

            foreach (var wmsg in _scan.warnings)
                host.Add(Sp2Style.Notice(wmsg, Sp2Style.Warn));
        }

        static void AddPart(VisualElement host, string label, long bytes, long total, Color color)
        {
            var row = Sp2Style.Row();
            row.style.justifyContent = Justify.SpaceBetween;
            var l = new Label(label);
            l.style.fontSize = 11;
            l.style.color = Sp2Style.Muted;
            var v = new Label(Sp2Style.Bytes(bytes));
            v.style.fontSize = 11;
            v.style.color = color;
            row.Add(l);
            row.Add(v);
            host.Add(row);
            host.Add(Sp2Style.Bar(bytes / (float)total, color, 6));
        }

        // ------------------------------------------------------------ export

        void BuildExport(VisualElement host)
        {
            var state = Sp2Style.Card();
            state.Add(Sp2Style.Header("Status"));
            if (_busy)
                state.Add(Sp2Style.Notice(_busyText, Sp2Style.Accent));
            else if (_packResult != null)
                state.Add(Sp2Style.Notice(_packResult, _packFailed ? Sp2Style.Error : Sp2Style.Ok));
            else if (_lastExport != null)
                state.Add(Sp2Style.KV("Last export", _lastExport.files + " meshes, " +
                    Sp2Style.Count(_lastExport.triangles) + " tris, " + Sp2Style.Count(_lastExport.textures) + " textures"));
            else
                state.Add(Sp2Style.Body("Idle. Nothing has been exported yet."));
            host.Add(state);

            var mod = Sp2Style.Card();
            mod.Add(Sp2Style.Header("Module"));

            var nameField = new TextField("Name") { value = _cfg.moduleName };
            nameField.style.fontSize = 11;
            nameField.RegisterValueChangedCallback(e => { _cfg.moduleName = e.newValue; _cfg.Save(); });
            mod.Add(nameField);

            mod.Add(Sp2Style.KV("Assets folder", Pretty(AssetsDir), Sp2Style.Muted));
            mod.Add(Sp2Style.KV("Pack output", Pretty(DistDir), Sp2Style.Muted));
            host.Add(mod);

            var actions = Sp2Style.Card();
            actions.Add(Sp2Style.Header("Actions"));

            var row = Sp2Style.Row();
            row.style.flexWrap = Wrap.Wrap;

            _exportBtn = Sp2Style.Btn("Export Assets", () =>
            {
                if (GuardClick()) return;
                DoExport(true);
            }, primary: true);
            _exportBtn.SetEnabled(!_busy);

            _packageBtn = Sp2Style.Btn("Build & Package", () =>
            {
                if (GuardClick()) return;
                DoPackage();
            }, tint: Sp2Style.Violet);
            _packageBtn.SetEnabled(!_busy);

            row.Add(_exportBtn);
            row.Add(_packageBtn);
            row.Add(Sp2Style.Btn("Open Assets Folder", () =>
            {
                Directory.CreateDirectory(AssetsDir);
                EditorUtility.RevealInFinder(AssetsDir);
            }));
            row.Add(Sp2Style.Btn("Open Pack Folder", () =>
            {
                Directory.CreateDirectory(DistDir);
                EditorUtility.RevealInFinder(DistDir);
            }));
            actions.Add(row);

            actions.Add(Sp2Style.Caption("Export writes OBJ/MTL/PNG into the assets folder. Build & Package then compiles the plugin and bundles everything with BepInEx into one zip. Both buttons lock while a build runs."));

            if (_lastExport != null)
            {
                actions.Add(Sp2Style.Divider());
                actions.Add(Sp2Style.KV("Last export", _lastExport.files + " meshes, " +
                    Sp2Style.Count(_lastExport.triangles) + " tris, " + Sp2Style.Count(_lastExport.textures) + " textures"));
                actions.Add(Sp2Style.KV("Written", Sp2Style.Bytes(_lastExport.objBytes + _lastExport.mtlBytes + _lastExport.texBytes),
                    Sp2Style.Ok));
                foreach (var n in _lastExport.notes)
                    actions.Add(Sp2Style.Notice(n, Sp2Style.Warn));
            }
            host.Add(actions);

            string log;
            lock (_procLog) log = _procLog.ToString();
            if (!string.IsNullOrEmpty(log))
            {
                var logs = Sp2Style.Card();
                logs.Add(Sp2Style.Header("Packager output"));
                var text = new Label(log);
                text.style.fontSize = 10;
                text.style.color = Sp2Style.Muted;
                text.style.whiteSpace = WhiteSpace.Normal;
                logs.Add(text);
                host.Add(logs);
            }
        }

        // ------------------------------------------------------------ about

        void BuildAbout(VisualElement host)
        {
            var card = Sp2Style.Card();
            card.Add(Sp2Style.Header("SP2 Pack"));
            card.Add(Sp2Style.Caption("Version 1.0"));
            card.Add(Sp2Style.Body(
                "Exports a Unity scene into the asset layout expected by the SimplePlanes 2 loader plugin, " +
                "then packages it with BepInEx into an extract-and-play mod.\n\n" +
                "Pipeline:\n" +
                "  1. Unity  — export OBJ / MTL / PNG  (this tool)\n" +
                "  2. plugin — the loader is prebuilt in Plugin~ (build-plugin.sh rebuilds it)\n" +
                "  3. package — package.sh bundles everything into one zip\n\n" +
                "AssetBundle is intentionally not used: SimplePlanes 2 targets Unity 6000, " +
                "and bundles are not loadable across Unity major versions, so the geometry " +
                "travels as OBJ and is rebuilt in game."));
            host.Add(card);

            var req = Sp2Style.Card();
            req.Add(Sp2Style.Header("Files"));
            req.Add(Sp2Style.KV("Tool folder", Pretty(ToolDir), Sp2Style.Muted));
            req.Add(Sp2Style.KV("Plugin source", "Plugin~/src/Plugin.cs"));
            req.Add(Sp2Style.KV("Packager", "package.sh"));
            host.Add(req);
        }

        // ------------------------------------------------------------ actions

        void Changed(bool affectsScan)
        {
            _cfg.Save();

            if (affectsScan) RequestRescan();
            else if (_scan != null) _est = Sp2Model.Estimate(_scan, _cfg);

            _dirty = true;
        }

        /// <summary>Exports the assets. Returns false when there is nothing to export.</summary>
        bool DoExport(bool reveal)
        {
            EnsureScan();
            if (_scan == null || _scan.Empty)
            {
                EditorUtility.DisplayDialog("SP2 Pack", "Nothing to export. Check the target and filters.", "OK");
                return false;
            }

            SetBusy(true, "Exporting assets…");
            try
            {
                var result = Sp2Exporter.Export(_scan, _cfg, AssetsDir, Slug(_cfg.moduleName),
                    (p, name) => EditorUtility.DisplayProgressBar("SP2 Pack", "Exporting " + name, p));

                _lastExport = result;
                AssetDatabase.Refresh();
                UnityEngine.Debug.Log($"[SP2 Pack] Exported {result.files} meshes, {result.triangles} triangles to {Pretty(result.outDir)}");
                if (reveal) EditorUtility.RevealInFinder(result.outDir);
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[SP2 Pack] Export failed: " + e);
                EditorUtility.DisplayDialog("SP2 Pack", "Export failed:\n" + e.Message, "OK");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                SetBusy(false, null);
                RefreshPage();
            }
        }

        void DoPackage()
        {
            if (!DoExport(false)) return;

            string script = Path.Combine(ToolAbs, "package.sh");
            if (!File.Exists(script))
            {
                EditorUtility.DisplayDialog("SP2 Pack", "Packager not found:\n" + Pretty(script), "OK");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/zsh",
                    Arguments = "-lc '" + Escape(script) + "' '" + Escape(_cfg.moduleName) + "'",
                    WorkingDirectory = ToolAbs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                lock (_procLog) _procLog.Clear();
                _packResult = null;

                _proc = Process.Start(psi);
                _proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (_procLog) _procLog.AppendLine(e.Data); };
                _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_procLog) _procLog.AppendLine(e.Data); };
                _proc.EnableRaisingEvents = true;
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();

                _procStart = EditorApplication.timeSinceStartup;
                SetBusy(true, "Packaging… 0.0s");
                RefreshPage();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[SP2 Pack] Packaging error: " + e);
                SetBusy(false, null);
            }
        }

        void FinishPackage()
        {
            int code;
            try { code = _proc.ExitCode; }
            catch { code = -1; }

            string log;
            lock (_procLog) log = _procLog.ToString();

            try { _proc.Dispose(); } catch { }
            _proc = null;

            if (code == 0)
            {
                _packResult = "Package built. Check the pack folder.";
                _packFailed = false;
                UnityEngine.Debug.Log("[SP2 Pack] Packaging OK\n" + log);
                EditorUtility.RevealInFinder(DistDir);
            }
            else
            {
                _packResult = "Packaging failed (exit " + code + "). See the output below and the Console.";
                _packFailed = true;
                UnityEngine.Debug.LogError("[SP2 Pack] Packaging failed\n" + log);
            }

            SetBusy(false, null);
            RefreshPage();
        }

        static string Escape(string s) => s.Replace("'", "'\\''");

        // ------------------------------------------------------------ paths

        static string _toolDir;

        /// <summary>
        /// Project-relative folder holding this tool. Discovered from the script asset so the
        /// folder can be renamed or moved anywhere under Assets/ without breaking anything.
        /// </summary>
        public static string ToolDir
        {
            get
            {
                if (!string.IsNullOrEmpty(_toolDir)) return _toolDir;

                const string file = "Sp2Pack_Window.cs";
                try
                {
                    foreach (var guid in AssetDatabase.FindAssets("Sp2Pack_Window t:MonoScript"))
                    {
                        var p = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                        if (p.EndsWith("/" + file))
                        {
                            _toolDir = p.Substring(0, p.Length - file.Length - 1);
                            return _toolDir;
                        }
                    }
                }
                catch { }

                _toolDir = "Assets/Editor/SP2Pack";
                return _toolDir;
            }
        }

        /// <summary>Absolute path of the tool folder.</summary>
        public static string ToolAbs =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", ToolDir)).Replace('\\', '/');

        /// <summary>Plugin source, vendor BepInEx and the compiled loader. Unity ignores "~" folders.</summary>
        public static string PluginDir => Path.Combine(ToolAbs, "Plugin~");

        /// <summary>Generated output (also a "~" folder, so Unity never tries to import the OBJ files).</summary>
        public static string WorkDir => Path.Combine(ToolAbs, "Output~");

        public static string AssetsDir => Path.Combine(WorkDir, "obj");
        public static string DistDir => Path.Combine(WorkDir, "dist");

        /// <summary>
        /// Drops the home folder prefix so screenshots show "Desktop/Game/..." instead of
        /// "/Users/name/Desktop/Game/...". Everything a user owns already lives under the
        /// home folder, so nothing useful is lost — same idea as Windows hiding the drive.
        /// </summary>
        public static string Pretty(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal))
            {
                string rest = path.Substring(home.Length).TrimStart('/', '\\');
                if (rest.Length > 0) return rest;
            }
            return path;
        }

        static string Slug(string raw)
        {
            var sb = new StringBuilder();
            foreach (char c in raw)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            var s = sb.ToString().Trim('_');
            return s.Length == 0 ? "airport" : s;
        }
    }
}
