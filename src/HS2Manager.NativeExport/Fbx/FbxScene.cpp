#include "Fbx/FbxScene.h"
#include <windows.h>
#include <cmath>
#include <limits>
#include <set>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        std::string Utf8(const std::filesystem::path &path)
        {
            auto bytes = path.u8string();
            return {reinterpret_cast<const char *>(bytes.data()), bytes.size()};
        }

        void Text(const std::string &value)
        {
            if (value.find('\0') != std::string::npos)
                throw std::invalid_argument("FBX names and paths cannot contain NUL.");
        }

        void Finite(std::span<const float> values)
        {
            for (float value : values)
                if (!std::isfinite(value))
                    throw std::invalid_argument("Non-finite FBX data.");
        }

        int Count(size_t size)
        {
            if (size > static_cast<size_t>(std::numeric_limits<int>::max()))
                throw std::length_error("FBX span exceeds integer range.");
            return static_cast<int>(size);
        }
    }

    struct FbxScene::Library
    {
        HMODULE Module = nullptr;
        const char *(__cdecl *Error)() = nullptr;
        void *(__cdecl *Create)() = nullptr;
        void(__cdecl *Destroy)(void *) = nullptr;
        int(__cdecl *Bone)(void *, const char *, int, const float *, const float *) = nullptr;
        int(__cdecl *Material)(void *, const char *, const char *, const char *, const char *) = nullptr;
        int(__cdecl *Mesh)(void *, const char *, int, const float *, const float *, const float *,
                           const float *) = nullptr;
        int(__cdecl *Uv)(void *, int, int, const float *) = nullptr;
        int(__cdecl *Colors)(void *, int, const float *) = nullptr;
        int(__cdecl *Triangles)(void *, int, int, int, const int *, int) = nullptr;
        int(__cdecl *Skin)(void *, int, int, int, const int *, const float *) = nullptr;
        int(__cdecl *Shape)(void *, int, const char *, float, const float *, const float *, const float *) = nullptr;
        int(__cdecl *Write)(void *, const char *) = nullptr;
        int(__cdecl *Reopen)(const char *, NativeFbxSummary *) = nullptr;
        std::set<std::pair<int, std::string>> Shapes;

        template <class T> void Bind(T &target, const char *name)
        {
            target = reinterpret_cast<T>(GetProcAddress(Module, name));
            if (!target)
                throw std::runtime_error(std::string("FBX bridge entry point missing: ") + name);
        }

        explicit Library(const std::filesystem::path &path)
        {
            if (!path.is_absolute() || !std::filesystem::is_regular_file(path))
                throw std::invalid_argument("FBX bridge must be an existing absolute library path.");
            // DLL_LOAD_DIR 查找依赖时使用 Windows 路径分隔符，而 std::filesystem 会保留调用者传入路径中的正斜杠。
            auto native = std::filesystem::absolute(path).lexically_normal();
            native.make_preferred();
            Module = LoadLibraryExW(native.c_str(), nullptr,
                                    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            if (!Module)
                throw std::runtime_error("Cannot load FBX bridge (Win32 error " + std::to_string(GetLastError()) +
                                         ").");
            try
            {
                Bind(Error, "hs2_fbx_error");
                Bind(Create, "hs2_fbx_create");
                Bind(Destroy, "hs2_fbx_destroy");
                Bind(Bone, "hs2_fbx_bone");
                Bind(Material, "hs2_fbx_material");
                Bind(Mesh, "hs2_fbx_mesh");
                Bind(Uv, "hs2_fbx_uv");
                Bind(Colors, "hs2_fbx_colors");
                Bind(Triangles, "hs2_fbx_triangles");
                Bind(Skin, "hs2_fbx_skin");
                Bind(Shape, "hs2_fbx_shape");
                Bind(Write, "hs2_fbx_write");
                Bind(Reopen, "hs2_fbx_reopen");
            }
            catch (...)
            {
                FreeLibrary(Module);
                Module = nullptr;
                throw;
            }
        }

        ~Library()
        {
            if (Module)
                FreeLibrary(Module);
        }
    };

    FbxScene::FbxScene(const std::filesystem::path &path) : library_(std::make_unique<Library>(path))
    {
        scene_ = library_->Create();
        if (!scene_)
            throw std::runtime_error(library_->Error() ? library_->Error() : "FBX initialization failed.");
    }

    FbxScene::~FbxScene()
    {
        if (scene_)
            library_->Destroy(scene_);
    }

    int FbxScene::Check(int result) const
    {
        if (result < 0)
            throw std::runtime_error(library_->Error() ? library_->Error() : "FBX operation failed.");
        return result;
    }

    void FbxScene::CheckSpan(int mesh, std::span<const float> values, int components) const
    {
        if (mesh < 0 || static_cast<size_t>(mesh) >= vertices_.size() ||
            values.size() != static_cast<size_t>(vertices_[mesh]) * components)
            throw std::invalid_argument("FBX attribute dimensions differ from mesh.");
        Finite(values);
    }

    int FbxScene::Bone(const std::string &name, int parent, const std::array<float, 3> &translation,
                       const std::array<float, 4> &rotation)
    {
        Text(name);
        Finite(translation);
        Finite(rotation);
        if (parent < -1 || parent >= expected_.Bones)
            throw std::invalid_argument("Invalid parent bone.");
        const auto index = Check(library_->Bone(scene_, name.c_str(), parent, translation.data(), rotation.data()));
        ++expected_.Bones;
        return index;
    }

    int FbxScene::Material(const std::string &name, const std::string &color, const std::string &normal,
                           const std::string &emission)
    {
        for (const auto *text : {&name, &color, &normal, &emission})
            Text(*text);
        const auto index =
            Check(library_->Material(scene_, name.c_str(), color.c_str(), normal.c_str(), emission.c_str()));
        ++expected_.Materials;
        return index;
    }

    int FbxScene::Mesh(const std::string &name, std::span<const float> positions, std::span<const float> normals,
                       std::span<const float> tangents, std::span<const float> binormals)
    {
        Text(name);
        if (positions.empty() || positions.size() % 3 || positions.size() != normals.size() ||
            (!tangents.empty() && tangents.size() != positions.size()) ||
            (!binormals.empty() && binormals.size() != positions.size()) || tangents.empty() != binormals.empty())
            throw std::invalid_argument("Invalid FBX mesh attributes.");
        for (auto values : {positions, normals, tangents, binormals})
            Finite(values);
        const auto count = Count(positions.size() / 3);
        const auto index = Check(library_->Mesh(scene_, name.c_str(), count, positions.data(), normals.data(),
                                                tangents.empty() ? nullptr : tangents.data(),
                                                binormals.empty() ? nullptr : binormals.data()));
        if (index != static_cast<int>(vertices_.size()))
            throw std::runtime_error("FBX bridge returned a nonsequential mesh index.");
        vertices_.push_back(count);
        ++expected_.Meshes;
        expected_.Vertices += count;
        return index;
    }

    void FbxScene::Uv(int mesh, int channel, std::span<const float> uv)
    {
        CheckSpan(mesh, uv, 2);
        if (channel < 0 || channel > 7)
            throw std::invalid_argument("Invalid UV channel.");
        Check(library_->Uv(scene_, mesh, channel, uv.data()));
    }

    void FbxScene::Colors(int mesh, std::span<const float> rgba)
    {
        CheckSpan(mesh, rgba, 4);
        Check(library_->Colors(scene_, mesh, rgba.data()));
    }

    void FbxScene::Triangles(int mesh, int material, std::span<const int> indices, bool mirrored)
    {
        if (mesh < 0 || static_cast<size_t>(mesh) >= vertices_.size() || material < 0 ||
            material >= expected_.Materials || indices.size() % 3)
            throw std::invalid_argument("Invalid FBX triangle range.");
        for (int index : indices)
            if (index < 0 || index >= vertices_[mesh])
                throw std::invalid_argument("Invalid FBX triangle index.");
        if (indices.empty())
            return;
        Check(library_->Triangles(scene_, mesh, material, Count(indices.size()), indices.data(), mirrored ? 1 : 0));
        expected_.Polygons += Count(indices.size() / 3);
    }

    void FbxScene::Skin(int mesh, int bone, std::span<const int> indices, std::span<const float> weights)
    {
        if (mesh < 0 || static_cast<size_t>(mesh) >= vertices_.size() || bone < 0 || bone >= expected_.Bones ||
            indices.size() != weights.size())
            throw std::invalid_argument("Invalid FBX skin range.");
        Finite(weights);
        for (size_t i = 0; i < indices.size(); ++i)
            if (indices[i] < 0 || indices[i] >= vertices_[mesh] || weights[i] < 0)
                throw std::invalid_argument("Invalid FBX skin influence.");
        if (indices.empty())
            return;
        Check(library_->Skin(scene_, mesh, bone, Count(indices.size()), indices.data(), weights.data()));
        ++expected_.Clusters;
    }

    void FbxScene::Shape(int mesh, const std::string &name, float weight, std::span<const float> positions,
                         std::span<const float> normals, std::span<const float> tangents)
    {
        Text(name);
        CheckSpan(mesh, positions, 3);
        CheckSpan(mesh, normals, 3);
        if (!tangents.empty())
            CheckSpan(mesh, tangents, 3);
        if (!std::isfinite(weight) || weight <= 0)
            throw std::invalid_argument("Invalid blend-shape weight.");
        Check(library_->Shape(scene_, mesh, name.c_str(), weight, positions.data(), normals.data(),
                              tangents.empty() ? nullptr : tangents.data()));
        ++expected_.ShapeFrames;
        if (library_->Shapes.emplace(mesh, name).second)
            ++expected_.Channels;
    }

    NativeFbxSummary FbxScene::Reopen(const std::filesystem::path &output) const
    {
        NativeFbxSummary result;
        auto path = Utf8(std::filesystem::absolute(output));
        Text(path);
        Check(library_->Reopen(path.c_str(), &result));
        return result;
    }

    NativeFbxSummary FbxScene::WriteAndVerify(const std::filesystem::path &output)
    {
        if (std::filesystem::exists(output))
            throw std::runtime_error("FBX output already exists; refusing to overwrite.");
        auto path = Utf8(std::filesystem::absolute(output));
        Text(path);
        Check(library_->Write(scene_, path.c_str()));
        auto result = Reopen(output);
        if (result.Meshes != expected_.Meshes || result.Vertices != expected_.Vertices ||
            result.Polygons != expected_.Polygons || result.Bones != expected_.Bones ||
            result.Clusters != expected_.Clusters || result.Channels != expected_.Channels ||
            result.ShapeFrames != expected_.ShapeFrames || result.Materials != expected_.Materials)
            throw std::runtime_error("FBX reopen changed geometry, skin, shape or material counts.");
        return result;
    }
}
