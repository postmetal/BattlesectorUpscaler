#include "ngx_feature.h"

#include <d3d11.h>

#include <nvsdk_ngx.h>
#include <nvsdk_ngx_helpers.h>
#include <nvsdk_ngx_helpers_d3d.h>

#include <string>

#include "log.h"

namespace
{
    // The driver validates that a custom project id looks like a GUID.
    const char* kProjectId = "b4c0f1a2-7e3d-4a5b-9c8d-0123456789ab";

    ID3D11Device* g_device = nullptr;
    ID3D11DeviceContext* g_context = nullptr;
    NVSDK_NGX_Parameter* g_parameters = nullptr;
    NVSDK_NGX_Handle* g_feature = nullptr;
    HMODULE g_dlssRuntime = nullptr;
    std::wstring g_moduleDir;
    std::wstring g_appDataDir;
    int g_status = 0;
    uint32_t g_lastResult = 0;

    // NGX writes logs and temporary files here. The game folder may be read-only, so use
    // %LOCALAPPDATA%\BattlesectorUpscaler and create it if needed.
    const wchar_t* ApplicationDataPath()
    {
        if (!g_appDataDir.empty()) return g_appDataDir.c_str();

        wchar_t buffer[MAX_PATH] = {};
        DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
        if (length > 0 && length < MAX_PATH)
        {
            g_appDataDir = std::wstring(buffer) + L"\\BattlesectorUpscaler";
            CreateDirectoryW(g_appDataDir.c_str(), nullptr);
        }

        return g_appDataDir.empty() ? L"" : g_appDataDir.c_str();
    }

    void SetInt(const char* name, int value)
    {
        if (g_parameters != nullptr) g_parameters->Set(name, value);
    }

    void SetUInt(const char* name, unsigned int value)
    {
        if (g_parameters != nullptr) g_parameters->Set(name, value);
    }

    void SetFloat(const char* name, float value)
    {
        if (g_parameters != nullptr) g_parameters->Set(name, value);
    }

