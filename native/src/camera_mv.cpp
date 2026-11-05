#include "camera_mv.h"

#include <d3d11.h>
#include <d3dcompiler.h>

#include <cstring>

#include "log.h"

namespace
{
    const char* kShaderSource = R"HLSL(
cbuffer Params : register(b0)
{
    float4x4 InvVPJittered;
    float4x4 VPCurrent;
    float4x4 VPPrevious;
    float2 InvSize;
    uint2 Size;
};

Texture2D<float> DepthTexture : register(t0);
RWTexture2D<float4> MotionOut : register(u0);

// Reconstruct the world position from the jittered depth buffer, then project it with the
// unjittered current and previous view-projection matrices. The difference is the screen-space
// motion of static geometry, which is exactly what is missing when the camera moves.
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Size.x || id.y >= Size.y)
        return;

    float depth = DepthTexture.Load(int3(id.xy, 0));
    float2 uv = (float2(id.xy) + 0.5) * InvSize;

    float4 clip = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);

    float4 world = mul(InvVPJittered, clip);
    if (abs(world.w) < 1e-6)
        return;
    world /= world.w;

    float4 current = mul(VPCurrent, world);
    float4 previous = mul(VPPrevious, world);
    if (abs(current.w) < 1e-6 || abs(previous.w) < 1e-6)
        return;

    float2 currentUv = current.xy / current.w * 0.5 + 0.5;
    float2 previousUv = previous.xy / previous.w * 0.5 + 0.5;

    // DLSS expects motion vectors in screen-space *pixels*, not UV units.
    float2 motionPixels = (previousUv - currentUv) * float2(Size);

    MotionOut[id.xy] = float4(motionPixels, 0.0, 1.0);
}
)HLSL";

    struct MvConstants
    {
        float invVPJittered[16];
        float vpCurrent[16];
        float vpPrevious[16];
        float invWidth;
        float invHeight;
        unsigned int width;
        unsigned int height;
    };

    ID3D11ComputeShader* g_shader = nullptr;
    ID3D11Buffer* g_constants = nullptr;
    ID3D11ShaderResourceView* g_depthSrv = nullptr;
    ID3D11UnorderedAccessView* g_motionUav = nullptr;
    ID3D11Texture2D* g_depthTexture = nullptr;
    ID3D11Texture2D* g_motionTexture = nullptr;
    bool g_pipelineFailed = false;

    DXGI_FORMAT DepthSrvFormat(DXGI_FORMAT format)
    {
        // Numeric values are used deliberately: the SDK enum names available to this build do
        // not line up with the driver's runtime values, which is why 19 was reported as
        // "unsupported". Runtime-verified mapping:
        //   19 R32G8X24_TYPELESS, 20 D32_FLOAT_S8X24_UINT -> 21 R32_FLOAT_X8X24_TYPELESS
        //   40 D32_FLOAT, 41 R32_FLOAT                   -> 41 R32_FLOAT
        switch (static_cast<int>(format))
        {
        case 19:
        case 20:
            return static_cast<DXGI_FORMAT>(21);
        case 40:
        case 41:
            return static_cast<DXGI_FORMAT>(41);
        default:
            return DXGI_FORMAT_UNKNOWN;
        }
    }

    bool EnsurePipeline(ID3D11Device* device)
    {
        if (g_shader != nullptr) return true;
        if (g_pipelineFailed) return false;

        ID3DBlob* bytecode = nullptr;
        ID3DBlob* errors = nullptr;
        HRESULT hr = D3DCompile(kShaderSource, strlen(kShaderSource), "CameraMV", nullptr, nullptr,
                                "CSMain", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &bytecode, &errors);

        if (FAILED(hr))
        {
            if (errors != nullptr)
            {
                BsLog("MV: shader compile failed: %s", static_cast<const char*>(errors->GetBufferPointer()));
                errors->Release();
            }
            g_pipelineFailed = true;
            return false;
        }
        if (errors != nullptr) errors->Release();

        hr = device->CreateComputeShader(bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr, &g_shader);
        bytecode->Release();

        if (FAILED(hr))
        {
            BsLog("MV: CreateComputeShader failed, hr 0x%08X", static_cast<unsigned int>(hr));
            g_pipelineFailed = true;
            return false;
        }

        D3D11_BUFFER_DESC buffer = {};
        buffer.ByteWidth = sizeof(MvConstants);
        buffer.Usage = D3D11_USAGE_DYNAMIC;
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        buffer.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

        hr = device->CreateBuffer(&buffer, nullptr, &g_constants);
        if (FAILED(hr))
        {
            BsLog("MV: CreateBuffer failed, hr 0x%08X", static_cast<unsigned int>(hr));
            g_pipelineFailed = true;
            return false;
        }

        return true;
    }

    bool EnsureDepthSrv(ID3D11Device* device, ID3D11Texture2D* texture)
    {
        if (g_depthTexture == texture && g_depthSrv != nullptr) return true;

        if (g_depthSrv != nullptr)
        {
            g_depthSrv->Release();
            g_depthSrv = nullptr;
        }
        if (g_depthTexture != nullptr)
        {
            g_depthTexture->Release();
            g_depthTexture = nullptr;
        }

        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);

        DXGI_FORMAT format = DepthSrvFormat(desc.Format);
        if (format == DXGI_FORMAT_UNKNOWN)
        {
            // Be permissive: a depth/stencil resource can almost always be viewed as R32_FLOAT.
            // This also covers SDK enum mismatches for D32S8 variants.
            format = DXGI_FORMAT_R32_FLOAT;
            BsLog("MV: depth format %d not mapped; trying R32_FLOAT", static_cast<int>(desc.Format));
        }

        BsLog("MV: depth desc fmt=%d %ux%u mips=%u array=%u samples=%u bind=0x%X",
              static_cast<int>(desc.Format), desc.Width, desc.Height, desc.MipLevels,
              desc.ArraySize, desc.SampleDesc.Count, desc.BindFlags);

        if (format == DXGI_FORMAT_UNKNOWN) format = DXGI_FORMAT_R32_FLOAT;

        const DXGI_FORMAT candidates[] =
        {
            format,
            DXGI_FORMAT_R32_FLOAT,
            DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS,
            DXGI_FORMAT_R24_UNORM_X8_TYPELESS,
        };

        HRESULT hr = E_FAIL;
        for (DXGI_FORMAT candidate : candidates)
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC srv = {};
            srv.Format = candidate;

            if (desc.SampleDesc.Count > 1)
            {
                srv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DMS;
            }
            else if (desc.ArraySize > 1)
            {
                srv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
                srv.Texture2DArray.MipLevels = 1;
                srv.Texture2DArray.ArraySize = desc.ArraySize;
            }
            else
            {
                srv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
                srv.Texture2D.MipLevels = 1;
            }

            hr = device->CreateShaderResourceView(texture, &srv, &g_depthSrv);
            if (SUCCEEDED(hr))
            {
                BsLog("MV: depth SRV created with format %d, view %d", static_cast<int>(candidate), static_cast<int>(srv.ViewDimension));
                break;
            }
        }

        if (FAILED(hr))
        {
            BsLog("MV: CreateShaderResourceView(depth) failed for all formats, hr 0x%08X", static_cast<unsigned int>(hr));
            return false;
        }

        g_depthTexture = texture;
        g_depthTexture->AddRef();
        return true;
    }

    bool EnsureMotionUav(ID3D11Device* device, ID3D11Texture2D* texture)
    {
        if (g_motionTexture == texture && g_motionUav != nullptr) return true;

        if (g_motionUav != nullptr)
        {
            g_motionUav->Release();
            g_motionUav = nullptr;
        }
        if (g_motionTexture != nullptr)
        {
            g_motionTexture->Release();
            g_motionTexture = nullptr;
        }

        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);

        BsLog("MV: motion desc fmt=%d %ux%u bind=0x%X", static_cast<int>(desc.Format),
              desc.Width, desc.Height, desc.BindFlags);

        // DXGI: 9 = R16G16B16A16_TYPELESS, 10 = R16G16B16A16_FLOAT.
        // A UAV must use the typed format, not the typeless one.
        DXGI_FORMAT uavFormat = desc.Format;
        if (static_cast<int>(uavFormat) == 9 || static_cast<int>(uavFormat) == 0)
        {
            uavFormat = static_cast<DXGI_FORMAT>(10);
        }

        D3D11_UNORDERED_ACCESS_VIEW_DESC uav = {};
        uav.Format = uavFormat;
        uav.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

        HRESULT hr = device->CreateUnorderedAccessView(texture, &uav, &g_motionUav);
        if (FAILED(hr))
        {
            BsLog("MV: UAV (format %d) failed hr 0x%08X", static_cast<int>(uavFormat), static_cast<unsigned int>(hr));
            return false;
        }

        g_motionTexture = texture;
        g_motionTexture->AddRef();
        return true;
    }
}

