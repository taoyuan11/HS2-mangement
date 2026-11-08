# Third-party components

HS2 Manager is an independent utility. HoneySelect 2, Studio, Unity assets and game assemblies belong to their respective owners and are not distributed with this project or the portable package.

- .NET / WPF runtime and Microsoft.Data.Sqlite: Microsoft and .NET Foundation, MIT. License texts and runtime third-party notices are included in `licenses`. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf and https://github.com/dotnet/efcore.
- SQLitePCLRaw: Eric Sink and contributors, Apache-2.0. Source: https://github.com/ericsink/SQLitePCL.raw.
- SQLite: public domain. https://www.sqlite.org/copyright.html.
- AssetsTools.NET 3.0.5: nesrak1, MIT. Reads serialized Unity data without loading the Unity runtime. License included in `licenses/AssetsTools.NET-LICENSE.txt`. Source: https://github.com/nesrak1/AssetsTools.NET.
- AssetsTools.NET.Texture 3.0.2: nesrak1, MIT; shares the AssetsTools.NET license above. Managed Unity texture decoding.
- AssetRipper.TextureDecoder 1.3.0: ds5678, MIT. Transitive texture decoder dependency; license included in `licenses/AssetRipper.TextureDecoder-LICENSE.txt`. Source: https://github.com/AssetRipper/TextureDecoder.
- BepInEx is referenced only from the user's existing installation and is not bundled. https://github.com/BepInEx/BepInEx.
- Microsoft.NETFramework.ReferenceAssemblies.net46 is a build-only dependency and is not included in the application package.

The standalone preview reads the user's local `abdata` and MOD archives and caches decoded geometry and textures locally. Shape formulas are read from the installed game's PE metadata with a restricted numeric evaluator; game assemblies and plugin code are never loaded or invoked. No character cards, preview screenshots, model caches, MOD packages or proprietary game DLLs are bundled. The legacy bridge source remains available separately and is not built or shipped by the default build.

- AssetsTools.NET.MonoCecil 3.0.4 and Mono.Cecil: MIT; metadata-only component schema reading.
- AssetRipper TPK class database: MIT; license in `licenses/AssetRipper-Tpk.txt`.
- Unity Post Processing 2.1.7: Unity Companion License, in `licenses/Unity-PostProcessing.md`.
- Unity 2018.4.11f1 Player and Mono runtime are distributed as output of the activated Unity editor, subject to Unity's applicable runtime distribution terms. The editor itself is not included.
