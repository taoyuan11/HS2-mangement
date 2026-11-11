#pragma once
#include "Scene/SceneTypes.h"
#include <array>

namespace hs2
{
    struct NativeAnimationBinding
    {
        std::uint32_t PathHash = 0, Attribute = 0;
        int ClassId = 0, CustomType = 0, Offset = 0, Components = 0;
    };

    class NativeAnimationClip
    {
    public:
        explicit NativeAnimationClip(const Parameter &data);
        std::string Name;
        float Start = 0, Duration = 0;
        int CurveCount = 0;
        std::vector<NativeAnimationBinding> Bindings;
        std::vector<float> Evaluate(float time, bool loop = true) const;
        static std::uint32_t PathHash(std::string_view path);

    private:
        struct StreamKey
        {
            float Time, A, B, C, D;
        };

        std::vector<std::vector<StreamKey>> streamed_;
        std::vector<float> dense_, constants_;
        int denseCount_ = 0, denseFrames_ = 0;
        float denseRate_ = 0, denseStart_ = 0;
    };

    class NativeAnimationPlayer
    {
    public:
        NativeAnimationPlayer(std::shared_ptr<const NativeAnimationClip> clip, NativeSceneNode &root,
                              std::span<NativeSceneNode *const> bindingNodes = {},
                              std::span<NativeSceneRenderer *const> renderers = {});
        void Sample(float time, bool loop = true);
        std::vector<std::uint32_t> UnboundPaths;
        std::unordered_map<std::uint32_t, float> RootMotion;

        float Duration() const
        {
            return clip_->Duration;
        }

    private:
        std::shared_ptr<const NativeAnimationClip> clip_;

        struct Track
        {
            NativeAnimationBinding Binding;
            NativeSceneNode *Node;
        };

        struct Morph
        {
            int Offset;
            NativeSceneRenderer *Renderer;
            size_t Shape;
        };

        std::vector<Track> tracks_;
        std::vector<Morph> morphs_;
    };
    enum class CurveWrap
    {
        Clamp,
        Loop,
        PingPong
    };

    struct CurveKey
    {
        float Time = 0, Value = 0, InSlope = 0, OutSlope = 0, InWeight = 1.f / 3, OutWeight = 1.f / 3;
        int Weighted = 0;
    };

    class AnimationCurve
    {
    public:
        explicit AnimationCurve(std::vector<CurveKey> keys);
        CurveWrap Pre = CurveWrap::Clamp, Post = CurveWrap::Clamp;
        float Evaluate(float time) const;

    private:
        std::vector<CurveKey> keys_;
    };

    class IndependentBlink
    {
    public:
        explicit IndependentBlink(int seed);
        float Evaluate(float time);

    private:
        std::uint32_t Next();
        float Duration();
        std::array<std::uint32_t, 4> state_{};
        int phase_ = 0, closedFrames_ = 0;
        float deadline_ = 0, duration_ = 0, open_ = 1;
    };

    float FixedTime(int frame);
    Quaternion EulerRotation(Vector3 degrees);
}
