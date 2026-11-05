#include "common.h"

#include <cwchar>

#include "camera_mv.h"
#include "log.h"
#include "ngx_feature.h"

namespace
{
    void BS_INTERFACE_API OnRenderEvent(int eventId, void* data)
    {
        switch (eventId)
        {
        case BsEventCreate:
            if (data != nullptr) NgxCreate(*static_cast<CreateParams*>(data));
            break;

        case BsEventEvaluate:
            if (data != nullptr) NgxEvaluate(*static_cast<EvalParams*>(data));
            break;

        case BsEventRelease:
            NgxRelease();
            break;

        case BsEventCameraMotionVectors:
            if (data != nullptr) CameraMvDispatch(*static_cast<MvParams*>(data));
            break;

        case BsEventGammaEncode:
            if (data != nullptr) GammaEncodeDispatch(*static_cast<GammaParams*>(data));
            break;

        default:
            break;
        }
    }
}

extern "C"
{
    __declspec(dllexport) void* BSUpscaler_GetEventFunc()
    {
        return reinterpret_cast<void*>(&OnRenderEvent);
    }

    __declspec(dllexport) void BSUpscaler_SetLogPath(const wchar_t* path)
    {
        BsLogInit(path);
        BsLog("BattlesectorUpscalerNative loaded.");
    }

    __declspec(dllexport) int BSUpscaler_GetStatus()
    {
        static unsigned counter = 0;
        int status = NgxStatus();
        if (++counter % 600 == 0)
        {
            BsLog("NGX status heartbeat: status=%d (polled %u times)", status, counter);
        }
        return status;
    }

    __declspec(dllexport) uint32_t BSUpscaler_GetLastResult()
    {
        return NgxLastResult();
    }

    __declspec(dllexport) int BSUpscaler_StructSizes(int which)
    {
        switch (which)
        {
        case 0: return static_cast<int>(sizeof(CreateParams));
        case 1: return static_cast<int>(sizeof(EvalParams));
        case 2: return static_cast<int>(sizeof(MvParams));
        case 3: return static_cast<int>(sizeof(GammaParams));
        default: return -1;
        }
    }

    __declspec(dllexport) void BSUpscaler_ReleaseFeature()
    {
        NgxRelease();
    }

    __declspec(dllexport) void BSUpscaler_Shutdown()
    {
        NgxShutdown();
        CameraMvShutdown();
        GammaShutdown();
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        wchar_t path[MAX_PATH] = {};
        DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
        if (length > 0 && length < MAX_PATH)
        {
            wchar_t* slash = wcsrchr(path, L'\\');
            if (slash != nullptr)
            {
                *slash = L'\0';
                NgxSetModuleDir(path);
            }
        }
    }

    // Deliberately no cleanup here: DllMain runs under the loader lock and the D3D device may
    // already be gone. The managed plugin calls BSUpscaler_Shutdown() from OnDestroy instead.
    return TRUE;
}
