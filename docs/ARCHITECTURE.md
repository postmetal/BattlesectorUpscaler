# Architecture notes

## Threading

Unity's render thread is the only place where D3D11 immediate-context calls are legal. The managed
plugin therefore never calls NGX directly. Instead it builds a small blittable struct and issues:

```csharp
cb.IssuePluginEventAndData(eventFn, eventId, dataPtr);
Graphics.ExecuteCommandBuffer(cb);
```

`ExecuteCommandBuffer` runs the event synchronously on the render thread, so the struct only has to
stay alive for the duration of the call. Ring buffers are still used because the native side may
hold a pointer across a frame boundary in future asynchronous paths, and because the cost is
trivial.

## Why the feature is created asynchronously

`NVSDK_NGX_D3D11_CreateFeature` can take tens of milliseconds (it builds pipelines). Doing it
inline would produce a visible hitch. The managed side issues the create event, then polls
`BSUpscaler_GetStatus()` for a few frames and only starts evaluating once the native side reports
ready. Failed creation is terminal until the mode or resolution changes.

## Render path

Unity's built-in pipeline has no notion of a render scale. The mod gets one by:

1. creating a `RenderTexture` smaller than the screen;
2. assigning it to `camera.targetTexture` in `OnPreCull`;
3. letting the camera render normally (including the game's own image effects, if any);
4. restoring `targetTexture = null` in `OnPostRender`, after capturing
   `_CameraDepthTexture`'s native pointer.

Because `targetTexture` is set, the game's post-processing runs at render resolution. That is the
"upscale after post" variant. Upscaling before post-processing needs the camera to render into an
HDR target and the post stack to be redirected — a later milestone.

## IL2CPP constraints

Battlesector ships as IL2CPP, which changes three things:

- there is no `Assembly-CSharp.dll` to reference; the plugin compiles against
  `BepInEx\interop\*.dll`, which BepInEx generates from `GameAssembly.dll` on first launch;
- adding a custom `MonoBehaviour` would require injecting a new type into the IL2CPP runtime
  (`ClassInjector`), and Unity's message dispatch for injected components is not reliable. The
  presenter therefore avoids components entirely and reacts to the static `Camera.onPostRender`
  event;
- Unity objects are `Il2CppInterop` proxies. They behave like the real thing for calls and property
  access, but they must not be kept after their native object is destroyed — hence the null checks
  on `_camera` every frame.

## Coordinate conventions

The velocity pass reconstructs world position with the **jittered** inverse view-projection, because
that is the matrix the depth buffer was produced with. It then projects with the **unjittered**
current and previous matrices, so the resulting vectors contain object/camera motion but not the
jitter itself. DLSS is told about the jitter separately through `Jitter_Offset_X/Y`, which keeps
the two from double-counting.

Depth on D3D is reversed (1 at the near plane), hence `DepthInverted` in the create flags. If the
flag is wrong, DLSS reconstructs geometry at the wrong distance and the image develops a
characteristic "melted" look during camera motion.

Matrix layout: Unity's `Matrix4x4` is uploaded as-is and multiplied with `mul(M, v)` in HLSL, which
matches Unity's own shader convention.

## NGX initialisation

`NVSDK_NGX_D3D11_Init_with_ProjectId` is used with a custom engine id rather than a store app id.
Some driver versions are stricter than others about unknown project ids; if initialisation fails
with `NVSDK_NGX_Result_FAIL_ProjectNotApproved` (or similar), the alternatives are:

- initialise with a known DLSS-enabled application id (legally dubious, works technically);
- ship the mod only to users who already have a DLSS title installed and reuse its runtime.

This is documented rather than solved because the right answer depends on the licence you are
comfortable with.

## Why not hook `Present`

For a DX11 Unity game, hooking `IDXGISwapChain::Present` would also work, but it would put the mod
below the HUD: the whole frame, interface included, would be upscaled. Rendering the 3D camera into
its own target and blitting it from a presenter camera keeps the interface native, which matters
much more in a strategy game than in a shooter.

The presenter is a bare `Camera` with `cullingMask = 0` and `clearFlags = SolidColor`, handled from
the static `Camera.onPostRender` event. `SolidColor` is deliberate: it guarantees the camera runs a
render pass (and therefore fires the event) even though it draws no geometry, and the full-screen
blit immediately overwrites the clear within the same frame.
