#include "Preview/TemporaryPreviewSession.h"
#include "Archive/PackageArchive.h"
#include <windows.h>
#include <algorithm>
#include <fstream>
#include <set>

namespace hs2
{
    namespace
    {
        constexpr char LockContents[] = "HS2Manager temporary preview v1\n";

        std::string Lower(std::string value)
        {
            for (auto &c : value)
                if (c >= 'A' && c <= 'Z')
                    c = char(c - 'A' + 'a');
            return value;
        }

        fs::path Extended(const fs::path &path)
        {
            auto full = fs::absolute(path).lexically_normal().wstring();
            if (full.starts_with(L"\\\\?\\"))
                return full;
            if (full.starts_with(L"\\\\"))
                return L"\\\\?\\UNC\\" + full.substr(2);
            return L"\\\\?\\" + full;
        }

        void Require(bool valid, const char *message)
        {
            if (!valid)
                throw std::runtime_error(message);
        }

        bool Reparse(const fs::path &path)
        {
            auto value = GetFileAttributesW(path.c_str());
            return value != INVALID_FILE_ATTRIBUTES && (value & FILE_ATTRIBUTE_REPARSE_POINT);
        }

        void SafeRoot(const fs::path &path)
        {
            auto full = fs::absolute(path).lexically_normal();
            for (auto parent = full; !parent.empty();)
            {
                Require(!Reparse(parent), "临时预览目录不能包含链接。");
                auto next = parent.parent_path();
                if (next == parent)
                    break;
                parent = next;
            }
        }

        bool OwnedName(const fs::path &path)
        {
            auto name = Utf8(path.filename());
            return name.size() == 32 && std::all_of(name.begin(), name.end(),
                                                    [](unsigned char c)
                                                    {
                                                        return std::isxdigit(c) != 0;
                                                    });
        }

        void RemoveTree(const fs::path &root, const fs::path &path)
        {
            auto full = fs::absolute(path).lexically_normal();
            auto relative = full.lexically_relative(root);
            Require(full == root || (!relative.empty() && *relative.begin() != ".."), "临时预览清理目录越界。");
            auto attributes = GetFileAttributesW(full.c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES)
            {
                auto error = GetLastError();
                if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
                    return;
                throw std::system_error(static_cast<int>(error), std::system_category());
            }
            if ((attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT))
                for (auto &entry : fs::directory_iterator(full))
                    RemoveTree(root, entry.path());
            auto removed =
                attributes & FILE_ATTRIBUTE_DIRECTORY ? RemoveDirectoryW(full.c_str()) : DeleteFileW(full.c_str());
            if (!removed)
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category());
        }

        void Cleanup(const fs::path &root) noexcept
        {
            try
            {
                Require(OwnedName(root) && root.parent_path().filename() == L"TemporaryPreview", "临时目录标识无效。");
                SafeRoot(root);
                // 锁文件最后移除，失败时下次启动仍能确认目录归属。
                for (auto &entry : fs::directory_iterator(root))
                    if (entry.path().filename() != L"session.lock")
                        RemoveTree(root, entry.path());
                fs::remove(root / "session.lock");
                fs::remove(root);
            }
            catch (...)
            {
                try
                {
                    SafeRoot(root);
                    if (fs::is_directory(root))
                        WriteJson(root / "cleanup-pending.json", Json{{"Pending", true}});
                }
                catch (...)
                {
                }
            }
        }

        size_t CommonDepth(const fs::path &first, const fs::path &second)
        {
            auto a = first.begin(), b = second.begin();
            size_t depth = 0;
            for (; a != first.end() && b != second.end() && Lower(Utf8(*a)) == Lower(Utf8(*b)); ++a, ++b)
                ++depth;
            return depth;
        }

        struct PackageFile
        {
            fs::path Path, Root;
            std::string Logical;
            std::optional<ModFileInfo> Mod;
            std::optional<PluginFileInfo> Plugin;
            size_t Scope = 0;
        };

