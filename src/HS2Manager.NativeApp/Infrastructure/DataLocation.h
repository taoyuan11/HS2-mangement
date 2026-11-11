#pragma once
#include "Common/Core.h"

namespace hs2::ui
{
    struct LibraryLocation
    {
        fs::path Directory, MigrationSource;
    };

    bool IsNetworkPath(fs::path const &path);
    fs::path StorageIdentity(fs::path const &path);
    std::wstring LibraryMutexName(fs::path const &path);
    LibraryLocation ResolveLibraryLocation(fs::path baseDirectory, std::vector<std::wstring> const &arguments,
                                           fs::path localAppData);
    void CopyLibrary(fs::path const &source, fs::path const &destination);
    void PrepareLibrary(LibraryLocation const &location);
} // 命名空间 hs2::ui
