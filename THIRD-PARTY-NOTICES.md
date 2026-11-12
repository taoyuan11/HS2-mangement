# Third-party components

## C++ application and portable package

- 7-Zip 26.06 complete format library (`7z.dll`): Igor Pavlov and contributors; GNU LGPL 2.1 or later, BSD components and the unRAR restriction. Built from the unmodified official source at https://github.com/ip7z/7zip/releases/tag/26.06 (source archive https://github.com/ip7z/7zip/releases/download/26.06/7z2606-src.tar.xz). Used only to read temporary ZIP, 7Z and RAR packages. See `licenses/7zip-LICENSE.txt`, `licenses/7zip-LGPL.txt` and `licenses/7zip-unRAR.txt`. Source checksum and reproducible build entry: `scripts/build/Build-NativeArchive.ps1`.

- C++/WinRT 3.0.260818.1: Microsoft, MIT. See `licenses/CppWinRT-LICENSE.txt`. Source: https://github.com/microsoft/cppwinrt. Used to project the Windows and WinUI APIs into native C++; no C#/WinRT runtime is loaded.
- Windows App SDK 1.8, including WinUI 3: Microsoft. See `licenses/WindowsAppSDK-LICENSE.txt` and `licenses/WindowsAppSDK-NOTICE.txt`. Source: https://github.com/microsoft/WindowsAppSDK and https://github.com/microsoft/microsoft-ui-xaml.
- nlohmann/json 3.12.0: Niels Lohmann and contributors, MIT. See `licenses/nlohmann-json-LICENSE.txt`. Source: https://github.com/nlohmann/json/tree/v3.12.0.
- miniz inflate implementation: Rich Geldreich, RAD Game Tools and contributors, MIT. See `licenses/miniz-LICENSE.txt`. Source: https://github.com/richgel999/miniz. Used for reading ZIP / ZIPMOD archives.
- LZMA SDK decoder: Igor Pavlov, public domain. See `licenses/LZMA-PUBLIC-DOMAIN.txt`. Source: https://www.7-zip.org/sdk.html.
- SQLite: public domain. The C++ application uses the Windows `winsqlite3.dll` system API. See `licenses/sqlite-LICENSE.txt` and https://www.sqlite.org/copyright.html.
- cgltf 1.15: MIT. See `licenses/cgltf-LICENSE.txt`. Used for native GLB/GLTF asset import.
- LZ4 1.10.0: BSD 2-Clause. See `licenses/LZ4-LICENSE.txt`. Used for native UnityFS block compression.
- AssetRipper TPK class database: MIT. See `licenses/AssetRipper-Tpk.txt`. The database describes serialized asset layouts; no Unity runtime is included.
- Independent cloth adapter: NVIDIA PhysX SDK 3.4.2, BSD-style license. See `licenses/NVIDIA-PhysX-3.4.txt`. Source: https://github.com/NVIDIAGameWorks/PhysX-3.4.
- Native FBX adapter: Autodesk FBX SDK 2020.3.7. See the supplied SDK terms in `licenses/Autodesk-FBX-SDK-2020.3.7.rtf`. Source/download: https://aps.autodesk.com/developer/overview/fbx-sdk.
- Microsoft Visual C++ 2022 VC143 x64 redistributable binaries: Microsoft Corporation. Copied unchanged from the installed Visual Studio redistributable directory. See the [distributable list](https://learn.microsoft.com/visualstudio/releases/2022/redistribution) and [redistribution terms](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files).
- Unity Post Processing reference: 2.1.7, Unity Companion License; see `licenses/Unity-PostProcessing.md`. The renderer reads the user's installed effect resources; game shader packages are not distributed.

Blender and Unreal Engine are optional, separately installed user tools. Their automation templates are shipped as text resources and run only inside the selected editor. Neither editor nor a standalone Python interpreter is bundled.

The manual creation workbench uses the same native dependencies listed above. Its editable topology, UV, sculpting, layered painting, project history and installation journal are implemented in this repository; no additional modeling editor, game presets, game shaders or plugin binaries are bundled for creation.

HS2 Manager is an independent utility. HoneySelect 2, Studio, Unity assets and game assemblies belong to their respective owners. No game DLLs, MOD archives, character cards or reference screenshots are distributed. PE / CLI metadata is read as data with a restricted numeric evaluator; game or plugin code is not loaded or invoked.

Historical C# / Unity dependency notices and their license texts are available in Git history alongside the retired implementations. They are not dependencies of the current C++ application.
