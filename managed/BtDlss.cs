using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace BattlesectorUpscaler
{
    /// <summary>
    /// Backend for the prebuilt BTDLSS.dll that ships with the BTScale BattleTech mod
    /// (github.com/Malla-h/BattleTech-DLSS-Native). Same idea as our own bridge, different C ABI.
    ///
    /// It has no camera motion-vector pass, so it is only usable for the DLAA/no-MV stages.
    /// </summary>
    internal static class BtDlss
    {
        private const string DllName = "BTDLSS";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void BTDLSS_SetLogPath(string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr BTDLSS_GetEventFunc();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int BTDLSS_GetStatus();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint BTDLSS_GetLastResult();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int BTDLSS_StructSizes(int which);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CreateParams
        {
            public IntPtr AnyTexture;
            public int RenderW, RenderH, OutW, OutH;
            public int Quality, Flags, Preset;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string Dir;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EvalParams
        {
            public IntPtr Color, Depth, Motion, Output;
            public float JitterX, JitterY, MvScaleX, MvScaleY;
            public int Reset, RenderW, RenderH;
            public float Sharpness, PreExposure;
        }

        private static readonly int CreateSize = Marshal.SizeOf(typeof(CreateParams));
        private static readonly int EvalSize = Marshal.SizeOf(typeof(EvalParams));

        private static IntPtr _eventFn;
        private static IntPtr _evalRing, _createRing;
        private static int _ringSlot;
        private static bool _loaded;
        private static bool _loadFailed;
        private static CommandBuffer _cb;

        internal static bool Loaded => _loaded;
        internal static string LastError { get; private set; } = "";

        private static string PluginDir =>
            Path.GetDirectoryName(typeof(BtDlss).Assembly.Location) ?? ".";

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
                    return false;
                }

                BTDLSS_SetLogPath(Path.Combine(PluginDir, "BattlesectorUpscaler.bt.log"));

                int c = BTDLSS_StructSizes(0);
                int e = BTDLSS_StructSizes(1);
                if (c != CreateSize || e != EvalSize)
                {
                    LastError = string.Format("BTDLSS struct size mismatch (native {0}/{1}, managed {2}/{3}).", c, e, CreateSize, EvalSize);
                    Plugin.Logger.LogError(LastError);
                    _loadFailed = true;
                    return false;
                }

                _eventFn = BTDLSS_GetEventFunc();
                _evalRing = Marshal.AllocHGlobal(EvalSize * 16);
                _createRing = Marshal.AllocHGlobal(CreateSize * 4);

                _loaded = true;
                Plugin.Logger.LogInfo("BTDLSS backend loaded (" + path + ").");
                return true;
            }
            catch (Exception ex)
            {
                LastError = "BTDLSS load failed: " + ex.Message;
                _loadFailed = true;
                Plugin.Logger.LogError(LastError);
                return false;
            }
        }

        internal static int Status => _loaded ? BTDLSS_GetStatus() : -2;
        internal static uint LastResult => _loaded ? BTDLSS_GetLastResult() : 0;

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
                Flags = (hdr ? 1 : 0) | (ModConfig.DepthInverted.Value ? 8 : 0),
                Preset = preset,
                Dir = PluginDir,
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
                PreExposure = 1f,
            };

            IntPtr p = _evalRing + (_ringSlot++ & 15) * EvalSize;
            Marshal.StructureToPtr(data, p, false);
            Issue(2, p);
        }

        internal static void ReleaseFeature()
        {
            // BTDLSS releases the previous feature itself at the start of a create.
        }

        internal static void Shutdown()
        {
            if (!_loaded) return;
            Issue(3, IntPtr.Zero);
        }

        private static void Issue(int eventId, IntPtr data)
        {
            if (_cb == null) _cb = new CommandBuffer { name = "BTDLSS" };
            _cb.Clear();
            _cb.IssuePluginEventAndData(_eventFn, eventId, data);
            Graphics.ExecuteCommandBuffer(_cb);
        }
    }
}