bool CameraMvDispatch(const MvParams& p)
{
    if (p.depth == nullptr || p.motion == nullptr) return false;
    if (p.width <= 0 || p.height <= 0) return false;

    auto* depth = static_cast<ID3D11Texture2D*>(p.depth);
    auto* motion = static_cast<ID3D11Texture2D*>(p.motion);

    ID3D11Device* device = nullptr;
    depth->GetDevice(&device);
    if (device == nullptr) return false;

    ID3D11DeviceContext* context = nullptr;
    device->GetImmediateContext(&context);
    if (context == nullptr)
    {
        device->Release();
        return false;
    }

    bool ok = EnsurePipeline(device) && EnsureDepthSrv(device, depth) && EnsureMotionUav(device, motion);

    if (ok)
    {
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        HRESULT hr = context->Map(g_constants, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        if (FAILED(hr))
        {
            ok = false;
        }
        else
        {
            auto* constants = static_cast<MvConstants*>(mapped.pData);
            memcpy(constants->invVPJittered, p.invVPJittered, sizeof(constants->invVPJittered));
            memcpy(constants->vpCurrent, p.vpCurrent, sizeof(constants->vpCurrent));
            memcpy(constants->vpPrevious, p.vpPrevious, sizeof(constants->vpPrevious));
            constants->invWidth = 1.0f / static_cast<float>(p.width);
            constants->invHeight = 1.0f / static_cast<float>(p.height);
            constants->width = static_cast<unsigned int>(p.width);
            constants->height = static_cast<unsigned int>(p.height);
            context->Unmap(g_constants, 0);

            ID3D11ShaderResourceView* srvs[] = { g_depthSrv };
            ID3D11UnorderedAccessView* uavs[] = { g_motionUav };
            ID3D11Buffer* buffers[] = { g_constants };

            context->CSSetShader(g_shader, nullptr, 0);
            context->CSSetShaderResources(0, 1, srvs);
            context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
            context->CSSetConstantBuffers(0, 1, buffers);

            const UINT groupsX = (static_cast<UINT>(p.width) + 7) / 8;
            const UINT groupsY = (static_cast<UINT>(p.height) + 7) / 8;
            context->Dispatch(groupsX, groupsY, 1);

            ID3D11ShaderResourceView* nullSrv[] = { nullptr };
            ID3D11UnorderedAccessView* nullUav[] = { nullptr };
            context->CSSetShaderResources(0, 1, nullSrv);
            context->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);
            context->CSSetShader(nullptr, nullptr, 0);
        }
    }

    context->Release();
    device->Release();
    return ok;
}

