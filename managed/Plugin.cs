using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace BattlesectorUpscaler
{
    /// <summary>
    /// BepInEx 6 IL2CPP entry point. Does no rendering itself: owns the <see cref="UpscaleManager"/>
    /// and forwards the camera callbacks it needs.
    ///
    /// Camera.onPreCull is a field of an IL2CPP delegate type, not a System delegate, so "+= "
    /// does not compile. It exposes an implicit conversion from Action&lt;Camera&gt; and an
    /// addition operator, which is what the helpers below use.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BasePlugin
    {
        public const string Guid = "com.battlesector.upscaler";
        public const string Name = "Battlesector Upscaler";
        public const string Version = "0.1.0";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Logger { get; private set; }

        private UpscaleManager _manager;
        private Camera.CameraCallback _preCull;
        private Camera.CameraCallback _postRender;

        public override void Load()
        {
            Instance = this;
            Logger = base.Log;
            ModConfig.Bind(Config);

            _manager = new UpscaleManager();
            _preCull = (Action<Camera>)_manager.OnPreCullEntry;
            _postRender = (Action<Camera>)_manager.OnPostRender;

            Camera.onPreCull = Add(Camera.onPreCull, _preCull);
            Camera.onPostRender = Add(Camera.onPostRender, _postRender);

            Logger.LogInfo(Name + " " + Version + " loaded.");
            Logger.LogInfo("Game dir: " + Paths.GameRootPath);
        }

        public override bool Unload()
        {
            if (_manager != null)
            {
                Camera.onPreCull = Remove(Camera.onPreCull, _preCull);
                Camera.onPostRender = Remove(Camera.onPostRender, _postRender);
                _manager.Dispose();
                _manager = null;
            }

            return true;
        }

        private static Camera.CameraCallback Add(Camera.CameraCallback existing, Camera.CameraCallback callback)
        {
            return existing == null ? callback : existing + callback;
        }

        private static Camera.CameraCallback Remove(Camera.CameraCallback existing, Camera.CameraCallback callback)
        {
            if (existing == null) return null;
            Camera.CameraCallback result = existing - callback;
            return result;
        }
    }
}
