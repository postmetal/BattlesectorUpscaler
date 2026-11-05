@echo off
setlocal

if "%DLSS_SDK%"=="" (
    echo DLSS_SDK is not set.
    echo Download the NVIDIA DLSS SDK from https://github.com/NVIDIA/DLSS and set, for example:
    echo     set DLSS_SDK=C:\SDK\DLSS
    exit /b 1
)

cmake -S "%~dp0" -B "%~dp0build" -G "Visual Studio 17 2022" -A x64 -DDLSS_SDK="%DLSS_SDK%"
if errorlevel 1 exit /b 1

cmake --build "%~dp0build" --config Release
if errorlevel 1 exit /b 1

if not exist "%~dp0..\dist\BattlesectorUpscaler" mkdir "%~dp0..\dist\BattlesectorUpscaler"
copy /Y "%~dp0build\Release\BattlesectorUpscalerNative.dll" "%~dp0..\dist\BattlesectorUpscaler\" >nul
echo Built dist\BattlesectorUpscaler\BattlesectorUpscalerNative.dll
