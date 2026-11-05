using System.IO;

namespace BattlesectorUpscaler
{
    /// <summary>
    /// Chooses the DLSS backend. We prefer our own bridge (it also generates camera motion
    /// vectors); if it is not present, the prebuilt BTDLSS.dll from the BattleTech mod is used,
    /// which needs no compiler but has no motion-vector pass.
    /// </summary>
    internal static class DlssBridge
    {
        private enum Backend { None, Own, Bt }

        private static Backend _backend = Backend.None;
        private static bool _announced;

        internal static bool SupportsCameraMotionVectors => _backend == Backend.Own;

        private static string PluginDir =>
            Path.GetDirectoryName(typeof(DlssBridge).Assembly.Location) ?? ".";

        internal static bool EnsureLoaded()
        {
            switch (_backend)
            {
                case Backend.Own: return OwnDlss.Loaded || OwnDlss.EnsureLoaded();
                case Backend.Bt: return BtDlss.Loaded || BtDlss.EnsureLoaded();
            }

            string dir = PluginDir;

            if (File.Exists(Path.Combine(dir, "BattlesectorUpscalerNative.dll")) && OwnDlss.EnsureLoaded())
            {
                _backend = Backend.Own;
                if (!_announced)
                {
                    _announced = true;
                    Plugin.Logger.LogInfo("DLSS backend: BattlesectorUpscalerNative (own bridge).");
                }
                return true;
            }

            if (File.Exists(Path.Combine(dir, "BTDLSS.dll")) && BtDlss.EnsureLoaded())
            {
                _backend = Backend.Bt;
                if (!_announced)
                {
                    _announced = true;
                    Plugin.Logger.LogInfo("DLSS backend: BTDLSS (prebuilt).");
                    Plugin.Logger.LogWarning("BTDLSS has no camera motion-vector pass. " +
                                             "Use Stage = DlssNoMotionVectors with this backend.");
                }
                return true;
            }

            return false;
        }

        internal static int Status
        {
            get
            {
                if (_backend == Backend.Own) return OwnDlss.Status;
                if (_backend == Backend.Bt) return BtDlss.Status;
                return -2;
            }
        }

        internal static uint LastResult
        {
            get
            {
                if (_backend == Backend.Own) return OwnDlss.LastResult;
                if (_backend == Backend.Bt) return BtDlss.LastResult;
                return 0;
            }
        }

        internal static void QueueCreate(UnityEngine.RenderTexture anyTexture, int renderW, int renderH,
                                         int outW, int outH, int quality, int preset, bool hdr)
        {
            if (!EnsureLoaded()) return;

            if (_backend == Backend.Own)
                OwnDlss.QueueCreate(anyTexture, renderW, renderH, outW, outH, quality, preset, hdr);
            else if (_backend == Backend.Bt)
                BtDlss.QueueCreate(anyTexture, renderW, renderH, outW, outH, quality, preset, hdr);
        }

        internal static void QueueEvaluate(UnityEngine.Texture color, UnityEngine.Texture depth,
                                           UnityEngine.Texture motion, UnityEngine.RenderTexture output,
                                           UnityEngine.Vector2 jitterPixels, bool reset,
                                           int renderW, int renderH)
        {
            if (!EnsureLoaded()) return;

            if (_backend == Backend.Own)
                OwnDlss.QueueEvaluate(color, depth, motion, output, jitterPixels, reset, renderW, renderH);
            else if (_backend == Backend.Bt)
                BtDlss.QueueEvaluate(color, depth, motion, output, jitterPixels, reset, renderW, renderH);
        }

        internal static void QueueCameraMotionVectors(UnityEngine.Texture depth, UnityEngine.RenderTexture motion,
                                                      int width, int height, UnityEngine.Matrix4x4 invVPJittered,
                                                      UnityEngine.Matrix4x4 vpCurrent, UnityEngine.Matrix4x4 vpPrevious)
        {
            if (_backend != Backend.Own) return;
            OwnDlss.QueueCameraMotionVectors(depth, motion, width, height, invVPJittered, vpCurrent, vpPrevious);
        }

        internal static bool PresentFinal(UnityEngine.RenderTexture source, int width, int height)
        {
            if (!EnsureLoaded()) return false;
            if (_backend != Backend.Own) return false;
            return OwnDlss.QueuePresent(source, width, height);
        }
        internal static bool EncodeGamma(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)
        {
            if (!EnsureLoaded()) return false;
            if (_backend != Backend.Own) return false;
            return OwnDlss.QueueGammaEncode(source, destination);
        }
        internal static void ReleaseFeature()
        {
            if (_backend == Backend.Own) OwnDlss.ReleaseFeature();
            else if (_backend == Backend.Bt) BtDlss.ReleaseFeature();
        }

        internal static void Shutdown()
        {
            if (_backend == Backend.Own) OwnDlss.Shutdown();
            else if (_backend == Backend.Bt) BtDlss.Shutdown();
        }
    }
}
