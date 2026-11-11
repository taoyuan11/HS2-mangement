#pragma once
#include "Character/CharacterDefinition.h"
#include "../../src/HS2Manager.NativeCore/third_party/json.hpp"

// 此读取器仅用于测试：使用迁移前独立冻结的夹具为两种渲染器提供相同输入，不依赖人物卡准备流程。
namespace fixture
{
    using Json = nlohmann::json;
    hs2::Parameter Parameter(const Json &value);
    hs2::Vector3 Vector3(const Json &value, hs2::Vector3 fallback = {});
    hs2::Quaternion Quaternion(const Json &value);
    std::shared_ptr<hs2::CharacterDefinition> Definition(const std::filesystem::path &file);
    void Pose(hs2::NativeCharacterScene &scene, const std::filesystem::path &file);
}
