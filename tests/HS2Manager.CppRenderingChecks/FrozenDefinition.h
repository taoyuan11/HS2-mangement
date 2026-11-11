#pragma once
#include "../HS2Manager.NativeScene/CharacterDefinition.h"
#include "../../src/HS2Manager.NativeCore/third_party/json.hpp"

// This reader is intentionally test-only: an independent pre-migration fixture
// provides identical inputs to both renderers without using card preparation.
namespace fixture {
using Json=nlohmann::json;
hs2::Parameter Parameter(const Json& value);
hs2::Vector3 Vector3(const Json& value,hs2::Vector3 fallback={});
hs2::Quaternion Quaternion(const Json& value);
std::shared_ptr<hs2::CharacterDefinition> Definition(const std::filesystem::path& file);
void Pose(hs2::NativeCharacterScene& scene,const std::filesystem::path& file);
}
