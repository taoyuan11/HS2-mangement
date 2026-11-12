#pragma once
#include "CreationProject.h"
#include "Device/GpuResources.h"
#include <mutex>
#include <condition_variable>

namespace hs2::ui
{
    struct CreationCamera
    {
        Vector3 Center{0, 1, 0};
        float Yaw = 3.14159265f, Pitch = .1f, Scale = 10;
        Vector3 Right() const;
        Vector3 Up() const;
        Vector3 Forward() const;
        Vector2 Project(Vector3 point, float width, float height) const;
        std::pair<Vector3, Vector3> Ray(Vector2 point, float width, float height) const;
    };

    struct CreationView
    {
        std::shared_ptr<const creation::CreationState> State;
        std::shared_ptr<const asset::Geometry> Reference;
        std::string Object, Bone, Texture;
        std::set<std::string> Objects;
        creation::MeshSelection Selection;
        CreationCamera Camera;
        Vector3 Pivot{};
        bool Checker = false, Weights = false, ShowReference = true, HalfState = false;
        int Clothing = 0;
    };

    class CreationViewport
    {
    public:
        explicit CreationViewport(std::function<void(ComPtr<IDXGISwapChain2>)> ready,
                                  std::function<void(std::string)> error);
        ~CreationViewport();
        void Update(CreationView view, PreviewViewportSize size);
        void Visible(bool value);
        void Stop();

    private:
        void Run(std::stop_token token);
        std::mutex mutex_;
        std::condition_variable_any wake_;
        CreationView view_;
        PreviewViewportSize size_{640, 640, 1};
        bool dirty_ = false, visible_ = true;
        std::function<void(ComPtr<IDXGISwapChain2>)> ready_;
        std::function<void(std::string)> error_;
        std::jthread worker_;
    };
}
