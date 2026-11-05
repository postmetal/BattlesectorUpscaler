using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace BattlesectorUpscaler
{
    /// <summary>
    /// Owns the whole render path: finds the 3D camera, renders it into a smaller target,
    /// jitters its projection, drives the native DLSS bridge, and hands a full-resolution
    /// frame to the presenter. The HUD canvas is drawn after this and stays native.
    /// </summary>
    internal sealed class UpscaleManager : IDisposable
    {
        private const int DepthBits = 24;

        private enum FeatureState { Off, Creating, Ready, Failed }

        private Camera _camera;
        private Camera _presenter;

        private RenderTexture _low;
        private RenderTexture _full;
        private RenderTexture _motion;
        private RenderTexture _present;

        private int _outWidth, _outHeight;
        private int _renderWidth, _renderHeight;
        private bool _active;

        private FeatureState _featureState = FeatureState.Off;
        private int _createFrame;
        private bool _resetHistory = true;
        private bool _loggedMissingDepth;

        private Vector2 _jitter;
        private Matrix4x4 _baseProjection;
        private Matrix4x4 _lastAssignedProjection;
        private bool _hasAssignedProjection;
        private bool _resetProjectionFailed;
        private bool _loggedProjection;
        private bool _hasBaseProjection;
        private Matrix4x4 _vpCurrent;
        private Matrix4x4 _vpPrevious;
        private bool _hasPreviousVp;

        private int _savedMsaa;
        private bool _msaaSaved;
        private bool _savedAllowMsaa;
        private bool _savedAllowDynamicResolution;
        private DepthTextureMode _savedDepthMode;
        private int _lastPresentFrame;
        private bool _warnedNoPresent;
        private bool _presentLogged;
        private bool _forceMainCameraPresent;
        private bool _fallbackPresent;
        private int _activateFrame;
        private bool _probeDone;
        private bool _rebuildErrorLogged;
        private bool _presentSizeLogged;
        private bool _gammaProbeDone;
        private Material _tonemapMaterial;
        private bool _tonemapMaterialSearched;

        // ------------------------------------------------------------------ frame loop

        private int _tickFrame = -1;

        /// <summary>
        /// Called from Camera.onPreCull (there is no Update in a BasePlugin). Runs the per-frame
        /// bookkeeping once, then the camera hook itself.
        /// </summary>
        internal void OnPreCullEntry(Camera cam)
        {
            if (Time.frameCount != _tickFrame)
            {
                _tickFrame = Time.frameCount;
                Tick();
            }

            OnPreCull(cam);
        }

        internal void Tick()
        {
            if (!ModConfig.Enabled.Value || ModConfig.Mode.Value == UpscaleMode.Off)
            {
                if (_active) Deactivate();
                return;
            }

            if (_camera == null || !_camera || !IsStillRendering(_camera))
            {
                Camera found = FindCamera();
                if (found == null) return;

                if (!SameCamera(found, _camera))
                {
                    Plugin.Logger.LogInfo("3D camera changed; re-targeting on '" + found.name + "'.");
                    SwitchCamera(found);
                }
                else
                {
                    _camera = found;
                }
            }

            if (!_active) Activate();

            // Self-heal: if Unity unloaded the targets despite the hide flags, rebuild them.
            if (_active && (_low == null || _full == null || _motion == null))
            {
                _outWidth = 0;
                _outHeight = 0;
                _renderWidth = 0;
                _renderHeight = 0;
            }

            int width = Mathf.Max(1, Screen.width);
            int height = Mathf.Max(1, Screen.height);
            int wantedRenderW = Mathf.Max(64, Mathf.RoundToInt(width * ModConfig.Scale));
            int wantedRenderH = Mathf.Max(64, Mathf.RoundToInt(height * ModConfig.Scale));

            if (width != _outWidth || height != _outHeight || wantedRenderW != _renderWidth || wantedRenderH != _renderHeight)
            {
                try
                {
                    Rebuild(width, height, wantedRenderW, wantedRenderH);
                }
                catch (Exception ex)
                {
                    if (!_rebuildErrorLogged)
                    {
                        _rebuildErrorLogged = true;
                        Plugin.Logger.LogError("Rebuild failed: " + ex);
                    }
                }
            }

            UpdateFeatureState();
            EnsurePresenter();

            if (_low != null)
            {
                // Assign unconditionally: comparing RenderTexture wrappers by reference is
                // unreliable under IL2CPP, and re-assigning the same target is free.
                _camera.targetTexture = _low;
            }

            // Readback probes are intentionally disabled: they change RenderTexture.active and
            // mutate global render-target state from inside a camera callback.

            // If onPostRender stopped firing, present from OnPreCull instead. That is one frame
            // late but keeps the image alive and tells us the callback is the problem.
            if (!_fallbackPresent && _active && _featureState != FeatureState.Creating &&
                Time.frameCount - _lastPresentFrame > 30)
            {
                _fallbackPresent = true;
                Plugin.Logger.LogWarning("onPostRender stopped firing; presenting from OnPreCull (one frame late).");
            }

            // Watchdog: if nothing has been presented for 30 frames, say why instead of leaving
            // the player with a frozen image.
            if (!_warnedNoPresent && _active && Time.frameCount - _lastPresentFrame > 30)
            {
                _warnedNoPresent = true;

                if (ModConfig.Present.Value == PresentMode.PresenterCamera)
                {
                    _forceMainCameraPresent = true;
                    DestroyPresenter();
                    Plugin.Logger.LogWarning("The presenter camera never presented a frame. " +
                                             "Switched to main-camera presenting for this session.");
                }
                else
                {
                    Plugin.Logger.LogWarning("No frame has been presented for 30 frames. " +
                                             "Send the log: the camera callbacks may not be firing.");
                }
            }
        }

        private void Activate()
        {
            _active = true;
            if (ModConfig.Stage.Value == PipelineStage.NgxOnly) return;
            _lastPresentFrame = Time.frameCount;
            _activateFrame = Time.frameCount;
            _warnedNoPresent = false;
            _presentLogged = false;
            _probeDone = false;
            _hasAssignedProjection = false;
            _loggedProjection = false;
            _savedMsaa = QualitySettings.antiAliasing;
            _msaaSaved = true;
            QualitySettings.antiAliasing = 0;

            _savedAllowMsaa = _camera.allowMSAA;
            _savedAllowDynamicResolution = _camera.allowDynamicResolution;
            _savedDepthMode = _camera.depthTextureMode;

            _camera.allowMSAA = false;
            _camera.allowDynamicResolution = false;
            _camera.depthTextureMode |= DepthTextureMode.Depth;

            Plugin.Logger.LogInfo("Upscaler active on camera '" + _camera.name + "' (" + _camera.GetType().Name + ").");
            Plugin.Logger.LogInfo("framerate state: target=" + Application.targetFrameRate + " vSync=" + QualitySettings.vSyncCount + " colorSpace=" + QualitySettings.activeColorSpace);
            Plugin.Logger.LogInfo(string.Format(
                "camera projection: ortho={0} fov={1:F2} aspect={2:F3} size={3:F2} near={4:F2} far={5:F1}",
                _camera.orthographic, _camera.fieldOfView, _camera.aspect, _camera.orthographicSize,
                _camera.nearClipPlane, _camera.farClipPlane));
            LogCameraList();

            // Assign once. OnPreCull re-assigns defensively, but keeping it set is what makes the
            // camera render into the low-res target on every subsequent frame.
            _camera.targetTexture = _low;
        }

        private static bool IsStillRendering(Camera camera)
        {
            foreach (Camera cam in Camera.allCameras)
            {
                if (SameCamera(cam, camera)) return true;
            }
            return false;
        }

        private void LogCameraList()
        {
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam == null) continue;
                string target = cam.targetTexture != null ? cam.targetTexture.name : "screen";
                Plugin.Logger.LogInfo(string.Format(
                    "camera '{0}' depth={1} clear={2} mask=0x{3:X} target={4} {5}x{6}",
                    cam.name, cam.depth, cam.clearFlags, cam.cullingMask, target, cam.pixelWidth, cam.pixelHeight));
            }
        }

        /// <summary>
        /// A scene change swaps the camera but keeps the same resolution and quality. The DLSS
        /// feature is device-level, so keep it and the render targets; only move the camera
        /// bookkeeping. Rebuilding here used to destroy the targets, leave Present with null
        /// textures and force a multi-second NGX feature recreation.
        /// </summary>
        private void SwitchCamera(Camera found)
        {
            if (_camera != null && _camera)
            {
                _camera.targetTexture = null;
                _camera.allowMSAA = _savedAllowMsaa;
                _camera.allowDynamicResolution = _savedAllowDynamicResolution;
                _camera.depthTextureMode = _savedDepthMode;
                if (_hasBaseProjection) _camera.projectionMatrix = _baseProjection;
            }

            _camera = found;
            _hasAssignedProjection = false;
            _hasPreviousVp = false;
            _loggedProjection = false;
            _resetHistory = true;
            _lastPresentFrame = Time.frameCount;
            _warnedNoPresent = false;
            _fallbackPresent = false;

            if (!_active)
            {
                Activate();
                return;
            }

            _savedAllowMsaa = _camera.allowMSAA;
            _savedAllowDynamicResolution = _camera.allowDynamicResolution;
            _savedDepthMode = _camera.depthTextureMode;
            _camera.allowMSAA = false;
            _camera.allowDynamicResolution = false;
            _camera.depthTextureMode |= DepthTextureMode.Depth;
            if (_low != null) _camera.targetTexture = _low;
            if (_presenter != null) _presenter.depth = _camera.depth + 10f;

            Plugin.Logger.LogInfo("Upscaler re-targeted on camera '" + _camera.name + "'.");
        }

        private void Deactivate()
        {
            _active = false;
            DlssBridge.ReleaseFeature();
            _featureState = FeatureState.Off;

            if (_camera != null && _camera)
            {
                _camera.targetTexture = null;
                _camera.allowMSAA = _savedAllowMsaa;
                _camera.allowDynamicResolution = _savedAllowDynamicResolution;
                _camera.depthTextureMode = _savedDepthMode;
                if (_hasBaseProjection) _camera.projectionMatrix = _baseProjection;
            }

            if (_msaaSaved)
            {
                QualitySettings.antiAliasing = _savedMsaa;
                _msaaSaved = false;
            }

            DestroyPresenter();
            ReleaseTargets("deactivate");
            _hasPreviousVp = false;
            Jitter.Reset();

            // Force a rebuild on the next activation. Without this, a scene change reuses the
            // previous resolution fields, Rebuild() is skipped, and the camera keeps rendering to
            // a destroyed target while DLSS waits forever for a feature.
            _outWidth = 0;
            _outHeight = 0;
            _renderWidth = 0;
            _renderHeight = 0;
        }

        public void Dispose()
        {
            if (_active) Deactivate();
            DestroyPresenter();
            ReleaseTargets("dispose");
            DlssBridge.Shutdown();
        }

        // ------------------------------------------------------------------ camera callbacks

        private RenderTexture _ngxLow, _ngxDepth, _ngxMotion;
        private RenderTexture _screen;

        private void NgxOnlyTick()
        {
            if (_ngxLow == null)
            {
                _ngxLow = CreateTarget("BSUpscalerNgxLow", 256, 256, RenderTextureFormat.ARGB32, 24, false, RenderTextureReadWrite.Default);
                _ngxDepth = CreateTarget("BSUpscalerNgxDepth", 256, 256, RenderTextureFormat.Depth, 24, false, RenderTextureReadWrite.Default);
                _ngxMotion = CreateTarget("BSUpscalerNgxMotion", 256, 256, RenderTextureFormat.ARGBHalf, 0, true, RenderTextureReadWrite.Linear);
            }

            if (_featureState == FeatureState.Off)
            {
                DlssBridge.QueueCreate(_ngxLow, 256, 256, 256, 256, 5, ModConfig.DlssPreset.Value, false);
                _featureState = FeatureState.Creating;
                _createFrame = Time.frameCount;
                Plugin.Logger.LogInfo("NGX-only: create issued (dummy targets) for OptiScaler interception.");
            }
            else if (_featureState == FeatureState.Creating && DlssBridge.Status == 1)
            {
                _featureState = FeatureState.Ready;
                Plugin.Logger.LogInfo("NGX-only: feature ready for interception.");
            }
            else if (_featureState == FeatureState.Ready)
            {
                DlssBridge.QueueEvaluate(_ngxLow, _ngxDepth, _ngxMotion, _ngxLow, Vector2.zero, false, 256, 256);
            }
        }
        internal void OnPreCull(Camera cam)
        {
            if (_active && ModConfig.Stage.Value == PipelineStage.NgxOnly) { NgxOnlyTick(); return; }
            if (!_active || !SameCamera(cam, _camera) || _low == null) return;

            cam.targetTexture = _low;

            if (_fallbackPresent)
            {
                // onPostRender is not firing; present the previous frame here instead.
                Present();
            }

            if (ModConfig.Stage.Value == PipelineStage.BlitOnly)
            {
                // No upscaler yet, so do not touch the projection at all. Assigning
                // camera.projectionMatrix makes Unity ignore the game's own FOV/zoom updates.
                _vpCurrent = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
                return;
            }

            // Ask Unity for the unjittered matrix every frame. ResetProjectionMatrix recomputes it
            // from FOV/orthographic size, so last frame's jitter cannot become this frame's base.
            // The previous "compare and cache" approach accumulated jitter when read-back floats
            // differed slightly, which produced exactly the amplitude-dependent blur we saw.
            Matrix4x4 baseProjection;
            try
            {
                cam.ResetProjectionMatrix();
                baseProjection = cam.projectionMatrix;
            }
            catch (Exception)
            {
                if (!_resetProjectionFailed)
                {
                    _resetProjectionFailed = true;
                    Plugin.Logger.LogWarning("ResetProjectionMatrix unavailable; deriving projection from camera FOV.");
                }
                baseProjection = ComputeBaseProjection(cam);
            }

            _baseProjection = baseProjection;
            _hasBaseProjection = true;

            if (!_loggedProjection)
            {
                _loggedProjection = true;
                Plugin.Logger.LogInfo(string.Format(
                    "base projection: m00={0:F4} m11={1:F4} m22={2:F4} m23={3:F4}",
                    _baseProjection.m00, _baseProjection.m11, _baseProjection.m22, _baseProjection.m23));
            }

            _jitter = Jitter.Next();
            // Canonical Unity TAA pattern: tell the engine both the clean and the jittered matrix,
            // otherwise the renderer can keep using the non-jittered one while DLSS is told the
            // image is jittered.
            cam.nonJitteredProjectionMatrix = _baseProjection;
            cam.projectionMatrix = Jitter.Apply(_baseProjection, _jitter);

            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(_baseProjection, true);
            Matrix4x4 view = cam.worldToCameraMatrix;

            _vpCurrent = gpuProjection * view;

            if (ModConfig.Verbose.Value)
            {
                Plugin.Logger.LogDebug(string.Format("jitter {0:F3}/{1:F3} proj {2}x{3}", _jitter.x, _jitter.y, _renderWidth, _renderHeight));
            }
        }

        internal void OnPostRender(Camera cam)
        {
            if (!_active) return;
            if (ModConfig.Stage.Value == PipelineStage.NgxOnly) return;

            if (SameCamera(cam, _camera))
            {
                LogPostRender(cam);

                // The depth texture is a per-camera global; it must be grabbed while this camera's
                // render is still the most recent one, not later from the presenter.
                Texture depth = Shader.GetGlobalTexture("_CameraDepthTexture");
                if (depth != null)
                {
                    _depthTexture = depth;
                }
                else if (!_loggedMissingDepth)
                {
                    _loggedMissingDepth = true;
                    Plugin.Logger.LogWarning("_CameraDepthTexture is not available. The game may not use a depth " +
                                             "texture; DLSS will run without depth and quality will suffer.");
                }

                // Restore the camera's screen target before presenting. Leaving targetTexture
                // assigned makes the game's input code use camera.pixelWidth (the render size)
                // while the mouse is in screen space, which offsets unit selection.

                if (ModConfig.Stage.Value != PipelineStage.BlitOnly && _hasBaseProjection)
                {
                    cam.projectionMatrix = _baseProjection;
                }

                if (ModConfig.Present.Value == PresentMode.MainCameraPostRender || _forceMainCameraPresent)
                {
                    Present();
                }

                return;
            }

            if (SameCamera(cam, _presenter) && ModConfig.Present.Value == PresentMode.PresenterCamera)
            {
                Present();
            }
        }

        private Texture _depthTexture;
        private int _postRenderCount;
        private bool _presentErrorLogged;

        private static Matrix4x4 ComputeBaseProjection(Camera cam)
        {
            if (cam.orthographic)
            {
                float halfHeight = cam.orthographicSize;
                float halfWidth = halfHeight * cam.aspect;
                return Matrix4x4.Ortho(-halfWidth, halfWidth, -halfHeight, halfHeight,
                    cam.nearClipPlane, cam.farClipPlane);
            }

            return Matrix4x4.Perspective(cam.fieldOfView, cam.aspect,
                cam.nearClipPlane, cam.farClipPlane);
        }

        private void LogPostRender(Camera cam)
        {
            _postRenderCount++;
            if (_postRenderCount <= 3 || _postRenderCount % 600 == 0)
            {
                Plugin.Logger.LogInfo(string.Format("onPostRender #{0} '{1}' id={2} frame={3}",
                    _postRenderCount, cam.name, cam.GetInstanceID(), Time.frameCount));
            }
        }

        // ------------------------------------------------------------------ presenting

        internal void Present()
        {
            _lastPresentFrame = Time.frameCount;

            if (!_active || _low == null || _full == null)
            {
                if (Time.frameCount % 300 == 0)
                {
                    Plugin.Logger.LogInfo(string.Format(
                        "Present blocked: active={0} low={1} full={2}",
                        _active, _low != null, _full != null));
                }
                return;
            }

            try
            {
                PresentCore();
            }
            catch (Exception ex)
            {
                if (!_presentErrorLogged)
                {
                    _presentErrorLogged = true;
                    Plugin.Logger.LogError("Present failed: " + ex);
                }
            }
        }

        private void PresentCore()
        {
            if (Time.frameCount % 300 == 0)
            {
                Plugin.Logger.LogInfo(string.Format(
                    "PresentCore: state={0} low={1} full={2} depth={3} stage={4}",
                    _featureState, _low != null, _full != null, _depthTexture != null, ModConfig.Stage.Value));
            }

            if (!_presentLogged)
            {
                _presentLogged = true;
                Plugin.Logger.LogInfo(string.Format(
                    "First frame presented: {0}x{1} -> {2}x{3} (stage {4}, mode {5}).",
                    _renderWidth, _renderHeight, _outWidth, _outHeight, ModConfig.Stage.Value, ModConfig.Present.Value));
            }

            if (ModConfig.UsesDlss && _featureState == FeatureState.Off &&
                _low != null && _full != null && _depthTexture != null)
            {
                DlssBridge.ReleaseFeature();
                DlssBridge.QueueCreate(_low, _renderWidth, _renderHeight, _outWidth, _outHeight,
                    QualityValue(), ModConfig.DlssPreset.Value, ModConfig.Color.Value == ColorMode.Hdr);
                _featureState = FeatureState.Creating;
                _createFrame = Time.frameCount;
                _resetHistory = true;
                Plugin.Logger.LogInfo("create issued; state=" + _featureState);
                Plugin.Logger.LogInfo(string.Format("DLSS create requested: {0}x{1} -> {2}x{3}, quality {4}, preset {5}",
                    _renderWidth, _renderHeight, _outWidth, _outHeight, QualityValue(), ModConfig.DlssPreset.Value));
                Plugin.Logger.LogInfo("post-create probe: status=" + DlssBridge.Status);
            }

            if (_featureState == FeatureState.Creating)
            {
                int pendingStatus = DlssBridge.Status;
                if (pendingStatus == 1)
                {
                    _featureState = FeatureState.Ready;
                    Plugin.Logger.LogInfo("DLSS ready (render-thread poll).");
                }
                else if (Time.frameCount % 120 == 0)
                {
                    Plugin.Logger.LogInfo("DLSS create pending, native status=" + pendingStatus);
                }
            }

            bool dlss = ModConfig.Stage.Value != PipelineStage.BlitOnly &&
                        _featureState == FeatureState.Ready &&
                        _depthTexture != null &&
                        DlssBridge.EnsureLoaded();

            if (dlss)
            {
                if (ModConfig.Stage.Value == PipelineStage.DlssCameraMotionVectors && DlssBridge.SupportsCameraMotionVectors && _hasPreviousVp && _motion != null)
                {
                    // The colour was rendered with the jittered projection, so depth must be
                    // unprojected with the jittered matrix. Reprojecting with the unjittered
                    // current/previous matrices gives motion vectors without jitter in them.
                    Matrix4x4 vpJittered = JitterTranslation() * _vpCurrent;
                    DlssBridge.QueueCameraMotionVectors(_depthTexture, _motion, _renderWidth, _renderHeight,
                        vpJittered.inverse, _vpCurrent, _vpPrevious);
                }

                float reportSign = ModConfig.JitterReportSign.Value;
                var reportedJitter = new Vector2(_jitter.x * reportSign, _jitter.y * reportSign);
                DlssBridge.QueueEvaluate(_low, _depthTexture, _motion, _full, reportedJitter, _resetHistory,
                    _renderWidth, _renderHeight);
                _resetHistory = false;
            }
            else
            {
                // No DLSS (stage 0, no RTX, or a failed init): still show the low-res image.
                Graphics.Blit(_low, _full);
            }

            _vpPrevious = _vpCurrent;
            _hasPreviousVp = true;

            // Present with a direct blit so GL.sRGBWrite is honoured (command buffers ignore it),
            // and restore the backbuffer/viewport explicitly after rendering into the smaller RT.
            RenderTexture.active = null;
            Graphics.SetRenderTarget(null);
            GL.Viewport(new Rect(0f, 0f, Screen.width, Screen.height));

            // Final presentation. The native sRGB pass is kept in the DLL but the managed path
            // uses Unity's blit: the native draw is overwritten by Unity's own present.
            Graphics.Blit(_full, (RenderTexture)null);
        }

        /// <summary>
        /// IL2CPP creates a fresh managed wrapper per access to the same native object, so '=='
        /// compares wrapper references and can be false for the same camera. Instance IDs are the
        /// only stable identity here.
        /// </summary>
        private static bool SameCamera(Camera a, Camera b)
        {
            if (a == null || b == null) return false;
            return a.GetInstanceID() == b.GetInstanceID();
        }

        private static bool SameTexture(Texture a, Texture b)
        {
            if (a == null || b == null) return false;
            return a.GetInstanceID() == b.GetInstanceID();
        }

        private static bool MatricesClose(Matrix4x4 a, Matrix4x4 b)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (Mathf.Abs(a[r, c] - b[r, c]) > 1e-4f) return false;
                }
            }
            return true;
        }

        private void ProbeMotion()
        {
            if (_motion == null) return;

            RenderTexture previous = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                RenderTexture.active = _motion;
                readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
                readback.ReadPixels(new Rect(_motion.width / 2, _motion.height / 2, 1, 1), 0, 0);
                readback.Apply();
                Color c = readback.GetPixel(0, 0);
                Plugin.Logger.LogInfo(string.Format("MV center: x={0:F3} y={1:F3} (render {2}x{3})",
                    c.r, c.g, _renderWidth, _renderHeight));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("MV probe failed: " + ex.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.Destroy(readback);
            }
        }

        private void ProbeTarget(string label, RenderTexture target)
        {
            if (target == null) return;

            RenderTexture previous = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                RenderTexture.active = target;
                readback = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(target.width / 2, target.height / 2, 1, 1), 0, 0);
                readback.Apply();
                Color c = readback.GetPixel(0, 0);
                Plugin.Logger.LogInfo(string.Format("Probe {0} {1}x{2}: rgba=({3:F3},{4:F3},{5:F3},{6:F3})",
                    label, target.width, target.height, c.r, c.g, c.b, c.a));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Probe " + label + " failed: " + ex.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.Destroy(readback);
            }
        }

        private Matrix4x4 JitterTranslation()
        {
            Matrix4x4 shift = Matrix4x4.identity;
            // Match Jitter.Apply: the z column gives a constant NDC offset after the divide.
            shift.m02 = 2f * _jitter.x / Mathf.Max(1, _renderWidth);
            shift.m12 = 2f * _jitter.y / Mathf.Max(1, _renderHeight);
            return shift;
        }

        // ------------------------------------------------------------------ resources

        private void Rebuild(int outWidth, int outHeight, int renderWidth, int renderHeight)
        {
            ReleaseTargets("rebuild");

            _outWidth = outWidth;
            _outHeight = outHeight;
            _renderWidth = renderWidth;
            _renderHeight = renderHeight;

            RenderTextureFormat format = _camera != null && _camera.allowHDR
                ? RenderTextureFormat.DefaultHDR
                : RenderTextureFormat.ARGB32;
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default;

            _low = CreateTarget("BSUpscalerLow", renderWidth, renderHeight, format, DepthBits, false, readWrite);
            // DLSS writes its result through a UAV; without enableRandomWrite NGX returns
            // 0xBAD00009 (RWFlagMissing) on every EvaluateFeature call.
            _full = CreateTarget("BSUpscalerFull", outWidth, outHeight, format, 0, true, readWrite);
            _motion = CreateTarget("BSUpscalerMotion", renderWidth, renderHeight, RenderTextureFormat.ARGBHalf, 0, true, RenderTextureReadWrite.Linear);
            Plugin.Logger.LogInfo("targets created: low=" + (_low != null) + " full=" + (_full != null) +
                                  " motion=" + (_motion != null) + " format=" + format + " color=" + ModConfig.Color.Value);

            Jitter.SetTarget(renderWidth, renderHeight);
            Jitter.Reset();
            _hasPreviousVp = false;
            _resetHistory = true;
            _featureState = FeatureState.Off;

            // Zero the velocity buffer once so the no-MV stage has defined input. Use a command
            // buffer: changing the global active render target inside camera callbacks does not
            // stick and leaves the wrong target bound for later blits.
            var clearMotion = new CommandBuffer { name = "BSUpscaler clear motion" };
            clearMotion.SetRenderTarget(_motion);
            clearMotion.ClearRenderTarget(false, true, Color.clear);
            Graphics.ExecuteCommandBuffer(clearMotion);
            clearMotion.Release();

            Plugin.Logger.LogInfo(string.Format("Render target rebuilt: {0}x{1} -> {2}x{3}", renderWidth, renderHeight, outWidth, outHeight));
        }

        private static RenderTexture CreateTarget(string name, int width, int height, RenderTextureFormat format, int depth, bool randomWrite, RenderTextureReadWrite readWrite)
        {
            var rt = new RenderTexture(width, height, depth, format, readWrite)
            {
                name = name,
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                enableRandomWrite = randomWrite,
                // Scene loads call Resources.UnloadUnusedAssets, which destroys runtime textures
                // referenced only from managed code. Keep them alive across loads.
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            rt.Create();
            return rt;
        }

        private void ReleaseTargets(string reason)
        {
            Plugin.Logger.LogInfo("ReleaseTargets(" + reason + ") low=" + (_low != null) +
                                  " full=" + (_full != null) + " motion=" + (_motion != null));
            Release(ref _low);
            Release(ref _full);
            Release(ref _motion);
            Release(ref _present);
            _depthTexture = null;
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        // ------------------------------------------------------------------ DLSS lifecycle

        private void UpdateFeatureState()
        {
            if (!ModConfig.UsesDlss)
            {
                _featureState = FeatureState.Off;
                return;
            }

            if (!DlssBridge.EnsureLoaded())
            {
                _featureState = FeatureState.Failed;
                return;
            }

            // Targets are recreated on activation; never issue a create with a destroyed texture.
            if (_low == null || _full == null)
            {
                _featureState = FeatureState.Off;
                return;
            }

            switch (_featureState)
            {
                case FeatureState.Off:
                    // Creation is issued from Present (OnPostRender). Executing a command buffer
                    // inside OnPreCull is not reliable in every game state; the evaluate path in
                    // OnPostRender has proven to work.
                    return;

                case FeatureState.Creating:
                    if (Time.frameCount - _createFrame < 3) return;
                    int status = DlssBridge.Status;

                    if (status == 0 && Time.frameCount - _createFrame > 1800)
                    {
                        _featureState = FeatureState.Failed;
                        Plugin.Logger.LogError("DLSS create never completed (status 0 after 1800 frames). " +
                                               "Falling back to plain blitting.");
                        return;
                    }
                    if (status == 1)
                    {
                        _featureState = FeatureState.Ready;
                        Plugin.Logger.LogInfo("DLSS ready.");
                    }
                    else if (status < 0)
                    {
                        _featureState = FeatureState.Failed;
                        Plugin.Logger.LogError(string.Format(
                            "DLSS failed to initialise (status {0}, NGX result 0x{1:X8}). " +
                            "An RTX card and nvngx_dlss.dll are required. Falling back to plain blit.",
                            status, DlssBridge.LastResult));
                    }
                    break;

                case FeatureState.Failed:
                    // NGX feature creation can finish long after the create call returns (driver
                    // pipeline compilation). Recover instead of staying disabled forever.
                    if (DlssBridge.Status == 1)
                    {
                        _featureState = FeatureState.Ready;
                        Plugin.Logger.LogInfo("DLSS became ready after a delayed create.");
                    }
                    return;
            }
        }

        private int QualityValue()
        {
            float scale = ModConfig.Scale;
            if (scale >= 0.999f) return 5; // DLAA
            if (scale >= 0.62f) return 2;  // quality
            if (scale >= 0.54f) return 1;  // balanced
            if (scale >= 0.40f) return 0;  // performance
            return 3;                       // ultra performance
        }

        // ------------------------------------------------------------------ presenter

        private void EnsurePresenter()
        {
            if (ModConfig.Present.Value != PresentMode.PresenterCamera || _presenter != null) return;

            var go = new GameObject("BSUpscalerPresenter");
            UnityEngine.Object.DontDestroyOnLoad(go);

            // No custom MonoBehaviour here: injected components are unreliable under IL2CPP, so the
            // presenter works purely through the Camera.onPostRender event. SolidColor guarantees the
            // camera actually runs its render loop (and therefore fires the event); the blit paints
            // over the clear in the same frame.
            _presenter = go.AddComponent<Camera>();
            if (_presenter == null)
            {
                Plugin.Logger.LogError("Could not create the presenter camera.");
                UnityEngine.Object.Destroy(go);
                return;
            }

            _presenter.clearFlags = CameraClearFlags.SolidColor;
            _presenter.backgroundColor = Color.black;
            _presenter.cullingMask = 0;
            _presenter.depth = _camera != null ? _camera.depth + 10f : 10f;
            _presenter.allowMSAA = false;
            _presenter.allowHDR = false;
            _presenter.useOcclusionCulling = false;
            _presenter.orthographic = true;
            _presenter.orthographicSize = 1f;
            _presenter.nearClipPlane = 0.01f;
            _presenter.farClipPlane = 2f;
        }

        private void DestroyPresenter()
        {
            if (_presenter != null) UnityEngine.Object.Destroy(_presenter.gameObject);
            _presenter = null;
        }

        // ------------------------------------------------------------------ camera discovery

        private Camera FindCamera()
        {
            if (!string.IsNullOrEmpty(ModConfig.CameraName.Value))
            {
                GameObject go = GameObject.Find(ModConfig.CameraName.Value);
                if (go != null)
                {
                    Camera named = go.GetComponent<Camera>();
                    if (named != null) return named;
                }
                Plugin.Logger.LogWarning("Camera '" + ModConfig.CameraName.Value + "' was not found; auto-detecting.");
            }

            Camera best = null;
            float bestScore = -1f;

            foreach (Camera cam in Camera.allCameras)
            {
                if (cam == null || SameCamera(cam, _presenter)) continue;
                if (cam.cameraType != CameraType.Game) continue;
                if (cam.cullingMask == 0) continue;
                // A camera is allowed to have our own low-res target assigned; any other target
                // means the game renders it somewhere we do not control.
                if (cam.targetTexture != null && !SameTexture(cam.targetTexture, _low)) continue;

                float score = (float)cam.pixelWidth * cam.pixelHeight;

                // A 3D scene camera almost always clears to the skybox; prefer that over an overlay.
                if (cam.clearFlags == CameraClearFlags.Skybox) score *= 2f;

                if (score <= bestScore) continue;
                bestScore = score;
                best = cam;
            }

            if (best == null)
            {
                Plugin.Logger.LogWarning("No suitable 3D camera found yet.");
            }
            else if (ModConfig.Verbose.Value)
            {
                Plugin.Logger.LogDebug("Using camera '" + best.name + "' (" + best.pixelWidth + "x" + best.pixelHeight + ").");
            }

            return best;
        }
    }
}