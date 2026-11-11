#pragma once
#include "Scene/SceneTypes.h"

namespace hs2
{
    class NativeIdleLimbs
    {
    public:
        NativeIdleLimbs(const Parameter &component, const std::unordered_map<std::string, NativeSceneNode *> &bones,
                        const std::unordered_map<std::string, NativeSceneNode *> &rest,
                        std::vector<std::string> &diagnostics);
        ~NativeIdleLimbs();
        void RestoreInputPose();
        void Step();

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };

    class NativeEyeLook
    {
    public:
        NativeEyeLook(const Parameter &data, const std::unordered_map<std::string, NativeSceneNode *> &nodes,
                      const std::unordered_map<std::string, NativeSceneNode *> &source);
        ~NativeEyeLook();
        void Step(float delta);
        float HorizontalRate() const;
        float VerticalRate() const;

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };
}
