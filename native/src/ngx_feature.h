#pragma once

#include <cstdint>

#include "common.h"

// Directory of BattlesectorUpscalerNative.dll, used to locate nvngx_dlss.dll.
void NgxSetModuleDir(const wchar_t* directory);

// Creates the DLSS feature. Must run on the render thread with a valid immediate context.
bool NgxCreate(const CreateParams& params);
void NgxEvaluate(const EvalParams& params);
void NgxRelease();
void NgxShutdown();

// 0 = nothing created yet, 1 = ready, negative = failed.
int NgxStatus();
uint32_t NgxLastResult();
