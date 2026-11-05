#pragma once

#include <string>

// Minimal, thread-safe logger. NGX and D3D failures are otherwise invisible from inside Unity.
void BsLogInit(const wchar_t* path);
void BsLog(const char* format, ...);
void BsLogWide(const wchar_t* message);
