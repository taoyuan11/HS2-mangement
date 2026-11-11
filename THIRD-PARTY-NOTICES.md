# Third-party components

- Native rendering modules: Vortice.Direct3D11, Vortice.DXGI and Vortice.D3DCompiler 3.8.3, and Vortice.Mathematics 2.1.0, Amer Koleci and contributors, MIT. See `licenses/Vortice.Windows-LICENSE.txt`. Source: https://github.com/amerkoleci/Vortice.Windows.
- SharpGen.Runtime and SharpGen.Runtime.COM 2.4.2-beta: Alexandre Mutel, Jeremy Koritzinsky and Amer Koleci, MIT. See `licenses/SharpGen.Runtime-LICENSE.txt`. Source: https://github.com/SharpGenTools/SharpGenTools.
- Independent cloth adapter: NVIDIA PhysX SDK 3.4.2, Copyright (c) 2018 NVIDIA Corporation, BSD-style license. See `licenses/NVIDIA-PhysX-3.4.txt`. Source: https://github.com/NVIDIAGameWorks/PhysX-3.4. This adapter is built directly from the SDK and contains no Unity engine components.
- Native FBX adapter: Autodesk FBX SDK 2020.3.7, Autodesk FBX SDK License and Services Agreement, supplied unmodified in `licenses/Autodesk-FBX-SDK-2020.3.7.rtf`. The project's C++ bridge uses Autodesk's SDK directly and does not use the historical Unity C# wrapper. SDK source/download: https://aps.autodesk.com/developer/overview/fbx-sdk.
- App-local C++ runtime: Microsoft Visual C++ 2022 VC143 x64 redistributable binaries, Copyright Microsoft Corporation. Supplied unchanged from the installed Visual Studio redistributable directory for the independent FBX adapter. See the [Visual Studio 2022 distributable list](https://learn.microsoft.com/visualstudio/releases/2022/redistribution) and [redistribution terms](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files).


HS2 Manager is an independent utility. HoneySelect 2, Studio, Unity assets and game assemblies belong to their respective owners and are not distributed with this project or the portable package.

- .NET runtime and Microsoft.Data.Sqlite: Microsoft and .NET Foundation, MIT. License texts and runtime third-party notices are included in `licenses`. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/efcore.
- C#/WinRT 2.2.0 (`WinRT.Runtime.dll`): Microsoft, MIT. See `licenses/CsWinRT-LICENSE.txt`. Source: https://github.com/microsoft/CsWinRT/tree/2.2.0.241111.1. The distributed .NET binary contains unused Mono compatibility declarations; no Mono runtime is bundled or loaded. The package audit records the inspected binary fingerprint and optional declarations.
- Windows App SDK 1.8 (including WinUI 3): Microsoft, distributed under the Microsoft Windows App SDK license terms. See `licenses/WindowsAppSDK-LICENSE.txt` and `licenses/WindowsAppSDK-NOTICE.txt`. Source: https://github.com/microsoft/WindowsAppSDK and https://github.com/microsoft/microsoft-ui-xaml. WPF is only used by independent test fixtures and the historical software-renderer diagnostic project; it is not shipped in the application.
- SQLitePCLRaw: Eric Sink and contributors, Apache-2.0. Source: https://github.com/ericsink/SQLitePCL.raw.
- SQLite: public domain. https://www.sqlite.org/copyright.html.
- AssetsTools.NET 3.0.5: nesrak1, MIT. Reads serialized Unity data without loading the Unity runtime. License included in `licenses/AssetsTools.NET-LICENSE.txt`. Source: https://github.com/nesrak1/AssetsTools.NET.
- AssetsTools.NET.Texture 3.0.2: nesrak1, MIT; shares the AssetsTools.NET license above. Managed Unity texture decoding.
- AssetRipper.TextureDecoder 1.3.0: ds5678, MIT. Transitive texture decoder dependency; license included in `licenses/AssetRipper.TextureDecoder-LICENSE.txt`. Source: https://github.com/AssetRipper/TextureDecoder.
- BepInEx is referenced only from the user's existing installation and is not bundled. https://github.com/BepInEx/BepInEx.
- Microsoft.NETFramework.ReferenceAssemblies.net46 is a build-only dependency and is not included in the application package.

The in-process native preview reads the user's local `abdata` and MOD archives and caches decoded geometry and textures locally. Shape formulas are read from the installed game's PE metadata with a restricted numeric evaluator; game assemblies and plugin code are never loaded or invoked. No character cards, preview screenshots, model caches, MOD packages or proprietary game DLLs are bundled. The legacy bridge source remains available separately and is not built or shipped by the default build.

- AssetsTools.NET.MonoCecil 3.0.4 and Mono.Cecil: MIT; metadata-only component schema reading.
- AssetRipper TPK class database: MIT; license in `licenses/AssetRipper-Tpk.txt`.
- Unity Post Processing 2.1.7: Unity Companion License, in `licenses/Unity-PostProcessing.md`.

Historical comparison sources retain their original notices, including the Unity Companion licensed FBX C# wrapper. No Unity Player, Mono runtime or UnityEngine assembly is distributed in the native package.
