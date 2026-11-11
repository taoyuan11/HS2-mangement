#include "Assets/AssetStore.h"
#include <algorithm>
#include <chrono>
#include <cwctype>
#include <future>
#include <thread>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        std::string Key(const std::filesystem::path &path)
        {
            auto s = (path.is_absolute() ? path : std::filesystem::absolute(path)).lexically_normal().wstring();
            for (auto &c : s)
                c = static_cast<wchar_t>(std::towlower(c));
            auto u = std::filesystem::path(s).u8string();
            return {reinterpret_cast<const char *>(u.data()), u.size()};
        }

        std::string Key(const GpuAssetPointer &p)
        {
            return Key(p.Bundle) + "|" + std::to_string(p.FileId) + "|" + std::to_string(p.PathId);
        }

        std::string Lower(std::string text)
        {
            std::transform(text.begin(), text.end(), text.begin(),
                           [](unsigned char c)
                           {
                               return char(std::tolower(c));
                           });
            return text;
        }
    }

    NativeGpuAssetStore::NativeGpuAssetStore(std::vector<std::filesystem::path> paths, std::filesystem::path game,
                                             std::shared_ptr<const UnityClassDatabase> database,
                                             std::stop_token cancellation, std::shared_ptr<UnityBlockCache> blockCache)
        : game_(std::move(game)), database_(std::move(database)), blockCache_(std::move(blockCache)),
          cancellation_(cancellation)
    {
        for (auto &path : paths)
            AddPath(path);
    }

    void NativeGpuAssetStore::SetScriptSchema(
        std::function<std::vector<UnityTypeNode>(const GpuAssetPointer &, std::string_view)> resolver)
    {
        scriptSchema_ = std::move(resolver);
        for (auto &[key, reader] : readers_)
            reader->ScriptSchema = scriptSchema_;
    }

    void NativeGpuAssetStore::Cancel() const
    {
        if (cancellation_.stop_requested())
            throw std::runtime_error("Resource loading cancelled.");
    }

    void NativeGpuAssetStore::AddPath(const std::filesystem::path &path)
    {
        auto absolute = std::filesystem::absolute(path);
        auto key = Key(absolute);
        if (pathKeys_.insert(key).second)
            paths_.push_back(absolute);
    }

    void NativeGpuAssetStore::TrimReaders(const std::string &keep)
    {
        // 按原始数据和解析字段的内存预算淘汰，避免小包也只能保留三个而反复解压。
        // 读取器可能在两次请求间增长，因此每次请求均重新统计；当前正在使用的单包允许超预算。
        size_t bytes = 0;
        for (auto &[name, reader] : readers_)
            bytes += reader->ResidentBytes();
        while (readers_.size() > 1 && (bytes > 512ULL * 1024 * 1024 || readers_.size() > 64))
        {
            auto last = recent_.back();
            if (last == keep)
            {
                recent_.pop_back();
                recent_.push_front(last);
                continue;
            }
            bytes -= readers_.at(last)->ResidentBytes();
            recent_.pop_back();
            readers_.erase(last);
            ++readerStats_.Evictions;
        }
    }

    std::shared_ptr<NativeGpuAssetReader> NativeGpuAssetStore::Reader(const std::filesystem::path &path)
    {
        Cancel();
        auto key = Key(path);
        TrimReaders(key);
        auto found = readers_.find(key);
        if (found != readers_.end())
        {
            recent_.remove(key);
            recent_.push_front(key);
            ++readerStats_.Hits;
            return found->second;
        }
        auto started = std::chrono::steady_clock::now();
        auto reader = std::make_shared<NativeGpuAssetReader>(path, database_.get(), blockCache_, cancellation_);
        auto elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        RememberReader(key, reader, elapsed);
        return reader;
    }

    void NativeGpuAssetStore::RememberReader(const std::string &key,
                                             const std::shared_ptr<NativeGpuAssetReader> &reader, double seconds)
    {
        ++readerStats_.Loads;
        readerStats_.LoadSeconds += seconds;
        auto &resource = readerStats_.Resources[key];
        ++resource.first;
        resource.second += seconds;
        reader->ScriptSchema = scriptSchema_;
        cabNames_[key] = reader->CabName;
        readers_[key] = reader;
        recent_.push_front(key);
        TrimReaders(key);
    }

    std::shared_ptr<const GpuPrefabData> NativeGpuAssetStore::Prefab(const std::filesystem::path &path,
                                                                     const std::string &name)
    {
        Cancel();
        auto &prefabs = prefabs_[Key(path)];
        auto found = prefabs.find(name);
        if (found != prefabs.end())
            return found->second;
        auto prefab = std::make_shared<const GpuPrefabData>(Reader(path)->Prefab(name));
        prefabs.emplace(name, prefab);
        return prefab;
    }

    void NativeGpuAssetStore::IndexCabNames()
    {
        // 仅并行构造相互独立的读取器；脚本回调、索引和 LRU 仍由调用线程维护。
        // 每批最多四个包，避免同时展开大量 MOD。future 在取消/异常时也会等待线程退出。
        using Loaded = std::pair<std::shared_ptr<NativeGpuAssetReader>, double>;
        const auto workers = std::clamp(std::thread::hardware_concurrency(), 1u, 4u);
        while (indexed_ < paths_.size())
        {
            Cancel();
            const auto end = std::min(paths_.size(), indexed_ + workers);
            std::vector<std::future<Loaded>> pending;
            for (size_t i = indexed_; i < end; ++i)
            {
                if (cabNames_.contains(Key(paths_[i])))
                    pending.emplace_back();
                else
                    pending.push_back(std::async(
                        std::launch::async,
                        [path = paths_[i], database = database_, cache = blockCache_, cancellation = cancellation_]
                        {
                            auto started = std::chrono::steady_clock::now();
                            auto reader =
                                std::make_shared<NativeGpuAssetReader>(path, database.get(), cache, cancellation);
                            return Loaded{
                                std::move(reader),
                                std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count()};
                        }));
            }
            for (auto &future : pending)
            {
                Cancel();
                auto &path = paths_[indexed_];
                auto key = Key(path);
                if (future.valid())
                {
                    auto [reader, seconds] = future.get();
                    RememberReader(key, reader, seconds);
                }
                cabs_[Lower(cabNames_.at(key))].push_back(path);
                ++indexed_;
            }
        }
    }

    void NativeGpuAssetStore::IndexShaderNames(const std::shared_ptr<NativeGpuAssetReader> &reader)
    {
        Cancel();
        auto key = Key(reader->Path);
        if (!shaderNames_.contains(key))
            shaderNames_.emplace(key, reader->ShaderNames());
    }

    std::optional<GpuAssetPointer> NativeGpuAssetStore::FindShader(const std::filesystem::path &path,
                                                                   std::string_view name)
    {
        Cancel();
        auto key = Key(path);
        if (!shaderNames_.contains(key))
            IndexShaderNames(Reader(path));
        auto &names = shaderNames_.at(key);
        auto found = names.find(Lower(std::string(name)));
        if (found == names.end() || found->second.empty())
            return {};
        return GpuAssetPointer{std::filesystem::absolute(path), 0, found->second.front()};
    }

    GpuAssetPointer NativeGpuAssetStore::Resolve(const GpuAssetPointer &pointer)
    {
        Cancel();
        if (pointer.FileId == 0 || pointer.PathId == 0)
            return pointer;
        auto key = Key(pointer);
        auto cached = resolved_.find(key);
        if (cached != resolved_.end())
            return cached->second;
        auto origin = Reader(pointer.Bundle);
        if (pointer.FileId < 1 || static_cast<size_t>(pointer.FileId) > origin->ExternalNames.size())
            throw std::runtime_error("External resource index is invalid.");
        auto cab = origin->ExternalNames[pointer.FileId - 1];
        auto identity = Lower(cab);
        GpuAssetPointer result;
        if (identity == "unity default resources" || identity == "unity_builtin_extra")
        {
            if (game_.empty())
                throw std::runtime_error("Game directory is required for built-in resource data.");
            auto path = game_ / "HoneySelect2_Data/Resources" / cab;
            if (!std::filesystem::is_regular_file(path))
                throw std::runtime_error("Built-in resource data is missing.");
            result = {path, 0, pointer.PathId};
        }
        else if (identity.ends_with(".assets"))
        {
            auto path = pointer.Bundle.parent_path() / cab;
            if (!std::filesystem::is_regular_file(path) && !game_.empty())
                path = game_ / "HoneySelect2_Data" / cab;
            if (!std::filesystem::is_regular_file(path))
                throw std::runtime_error("Referenced serialized file is missing.");
            result = {path, 0, pointer.PathId};
        }
        else
        {
            IndexCabNames();
            auto found = cabs_.find(identity);
            if (found == cabs_.end())
                throw std::runtime_error("Referenced resource bundle is missing: " + cab);
            auto &candidates = found->second;
            auto selected = candidates[0];
            if (candidates.size() > 1)
            {
                auto local = std::find_if(candidates.begin(), candidates.end(),
                                          [&](auto &p)
                                          {
                                              return Key(p) == Key(pointer.Bundle);
                                          });
                if (local == candidates.end())
                    throw std::runtime_error("Ambiguous resource bundle identity: " + cab);
                selected = *local;
            }
            result = {selected, 0, pointer.PathId};
        }
        resolved_[key] = result;
        return result;
    }

    std::shared_ptr<GpuMeshData> NativeGpuAssetStore::Mesh(const GpuAssetPointer &pointer)
    {
        auto p = Resolve(pointer);
        auto key = Key(p);
        auto found = meshes_.find(key);
        if (found != meshes_.end())
            return found->second;
        auto mesh = std::make_shared<GpuMeshData>(Reader(p.Bundle)->Mesh(p.PathId));
        meshes_[key] = mesh;
        return mesh;
    }

    GpuTextureData NativeGpuAssetStore::Texture(const GpuAssetPointer &pointer)
    {
        auto p = Resolve(pointer);
        return Reader(p.Bundle)->Texture(p.PathId);
    }

    std::shared_ptr<GpuMaterialData> NativeGpuAssetStore::Material(const GpuAssetPointer &pointer)
    {
        auto p = Resolve(pointer);
        auto key = Key(p);
        auto found = materials_.find(key);
        if (found != materials_.end())
            return found->second;
        auto material = std::make_shared<GpuMaterialData>(Reader(p.Bundle)->Material(p.PathId));
        materials_[key] = material;
        return material;
    }

    Parameter NativeGpuAssetStore::Metadata(const GpuAssetPointer &pointer)
    {
        auto p = Resolve(pointer);
        return Reader(p.Bundle)->Metadata(p.PathId);
    }

    bool NativeGpuAssetStore::ContainsAsset(const GpuAssetPointer &pointer)
    {
        return AssetClass(pointer).has_value();
    }

    std::optional<int> NativeGpuAssetStore::AssetClass(const GpuAssetPointer &pointer)
    {
        auto p = Resolve(pointer);
        auto key = Key(p);
        if (auto found = classes_.find(key); found != classes_.end())
            return found->second;
        return classes_.emplace(key, Reader(p.Bundle)->AssetClass(p.PathId)).first->second;
    }

    GpuAssetPointer NativeGpuAssetStore::GameResource(std::string_view name, int classId, int textureSize)
    {
        Cancel();
        if (game_.empty() || textureSize < 0 || (textureSize > 0 && classId != 28))
            throw std::runtime_error("Invalid game resource request.");
        auto key = std::string(name) + "|" + std::to_string(classId) + "|" + std::to_string(textureSize);
        auto found = gameResources_.find(key);
        if (found != gameResources_.end())
            return found->second;
        auto root = game_ / "HoneySelect2_Data";
        std::vector<std::filesystem::path> files;
        for (auto &file : std::filesystem::directory_iterator(root))
            if (file.is_regular_file() && file.path().extension() == ".assets")
                files.push_back(file.path());
        std::stable_sort(files.begin(), files.end(),
                         [](auto &a, auto &b)
                         {
                             return a.filename() == "sharedassets2.assets" && b.filename() != "sharedassets2.assets";
                         });
        for (auto builtinName : {"unity_builtin_extra", "unity default resources"})
            if (auto p = root / "Resources" / builtinName; std::filesystem::is_regular_file(p))
                files.push_back(p);
        for (auto &p : files)
        {
            Cancel();
            if (auto resource = Reader(p)->TryFind(name, classId, textureSize))
            {
                gameResources_[key] = *resource;
                return *resource;
            }
        }
        throw std::runtime_error("Game resource is missing: " + std::to_string(classId) + "/" + std::string(name));
    }
}
