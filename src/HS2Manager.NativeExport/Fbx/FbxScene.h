#pragma once
#include <array>
#include <filesystem>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace hs2
{
    struct NativeFbxSummary
    {
        int Nodes = 0, Meshes = 0, Vertices = 0, Polygons = 0, Bones = 0, Clusters = 0, Channels = 0, ShapeFrames = 0,
            Materials = 0;
    };

    // 以 RAII 管理独立编译的 Autodesk FBX C ABI；
    // 仅接受动态库的绝对路径，避免当前目录或 PATH 改变加载目标。
    class FbxScene final
    {
    public:
        explicit FbxScene(const std::filesystem::path &bridgeLibrary);
        ~FbxScene();
        FbxScene(const FbxScene &) = delete;
        FbxScene &operator=(const FbxScene &) = delete;
        int Bone(const std::string &name, int parent, const std::array<float, 3> &translation,
                 const std::array<float, 4> &rotation);
        int Material(const std::string &name, const std::string &color, const std::string &normal,
                     const std::string &emission);
        int Mesh(const std::string &name, std::span<const float> positions, std::span<const float> normals,
                 std::span<const float> tangents = {}, std::span<const float> binormals = {});
        void Uv(int mesh, int channel, std::span<const float> uv);
        void Colors(int mesh, std::span<const float> rgba);
        void Triangles(int mesh, int material, std::span<const int> indices, bool mirrored);
        void Skin(int mesh, int bone, std::span<const int> indices, std::span<const float> weights);
        void Shape(int mesh, const std::string &name, float weight, std::span<const float> positions,
                   std::span<const float> normals, std::span<const float> tangents = {});
        NativeFbxSummary WriteAndVerify(const std::filesystem::path &output);
        NativeFbxSummary Reopen(const std::filesystem::path &output) const;

    private:
        struct Library;
        std::unique_ptr<Library> library_;
        void *scene_ = nullptr;
        std::vector<int> vertices_;
        NativeFbxSummary expected_;
        int Check(int result) const;
        void CheckSpan(int mesh, std::span<const float> values, int components) const;
    };
}