        void Identify(PackageFile &file, const fs::path &payload)
        {
            auto relative = file.Path.lexically_relative(payload);
            fs::path root = payload, logical;
            bool found = false;
            for (auto &part : relative)
            {
                auto name = Lower(Utf8(part));
                if (!found && (name == "abdata" || name == "mods" || name == "userdata" || name == "bepinex" ||
                               name == "dhh_data"))
                    found = true;
                if (found)
                    logical /= part;
                else
                    root /= part;
            }
            if (found)
            {
                file.Root = root;
                file.Logical = Lower(Utf8(logical));
                std::replace(file.Logical.begin(), file.Logical.end(), '\\', '/');
            }
        }

        PreviewResources ResourcesFor(const TemporaryPreviewCard &card, const std::vector<PackageFile> &files,
                                      const ModIndex &base, const std::shared_ptr<TemporaryPreviewStorage> &storage,
                                      const fs::path &game, const fs::path &data, std::stop_token token)
        {
            auto context = std::make_shared<PreviewResourceContext>();
            context->Game = fs::absolute(game);
            context->Package = storage->Root() / "files";
            context->Storage = storage;
            context->Data = storage->Root() / "cache" / Path(context->Id);
            context->CatalogCache = fs::absolute(data) / "NativeCpp/resources";
            context->Index = base;
            context->Index.Files.clear();
            context->Index.PluginFiles.clear();
            context->Index.Plugins.clear();
            std::map<size_t, std::vector<const PackageFile *>, std::greater<size_t>> layers;
            for (auto &file : files)
            {
                CheckCancelled(token);
                auto depth = CommonDepth(Path(card.Card.Path).parent_path(), file.Path.parent_path());
                if (depth == file.Scope)
                    layers[depth].push_back(&file);
            }
            std::set<std::string> guids, assemblies, filenames, pluginGuids, roots;
            auto appendMods = [&](const std::vector<ModFileInfo> &mods)
            {
                for (auto &mod : NativeModSelection::Select(mods))
                    if (guids.insert(Lower(mod.Guid)).second)
                        context->Index.Files.push_back(mod);
            };
            auto appendPlugins = [&](std::vector<PluginFileInfo> plugins)
            {
                std::stable_sort(plugins.begin(), plugins.end(),
                                 [](auto &a, auto &b)
                                 {
                                     return PluginFileIndex::CompareVersions(a.Version, b.Version) > 0;
                                 });
                for (auto &plugin : plugins)
                {
                    auto name = Lower(Utf8(Path(plugin.Path).filename()));
                    if (filenames.contains(name) ||
                        (!plugin.AssemblyName.empty() && assemblies.contains(Lower(plugin.AssemblyName))) ||
                        std::any_of(plugin.Plugins.begin(), plugin.Plugins.end(),
                                    [&](auto &p)
                                    {
                                        return pluginGuids.contains(Lower(p.Guid));
                                    }))
                        continue;
                    filenames.insert(name);
                    if (!plugin.AssemblyName.empty())
                        assemblies.insert(Lower(plugin.AssemblyName));
                    for (auto &p : plugin.Plugins)
                    {
                        pluginGuids.insert(Lower(p.Guid));
                        context->Index.Plugins[p.Guid] = Utf8(Path(plugin.Path).filename());
                    }
                    context->Index.PluginFiles.push_back(std::move(plugin));
                }
            };
            for (auto &[depth, layer] : layers)
            {
                std::vector<ModFileInfo> mods;
                std::vector<PluginFileInfo> plugins;
                for (auto file : layer)
                {
                    context->Priorities.emplace(file->Path, depth + 1);
                    if (file->Mod)
                        mods.push_back(*file->Mod);
                    if (file->Plugin)
                        plugins.push_back(*file->Plugin);
                    if (!file->Logical.empty())
                    {
                        context->Files.try_emplace(file->Logical, file->Path);
                        if (roots.insert(Lower(Utf8(file->Root))).second)
                            context->Roots.push_back(file->Root);
                    }
                }
                appendMods(mods);
                appendPlugins(std::move(plugins));
            }
            appendMods(base.Files);
            appendPlugins(base.PluginFiles);
            fs::create_directories(context->Data);
            return context;
        }
    }

    std::shared_ptr<TemporaryPreviewStorage> TemporaryPreviewStorage::Create(const fs::path &data)
    {
        auto storage = std::shared_ptr<TemporaryPreviewStorage>(new TemporaryPreviewStorage());
        auto parent = Extended(data) / "TemporaryPreview";
        SafeRoot(parent);
        fs::create_directories(parent);
        storage->root_ = parent / Path(NewId());
        Require(OwnedName(storage->root_), "临时会话标识无效。");
        Require(fs::create_directory(storage->root_), "无法创建临时预览目录。");
        auto handle = CreateFileW((storage->root_ / "session.lock").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                  CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(handle != INVALID_HANDLE_VALUE, "无法锁定临时预览目录。");
        storage->lock_ = handle;
        DWORD written = 0;
        Require(WriteFile(handle, LockContents, sizeof(LockContents), &written, nullptr) &&
                    written == sizeof(LockContents),
                "无法写入临时预览目录标识。");
        return storage;
    }

    TemporaryPreviewStorage::~TemporaryPreviewStorage()
    {
        if (lock_)
            CloseHandle(static_cast<HANDLE>(lock_));
        if (!root_.empty())
            Cleanup(root_);
    }

    void TemporaryPreviewStorage::CleanAbandoned(const fs::path &data)
    {
        auto parent = Extended(data) / "TemporaryPreview";
        SafeRoot(parent);
        if (!fs::is_directory(parent))
            return;
        for (auto &entry : fs::directory_iterator(parent))
        {
            if (!entry.is_directory() || Reparse(entry.path()) || !OwnedName(entry.path()))
                continue;
            auto lockPath = entry.path() / "session.lock";
            if (Reparse(lockPath))
                continue;
            auto handle = CreateFileW(lockPath.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                                      FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
            if (handle == INVALID_HANDLE_VALUE)
                continue;
            char marker[sizeof(LockContents)]{};
            DWORD read = 0;
            auto owned = ::ReadFile(handle, marker, sizeof(marker), &read, nullptr) && read == sizeof(marker) &&
                         std::equal(std::begin(marker), std::end(marker), std::begin(LockContents));
            CloseHandle(handle);
            if (owned)
                Cleanup(entry.path());
        }
    }

    std::shared_ptr<TemporaryPreviewSession> TemporaryPreviewSession::Open(
        const fs::path &source, const fs::path &game, const fs::path &data, const fs::path &decoder,
        const std::optional<ModIndex> &previous, const std::optional<std::wstring> &password, Progress progress,
        std::stop_token cancellation)
    {
        CheckCancelled(cancellation);
        Require(fs::is_regular_file(source), "请选择一个角色卡或压缩包文件。");
        auto extension = Lower(Utf8(source.extension()));
        Require(extension == ".png" || extension == ".zip" || extension == ".7z" || extension == ".rar",
                "临时预览支持 PNG 角色卡及 ZIP、7Z、RAR 压缩包；分卷包请先解压。");
        auto session = std::make_shared<TemporaryPreviewSession>();
        session->Source = fs::absolute(source);
        session->Storage = TemporaryPreviewStorage::Create(data);
        auto payload = session->Storage->Root() / "files";
        fs::create_directory(payload);
        if (extension == ".png")
        {
            if (progress)
                progress({0, 0, "读取临时角色卡"});
            auto bytes = ReadFile(source, 512ULL * 1024 * 1024, cancellation);
            WriteAtomic(payload / source.filename(), bytes);
        }
        else
        {
            try
            {
                if (progress)
                    progress({0, 0, "正在打开压缩包"});
                ExtractPackage(
                    source, payload, decoder, password,
                    [&](uint64_t completed, uint64_t total)
                    {
                        if (progress)
                            progress({total ? static_cast<int>(1000.0 * completed / total) : 0, total ? 1000 : 0,
                                      "正在解压临时预览资源"});
                    },
                    cancellation);
            }
            catch (...)
            {
                CheckCancelled(cancellation);
                throw;
            }
        }
        std::vector<fs::path> paths;
        for (auto &entry : fs::recursive_directory_iterator(payload))
        {
            CheckCancelled(cancellation);
            Require(!Reparse(entry.path()), "临时预览资源不能包含链接。");
            if (entry.is_regular_file())
                paths.push_back(entry.path());
        }
        std::sort(paths.begin(), paths.end(),
                  [](auto &a, auto &b)
                  {
                      return Lower(Utf8(a)) < Lower(Utf8(b));
                  });
        CardParser parser;
        size_t ignored = 0;
        for (size_t i = 0; i < paths.size(); ++i)
        {
            CheckCancelled(cancellation);
            auto &path = paths[i];
            if (Lower(Utf8(path.extension())) != ".png")
                continue;
            if (progress)
                progress({static_cast<int>(i), static_cast<int>(paths.size()), "识别包内角色卡"});
            auto card = parser.Parse(path, cancellation);
            if (card.Status != CardStatus::Valid)
            {
                ++ignored;
                continue;
            }
            card.OriginalPath = extension == ".png" ? Utf8(source) : Utf8(path.lexically_relative(payload));
            card.CoverFile = Utf8(session->Storage->Root() / "covers" / Path(card.Id + ".png"));
            CardParser::CopyCover(path, card.PngLength, Path(card.CoverFile), cancellation);
            session->Cards.push_back({std::move(card), path.lexically_relative(payload), {}});
        }
        Require(!session->Cards.empty(),
                "未找到有效角色卡。普通图片、服装卡和损坏卡不能用于角色预览；普通嵌套压缩包请先解压。");
        if (ignored)
            session->Warnings.push_back("已跳过 " + std::to_string(ignored) + " 张普通图片、服装卡或无效卡。");
        std::vector<PackageFile> files;
        for (size_t i = 0; i < paths.size(); ++i)
        {
            CheckCancelled(cancellation);
            PackageFile file;
            file.Path = paths[i];
            Identify(file, payload);
            auto ext = Lower(Utf8(file.Path.extension()));
            if (progress && i % 40 == 0)
                progress({static_cast<int>(i), static_cast<int>(paths.size()), "索引包内预览资源"});
            if (ext == ".zipmod" || ext == ".zip")
            {
                try
                {
                    file.Mod = ModDependencyService::ReadManifest(file.Path);
                    if (!file.Mod->Compatible)
                        session->Warnings.push_back("跳过不兼容的 MOD：" + Utf8(file.Path.filename()));
                }
                catch (const std::exception &e)
                {
                    session->Warnings.push_back("无法作为 MOD 加载 " + Utf8(file.Path.filename()) + "：" + e.what() +
                                                "；普通嵌套压缩包请先解压。");
                }
            }
            else if (ext == ".7z" || ext == ".rar")
                session->Warnings.push_back("普通嵌套压缩包请先解压：" + Utf8(file.Path.filename()));
            if (file.Logical.starts_with("bepinex/plugins/") && ext == ".dll")
            {
                try
                {
                    file.Plugin = PluginFileIndex::Read(file.Path);
                }
                catch (const std::exception &e)
                {
                    session->Warnings.push_back("插件元数据读取失败：" + Utf8(file.Path.filename()) + "：" + e.what());
                }
            }
            for (auto &card : session->Cards)
                file.Scope =
                    std::max(file.Scope, CommonDepth(Path(card.Card.Path).parent_path(), file.Path.parent_path()));
            files.push_back(std::move(file));
        }
        if (progress)
            progress({0, 0, "读取本机资源，补齐包内依赖"});
        auto base = ModDependencyService::ScanTransient(game, previous ? &*previous : nullptr, progress, cancellation);
        for (auto &card : session->Cards)
            card.Resources = ResourcesFor(card, files, base, session->Storage, game, data, cancellation);
        CheckCancelled(cancellation);
        return session;
    }
}
