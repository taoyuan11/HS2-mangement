# Third-party components

## C++ application and portable package

- C++/WinRT 3.0.260818.1: Microsoft, MIT. See `licenses/CppWinRT-LICENSE.txt`. Source: https://github.com/microsoft/cppwinrt. Used to project the Windows and WinUI APIs into native C++; no C#/WinRT runtime is loaded.
- Windows App SDK 1.8, including WinUI 3: Microsoft. See `licenses/WindowsAppSDK-LICENSE.txt` and `licenses/WindowsAppSDK-NOTICE.txt`. Source: https://github.com/microsoft/WindowsAppSDK and https://github.com/microsoft/microsoft-ui-xaml.
- nlohmann/json 3.12.0: Niels Lohmann and contributors, MIT. See `licenses/nlohmann-json-LICENSE.txt`. Source: https://github.com/nlohmann/json/tree/v3.12.0.
- miniz inflate implementation: Rich Geldreich, RAD Game Tools and contributors, MIT. See `licenses/miniz-LICENSE.txt`. Source: https://github.com/richgel999/miniz. Used for reading ZIP / ZIPMOD archives.
- LZMA SDK decoder: Igor Pavlov, public domain. See `licenses/LZMA-PUBLIC-DOMAIN.txt`. Source: https://www.7-zip.org/sdk.html.
- SQLite: public domain. The C++ application uses the Windows `winsqlite3.dll` system API. See `licenses/sqlite-LICENSE.txt` and https://www.sqlite.org/copyright.html.
- AssetRipper TPK class database: MIT. See `licenses/AssetRipper-Tpk.txt`. The database describes serialized asset layouts; no Unity runtime is included.
- Independent cloth adapter: NVIDIA PhysX SDK 3.4.2, BSD-style license. See `licenses/NVIDIA-PhysX-3.4.txt`. Source: https://github.com/NVIDIAGameWorks/PhysX-3.4.
- Native FBX adapter: Autodesk FBX SDK 2020.3.7. See the supplied SDK terms in `licenses/Autodesk-FBX-SDK-2020.3.7.rtf`. Source/download: https://aps.autodesk.com/developer/overview/fbx-sdk.
- Microsoft Visual C++ 2022 VC143 x64 redistributable binaries: Microsoft Corporation. Copied unchanged from the installed Visual Studio redistributable directory. See the [distributable list](https://learn.microsoft.com/visualstudio/releases/2022/redistribution) and [redistribution terms](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files).
- Unity Post Processing reference: 2.1.7, Unity Companion License; see `licenses/Unity-PostProcessing.md`. The renderer reads the user's installed effect resources; game shader packages are not distributed.

Blender and Unreal Engine are optional, separately installed user tools. Their automation templates are shipped as text resources and run only inside the selected editor. Neither editor nor a standalone Python interpreter is bundled.

HS2 Manager is an independent utility. HoneySelect 2, Studio, Unity assets and game assemblies belong to their respective owners. No game DLLs, MOD archives, character cards or reference screenshots are distributed. PE / CLI metadata is read as data with a restricted numeric evaluator; game or plugin code is not loaded or invoked.

## Historical C# comparison sources

The components below are retained for historical source and validation references. They are excluded from the C++ application's build, runtime and portable package. Their original license texts remain available in `licenses`.

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
