# SP2 Pack

A single-folder Unity editor tool that turns a scene into a **SimplePlanes 2
BepInEx mod**.

Select what you want in the scene, press export, and it bakes the geometry into
OBJ + MTL + PNG, then bundles it with the loader plugin and BepInEx into one
extract-and-play zip.

No dependencies, no setup: drop the folder in and it works, even if you have
never scripted anything.

## Install

Copy this whole folder into your Unity project's `Assets/Editor/`:

```
YourProject/
└── Assets/
    └── Editor/
        └── SP2Pack/        ← this folder
```

Open the project. That is the entire install.

## Use

1. Open **Tools → SP2 Pack**.
2. Choose a **Target** — `Use Selection` or `Use Scene Roots`.
3. Tune **Optimise** (skip small / hidden / far objects) and **Quality**
   (texture size, colour quantisation, vertex precision).
4. Check the **Preview** tab for the triangle count and the estimated pack size.
5. Click **Export Assets**, then **Build & Package**.
6. Take the zip from `SP2Pack/Output~/dist/` and extract it into your
   **SimplePlanes 2** folder (the one with `SimplePlanes 2.exe`).

Start the game, load into the world, and press `F9` — your model appears.

## In game

- `F9` load, `F10` unload, `F7` open the settings panel.
- The model is attached to a terrain by name. If the position, heading or scale
  is off, adjust `Offset` / `AxisMode` / `Scale` in the panel until it lines up,
  then keep those values in `BepInEx/config/com.sp2.worldmodelloader.cfg`.

## What is inside

```
SP2Pack/
├── Sp2Pack_*.cs              the editor tool (Unity compiles these)
├── build-plugin.sh           rebuilds the loader plugin (optional)
├── package.sh                bundles the mod zip
├── Plugin~/                  Unity ignores "~" folders
│   ├── src/Plugin.cs         loader plugin source
│   ├── SP2WorldModelLoader.dll   prebuilt loader — no compiler needed
│   └── vendor-BepInEx_*.zip  BepInEx 5.4.23.5 (x64)
└── Output~/                  generated exports and packages (git-ignored)
```

The loader plugin is **prebuilt and shipped**, so making a mod needs neither a
.NET SDK nor a copy of the game. Only rebuild it if you change
`Plugin~/src/Plugin.cs`.

## Why OBJ instead of an AssetBundle

SimplePlanes 2 runs on **Unity 6000**, and AssetBundles are not loadable across
major Unity versions — a bundle built by an older editor simply fails to load.
So the tool exports plain geometry and the in-game plugin rebuilds it inside
SP2, where the Unity version always matches.

## Rebuilding the plugin (optional)

Requires a .NET SDK and a local SimplePlanes 2 install:

```sh
Assets/Editor/SP2Pack/build-plugin.sh
```

`SP2_GAME_DIR` overrides the game folder. The script pulls the BepInEx
reference assemblies out of the bundled zip automatically.

## Command line export (optional)

Export without opening the window:

```sh
Unity -batchmode -quit -projectPath /path/to/project \
      -executeMethod Sp2Pack.Sp2Menu.BatchExportScene
```

## Credits

- **[SKAVIIND](https://www.simpleplanes.com/u/SARACONIKaviationIndustry)** —
  original author of the OBJ packaging script. SP2 Pack is built on top of his
  work, reworked with better Unity tooling. Huge thanks for the support.
- The in-game loader plugin is based on `SP2RailImport` (the original OBJ world
  loader).
- Thanks to **[Yukie570](https://www.simpleplanes.com/u/Yukie570)**,
  **[Trefle502](https://www.simpleplanes.com/u/Trefle502)** and
  **[Kendjello](https://www.simpleplanes.com/u/Kendjello)** for support and
  testing.
- [BepInEx](https://github.com/BepInEx/BepInEx) — plugin framework (LGPL).
- UI style inspired by the Neko toolset.

## License

MIT — see [LICENSE](LICENSE).
