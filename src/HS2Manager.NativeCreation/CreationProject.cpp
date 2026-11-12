#include "CreationProject.h"
#include "Export/CardExport.h"
#include <windows.h>
#include <algorithm>
#include <sstream>

namespace hs2::creation
{
    namespace
    {
        Json ReadJsonFile(const fs::path &path)
        {
            return Json::parse(ReadFile(path, 16 * 1024 * 1024));
        }

        bool Identifier(const std::string &value)
        {
            return value.size() == 32 && value.find_first_not_of("0123456789abcdefABCDEF") == value.npos;
        }

        Json TextureJson(const CreationTexture &t)
        {
            return {{"id", t.Id},
                    {"name", t.Name},
                    {"kind", t.Kind},
                    {"target", t.Target},
                    {"property", t.Property},
                    {"clothing_name", t.ClothingName},
                    {"slot", t.MaterialSlot},
                    {"replace", t.Replace},
                    {"image", t.Image.ToJson()}};
        }

        CreationTexture TextureOf(const Json &j)
        {
            CreationTexture t;
            t.Id = j.at("id");
            t.Name = j.at("name");
            t.Kind = j.at("kind");
            t.Target = j.at("target");
            t.Property = j.at("property");
            t.ClothingName = j.at("clothing_name");
            t.MaterialSlot = j.at("slot");
            t.Replace = j.at("replace");
            t.Image = PaintDocument::FromJson(j.at("image"));
            return t;
        }

        CreationState ReadSnapshot(const fs::path &directory, const std::string &snapshot)
        {
            if (!Identifier(snapshot))
                throw std::runtime_error("工程快照标识无效");
            auto folder = directory / "Snapshots" / Path(snapshot);
            ExportPaths::NoLinks(folder);
            auto manifest = ReadJsonFile(folder / "snapshot.json");
            auto bytes = ReadFile(folder / "document.cbor", 2ull * 1024 * 1024 * 1024);
            if (Sha256(bytes) != manifest.at("hash").get<std::string>())
                throw std::runtime_error("工程快照校验失败");
            auto state = CreationProject::Decode(Json::from_cbor(bytes), directory / "character.png");
            if (state.Token != manifest.at("token").get<std::string>())
                throw std::runtime_error("工程快照版本不一致");
            return state;
        }
    }

    CreationProject::CreationProject(fs::path directory) : directory_(fs::absolute(std::move(directory)))
    {
    }

