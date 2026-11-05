using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace BattlesectorUpscaler
{
    /// <summary>
    /// Own native NGX bridge (BattlesectorUpscalerNative.dll). Every D3D11 call happens on Unity's render thread:
    /// we only marshal a small struct and hand it to <c>IssuePluginEventAndData</c>. Calling NGX
    /// from the main thread would race the renderer and crash.
    /// </summary>
    internal static class OwnDlss
    {
        private const string DllName = "BattlesectorUpscalerNative";

        // NVSDK_NGX_DLSS_Feature_Flags
        private const int FlagHdr = 1 << 0;
        private const int FlagDepthInverted = 1 << 3;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void BSUpscaler_SetLogPath(string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr BSUpscaler_GetEventFunc();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int BSUpscaler_GetStatus();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint BSUpscaler_GetLastResult();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int BSUpscaler_StructSizes(int which);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void BSUpscaler_ReleaseFeature();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void BSUpscaler_Shutdown();

        [StructLayout(LayoutKind.Sequential)]
        private struct CreateParams
        {
            public IntPtr AnyTexture;
            public int RenderW, RenderH, OutW, OutH;
            public int Quality, Flags, Preset, Slot;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EvalParams
        {
            public IntPtr Color, Depth, Motion, Output;
            public float JitterX, JitterY, MvScaleX, MvScaleY;
            public int Reset, RenderW, RenderH;
            public float Sharpness;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MvParams
        {
            public IntPtr Depth, Motion;
            public int Width, Height;
            public IntPtr InvVPJittered, VPCurrent, VPPrevious;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GammaParams
        {
            public IntPtr Source, Destination;
            public int Width, Height;
        }
        private static readonly int CreateSize = Marshal.SizeOf(typeof(CreateParams));
        private static readonly int EvalSize = Marshal.SizeOf(typeof(EvalParams));
        private static readonly int MvSize = Marshal.SizeOf(typeof(MvParams));
        private static readonly int GammaSize = Marshal.SizeOf(typeof(GammaParams));

        private static IntPtr _eventFn;
        private static IntPtr _evalRing, _createRing, _mvRing, _matrixRing;
        private static IntPtr _gammaRing;
        private static int _ringSlot;
        private static bool _loaded;
        private static bool _loadFailed;
        private static CommandBuffer _cb;

        internal static string LastError { get; private set; } = "";
        internal static bool Loaded => _loaded;

        private static string PluginDir =>
            Path.GetDirectoryName(typeof(DlssBridge).Assembly.Location) ?? ".";

        /// <summary>Loads the native DLL and verifies that both sides agree on struct layout.</summary>
        internal static bool EnsureLoaded()
        {
            if (_loaded) return true;
            if (_loadFailed) return false;

            try
            {
                string path = Path.Combine(PluginDir, DllName + ".dll");
                if (LoadLibraryW(path) == IntPtr.Zero)
                {
                    LastError = DllName + ".dll not found at " + path;
                    _loadFailed = true;
                    Plugin.Logger.LogError(LastError);
                    return false;
                }

                BSUpscaler_SetLogPath(Path.Combine(PluginDir, "BattlesectorUpscaler.native.log"));

                int c = BSUpscaler_StructSizes(0);
                int e = BSUpscaler_StructSizes(1);
                int m = BSUpscaler_StructSizes(2);
                if (c != CreateSize || e != EvalSize || m != MvSize)
                {
                    LastError = string.Format(
                        "Native/managed struct size mismatch (native {0}/{1}/{2}, managed {3}/{4}/{5}). " +
                        "The DLL and the plugin are from different builds.", c, e, m, CreateSize, EvalSize, MvSize);
                    _loadFailed = true;
                    Plugin.Logger.LogError(LastError);
                    return false;
                }

                _eventFn = BSUpscaler_GetEventFunc();
                _evalRing = Marshal.AllocHGlobal(EvalSize * 16);
                _createRing = Marshal.AllocHGlobal(CreateSize * 4);
                _mvRing = Marshal.AllocHGlobal(MvSize * 16);
                _gammaRing = Marshal.AllocHGlobal(GammaSize * 4);
                _matrixRing = Marshal.AllocHGlobal(16 * 3 * sizeof(float) * 16);

                _loaded = true;
                Plugin.Logger.LogInfo("Native bridge loaded.");
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Native load failed: " + ex.Message;
                _loadFailed = true;
                Plugin.Logger.LogError(LastError);
                return false;
            }
        }

        /// <summary>0 = nothing yet, 1 = ready, negative = failed (NGX result in <see cref="LastResult"/>).</summary>
        internal static int Status => _loaded ? BSUpscaler_GetStatus() : -2;

        internal static uint LastResult => _loaded ? BSUpscaler_GetLastResult() : 0;

        /// <summary>Releases the NGX feature, keeping the device and NGX initialised for a fast recreate.</summary>
        internal static bool QueuePresent(RenderTexture source, int width, int height)
        {
            if (!_loaded || source == null) return false;

            var data = new GammaParams
            {
                Source = source.GetNativeTexturePtr(),
                Destination = IntPtr.Zero,
                Width = width,
                Height = height,
            };

            IntPtr p = _gammaRing + (_ringSlot++ & 3) * GammaSize;
            Marshal.StructureToPtr(data, p, false);
            Issue(5, p);
            return true;
        }
        internal static bool QueueGammaEncode(RenderTexture source, RenderTexture destination)
        {
            if (!_loaded || source == null || destination == null) return false;

            var data = new GammaParams
            {
                Source = source.GetNativeTexturePtr(),
                Destination = destination.GetNativeTexturePtr(),
                Width = source.width,
                Height = source.height,
            };

            IntPtr p = _gammaRing + (_ringSlot++ & 3) * GammaSize;
            Marshal.StructureToPtr(data, p, false);
            Issue(5, p);
            return true;
        }
        internal static void ReleaseFeature()
        {
            if (!_loaded) return;
            try { BSUpscaler_ReleaseFeature(); }
            catch (Exception ex) { Plugin.Logger.LogWarning("Native release failed: " + ex.Message); }
        }

        /// <summary>Full teardown, called when the plugin is destroyed.</summary>
        internal static void Shutdown()
        {
            if (!_loaded) return;
            try { BSUpscaler_Shutdown(); }
            catch (Exception ex) { Plugin.Logger.LogWarning("Native shutdown failed: " + ex.Message); }
        }

        /// <summary>Queues feature creation. Poll <see cref="Status"/> on later frames; NGX creation is not instant.</summary>
        internal static void QueueCreate(RenderTexture anyTexture, int renderW, int renderH, int outW, int outH,
                                         int quality, int preset, bool hdr)
        {
            if (!EnsureLoaded() || anyTexture == null) return;

            var data = new CreateParams
            {
                AnyTexture = anyTexture.GetNativeTexturePtr(),
                RenderW = renderW,
                RenderH = renderH,
                OutW = outW,
                OutH = outH,
                Quality = quality,
                Flags = (hdr ? FlagHdr : 0) | (ModConfig.DepthInverted.Value ? FlagDepthInverted : 0),
                Preset = preset,
                Slot = 0,
            };

            IntPtr p = _createRing + (_ringSlot++ & 3) * CreateSize;
            Marshal.StructureToPtr(data, p, false);
            Issue(1, p);
        }

        internal static void QueueEvaluate(Texture color, Texture depth, Texture motion,
                                           RenderTexture output, Vector2 jitterPixels, bool reset,
                                           int renderW, int renderH)
        {
            if (!_loaded || color == null || depth == null || motion == null || output == null) return;

            var data = new EvalParams
            {
                Color = color.GetNativeTexturePtr(),
                Depth = depth.GetNativeTexturePtr(),
                Motion = motion.GetNativeTexturePtr(),
                Output = output.GetNativeTexturePtr(),
                JitterX = jitterPixels.x,
                JitterY = jitterPixels.y,
                MvScaleX = ModConfig.MvScaleX.Value,
                MvScaleY = ModConfig.MvScaleY.Value,
                Reset = reset ? 1 : 0,
                RenderW = renderW,
                RenderH = renderH,
                Sharpness = ModConfig.Sharpness.Value,
            };

            IntPtr p = _evalRing + (_ringSlot++ & 15) * EvalSize;
            Marshal.StructureToPtr(data, p, false);
            Issue(2, p);
        }

        internal static void QueueCameraMotionVectors(Texture depth, RenderTexture motion, int width, int height,
                                                      Matrix4x4 invVPJittered, Matrix4x4 vpCurrent, Matrix4x4 vpPrevious)
        {
            if (!_loaded || depth == null || motion == null) return;

            // The native side may still be reading the previous frame's slot on the render thread, so
            // every call gets its own slice of a ring instead of one shared block.
            int slot = _ringSlot++ & 15;
            IntPtr basePtr = _matrixRing + slot * 16 * 3 * sizeof(float);
            WriteMatrix(basePtr, invVPJittered);
            WriteMatrix(basePtr + 64, vpCurrent);
            WriteMatrix(basePtr + 128, vpPrevious);

            var data = new MvParams
            {
                Depth = depth.GetNativeTexturePtr(),
                Motion = motion.GetNativeTexturePtr(),
                Width = width,
                Height = height,
                InvVPJittered = basePtr,
                VPCurrent = basePtr + 64,
                VPPrevious = basePtr + 128,
            };

            IntPtr p = _mvRing + (_ringSlot++ & 15) * MvSize;
            Marshal.StructureToPtr(data, p, false);
            Issue(4, p);
        }

        private static void WriteMatrix(IntPtr destination, Matrix4x4 matrix)
        {
            // Column-major, to match how Unity uploads matrices and how HLSL reads a float4x4
            // from a constant buffer. The shader then uses mul(matrix, vector).
            var flat = new float[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    flat[c * 4 + r] = matrix[r, c];
            Marshal.Copy(flat, 0, destination, 16);
        }

        private static void Issue(int eventId, IntPtr data)
        {
            if (_cb == null) _cb = new CommandBuffer { name = "BSUpscaler" };
            _cb.Clear();
            _cb.IssuePluginEventAndData(_eventFn, eventId, data);
            Graphics.ExecuteCommandBuffer(_cb);
        }
    }
}
