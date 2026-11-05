using BepInEx.Configuration;
using UnityEngine;

namespace BattlesectorUpscaler
{
    public enum UpscaleMode
    {
        Off = 0,
        Dlaa = 1,
        UltraQuality = 2,
        Quality = 3,
        Balanced = 4,
        Performance = 5,
        UltraPerformance = 6,
        Custom = 7,
    }

    /// <summary>
    /// How far the pipeline is allowed to go. Useful for bringing the mod up stage by stage:
    /// BlitOnly proves camera/RT plumbing without NVIDIA, DlssNoMotionVectors proves NGX,
    /// DlssCameraMotionVectors adds the depth-reprojection velocity pass.
    /// </summary>
    public enum PipelineStage
    {
        BlitOnly = 0,
        DlssNoMotionVectors = 1,
        DlssCameraMotionVectors = 2,
        NgxOnly = 3,
    }

    public enum ColorMode
    {
        Hdr = 0,
        LdrSrgb = 1,
        LdrLinear = 2,
    }

    public enum PresentMode
    {
        PresenterCamera = 0,
        MainCameraPostRender = 1,
    }

    internal static class ModConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<UpscaleMode> Mode;
        internal static ConfigEntry<float> CustomScale;
        internal static ConfigEntry<PipelineStage> Stage;
        internal static ConfigEntry<PresentMode> Present;
        internal static ConfigEntry<ColorMode> Color;
        internal static ConfigEntry<int> DlssPreset;
        internal static ConfigEntry<float> Sharpness;
        internal static ConfigEntry<float> JitterScale;
        internal static ConfigEntry<float> JitterSign;
        internal static ConfigEntry<float> JitterSignY;
        internal static ConfigEntry<float> JitterReportSign;
        internal static ConfigEntry<float> JitterProjectionScale;
        internal static ConfigEntry<bool> DepthInverted;
        internal static ConfigEntry<float> MvScaleX;
        internal static ConfigEntry<float> MvScaleY;
        internal static ConfigEntry<string> CameraName;
        internal static ConfigEntry<bool> Verbose;

        internal static void Bind(ConfigFile file)
        {
            Enabled = file.Bind("General", "Enabled", true,
                "Master switch. When off, the game renders exactly as it normally does.");

            Mode = file.Bind("General", "Mode", UpscaleMode.Dlaa,
                "DLAA renders at full resolution and only anti-aliases. The other modes render the 3D scene " +
                "smaller and upscale it. Custom uses RenderScale.");

            CustomScale = file.Bind("General", "RenderScale", 0.67f,
                new ConfigDescription("Render resolution multiplier used by Custom mode.",
                    new AcceptableValueRange<float>(0.25f, 1f)));

            Stage = file.Bind("General", "Stage", PipelineStage.DlssCameraMotionVectors,
                "Bring-up stage. BlitOnly = no DLSS, just the low-res render blitted back (sanity check). " +
                "DlssNoMotionVectors = DLSS with zero motion vectors. DlssCameraMotionVectors = full pipeline.");

            Present = file.Bind("General", "PresentMode", PresentMode.MainCameraPostRender,
                "PresenterCamera adds a second camera that only blits the upscaled frame; it is the safer " +
                "choice when the game has a separate UI camera. MainCameraPostRender blits at the end of the " +
                "3D camera and is cheaper, but a UI camera that clears colour will wipe the frame.");

            DlssPreset = file.Bind("DLSS", "Preset", 0,
                new ConfigDescription("NVIDIA render preset: 0 = automatic, 10 = K, 11 = K (newer SDKs), " +
                    "12 = L, 13 = M. Presets L and M cost noticeably more GPU time.",
                    new AcceptableValueRange<int>(0, 20)));

            Color = file.Bind("General", "ColorMode", ColorMode.Hdr,
                "Hdr keeps the camera's linear HDR output. LdrSrgb and LdrLinear use an 8-bit target instead. " +
                "If colours look washed out or too dark, try LdrSrgb first.");

            Sharpness = file.Bind("DLSS", "Sharpness", 0f,
                new ConfigDescription("Extra sharpening applied by DLSS (0 = off).",
                    new AcceptableValueRange<float>(0f, 1f)));

            JitterScale = file.Bind("DLSS", "JitterScale", 1f,
                new ConfigDescription("Multiplier on the sub-pixel camera jitter. 1 is correct; lower it only " +
                    "if the image shimmers.", new AcceptableValueRange<float>(0f, 2f)));

            JitterSign = file.Bind("DLSS", "JitterSign", 1f,
                new ConfigDescription("Sign of the jitter applied to the projection matrix. " +
                    "DLSS is told the positive offset. If the image is uniformly soft, try -1.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            JitterSignY = file.Bind("DLSS", "JitterSignY", 1f,
                new ConfigDescription("Sign of the Y jitter applied to the projection. D3D flips Y, so try -1 if the image is soft.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            JitterReportSign = file.Bind("DLSS", "JitterReportSign", -1f,
                new ConfigDescription("Sign of the jitter value reported to DLSS. The projection offset is unchanged; try -1 if the image is soft at full jitter.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            JitterProjectionScale = file.Bind("DLSS", "JitterProjectionScale", 1f,
                new ConfigDescription("Scale of the projection-matrix jitter relative to the offset reported to DLSS. " +
                    "If full jitter is soft but half is sharp, this is the knob that fixes it.",
                    new AcceptableValueRange<float>(0.1f, 2f)));

            DepthInverted = file.Bind("DLSS", "DepthInverted", true,

                "Unity on Direct3D uses reversed depth (1 at the near plane). DLSS needs to be told. " +
                "Flip this if the upscaled image looks like it is misreconstructing geometry.");

            MvScaleX = file.Bind("DLSS", "MotionVectorScaleX", -1f,
                new ConfigDescription("Sign/scale calibration for motion vectors, X.", new AcceptableValueRange<float>(-4f, 4f)));
            MvScaleY = file.Bind("DLSS", "MotionVectorScaleY", -1f,
                new ConfigDescription("Sign/scale calibration for motion vectors, Y.", new AcceptableValueRange<float>(-4f, 4f)));

            CameraName = file.Bind("General", "CameraName", "",
                "Leave empty to auto-detect the 3D camera. Set a GameObject name if the mod picks the wrong one.");

            Verbose = file.Bind("Debug", "Verbose", false,
                "Log per-frame details (noisy).");
        }

        internal static float Scale
        {
            get
            {
                switch (Mode.Value)
                {
                    case UpscaleMode.Dlaa: return 1f;
                    case UpscaleMode.UltraQuality: return 0.77f;
                    case UpscaleMode.Quality: return 0.67f;
                    case UpscaleMode.Balanced: return 0.58f;
                    case UpscaleMode.Performance: return 0.50f;
                    case UpscaleMode.UltraPerformance: return 0.33f;
                    case UpscaleMode.Custom: return Mathf.Clamp(CustomScale.Value, 0.2f, 1f);
                    default: return 1f;
                }
            }
        }

        internal static bool UsesDlss => Stage.Value != PipelineStage.BlitOnly && Mode.Value != UpscaleMode.Off;
    }
}