    void CreationProject::Lock()
    {
        ExportPaths::NoLinks(directory_);
        auto handle = CreateFileW(NativeFilePath(directory_ / ".project.lock").c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                                  nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_HIDDEN, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
            throw std::runtime_error("创作工程已被其他窗口打开，或目录无法写入");
        lock_ = std::shared_ptr<void>(handle,
                                      [](void *value)
                                      {
                                          CloseHandle(value);
                                      });
    }

    std::shared_ptr<CreationProject> CreationProject::Create(const fs::path &root, std::string name, std::string kind,
                                                             CharacterDocument character)
    {
        auto id = NewId();
        fs::create_directories(root / Path(id));
        auto project = std::shared_ptr<CreationProject>(new CreationProject(root / Path(id)));
        project->id_ = id;
        project->Lock();
        auto state = std::make_shared<CreationState>();
        state->Name = std::move(name);
        state->Kind = std::move(kind);
        state->Character = character.WithName(state->Name);
        Validate(*state);
        project->state_ = std::move(state);
        project->Save();
        return project;
    }

    std::shared_ptr<CreationProject> CreationProject::Open(const fs::path &path, bool recovery)
    {
        auto project = std::shared_ptr<CreationProject>(new CreationProject(path));
        project->Lock();
        auto manifest = ReadJsonFile(project->directory_ / "project.json");
        if (manifest.at("version") != 1)
            throw std::runtime_error("不支持的创作工程版本");
        project->id_ = manifest.at("id");
        if (!Identifier(project->id_))
            throw std::runtime_error("工程标识无效");
        auto saved = manifest.at("saved").get<std::string>();
        auto state = ReadSnapshot(project->directory_, saved);
        project->savedToken_ = state.Token;
        if (recovery && manifest.contains("recovery"))
            state = ReadSnapshot(project->directory_, manifest.at("recovery"));
        project->revision_ = manifest.value("revision", std::uint64_t(0));
        project->state_ = std::make_shared<CreationState>(std::move(state));
        auto jobs = project->directory_ / "Builds";
        if (fs::is_directory(jobs))
            for (const auto &entry : fs::directory_iterator(jobs))
                if (entry.is_directory() && fs::is_regular_file(entry.path() / "job.json"))
                {
                    auto job = ReadJsonFile(entry.path() / "job.json");
                    if (job.value("status", "") == "running")
                    {
                        job["status"] = "interrupted";
                        job["message"] = "上次构建中断，工程和上次有效成品已保留";
                        WriteJson(entry.path() / "job.json", job);
                    }
                }
        return project;
    }

    std::vector<ProjectSummary> CreationProject::List(const fs::path &root)
    {
        std::vector<ProjectSummary> result;
        if (!fs::is_directory(root))
            return result;
        for (const auto &entry : fs::directory_iterator(root))
            if (entry.is_directory() && fs::is_regular_file(entry.path() / "project.json"))
            {
                ProjectSummary item;
                item.Path = entry.path();
                try
                {
                    ExportPaths::NoLinks(entry.path());
                    auto j = ReadJsonFile(entry.path() / "project.json");
                    item.Id = j.at("id");
                    item.Name = j.at("name");
                    item.Kind = j.at("kind");
                    item.UpdatedUtc = j.at("updated_utc");
                    item.Sex = j.at("sex");
                    item.HasRecovery = j.contains("recovery") && j.at("recovery") != j.at("saved");
                }
                catch (const std::exception &e)
                {
                    item.Name = Utf8(entry.path().filename());
                    item.Error = e.what();
                }
                result.push_back(std::move(item));
            }
        std::sort(result.begin(), result.end(),
                  [](const auto &a, const auto &b)
                  {
                      return a.UpdatedUtc > b.UpdatedUtc;
                  });
        return result;
    }

    std::shared_ptr<CreationProject> CreationProject::Duplicate(const fs::path &root, const fs::path &source,
                                                                std::string name)
    {
        auto original = Open(source);
        auto copy = Create(root, name, original->State().Kind, *original->State().Character);
        auto state = original->State();
        state.Name = std::move(name);
        state.Token = NewId();
        state.LastBuild = nullptr;
        state.Character = state.Character->WithName(state.Name);
        state.CoverToken.clear();
        // 已构建素材引用仍指向原包，复制后清除相应槽位的旧引用并由新包重新绑定。
        for (auto &asset : state.Assets)
        {
            if (auto binding = CharacterAssetReferences::Describe(*state.Character, asset.TargetPath))
                state.Character = CharacterAssetReferences::Select(
                    *state.Character, asset.TargetPath, {binding->Category, 0, "未构建素材", "", "原版", asset.Parent});
        }
        copy->state_ = std::make_shared<CreationState>(std::move(state));
        auto sources = original->Directory() / "Sources";
        if (fs::is_directory(sources))
            for (const auto &entry : fs::recursive_directory_iterator(sources))
                if (entry.is_regular_file())
                {
                    ExportPaths::NoLinks(entry.path());
                    auto destination = copy->Directory() / "Sources" / fs::relative(entry.path(), sources);
                    fs::create_directories(destination.parent_path());
                    WriteAtomic(destination, ReadFile(entry.path(), 512ull * 1024 * 1024));
                }
        copy->Save();
        return copy;
    }

    void CreationProject::Validate(const CreationState &state)
    {
        if (state.Name.empty() || state.Name.size() > 1024 || state.Name.find('\0') != std::string::npos ||
            (state.Kind != "character" && state.Kind != "mod") || !state.Character)
            throw std::invalid_argument("工程名称、类型或角色数据无效");
        std::set<std::string> ids;
        std::set<int> slots;
        std::set<std::string> targets;
        for (const auto &asset : state.Assets)
        {
            if (!Identifier(asset.Id) || !ids.insert(asset.Id).second || !slots.insert(asset.Slot).second ||
                asset.Slot < 100000 || asset.Name.empty() ||
                (asset.Kind != "accessory" && asset.Kind != "hair" && asset.Kind != "clothes"))
                throw std::invalid_argument("工程素材标识、类别或编号无效");
            if (!targets.insert(asset.TargetPath).second)
                throw std::invalid_argument("同一角色槽位只能绑定一个素材，请选择其他槽位");
            for (const auto &object : asset.Objects)
            {
                if (!Identifier(object.Id) || !ids.insert(object.Id).second)
                    throw std::invalid_argument("对象编号重复");
                object.Mesh.Validate();
            }
            std::set<std::pair<int, std::string>> channels;
            for (const auto &texture : asset.Textures)
            {
                if (!Identifier(texture.Id) || !ids.insert(texture.Id).second ||
                    !channels.emplace(texture.MaterialSlot, texture.Kind + ":" + texture.Property).second)
                    throw std::invalid_argument("此材质通道已有贴图，请在现有贴图中添加图层");
                texture.Image.Validate();
            }
            std::set<std::string> bones;
            if (!asset.DynamicSettings.is_object())
                throw std::invalid_argument("动态骨骼设置格式无效");
            for (const auto &[bone, values] : asset.DynamicSettings.items())
            {
                if (!values.is_object())
                    throw std::invalid_argument("动态链设置格式无效");
                for (const auto &[key, value] : values.items())
                {
                    if (!value.is_number() || !std::isfinite(value.get<float>()) || value.get<float>() < 0 ||
                        value.get<float>() > (key == "radius" ? 10.f : 1.f))
                        throw std::invalid_argument("动态链参数超出范围");
                }
            }
            for (const auto &bone : asset.Bones)
                if (bone.Name.empty() || !bones.insert(bone.Name).second || !Finite(bone.Position))
                    throw std::invalid_argument("工程骨骼无效或重名");
            for (const auto &bone : asset.Bones)
            {
                std::set<std::string> visited{bone.Name};
                auto parent = bone.Parent;
                while (bones.contains(parent))
                {
                    if (!visited.insert(parent).second)
                        throw std::invalid_argument("骨骼父级存在循环");
                    parent = std::find_if(asset.Bones.begin(), asset.Bones.end(),
                                          [&](const auto &b)
                                          {
                                              return b.Name == parent;
                                          })
                                 ->Parent;
                }
            }
        }
        std::set<std::string> overlays;
        for (const auto &texture : state.Overlays)
        {
            if (!Identifier(texture.Id) || !ids.insert(texture.Id).second ||
                (texture.Target != "face" && texture.Target != "body" && texture.Target != "clothes") ||
                (texture.Target == "clothes" && ClothingOverlaySlot(texture.ClothingName) < 0))
                throw std::invalid_argument("覆盖贴图目标无效");
            if (!overlays.insert(texture.Target + (texture.Target == "clothes" ? texture.ClothingName : "")).second)
                throw std::invalid_argument("此部位已有覆盖贴图，请在现有贴图中添加图层");
            texture.Image.Validate();
        }
    }

    void CreationProject::Notify()
    {
        if (Changed)
            Changed();
    }

    void CreationProject::Edit(std::string label, const std::function<void(CreationState &)> &operation)
    {
        auto next = std::make_shared<CreationState>(*state_);
        operation(*next);
        Validate(*next);
        next->Token = NewId();
        if (!gesture_)
        {
            undo_.push_back(state_);
            if (undo_.size() > 64)
                undo_.erase(undo_.begin());
        }
        redo_.clear();
        state_ = std::move(next);
        ++revision_;
        Notify();
        (void)label;
    }

    void CreationProject::BeginGesture()
    {
        EndGesture();
        gesture_ = state_;
    }

    void CreationProject::EndGesture()
    {
        if (gesture_ && gesture_ != state_)
        {
            undo_.push_back(gesture_);
            if (undo_.size() > 64)
                undo_.erase(undo_.begin());
            redo_.clear();
        }
        gesture_.reset();
    }

    void CreationProject::CancelGesture()
    {
        if (gesture_)
        {
            state_ = gesture_;
            gesture_.reset();
            ++revision_;
            Notify();
        }
    }

    void CreationProject::Undo()
    {
        EndGesture();
        if (undo_.empty())
            return;
        redo_.push_back(state_);
        state_ = undo_.back();
        undo_.pop_back();
        ++revision_;
        Notify();
    }

    void CreationProject::Redo()
    {
        EndGesture();
        if (redo_.empty())
            return;
        undo_.push_back(state_);
        state_ = redo_.back();
        redo_.pop_back();
        ++revision_;
        Notify();
    }

    Json CreationProject::Encode(const CreationState &state)
    {
        Json assets = Json::array(), overlays = Json::array();
        for (const auto &a : state.Assets)
        {
            Json objects = Json::array(), textures = Json::array();
            for (const auto &o : a.Objects)
                objects.push_back({{"id", o.Id},
                                   {"hidden", o.Hidden},
                                   {"locked", o.Locked},
                                   {"half", o.HalfState},
                                   {"mesh", o.Mesh.ToJson()},
                                   {"generator", o.Generator},
                                   {"generator_hash", o.GeneratorMeshHash}});
            for (const auto &t : a.Textures)
                textures.push_back(TextureJson(t));
            asset::Geometry skeleton;
            skeleton.Bones = a.Bones;
            assets.push_back({{"id", a.Id},
                              {"name", a.Name},
                              {"kind", a.Kind},
                              {"slot", a.Slot},
                              {"target", a.TargetPath},
                              {"template", a.TemplateId},
                              {"parent", a.Parent},
                              {"objects", objects},
                              {"skeleton", asset::GeometryJson(skeleton)},
                              {"locked_bones", a.LockedBones},
                              {"dynamic_roots", a.DynamicRoots},
                              {"dynamic_settings", a.DynamicSettings},
                              {"template_source", a.TemplateSource},
                              {"textures", textures},
                              {"materials", a.MaterialValues},
                              {"half", a.IndependentHalfState}});
        }
        for (const auto &t : state.Overlays)
            overlays.push_back(TextureJson(t));
        return {{"version", 1},
                {"name", state.Name},
                {"kind", state.Kind},
                {"token", state.Token},
                {"character", Json::binary(state.Character->ToBytes())},
                {"assets", assets},
                {"overlays", overlays},
                {"cover_settings", state.CoverSettings},
                {"cover_token", state.CoverToken},
                {"last_build", state.LastBuild}};
    }

    CreationState CreationProject::Decode(const Json &j, const fs::path &source)
    {
        if (j.at("version") != 1 || !j.at("character").is_binary())
            throw std::runtime_error("工程数据版本无效");
        CreationState s;
        s.Name = j.at("name");
        s.Kind = j.at("kind");
        s.Token = j.at("token");
        const auto &binary = j.at("character").get_binary();
        s.Character = CharacterDocument::FromBytes(Bytes(binary.begin(), binary.end()), source);
        for (const auto &a : j.at("assets"))
        {
            CreationAsset asset;
            asset.Id = a.at("id");
            asset.Name = a.at("name");
            asset.Kind = a.at("kind");
            asset.Slot = a.at("slot");
            asset.TargetPath = a.at("target");
            asset.TemplateId = a.at("template");
            asset.Parent = a.at("parent");
            asset.Bones = asset::GeometryOf(a.at("skeleton")).Bones;
            asset.LockedBones = a.at("locked_bones").get<std::set<std::string>>();
            asset.DynamicRoots = a.at("dynamic_roots").get<std::vector<std::string>>();
            asset.DynamicSettings = a.value("dynamic_settings", Json::object());
            asset.TemplateSource = a.value("template_source", Json::object());
            asset.MaterialValues = a.at("materials");
            asset.IndependentHalfState = a.at("half");
            for (const auto &o : a.at("objects"))
            {
                CreationObject object;
                object.Id = o.at("id");
                object.Hidden = o.at("hidden");
                object.Locked = o.at("locked");
                object.HalfState = o.at("half");
                object.Mesh = MeshDocument::FromJson(o.at("mesh"));
                object.Generator = o.value("generator", Json(nullptr));
                object.GeneratorMeshHash = o.value("generator_hash", "");
                asset.Objects.push_back(std::move(object));
            }
            for (const auto &t : a.at("textures"))
                asset.Textures.push_back(TextureOf(t));
            s.Assets.push_back(std::move(asset));
        }
        for (const auto &t : j.at("overlays"))
            s.Overlays.push_back(TextureOf(t));
        s.CoverSettings = j.at("cover_settings");
        s.CoverToken = j.at("cover_token");
        s.LastBuild = j.at("last_build");
        Validate(s);
        return s;
    }

    void CreationProject::Save(bool recovery)
    {
        CommitSave(PrepareSave(state_, revision_), recovery);
    }

    PreparedProjectSave CreationProject::PrepareSave(std::shared_ptr<const CreationState> state,
                                                     std::uint64_t revision) const
    {
        std::lock_guard lock(saveGate_);
        Validate(*state);
        auto snapshot = NewId();
        auto folder = directory_ / "Snapshots" / Path(snapshot);
        fs::create_directories(folder);
        auto bytes = Json::to_cbor(Encode(*state));
        WriteAtomic(folder / "document.cbor", bytes);
        if (HashFile(folder / "document.cbor") != Sha256(bytes))
            throw std::runtime_error("工程写回校验失败");
        WriteJson(folder / "snapshot.json", {{"hash", Sha256(bytes)}, {"token", state->Token}, {"revision", revision}});
        return {snapshot, state->Token, revision};
    }

    void CreationProject::CommitSave(const PreparedProjectSave &save, bool recovery)
    {
        std::lock_guard lock(saveGate_);
        // 后台仅写独立快照；提交清单回到编辑线程，旧任务不能取代较新的保存。
        if (save.Token != state_->Token || save.Revision != revision_ || (recovery && !IsDirty()))
            return;
        auto snapshot = save.Snapshot;
        Json manifest = fs::is_regular_file(directory_ / "project.json") ? ReadJsonFile(directory_ / "project.json")
                                                                         : Json::object();
        manifest.update({{"version", 1},
                         {"id", id_},
                         {"name", state_->Name},
                         {"kind", state_->Kind},
                         {"sex", state_->Character->Sex()},
                         {"updated_utc", UtcNow()},
                         {"revision", revision_}});
        if (recovery && manifest.contains("saved"))
            manifest["recovery"] = snapshot;
        else
        {
            manifest["saved"] = snapshot;
            manifest.erase("recovery");
        }
        WriteJson(directory_ / "project.json", manifest);
        if (!recovery)
            savedToken_ = state_->Token;
        // 仅回收本工程自有的旧快照，保留手动保存、恢复及最近两份完整快照。
        std::vector<fs::directory_entry> candidates;
        for (const auto &entry : fs::directory_iterator(directory_ / "Snapshots"))
            if (entry.is_directory() && Identifier(Utf8(entry.path().filename())))
                candidates.push_back(entry);
        std::sort(candidates.begin(), candidates.end(),
                  [](const auto &a, const auto &b)
                  {
                      return a.last_write_time() > b.last_write_time();
                  });
        for (std::size_t i = 2; i < candidates.size(); ++i)
        {
            auto name = Utf8(candidates[i].path().filename());
            if (name == manifest.value("saved", "") || name == manifest.value("recovery", ""))
                continue;
            ExportPaths::NoLinks(candidates[i].path());
            if (!Within(candidates[i].path(), directory_ / "Snapshots"))
                continue;
            std::error_code ignored;
            fs::remove_all(candidates[i].path(), ignored);
        }
        if (!recovery)
            Notify();
    }

    void CreationProject::AcceptBuild(Json result, CharacterDocument character, std::uint64_t expectedRevision)
    {
        if (revision_ != expectedRevision)
            throw std::runtime_error("工程已有新修改；构建结果保留，未应用旧版本");
        Edit("应用构建",
             [&](CreationState &s)
             {
                 s.LastBuild = std::move(result);
                 s.Character = std::move(character);
             });
        auto accepted = std::make_shared<CreationState>(*state_);
        accepted->LastBuild["state_token"] = accepted->Token;
        accepted->CoverToken = accepted->Token;
        state_ = std::move(accepted);
        Notify();
        Save(true);
    }

    void CreationProject::AcceptCover(CharacterDocument character, Json settings, std::uint64_t expectedRevision)
    {
        if (revision_ != expectedRevision)
            throw std::runtime_error("工程已有新修改；旧封面未应用");
        Edit("更新封面",
             [&](CreationState &state)
             {
                 state.Character = std::move(character);
                 state.CoverSettings = std::move(settings);
             });
        auto covered = std::make_shared<CreationState>(*state_);
        covered->CoverToken = covered->Token;
        state_ = std::move(covered);
        Notify();
    }

    fs::path CreationProject::ImportSource(const fs::path &source)
    {
        auto root = fs::weakly_canonical(source.parent_path());
        std::map<fs::path, Bytes> files;
        std::size_t total = 0;
        std::function<void(fs::path)> add = [&](fs::path path)
        {
            path = fs::weakly_canonical(path);
            ExportPaths::NoLinks(path);
            if (!Within(path, root))
                throw std::runtime_error("模型引用了源目录外的文件，请先整理为同目录资源：" + Utf8(path));
            auto relative = path.lexically_relative(root);
            if (files.contains(relative))
                return;
            auto data = ReadFile(path, 256ull * 1024 * 1024);
            total += data.size();
            if (total > 512ull * 1024 * 1024 || files.size() >= 1024)
                throw std::runtime_error("导入资源总容量超过限制");
            files.emplace(relative, data);
            auto extension = path.extension().wstring();
            std::transform(extension.begin(), extension.end(), extension.begin(), towlower);
            if (extension == L".gltf")
            {
                auto json = Json::parse(data);
                for (auto key : {"buffers", "images"})
                    for (const auto &resource : json.value(key, Json::array()))
                    {
                        auto uri = resource.value("uri", "");
                        if (uri.empty() || uri.starts_with("data:"))
                            continue;
                        if (uri.find(':') != uri.npos || uri.find('?') != uri.npos || uri.find('#') != uri.npos)
                            throw std::runtime_error("模型外部资源只支持相对文件路径");
                        std::string decoded;
                        for (std::size_t i = 0; i < uri.size(); ++i)
                        {
                            if (uri[i] != '%')
                            {
                                decoded += uri[i];
                                continue;
                            }
                            if (i + 2 >= uri.size())
                                throw std::runtime_error("资源路径转义无效");
                            auto hex = uri.substr(i + 1, 2);
                            if (hex.find_first_not_of("0123456789abcdefABCDEF") != hex.npos)
                                throw std::runtime_error("资源路径转义无效");
                            auto c = static_cast<char>(std::stoi(hex, nullptr, 16));
                            if (!c)
                                throw std::runtime_error("资源路径包含空字符");
                            decoded += c;
                            i += 2;
                        }
                        if (Path(decoded).is_absolute())
                            throw std::runtime_error("资源路径必须相对于模型");
                        add(path.parent_path() / Path(decoded));
                    }
            }
            else if (extension == L".obj" || extension == L".mtl")
            {
                std::istringstream stream(std::string(data.begin(), data.end()));
                std::string line;
                while (std::getline(stream, line))
                {
                    std::istringstream row(line);
                    std::string key, value;
                    row >> key;
                    if (key != "mtllib" && !key.starts_with("map_") && key != "bump" && key != "disp" && key != "decal")
                        continue;
                    std::getline(row >> std::ws, value);
                    if (!value.empty() && value.back() == '\r')
                        value.pop_back();
                    if (value.empty())
                        continue;
                    if (value.front() == '-')
                        throw std::runtime_error("MTL 带选项的贴图路径请先整理为普通相对路径");
                    if (value.front() == '"' && value.back() == '"')
                        value = value.substr(1, value.size() - 2);
                    if (Path(value).is_absolute())
                        throw std::runtime_error("MTL 贴图路径必须相对于模型");
                    add(path.parent_path() / Path(value));
                }
            }
        };
        add(source);
        std::string fingerprint;
        for (const auto &[relative, data] : files)
            fingerprint += Utf8(relative) + ":" + Sha256(data) + "\n";
        auto directory = directory_ / "Sources" / Path(Sha256(Bytes(fingerprint.begin(), fingerprint.end())));
        ExportPaths::NoLinks(directory);
        fs::create_directories(directory);
        for (const auto &[relative, data] : files)
        {
            auto target = directory / relative;
            ExportPaths::NoLinks(target);
            fs::create_directories(target.parent_path());
            if (!fs::exists(target))
                WriteAtomic(target, data);
            else if (HashFile(target) != Sha256(data))
                throw std::runtime_error("工程导入资源已损坏");
        }
        return directory / fs::weakly_canonical(source).lexically_relative(root);
    }
}
