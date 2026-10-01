# Third-party notices

DynamicPortrait is licensed under GNU AGPL version 3 or (at your option) any later version. See LICENSE.

## FFXIV VR

- Source: https://github.com/WesleyLuk90/ffxiv-vr
- Author listed by the upstream project: Steelgunner90 (WesleyLuk90)
- Reference commit: bd5b4a9f6be472018411520c8fb4aae7cf43731e
- License: AGPL-3.0-or-later (upstream project declaration)
- Adapted parts: Framework tick / camera matrix / Present hook signatures and scheduling; pre-UI render-command marker protocol; GPU scene texture capture; reversed-Z projection convention.
- Changed for DynamicPortrait: no OpenXR or headset session, portrait/main phases, scoped camera restoration, subject-specific visibility override, cropped triple-buffer textures, independent settings UI and bone-following camera.

## Other references

- ProjectMimer/xivr-Ex: consulted for rendering architecture; no code copied directly.
- zysilm/FFXIV-CombatSimulator (MPL-2.0): consulted for skeleton traversal, camera handling and build/release organization. DynamicPortrait's implementations are independently written; no MPL source file is incorporated.
- Dalamud and FFXIVClientStructs are host-provided dependencies, not redistributed in the plugin ZIP.
- Silk.NET.Direct3D11 2.21.0 and its transitive Silk.NET dependencies: MIT, https://github.com/dotnet/Silk.NET. NuGet package licensing applies to redistributed assemblies.

## MIT notices for redistributed dependencies

Silk.NET 2.21.0:

- Copyright (c) 2019-2020 Ultz Limited
- Copyright (c) 2021- .NET Foundation and Contributors
- Source: https://github.com/dotnet/Silk.NET/blob/v2.21.0/LICENSE.md

Microsoft.Extensions.DependencyModel and Microsoft.DotNet.PlatformAbstractions:

Copyright (c) .NET Foundation and Contributors. All rights reserved.

The following MIT license applies to the dependencies identified above:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