    // NGX loads nvngx_dlss.dll by name and the mod lives in BepInEx\plugins, which is not in the
    // default search path. Load it explicitly from the plugin folder first, then from the game
    // folder, and keep it loaded for the process lifetime.
    void LoadDlssRuntime()
    {
        if (g_dlssRuntime != nullptr) return;

        if (!g_moduleDir.empty())
        {
            std::wstring local = g_moduleDir + L"\\nvngx_dlss.dll";
            if (GetFileAttributesW(local.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                g_dlssRuntime = LoadLibraryW(local.c_str());
                BsLog("NGX: loading runtime from plugin folder (%s)", g_dlssRuntime ? "ok" : "failed");
                return;
            }
        }

        g_dlssRuntime = LoadLibraryW(L"nvngx_dlss.dll");
        BsLog("NGX: loading runtime from default search path (%s)", g_dlssRuntime ? "ok" : "failed");
    }

    bool EnsureInitialised(ID3D11Device* device)
    {
        if (g_device != nullptr) return true;

        LoadDlssRuntime();

        // Tell NGX where to look for its feature DLLs in addition to the application folder.
        static const wchar_t* paths[1] = { nullptr };
        static std::wstring moduleDirCopy;
        moduleDirCopy = g_moduleDir;
        paths[0] = moduleDirCopy.c_str();

        NVSDK_NGX_FeatureCommonInfo featureInfo = {};
        featureInfo.PathListInfo.Path = paths;
        featureInfo.PathListInfo.Length = moduleDirCopy.empty() ? 0 : 1;

        // Make the plugin folder visible to NGX while it resolves dependencies.
        if (!g_moduleDir.empty()) SetDllDirectoryW(g_moduleDir.c_str());

        NVSDK_NGX_Result result = NVSDK_NGX_D3D11_Init_with_ProjectID(
            kProjectId,
            NVSDK_NGX_ENGINE_TYPE_CUSTOM,
            "1.0",
            ApplicationDataPath(),
            device,
            &featureInfo);

        if (!g_moduleDir.empty()) SetDllDirectoryW(nullptr);

        if (NVSDK_NGX_FAILED(result))
        {
            g_lastResult = static_cast<uint32_t>(result);
            BsLog("NGX: D3D11_Init failed, result 0x%08X", g_lastResult);
            return false;
        }

        result = NVSDK_NGX_D3D11_GetCapabilityParameters(&g_parameters);
        if (NVSDK_NGX_FAILED(result) || g_parameters == nullptr)
        {
            g_lastResult = static_cast<uint32_t>(result);
            BsLog("NGX: GetCapabilityParameters failed, result 0x%08X", g_lastResult);
            return false;
        }

        g_device = device;
        g_device->AddRef();
        g_device->GetImmediateContext(&g_context);
        BsLog("NGX: initialised with project id %s.", kProjectId);
        return true;
    }
}

void NgxSetModuleDir(const wchar_t* directory)
{
    if (directory != nullptr) g_moduleDir = directory;
}

bool NgxCreate(const CreateParams& p)
{
    g_status = 0;
    if (p.anyTexture == nullptr)
    {
        BsLog("NGX: create called without a texture.");
        g_status = -1;
        return false;
    }

    auto* texture = static_cast<ID3D11Texture2D*>(p.anyTexture);
    ID3D11Device* device = nullptr;
    texture->GetDevice(&device);
    if (device == nullptr)
    {
        BsLog("NGX: texture has no device.");
        g_status = -1;
        return false;
    }

    bool ok = EnsureInitialised(device);
    device->Release();
    if (!ok)
    {
        g_status = -1;
        return false;
    }

    NgxRelease();

    SetUInt(NVSDK_NGX_Parameter_CreationNodeMask, 1);
    SetUInt(NVSDK_NGX_Parameter_VisibilityNodeMask, 1);
    SetUInt(NVSDK_NGX_Parameter_Width, static_cast<unsigned int>(p.renderW));
    SetUInt(NVSDK_NGX_Parameter_Height, static_cast<unsigned int>(p.renderH));
    SetUInt(NVSDK_NGX_Parameter_OutWidth, static_cast<unsigned int>(p.outW));
    SetUInt(NVSDK_NGX_Parameter_OutHeight, static_cast<unsigned int>(p.outH));
    SetInt(NVSDK_NGX_Parameter_PerfQualityValue, p.quality);
    SetInt(NVSDK_NGX_Parameter_DLSS_Feature_Create_Flags, p.flags);

    if (p.preset > 0)
    {
        const unsigned int preset = static_cast<unsigned int>(p.preset);

        // NGX expects the hint on the parameter matching the selected quality mode. Setting all
        // of them at once can be ignored (the previous build did that and the driver kept its
        // default preset, which looked soft).
        const char* presetName = nullptr;
        switch (p.quality)
        {
        case 0: presetName = NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance; break;
        case 1: presetName = NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced; break;
        case 2: presetName = NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality; break;
        case 3: presetName = NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance; break;
        case 4: presetName = NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraQuality; break;
        case 5: presetName = NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA; break;
        default: break;
        }

        if (presetName != nullptr)
        {
            SetUInt(presetName, preset);
            BsLog("NGX: preset hint %u for quality %d (%s)", preset, p.quality, presetName);
        }
    }

    NVSDK_NGX_Result result = NVSDK_NGX_D3D11_CreateFeature(
        g_context, NVSDK_NGX_Feature_SuperSampling, g_parameters, &g_feature);

    if (NVSDK_NGX_FAILED(result) || g_feature == nullptr)
    {
        g_lastResult = static_cast<uint32_t>(result);
        BsLog("NGX: CreateFeature failed, result 0x%08X (%dx%d -> %dx%d, quality %d)",
              g_lastResult, p.renderW, p.renderH, p.outW, p.outH, p.quality);
        g_status = -1;
        return false;
    }

    int initResult = 0;
    g_parameters->Get(NVSDK_NGX_Parameter_SuperSampling_FeatureInitResult, &initResult);
    if (initResult == 0)
    {
        BsLog("NGX: feature reported init failure.");
        g_lastResult = static_cast<uint32_t>(initResult);
        NgxRelease();
        g_status = -1;
        return false;
    }

    g_status = 1;
    BsLog("NGX: feature created %dx%d -> %dx%d, quality %d, flags 0x%X",
          p.renderW, p.renderH, p.outW, p.outH, p.quality, p.flags);
    return true;
}

void NgxEvaluate(const EvalParams& p)
{
    if (g_feature == nullptr || g_context == nullptr || g_parameters == nullptr) return;
    if (p.color == nullptr || p.depth == nullptr || p.motion == nullptr || p.output == nullptr) return;

    // Note the argument order: (parameters, name, resource).
    NVSDK_NGX_Parameter_SetD3d11Resource(g_parameters, NVSDK_NGX_Parameter_Color, static_cast<ID3D11Resource*>(p.color));
    NVSDK_NGX_Parameter_SetD3d11Resource(g_parameters, NVSDK_NGX_Parameter_Depth, static_cast<ID3D11Resource*>(p.depth));
    NVSDK_NGX_Parameter_SetD3d11Resource(g_parameters, NVSDK_NGX_Parameter_MotionVectors, static_cast<ID3D11Resource*>(p.motion));
    NVSDK_NGX_Parameter_SetD3d11Resource(g_parameters, NVSDK_NGX_Parameter_Output, static_cast<ID3D11Resource*>(p.output));

    SetFloat(NVSDK_NGX_Parameter_Jitter_Offset_X, p.jitterX);
    SetFloat(NVSDK_NGX_Parameter_Jitter_Offset_Y, p.jitterY);
    SetFloat(NVSDK_NGX_Parameter_MV_Scale_X, p.mvScaleX);
    SetFloat(NVSDK_NGX_Parameter_MV_Scale_Y, p.mvScaleY);
    SetFloat(NVSDK_NGX_Parameter_Sharpness, p.sharpness);
    SetInt(NVSDK_NGX_Parameter_Reset, p.reset);
    SetUInt(NVSDK_NGX_Parameter_Width, static_cast<unsigned int>(p.renderW));
    SetUInt(NVSDK_NGX_Parameter_Height, static_cast<unsigned int>(p.renderH));
    SetUInt(NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Width, static_cast<unsigned int>(p.renderW));
    SetUInt(NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Height, static_cast<unsigned int>(p.renderH));

    // The NGX debug overlay is drawn in its own space; without this the text is mirrored
    // left-to-right on this swapchain.
    SetInt(NVSDK_NGX_Parameter_DLSS_Indicator_Invert_X_Axis, 1);
    SetInt(NVSDK_NGX_Parameter_DLSS_Indicator_Invert_Y_Axis, 0);

    NVSDK_NGX_Result result = NVSDK_NGX_D3D11_EvaluateFeature(
        g_context, g_feature, g_parameters, nullptr);

    if (NVSDK_NGX_FAILED(result))
    {
        g_lastResult = static_cast<uint32_t>(result);
        BsLog("NGX: EvaluateFeature failed, result 0x%08X", g_lastResult);
    }
}

void NgxRelease()
{
    if (g_feature != nullptr)
    {
        NVSDK_NGX_D3D11_ReleaseFeature(g_feature);
        g_feature = nullptr;
    }
    g_status = 0;
}

void NgxShutdown()
{
    NgxRelease();

    if (g_parameters != nullptr)
    {
        g_parameters = nullptr;
    }

    if (g_context != nullptr)
    {
        g_context->Release();
        g_context = nullptr;
    }

    if (g_device != nullptr)
    {
        NVSDK_NGX_D3D11_Shutdown1(g_device);
        g_device->Release();
        g_device = nullptr;
    }

    BsLog("NGX: shut down.");
}

int NgxStatus()
{
    return g_status;
}

uint32_t NgxLastResult()
{
    return g_lastResult;
}
