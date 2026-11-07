# Third-party components

HS2 Manager is an independent utility. HoneySelect 2, Studio, Unity assets and game assemblies belong to their respective owners and are not distributed with this project or the portable package.

- .NET / WPF runtime and Microsoft.Data.Sqlite: Microsoft and .NET Foundation, MIT. License texts and runtime third-party notices are included in `licenses`. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf and https://github.com/dotnet/efcore.
- SQLitePCLRaw: Eric Sink and contributors, Apache-2.0. Source: https://github.com/ericsink/SQLitePCL.raw.
- SQLite: public domain. https://www.sqlite.org/copyright.html.
- AssetsTools.NET 3.0.5: nesrak1, MIT. Reads serialized Unity data without loading the Unity runtime. License included in `licenses/AssetsTools.NET-LICENSE.txt`. Source: https://github.com/nesrak1/AssetsTools.NET.
- BepInEx is referenced only from the user's existing installation and is not bundled. https://github.com/BepInEx/BepInEx.
- Microsoft.NETFramework.ReferenceAssemblies.net46 is a build-only dependency and is not included in the application package.

The standalone preview reads the user's local `abdata` resources and caches decoded geometry locally. No character cards, preview screenshots, model caches, MOD packages or proprietary game DLLs are bundled. The legacy bridge source remains available separately and is not built or shipped by the default build.
