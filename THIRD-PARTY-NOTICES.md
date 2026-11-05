# Third-party notices

This project is an unofficial fan modification. It contains no game files and is not affiliated
with, sponsored by or endorsed by NVIDIA Corporation, Slitherine Ltd., Black Lab Games, Paradox
Interactive or Games Workshop.

## NVIDIA DLSS

DLSS is a technology of NVIDIA Corporation. NVIDIA, DLSS, RTX and GeForce RTX are trademarks of
NVIDIA Corporation.

The native bridge links against NVIDIA's NGX library and loads `nvngx_dlss.dll` at runtime. Neither
the SDK, nor `nvngx_dlss.dll`, nor any statically linked NGX code is distributed in this repository.
Building the native DLL requires the NVIDIA DLSS SDK, which is governed by the NVIDIA RTX SDKs
License: <https://github.com/NVIDIA/DLSS>.

If you redistribute a build, you are responsible for complying with that licence, including any
notice and redistribution conditions that apply to the NGX runtime.

## WotR Upscaler

The architecture — BepInEx/UnityModManager-side render-thread event bridge, struct hand-off to a
native NGX plugin, status polling, depth-reprojection motion vectors — is adapted from
[Malla-h/WotRUpscaler](https://github.com/Malla-h/WotRUpscaler), which is MIT licensed:

> MIT License, Copyright (c) Malla-h and contributors

No source files from that project are copied verbatim; the managed and native code here was
written for BepInEx and for this game's render path.

## BepInEx and Harmony

[BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and [Harmony](https://github.com/pardeike/Harmony)
(MIT) are runtime dependencies, not bundled.

## Unity

This mod is built against the Unity assemblies shipped with the game. Unity is a trademark of
Unity Technologies.

## Warhammer 40,000

Warhammer 40,000 is a trademark of Games Workshop Limited. Age of Wonders and Warhammer 40,000:
Battlesector are properties of their respective owners.
