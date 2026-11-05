# Getting started — from zero to a running build (IL2CPP edition)

This game is **IL2CPP**, not Mono. The usual BepInEx 5 setup does not work: the game has no
`Managed` folder to load managed code from. You need **BepInEx 6 (Unity IL2CPP)**, which generates a
set of "interop" assemblies the first time the game runs, and the plugin compiles against those.

Follow the phases in order. Each ends with a **checkpoint**; do not continue until it passes.

Total time: ~40 minutes for Phase A, another hour for Phase B.

---

## Phase 0 — What you are about to do

1. **BepInEx 6 IL2CPP** — the loader. It hooks `GameAssembly.dll` and, on first launch, produces
   `BepInEx\interop\*.dll` (a managed mirror of the game's IL2CPP code).
2. **The mod** — a C# plugin compiled against those interop assemblies, plus a C++ helper for DLSS.
3. **The DLSS SDK** — NVIDIA headers/library, needed only for the C++ helper.

Phase A (BepInEx + .NET SDK + plugin) needs no NVIDIA tools and proves the mod can see the game's
camera. Phase B adds DLSS.

---

## Phase 1 — Confirm the game is IL2CPP and find its folder

1. Steam → **Library** → right-click **Warhammer 40,000: Battlesector** → **Manage → Browse local
   files**.
2. Copy the path from the address bar. It looks like:

   ```
   C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 Battlesector
   ```

3. You should see:
   - the game executable (`Warhammer 40K Battlesector.exe` on some installs)
   - `GameAssembly.dll` ← the IL2CPP runtime, this must exist
   - a data folder whose name ends in `_Data` — on this install it is
     `Warhammer 40K Battlesector_Data\`, and inside it `il2cpp_data\`

   The data folder name varies between store builds. Nothing in this project depends on it:
   the build script looks for `GameAssembly.dll`, and BepInEx always creates `BepInEx\` next to
   the executable.

**Checkpoint 1:** `GameAssembly.dll` exists next to the exe.

---

## Phase 2 — Install BepInEx 6 (IL2CPP)

BepInEx 6 is distributed as "bleeding edge" builds, not on the normal releases page.

1. Open <https://builds.bepinex.dev/projects/bepinex_be>.
2. At the top (newest build) find the download named:

   ```
   BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.<number>+<hash>.zip
   ```

   Download **that one**. Not `Unity.Mono`, not `win-x86`, not `NET.CoreCLR`.
3. Open the zip. Extract **its contents** into the game folder so you end up with:

   ```
   ...\Warhammer 40,000 Battlesector\Battlesector.exe
   ...\Warhammer 40,000 Battlesector\GameAssembly.dll
   ...\Warhammer 40,000 Battlesector\winhttp.dll
   ...\Warhammer 40,000 Battlesector\doorstop_config.ini
   ...\Warhammer 40,000 Battlesector\BepInEx\
   ```

4. Start the game from Steam. **The first launch will take a long time** (2–10 minutes): BepInEx is
   decompiling `GameAssembly.dll` to build the interop assemblies. The window may look frozen.
   Let it finish. When the main menu appears, quit the game.
5. Open `BepInEx\LogOutput.log`. You want to see lines like:

   ```
   [Message: BepInEx] BepInEx 6.0.0-be.xxx
   [Info   : Il2CppInterop] Generated interop assemblies in ...\BepInEx\interop
   ```

6. Check that `BepInEx\interop` exists and contains many DLLs, including
   `UnityEngine.CoreModule.dll` and `Assembly-CSharp.dll`.

**Checkpoint 2:** `BepInEx\interop\UnityEngine.CoreModule.dll` exists.

- If the folder is missing or the game crashed during generation: open the log, look for
  `Il2CppInterop` errors, and send me the last 40 lines. This is the one step where the game's Unity
  version can fight the tooling.
- The log also prints the Unity version (`Unity version: ...`). Note it down; it is useful if things
  go wrong later.

---

## Phase 3 — Install the .NET SDK

The plugin targets .NET 6, but any newer SDK can build it.

1. Open <https://dotnet.microsoft.com/en-us/download> and install the latest **SDK** for **Windows
   x64** (not the Runtime).
2. Close all PowerShell windows, open a new one, and run:

   ```powershell
   dotnet --list-sdks
   ```

**Checkpoint 3:** it prints at least one version, e.g. `8.0.404 [C:\Program Files\dotnet\sdk]`.

---

## Phase 4 — Build the plugin (no NVIDIA needed)

1. Open PowerShell **as Administrator** (the game lives under `Program Files`).
2. Go to the project:

   ```powershell
   cd "C:\Users\imba\Documents\deepseek-harness\default-workspace\BattlesectorUpscaler"
   ```

3. If scripts are blocked:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   ```

4. Build the managed half only:

   ```powershell
   .\scripts\build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 Battlesector" -SkipNative
   ```

   Expected:

   ```
   Build succeeded.
   Plugin -> ...\BattlesectorUpscaler\dist\BattlesectorUpscaler
   Skipping native build (-SkipNative).
   ```

   If NuGet cannot be reached, the script prints a warning and compiles against the local .NET 6
   runtime instead. That is fine — it produces the same plugin.

5. Copy the plugin into the game (if the build did not):

   ```powershell
   $game = "C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 Battlesector"
   New-Item -ItemType Directory -Force "$game\BepInEx\plugins\BattlesectorUpscaler"
   Copy-Item ".\dist\BattlesectorUpscaler\BattlesectorUpscaler.dll" "$game\BepInEx\plugins\BattlesectorUpscaler\" -Force
   ```

6. Start the game, wait for the menu, quit. In `BepInEx\LogOutput.log` you should see:

   ```
   Battlesector Upscaler 0.1.0 loaded.
   ```

**Checkpoint 4a:** the mod loads.

7. Open `BepInEx\config\com.battlesector.upscaler.cfg` in Notepad. Set:

   ```ini
   Stage = BlitOnly
   ```

8. Launch, load any battle.

**Checkpoint 4b:** the battle is visible and slightly soft, and the log says:

   ```
   Upscaler active on camera '...' (...).
   ```

- Black screen? Quit (`Alt+F4`), set `PresentMode = MainCameraPostRender`, relaunch.
- Still black? Set `Enabled = false` and send me the logs.

---

## Phase 5 — C++ toolchain and DLSS SDK

Same as any native build.

### 5a. Visual Studio Build Tools

<https://visualstudio.microsoft.com/downloads/> → **Build Tools for Visual Studio 2022** → workload
**Desktop development with C++** → in the right-hand panel make sure these are ticked:

- MSVC v143 C++ x64/x86 build tools
- Windows 11 SDK (or Windows 10 SDK)
- C++ CMake tools for Windows

Install (~7 GB).

### 5b. CMake on PATH

New PowerShell window:

```powershell
cmake --version
```

If it fails, install CMake from <https://cmake.org/download/> with **Add CMake to the system PATH**.

### 5c. DLSS SDK

1. <https://github.com/NVIDIA/DLSS> → **Code → Download ZIP**.
2. Extract to `C:\SDK\DLSS` so that `C:\SDK\DLSS\include\nvsdk_ngx.h` **and**
   `C:\SDK\DLSS\lib\Windows_x86_64\x64\nvsdk_ngx_s.lib` exist.

The repository gives you headers and the static library. It does **not** contain
`nvngx_dlss.dll`; that comes separately (next step).

### 5d. `nvngx_dlss.dll`

Copy `nvngx_dlss.dll` from any DLSS-enabled game you own, for example:

```
...\Cyberpunk 2077\bin\x64\nvngx_dlss.dll
```

Put it in two places:

```
d:\Steam\steamapps\common\Warhammer 40000 Battlesector\nvngx_dlss.dll
...\BattlesectorUpscaler\dist\BattlesectorUpscaler\nvngx_dlss.dll
```

The first copy is the one NGX normally finds; the second one is so the build script can install it
next to the plugin. A recent DLL is fine — it does not have to match the SDK exactly.

**Checkpoint 5:** header exists and you have a `nvngx_dlss.dll`.

---

## Phase 6 — Build the native DLL

```powershell
cd "C:\Users\imba\Documents\deepseek-harness\default-workspace\BattlesectorUpscaler"
$env:DLSS_SDK = "C:\SDK\DLSS"
.\scripts\build.ps1 -GameDir "d:\Steam\steamapps\common\Warhammer 40000 Battlesector" -DlssSdk "C:\SDK\DLSS"
```

Expected: `Native -> ...\dist\BattlesectorUpscaler`.

Copy all three files (run as Administrator; the game folder is under `D:\Steam`):

```powershell
$game = "d:\Steam\steamapps\common\Warhammer 40000 Battlesector"
Copy-Item ".\dist\BattlesectorUpscaler\*.dll" "$game\BepInEx\plugins\BattlesectorUpscaler\" -Force
```

The plugins folder must contain:

```
BattlesectorUpscaler.dll
BattlesectorUpscalerNative.dll
nvngx_dlss.dll
```

**Checkpoint 6:** all three are present.

---

## Phase 7 — Turn DLSS on, one step at a time

Close the game before editing `BepInEx\config\com.battlesector.upscaler.cfg`.

### Step 1 — does NGX start?

```ini
Stage = DlssNoMotionVectors
Mode = Dlaa
```

Launch, load a battle, check `BepInEx\LogOutput.log`:

- `DLSS create requested: ...` then `DLSS ready.` → success.
- `DLSS failed to initialise (status ..., NGX result 0x...)` → send that line plus
  `BepInEx\plugins\BattlesectorUpscaler\BattlesectorUpscaler.native.log`.

DLAA runs at full resolution, so the picture should get sharper AA, not softer. Clean test.

### Step 2 — lower the resolution

```ini
Mode = Quality
```

The 3D scene renders at 67 %. The HUD should stay crisp.

### Step 3 — motion vectors

```ini
Stage = DlssCameraMotionVectors
```

Pan the camera. Terrain should stay stable; moving units may ghost slightly.

- Terrain smears on pan → `DepthInverted = false`.
- Wrong direction → `MotionVectorScaleX = -1`, `MotionVectorScaleY = -1`.
- Shimmer → `JitterScale = 0.5` as a test.

One change per launch.

---

## Phase 8 — If it breaks

Logs:

- `BepInEx\LogOutput.log` — loader + plugin.
- `BepInEx\interop\` — if this is empty, nothing else matters; fix Phase 2 first.
- `BepInEx\plugins\BattlesectorUpscaler\BattlesectorUpscaler.native.log` — NGX/D3D.

| Symptom | First thing to try |
|---|---|
| Game won't start after Phase 2 | Remove `winhttp.dll`, `doorstop_config.ini`, `BepInEx\`; game returns to normal |
| `BepInEx\interop` missing | Check log for `Il2CppInterop`; the Unity version may be unsupported |
| Plugin not in log | DLL not in `BepInEx\plugins\` or built for the wrong BepInEx |
| Black screen in battle | `PresentMode = MainCameraPostRender` |
| "DLSS is NOT available" | RTX card, driver, `nvngx_dlss.dll` next to the plugin |
| Game update broke it | Rebuild; Unity/IL2CPP layout changes |

To remove everything: delete `BepInEx`, `winhttp.dll` and `doorstop_config.ini` from the game
folder. No game files are modified.

---

## Phase 9 — What to send me when stuck

1. Last 40 lines of `BepInEx\LogOutput.log`.
2. Whether `BepInEx\interop\UnityEngine.CoreModule.dll` exists.
3. The whole `BattlesectorUpscaler.native.log` if you got that far.
4. Your `com.battlesector.upscaler.cfg`.
5. For build failures: the first red `error` line, verbatim.
