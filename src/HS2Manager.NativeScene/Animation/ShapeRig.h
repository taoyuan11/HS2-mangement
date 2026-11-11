#pragma once
#include "Scene/SceneTypes.h"
#include <array>
#include <stop_token>

namespace hs2
{
    struct BonePose
    {
        Vector3 Position{}, Scale{1, 1, 1};
        Quaternion Rotation{0, 0, 0, 1};
    };

    // 读取已安装游戏的 ECMA-335 元数据及受限数值公式指令集，不加载 DLL、CLR 或游戏组件。
    class NativeShapeRig
    {
    public:
        explicit NativeShapeRig(const std::filesystem::path &game, bool unlockedSliders = false);
        void Apply(std::unordered_map<std::string, BonePose> &bones, std::string_view recipe,
                   std::span<const std::uint8_t> animation, std::string_view categories, std::span<const float> values,
                   std::stop_token cancellation = {}) const;
        using Keys = std::unordered_map<std::string, std::vector<std::array<float, 9>>>;
        static Keys ReadKeys(std::span<const std::uint8_t> bytes);
        static float Interpolate(std::span<const std::array<float, 9>> frames, std::string_view name, float value,
                                 int component, bool unlocked);

    private:
        struct Metadata;
        std::shared_ptr<const Metadata> metadata_;
        bool unlocked_;
    };
}
