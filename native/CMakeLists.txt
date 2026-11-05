cmake_minimum_required(VERSION 3.20)
project(BattlesectorUpscalerNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static CRT keeps the DLL free of a VC++ redistributable dependency, which matters for a mod
# that is dropped into a game folder.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(NOT DLSS_SDK)
    set(DLSS_SDK "$ENV{DLSS_SDK}")
endif()

if(NOT DLSS_SDK)
    message(FATAL_ERROR
        "DLSS_SDK is not set. Point it at the root of the NVIDIA DLSS SDK "
        "(the folder that contains include/ and lib/). Download: https://github.com/NVIDIA/DLSS")
endif()

if(NOT EXISTS "${DLSS_SDK}/include/nvsdk_ngx.h")
    message(FATAL_ERROR
        "nvsdk_ngx.h was not found under ${DLSS_SDK}/include . Download "
        "https://github.com/NVIDIA/DLSS/archive/refs/heads/main.zip and copy its include folder "
        "into ${DLSS_SDK}.")
endif()

add_library(BattlesectorUpscalerNative SHARED
    src/bridge.cpp
    src/ngx_feature.cpp
    src/camera_mv.cpp
    src/log.cpp
)

target_include_directories(BattlesectorUpscalerNative PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${DLSS_SDK}/include"
)

target_compile_options(BattlesectorUpscalerNative PRIVATE /W3 /EHsc /permissive-)
target_compile_definitions(BattlesectorUpscalerNative PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

find_library(NGX_LIBRARY
    NAMES nvsdk_ngx_s nvsdk_ngx
    PATHS "${DLSS_SDK}/lib/Windows_x86_64/x64" "${DLSS_SDK}/lib/Windows/x64"
    NO_DEFAULT_PATH
)

if(NOT NGX_LIBRARY)
    message(FATAL_ERROR
        "nvsdk_ngx_s.lib was not found under ${DLSS_SDK}/lib/Windows_x86_64/x64 . "
        "Download the full DLSS SDK repository zip, not just the headers.")
endif()

target_link_libraries(BattlesectorUpscalerNative PRIVATE
    "${NGX_LIBRARY}"
    d3d11
    dxgi
    d3dcompiler
    version
)

set_target_properties(BattlesectorUpscalerNative PROPERTIES
    PREFIX ""
    OUTPUT_NAME "BattlesectorUpscalerNative"
)
