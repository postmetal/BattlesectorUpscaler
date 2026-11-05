#include "log.h"

#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <mutex>

namespace
{
    std::mutex g_logMutex;
    FILE* g_logFile = nullptr;
}

void BsLogInit(const wchar_t* path)
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (g_logFile != nullptr)
    {
        fclose(g_logFile);
        g_logFile = nullptr;
    }
    if (path == nullptr || path[0] == L'\0') return;
    g_logFile = _wfopen(path, L"w");
}

void BsLog(const char* format, ...)
{
    char buffer[2048];
    va_list args;
    va_start(args, format);
    vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, format, args);
    va_end(args);

    SYSTEMTIME time;
    GetLocalTime(&time);

    std::lock_guard<std::mutex> lock(g_logMutex);
    if (g_logFile != nullptr)
    {
        fprintf(g_logFile, "[%02d:%02d:%02d.%03d] %s\n", time.wHour, time.wMinute, time.wSecond,
                time.wMilliseconds, buffer);
        fflush(g_logFile);
    }
    OutputDebugStringA(buffer);
    OutputDebugStringA("\n");
}

void BsLogWide(const wchar_t* message)
{
    if (message == nullptr) return;
    char buffer[2048];
    WideCharToMultiByte(CP_UTF8, 0, message, -1, buffer, sizeof(buffer), nullptr, nullptr);
    BsLog("%s", buffer);
}