void CameraMvShutdown()
{
    if (g_depthSrv != nullptr) { g_depthSrv->Release(); g_depthSrv = nullptr; }
    if (g_motionUav != nullptr) { g_motionUav->Release(); g_motionUav = nullptr; }
    if (g_constants != nullptr) { g_constants->Release(); g_constants = nullptr; }
    if (g_shader != nullptr) { g_shader->Release(); g_shader = nullptr; }
    if (g_depthTexture != nullptr) { g_depthTexture->Release(); g_depthTexture = nullptr; }
    if (g_motionTexture != nullptr) { g_motionTexture->Release(); g_motionTexture = nullptr; }
}

namespace
{
    const char* kGammaShader = R"HLSL(
cbuffer Params : register(b0)
{
    uint2 Size;
    uint2 Padding;
};

Texture2D<float4> Source : register(t0);
RWTexture2D<float4> Destination : register(u0);

float3 LinearToSrgb(float3 c)
{
    float3 low = c * 12.92;
    float3 high = 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
    return lerp(low, high, step(0.0031308, c));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Size.x || id.y >= Size.y)
        return;
    float3 linearColor = Source.Load(int3(id.xy, 0)).rgb;
    Destination[id.xy] = float4(LinearToSrgb(linearColor), 1.0);
}
)HLSL";

    ID3D11ComputeShader* g_gammaShader = nullptr;
    ID3D11Buffer* g_gammaConstants = nullptr;
    ID3D11ShaderResourceView* g_gammaSrv = nullptr;
    ID3D11UnorderedAccessView* g_gammaUav = nullptr;
    ID3D11Texture2D* g_gammaSource = nullptr;
    ID3D11Texture2D* g_gammaDestination = nullptr;
    bool g_gammaFailed = false;

    DXGI_FORMAT TypedFormat(DXGI_FORMAT format)
    {
        // Numeric mapping: 1 = R32G32B32A32_TYPELESS -> 2 = FLOAT
        //                 9 = R16G16B16A16_TYPELESS -> 10 = FLOAT
        //                27 = R8G8B8A8_TYPELESS     -> 28 = UNORM
        const int value = static_cast<int>(format);
        if (value == 0 || value == 9) return static_cast<DXGI_FORMAT>(10);
        if (value == 1) return static_cast<DXGI_FORMAT>(2);
        if (value == 27) return static_cast<DXGI_FORMAT>(28);
        return format;
    }

    bool EnsureGammaPipeline(ID3D11Device* device)
    {
        if (g_gammaShader != nullptr) return true;
        if (g_gammaFailed) return false;

        ID3DBlob* bytecode = nullptr;
        ID3DBlob* errors = nullptr;
        HRESULT hr = D3DCompile(kGammaShader, strlen(kGammaShader), "GammaEncode", nullptr, nullptr,
                                "CSMain", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &bytecode, &errors);
        if (FAILED(hr))
        {
            if (errors != nullptr) { BsLog("Gamma: compile failed: %s", static_cast<const char*>(errors->GetBufferPointer())); errors->Release(); }
            g_gammaFailed = true;
            return false;
        }
        if (errors != nullptr) errors->Release();

        hr = device->CreateComputeShader(bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr, &g_gammaShader);
        bytecode->Release();
        if (FAILED(hr)) { BsLog("Gamma: CreateComputeShader 0x%08X", static_cast<unsigned int>(hr)); g_gammaFailed = true; return false; }

        D3D11_BUFFER_DESC buffer = {};
        buffer.ByteWidth = 16;
        buffer.Usage = D3D11_USAGE_DYNAMIC;
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        buffer.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        hr = device->CreateBuffer(&buffer, nullptr, &g_gammaConstants);
        if (FAILED(hr)) { BsLog("Gamma: CreateBuffer 0x%08X", static_cast<unsigned int>(hr)); g_gammaFailed = true; return false; }
        return true;
    }

    bool EnsureGammaViews(ID3D11Device* device, ID3D11Texture2D* source, ID3D11Texture2D* destination)
    {
        if (g_gammaSource != source || g_gammaSrv == nullptr)
        {
            if (g_gammaSrv != nullptr) { g_gammaSrv->Release(); g_gammaSrv = nullptr; }
            D3D11_TEXTURE2D_DESC desc; source->GetDesc(&desc);
            D3D11_SHADER_RESOURCE_VIEW_DESC srv = {};
            srv.Format = TypedFormat(desc.Format);
            srv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            srv.Texture2D.MipLevels = 1;
            HRESULT srvHr = device->CreateShaderResourceView(source, &srv, &g_gammaSrv);
            if (FAILED(srvHr)) { BsLog("Gamma: SRV failed fmt=%d hr=0x%08X", (int)desc.Format, (unsigned)srvHr); return false; }
            g_gammaSource = source;
        }

        if (g_gammaDestination != destination || g_gammaUav == nullptr)
        {
            if (g_gammaUav != nullptr) { g_gammaUav->Release(); g_gammaUav = nullptr; }
            D3D11_TEXTURE2D_DESC desc; destination->GetDesc(&desc);
            D3D11_UNORDERED_ACCESS_VIEW_DESC uav = {};
            uav.Format = TypedFormat(desc.Format);
            uav.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
            HRESULT uavHr = device->CreateUnorderedAccessView(destination, &uav, &g_gammaUav);
            if (FAILED(uavHr)) { BsLog("Gamma: UAV failed fmt=%d hr=0x%08X", (int)desc.Format, (unsigned)uavHr); return false; }
            g_gammaDestination = destination;
        }

        return true;
    }
}

