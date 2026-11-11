#pragma once
#include "SceneTypes.h"
#include <array>

namespace hs2 {
struct PostProfile {float Weight=1,Priority=0;std::vector<Parameter> Effects;};
struct SceneLight {std::string Name;bool CameraRelative=false;std::array<float,4> Rotation{0,0,0,1};Parameter Parameters;};
struct EnvironmentDefinition {
    Parameter RenderSettings,Quality,Camera,Physics;
    std::vector<SceneLight> Lights;
    std::vector<PostProfile> Profiles;
    std::array<float,4> Background{.5f,.5f,.5f,1};
    bool DhhEnabled=false;
    std::array<float,50> DhhFloats{};
    std::array<bool,15> DhhBools{};
    std::array<float,12> DhhColors{};
    int SkyboxId=6;
    float Brightness=0;
    std::string LutPath;
    bool BetterAA=false;
    std::array<float,6> Temporal{3,.001f,.32f,.25f,3,.5f};
};
}
