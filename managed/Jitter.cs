using UnityEngine;

namespace BattlesectorUpscaler
{
    /// <summary>
    /// Sub-pixel camera jitter. DLSS needs the projection to move by a known sub-pixel offset
    /// every frame; that offset is what turns N slightly different images into one high-resolution
    /// image. The same offset must be reported to DLSS in pixels.
    /// </summary>
    internal static class Jitter
    {
        private const int PhaseCount = 16;

        private static int _phase;
        private static int _width = 1;
        private static int _height = 1;

        /// <summary>Current jitter in render-resolution pixels.</summary>
        internal static Vector2 Pixels { get; private set; }

        internal static void SetTarget(int renderWidth, int renderHeight)
        {
            if (_width == renderWidth && _height == renderHeight) return;
            _width = Mathf.Max(1, renderWidth);
            _height = Mathf.Max(1, renderHeight);
            _phase = 0;
            Pixels = Vector2.zero;
        }

        internal static Vector2 Next()
        {
            _phase = (_phase + 1) % PhaseCount;
            float x = Halton(_phase + 1, 2) - 0.5f;
            float y = Halton(_phase + 1, 3) - 0.5f;
            Pixels = new Vector2(x * ModConfig.JitterScale.Value, y * ModConfig.JitterScale.Value);
            return Pixels;
        }

        internal static void Reset()
        {
            _phase = 0;
            Pixels = Vector2.zero;
        }

        /// <summary>Clip-space translation that produces the requested pixel offset.</summary>
        internal static Matrix4x4 Apply(Matrix4x4 projection, Vector2 pixels)
        {
            // Translation in the w column keeps the NDC offset constant with depth. Using the
            // z column (m02/m12) skews the image with depth and softens it.
            float signX = ModConfig.JitterSign.Value;
            float signY = ModConfig.JitterSignY.Value;
            float projectionScale = ModConfig.JitterProjectionScale.Value;
            Matrix4x4 shift = Matrix4x4.identity;
            shift.m02 = 2f * signX * projectionScale * pixels.x / _width;
            shift.m12 = 2f * signY * projectionScale * pixels.y / _height;
            return shift * projection;
        }

        private static float Halton(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / radix;
            while (index > 0)
            {
                result += (index % radix) * fraction;
                index /= radix;
                fraction /= radix;
            }
            return result;
        }
    }
}
