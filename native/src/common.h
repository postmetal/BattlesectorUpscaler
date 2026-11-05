#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <cstdint>

// Unity calls this on the render thread. The signature must match
// UnityRenderingEventAndData from IUnityGraphics.h.
#define BS_INTERFACE_API __stdcall
typedef void(BS_INTERFACE_API* BsRenderingEvent)(int eventId, void* data);

// Event ids shared with the managed side.
enum BsEvent
{
    BsEventCreate = 1,
    BsEventEvaluate = 2,
    BsEventRelease = 3,
    BsEventCameraMotionVectors = 4,
    BsEventGammaEncode = 5,
};

// These structs are marshalled from C# with LayoutKind.Sequential; field order and sizes
// must match managed/DlssBridge.cs exactly. BSUpscaler_StructSizes() lets the managed side
// verify that at load time instead of corrupting memory later.
#pragma pack(push, 8)
struct CreateParams
{
    void* anyTexture;
    int renderW;
    int renderH;
    int outW;
    int outH;
    int quality;
    int flags;
    int preset;
    int slot;
};

struct EvalParams
{
    void* color;
    void* depth;
    void* motion;
    void* output;
    float jitterX;
    float jitterY;
    float mvScaleX;
    float mvScaleY;
    int reset;
    int renderW;
    int renderH;
    float sharpness;
};

struct MvParams
{
    void* depth;
    void* motion;
    int width;
    int height;
    float* invVPJittered;
    float* vpCurrent;
    float* vpPrevious;
};

struct GammaParams
{
    void* source;
    void* destination;
    int width;
    int height;
};
#pragma pack(pop)

static_assert(sizeof(CreateParams) == 40, "CreateParams layout changed; update DlssBridge.cs");
static_assert(sizeof(EvalParams) == 64, "EvalParams layout changed; update DlssBridge.cs");
static_assert(sizeof(MvParams) == 48, "MvParams layout changed; update DlssBridge.cs");
static_assert(sizeof(GammaParams) == 24, "GammaParams layout changed; update OwnDlss.cs");
