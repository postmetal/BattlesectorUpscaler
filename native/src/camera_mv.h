#pragma once

#include "common.h"

// Depth-reprojection camera motion vectors. Runs as a compute pass on the render thread,
// writing straight into the velocity texture that DLSS consumes.
bool CameraMvDispatch(const MvParams& params);
void CameraMvShutdown();

// Linear -> sRGB encode for the final present, bypassing Unity's blit colour handling.
bool GammaEncodeDispatch(const GammaParams& params);
void GammaShutdown();