namespace
{
    const char* kPresentShader = R"HLSL(
cbuffer PresentParams : register(b0)
{
    uint EncodeSrgb;
    uint3 Padding;
};

Texture2D<float4> Source : register(t0);
SamplerState LinearSampler : register(s0);

struct VSOut
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(uint vertexId : SV_VertexID)
{
    VSOut output;
    float2 p = float2((vertexId == 2) ? 3.0 : -1.0, (vertexId == 1) ? 3.0 : -1.0);
    output.position = float4(p, 0.0, 1.0);
    output.uv = float2((p.x + 1.0) * 0.5, 1.0 - (p.y + 1.0) * 0.5);
    return output;
}

float3 LinearToSrgb(float3 c)
{
    float3 low = c * 12.92;
    float3 high = 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
    return lerp(low, high, step(0.0031308, c));
}

float4 PSMain(VSOut input) : SV_Target
{
    float3 color = Source.Sample(LinearSampler, input.uv).rgb;
    if (EncodeSrgb != 0)
        color = LinearToSrgb(color);
    return float4(color, 1.0);
}
)HLSL";

    ID3D11VertexShader* g_presentVs = nullptr;
    ID3D11PixelShader* g_presentPs = nullptr;
    ID3D11SamplerState* g_presentSampler = nullptr;
    ID3D11Buffer* g_presentConstants = nullptr;
    bool g_presentFailed = false;

    bool EnsurePresentPipeline(ID3D11Device* device)
    {
        if (g_presentPs != nullptr) return true;
        if (g_presentFailed) return false;

        ID3DBlob* vsCode = nullptr;
        ID3DBlob* psCode = nullptr;
        ID3DBlob* errors = nullptr;

        HRESULT hr = D3DCompile(kPresentShader, strlen(kPresentShader), "PresentVS", nullptr, nullptr,
                                "VSMain", "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &vsCode, &errors);
        if (FAILED(hr)) { if (errors) { BsLog("Present: VS %s", (const char*)errors->GetBufferPointer()); errors->Release(); } g_presentFailed = true; return false; }
        if (errors) { errors->Release(); errors = nullptr; }

        hr = D3DCompile(kPresentShader, strlen(kPresentShader), "PresentPS", nullptr, nullptr,
                        "PSMain", "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &psCode, &errors);
        if (FAILED(hr)) { if (errors) { BsLog("Present: PS %s", (const char*)errors->GetBufferPointer()); errors->Release(); } vsCode->Release(); g_presentFailed = true; return false; }
        if (errors) errors->Release();

        hr = device->CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(), nullptr, &g_presentVs);
        vsCode->Release();
        if (FAILED(hr)) { BsLog("Present: CreateVertexShader 0x%08X", (unsigned)hr); g_presentFailed = true; return false; }

        hr = device->CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(), nullptr, &g_presentPs);
        psCode->Release();
        if (FAILED(hr)) { BsLog("Present: CreatePixelShader 0x%08X", (unsigned)hr); g_presentFailed = true; return false; }

        D3D11_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.ComparisonFunc = D3D11_COMPARISON_NEVER;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        if (FAILED(device->CreateSamplerState(&sampler, &g_presentSampler))) { g_presentFailed = true; return false; }

        D3D11_BUFFER_DESC buffer = {};
        buffer.ByteWidth = 16;
        buffer.Usage = D3D11_USAGE_DYNAMIC;
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        buffer.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(device->CreateBuffer(&buffer, nullptr, &g_presentConstants))) { g_presentFailed = true; return false; }

        return true;
    }

    bool IsSrgbFormat(DXGI_FORMAT format)
    {
        const int value = static_cast<int>(format);
        return value == 29 || value == 91 || value == 93 || value == 99;
    }

    bool PresentToBackbuffer(ID3D11Texture2D* source, int width, int height)
    {
        if (source == nullptr || width <= 0 || height <= 0) return false;

        ID3D11Device* device = nullptr;
        source->GetDevice(&device);
        if (device == nullptr) return false;

        ID3D11DeviceContext* context = nullptr;
        device->GetImmediateContext(&context);
        if (context == nullptr) { device->Release(); return false; }

        bool ok = EnsurePresentPipeline(device);

        ID3D11RenderTargetView* renderTarget = nullptr;
        if (ok)
        {
            context->OMGetRenderTargets(1, &renderTarget, nullptr);
            if (renderTarget == nullptr) { BsLog("Present: no backbuffer RTV"); ok = false; }
        }

        if (ok)
        {
            D3D11_RENDER_TARGET_VIEW_DESC targetDesc = {};
            renderTarget->GetDesc(&targetDesc);
            const unsigned int encode = IsSrgbFormat(targetDesc.Format) ? 0u : 1u;

            D3D11_MAPPED_SUBRESOURCE mapped = {};
            if (SUCCEEDED(context->Map(g_presentConstants, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
            {
                unsigned int* data = static_cast<unsigned int*>(mapped.pData);
                data[0] = encode;
                data[1] = 0;
                data[2] = 0;
                data[3] = 0;
                context->Unmap(g_presentConstants, 0);
            }

            D3D11_TEXTURE2D_DESC sourceDesc; source->GetDesc(&sourceDesc);
            D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
            srvDesc.Format = TypedFormat(sourceDesc.Format);
            srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            srvDesc.Texture2D.MipLevels = 1;

            ID3D11ShaderResourceView* srv = nullptr;
            if (FAILED(device->CreateShaderResourceView(source, &srvDesc, &srv)))
            {
                BsLog("Present: SRV failed fmt=%d", (int)sourceDesc.Format);
                ok = false;
            }
            else
            {
                D3D11_VIEWPORT viewport = {};
                viewport.Width = static_cast<float>(width);
                viewport.Height = static_cast<float>(height);
                viewport.MaxDepth = 1.0f;

                context->OMSetRenderTargets(1, &renderTarget, nullptr);
                context->RSSetViewports(1, &viewport);
                context->IASetInputLayout(nullptr);
                context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                context->VSSetShader(g_presentVs, nullptr, 0);
                context->PSSetShader(g_presentPs, nullptr, 0);
                ID3D11ShaderResourceView* srvs[] = { srv };
                ID3D11SamplerState* samplers[] = { g_presentSampler };
                ID3D11Buffer* buffers[] = { g_presentConstants };
                context->PSSetShaderResources(0, 1, srvs);
                context->PSSetSamplers(0, 1, samplers);
                context->PSSetConstantBuffers(0, 1, buffers);
                context->Draw(3, 0);

                ID3D11ShaderResourceView* nullSrv[] = { nullptr };
                context->PSSetShaderResources(0, 1, nullSrv);
                context->PSSetShader(nullptr, nullptr, 0);
                context->VSSetShader(nullptr, nullptr, 0);
                srv->Release();
                BsLog("Present: drew %dx%d to backbuffer fmt=%d encode=%u", width, height, (int)targetDesc.Format, encode);
            }
        }

        if (renderTarget != nullptr) renderTarget->Release();
        context->Release();
        device->Release();
        return ok;
    }
}

bool GammaEncodeDispatch(const GammaParams& p)
{
    if (p.destination == nullptr)
        return PresentToBackbuffer(static_cast<ID3D11Texture2D*>(p.source), p.width, p.height);
    if (p.source == nullptr || p.destination == nullptr || p.width <= 0 || p.height <= 0) return false;

    auto* source = static_cast<ID3D11Texture2D*>(p.source);
    auto* destination = static_cast<ID3D11Texture2D*>(p.destination);

    ID3D11Device* device = nullptr;
    source->GetDevice(&device);
    if (device == nullptr) return false;

    ID3D11DeviceContext* context = nullptr;
    device->GetImmediateContext(&context);
    if (context == nullptr) { device->Release(); return false; }

    bool ok = EnsureGammaPipeline(device) && EnsureGammaViews(device, source, destination);

    if (ok)
    {
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        if (FAILED(context->Map(g_gammaConstants, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        {
            ok = false;
        }
        else
        {
            unsigned int* data = static_cast<unsigned int*>(mapped.pData);
            data[0] = static_cast<unsigned int>(p.width);
            data[1] = static_cast<unsigned int>(p.height);
            data[2] = 0;
            data[3] = 0;
            context->Unmap(g_gammaConstants, 0);

            ID3D11ShaderResourceView* srvs[] = { g_gammaSrv };
            ID3D11UnorderedAccessView* uavs[] = { g_gammaUav };
            ID3D11Buffer* buffers[] = { g_gammaConstants };

            context->CSSetShader(g_gammaShader, nullptr, 0);
            context->CSSetShaderResources(0, 1, srvs);
            context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
            context->CSSetConstantBuffers(0, 1, buffers);
            context->Dispatch((static_cast<UINT>(p.width) + 7) / 8, (static_cast<UINT>(p.height) + 7) / 8, 1);

            ID3D11ShaderResourceView* nullSrv[] = { nullptr };
            ID3D11UnorderedAccessView* nullUav[] = { nullptr };
            context->CSSetShaderResources(0, 1, nullSrv);
            context->CSSetUnorderedAccessViews(0, 1, nullUav, nullptr);
            context->CSSetShader(nullptr, nullptr, 0);

            static bool loggedPixel = false;
            if (!loggedPixel)
            {
                loggedPixel = true;

                auto readCenter = [&](ID3D11Texture2D* texture, const char* label)
                {
                    if (texture == nullptr) { BsLog("Gamma: %s null", label); return; }
                    D3D11_TEXTURE2D_DESC desc; texture->GetDesc(&desc);
                    D3D11_TEXTURE2D_DESC stagingDesc = desc;
                    stagingDesc.Usage = D3D11_USAGE_STAGING;
                    stagingDesc.BindFlags = 0;
                    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    stagingDesc.MiscFlags = 0;
                    ID3D11Texture2D* staging = nullptr;
                    if (FAILED(device->CreateTexture2D(&stagingDesc, nullptr, &staging))) { BsLog("Gamma: %s staging failed", label); return; }
                    context->CopyResource(staging, texture);
                    D3D11_MAPPED_SUBRESOURCE mappedRead = {};
                    if (FAILED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mappedRead))) { BsLog("Gamma: %s map failed", label); staging->Release(); return; }
                    const unsigned char* row = static_cast<const unsigned char*>(mappedRead.pData) + (desc.Height / 2) * mappedRead.RowPitch;
                    const float* pixel = reinterpret_cast<const float*>(row + (desc.Width / 2) * 16);
                    BsLog("Gamma: %s center = %.3f %.3f %.3f", label, pixel[0], pixel[1], pixel[2]);
                    context->Unmap(staging, 0);
                    staging->Release();
                };

                readCenter(source, "source");
                readCenter(destination, "destination");
            }
        }
    }

    context->Release();
    device->Release();
    BsLog("Gamma: encode %dx%d %s", p.width, p.height, ok ? "ok" : "failed");
    return ok;
}

void GammaShutdown()
{
    if (g_gammaSrv != nullptr) { g_gammaSrv->Release(); g_gammaSrv = nullptr; }
    if (g_gammaUav != nullptr) { g_gammaUav->Release(); g_gammaUav = nullptr; }
    if (g_gammaConstants != nullptr) { g_gammaConstants->Release(); g_gammaConstants = nullptr; }
    if (g_gammaShader != nullptr) { g_gammaShader->Release(); g_gammaShader = nullptr; }
    g_gammaSource = nullptr;
    g_gammaDestination = nullptr;
}
