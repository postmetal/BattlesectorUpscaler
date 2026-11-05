# Calibration

If the mod runs but the image is wrong, work down this table. Change one setting at a time and
look at a camera pan, because that is where motion-vector and depth errors are most visible.

| Symptom | Likely cause | Fix |
|---|---|---|
| Image is soft but stable | DLSS is not running, only the blit fallback | Check the BepInEx log for `DLSS ready.`; if it says failed, read the NGX result code |
| Everything is black | Presenter camera not drawing, or the blit target is wrong | Set `Stage = BlitOnly`, `PresentMode = MainCameraPostRender` |
| Image is upside down | Blit through a platform-specific flip | Not expected on D3D11; report with the log |
| Geometry "melts" during pans, edges trail | Wrong depth convention | Toggle `DepthInverted` |
| Static geometry shimmers while panning | Motion vectors wrong or missing | Start with `Stage = DlssNoMotionVectors`; if the shimmer disappears, the MV pass is at fault |
| Motion smears in the wrong direction | MV sign | Try `MotionVectorScaleX = -1` and/or `MotionVectorScaleY = -1` |
| Motion smears by roughly the screen size | MV units are wrong | MVs are UV-space here; if you changed the shader to pixel space, scale accordingly |
| Ghosting only on units, not on terrain | Expected: only camera motion is reconstructed | Per-object MVs are on the roadmap |
| Colours washed out or too dark | Gamma vs linear mismatch | Render targets follow the player colour space; a conversion pass is on the roadmap |
| The HUD is blurry | The HUD is drawn by the 3D camera (Screen Space – Camera), not an overlay canvas | Not fixable from here without redirecting the UI canvas |
| Big frame-time spike when toggling | Feature creation is synchronous | Expected; it happens once per resolution/mode change |

## Reading the logs

`BepInEx\LogOutput.log`:

```
Upscaler active on camera 'Main Camera' (Camera).
DLSS create requested: 1706x960 -> 2560x1440, quality 2, preset 0
DLSS ready.
```

`BattlesectorUpscaler.native.log` (next to the plugin DLL):

```
NGX: initialised.
NGX: feature created 1706x960 -> 2560x1440, quality 2, flags 0x9
```

The `flags` value tells you what was requested: `0x1` = HDR, `0x8` = depth inverted.

## Useful NGX result codes

| Code | Meaning |
|---|---|
| `0x00000000` (`NVSDK_NGX_Result_Success`) | OK |
| `0xBAD00001` (`FAIL_FeatureNotSupported`) | GPU or driver does not support this feature |
| `0xBAD00002` (`FAIL_InvalidParameter`) | A parameter is out of range — usually resolution or quality |
| `0xBAD00003` (`FAIL_Unsupported`) | Feature not supported for this device/API combination |
| `0xBAD0000A` (`FAIL_ProjectNotApproved`) | The project id was rejected; see `docs/ARCHITECTURE.md` |

The exact values differ between SDK versions; always trust the hex code in the log over this table.
