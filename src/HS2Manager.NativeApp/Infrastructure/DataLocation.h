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
    // 调用者持有该目录的资料库单实例锁；仅恢复崩溃后被残留映射占用的 SQLite 共享内存。
    std::unique_ptr<CardRepository> OpenLibrary(fs::path const &directory);
} // 命名空间 hs2::ui
