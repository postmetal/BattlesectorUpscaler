# Battlesector Upscaler

An unofficial DLSS / DLAA injector for **Warhammer 40,000: Battlesector**, built as a BepInEx 6
IL2CPP plugin with a small native NGX (D3D11) bridge.

The managed plugin redirects the game's 3D camera into an off-screen target, applies a sub-pixel
jitter, generates camera motion vectors from the depth buffer on the GPU, and hands the frame to
DLSS. The result is blitted over the frame, so the HUD stays at native resolution.

> **Status: working DLAA.** DLAA renders sharp with correct colour, working camera motion vectors
> and correct mouse selection. Quality/Balanced/Performance modes are **not usable** in the current
> build (the image does not fill the screen when the render target is smaller than the output).

Built on the architecture of [Malla-h/WotRUpscaler](https://github.com/Malla-h/WotRUpscaler), which
does the same job for Pathfinder WotR and was the reference for
the camera hook, NGX parameter usage and the config layout.

## Requirements

- **Warhammer 40,000: Battlesector** (Unity 6, IL2CPP, Direct3D 11, x64)
- **BepInEx 6.0.0-be.791** (IL2CPP build; `BepInEx.Unity.IL2CPP`)
- A **GeForce RTX** GPU with DLSS support and a recent NVIDIA driver
- `nvngx_dlss.dll` (DLSS 4 / 310.x), shipped in the drop-in package

## Install

1. Install BepInEx 6 IL2CPP into the game folder and run the game once so the interop assemblies are
   generated.
2. Close the game.
3. Extract `BattlesectorUpscaler-DLAA-dropin.zip` into the game folder:

```
BepInEx\plugins\BattlesectorUpscaler\BattlesectorUpscaler.dll
BepInEx\plugins\BattlesectorUpscaler\BattlesectorUpscalerNative.dll
BepInEx\plugins\BattlesectorUpscaler\nvngx_dlss.dll
```

4. Launch the game. Logs: `BepInEx\LogOutput.log` and
   `BepInEx\plugins\BattlesectorUpscaler\BattlesectorUpscaler.native.log`.

To pick up new defaults, delete `BepInEx\config\com.battlesector.upscaler.cfg` (it regenerates).

## Configuration

`BepInEx\config\com.battlesector.upscaler.cfg`

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `Mode` | `Dlaa` | Use `Dlaa` only. Other modes are implemented but Quality is not full-screen yet. |
| `Stage` | `DlssCameraMotionVectors` | Use `DlssCameraMotionVectors`. `BlitOnly` bypasses DLSS; `DlssNoMotionVectors` is a debugging fallback; `NgxOnly` is dormant and will break the frame. |
| `PresentMode` | `MainCameraPostRender` | Must stay `MainCameraPostRender`. `PresenterCamera` crashes. |
| `Preset` | `13` | DLSS preset M. `11` (K) is a softer alternative. |
| `JitterScale` | `1` | Sub-pixel jitter amplitude. |
| `JitterProjectionScale` | `1` | Projection-side jitter scale. |
| `JitterSign` / `JitterSignY` | `1` / `1` | Projection offset signs. |
| `JitterReportSign` | `-1` | Sign of the offset reported to DLSS. **Must stay `-1`**; `1` makes the image soft. |
| `MotionVectorScaleX` / `Y` | `-1` / `-1` | MV calibration for Unity's flipped render targets. |
| `DepthInverted` | `true` | Unity on D3D uses reversed depth. |

## How it works

```
main camera ──► low-res RT (jittered projection)
                    │
                    ├── colour ──────────────┐
                    ├── _CameraDepthTexture ─┤
                    └── velocity RT ◄── compute shader (depth reprojection)
                                             │
                                    native NGX bridge (render thread)
                                             │
                    full-res RT ◄────────────┘
                         │
                         └── blit to screen; HUD draws afterwards at native resolution
```

Key implementation details:

- the camera's projection matrix is jittered in the **z column** (`m02`/`m12`), which produces a
  constant NDC offset after the perspective divide; DLSS receives the **negated** offset;
- motion vectors are reconstructed from depth with a compute shader, using the jittered inverse
  view-projection for unprojection and the unjittered matrices for reprojection;
- `_low`/`_full` are created with `HideFlags.DontUnloadUnusedAsset` so `Resources.UnloadUnusedAssets`
  on scene loads does not destroy them;
- the NGX feature is created on the render thread through
  `CommandBuffer.IssuePluginEventAndData`, and the output target has `enableRandomWrite` (DLSS writes
  through a UAV and returns `0xBAD00009` otherwise).

## Known limitations

- **Quality / Balanced / Performance are not full-screen.** Only DLAA (1:1) is supported.
- **Colour depends on the present path.** The correct-colour build leaves `camera.targetTexture`
  assigned and uses a plain `Graphics.Blit`; resetting the render target or the active texture
  causes washed-out colours on this Unity build.
- The NVIDIA DLSS overlay is mirrored when the output is an intermediate render texture; disable it
  (`ShowDlssIndicator = 0`) or ignore it.
- The 60 FPS cap is the game's, not the mod's (it remains with the mod disabled).
- OptiScaler cannot replace DLSS here unless something makes real NGX calls at the real resolution.
  A dummy NGX call makes OptiScaler take over at the dummy size and the 3D disappears.

## Building

```
.\scripts\build.ps1 -GameDir "d:\Steam\steamapps\common\Warhammer 40000 Battlesector" -DlssSdk "C:\SDK\DLSS"
```

Requires the .NET 6 SDK, CMake and the DLSS SDK headers/libs. Output goes to `dist\BattlesectorUpscaler`.

## References

- [Malla-h/WotRUpscaler](https://github.com/Malla-h/WotRUpscaler) — the reference implementation this
  project's architecture is based on (camera hook, NGX bridge, jitter and config layout).
- [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS) — `nvngx_dlss.dll`, headers and programming guide.
- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity IL2CPP plugin loader.

## Disclaimer

Unofficial, fan-made, not affiliated with NVIDIA, Slitherine or Games Workshop. Use at your own risk.
