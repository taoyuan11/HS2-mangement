#pragma once
#include "Application/NativeWindow.h"
#include "Creation/CreationViewport.h"
#include "CreationService.h"
#include <winrt/Microsoft.UI.Xaml.Shapes.h>

namespace hs2::ui
{
    class NativeCreation : public std::enable_shared_from_this<NativeCreation>
    {
    public:
        static std::shared_ptr<NativeCreation> Open(std::shared_ptr<NativeWindow> owner,
                                                    std::shared_ptr<creation::CreationProject> project,
                                                    std::shared_ptr<creation::CreationService> service);
        winrt::Windows::Foundation::IAsyncAction Stop();
        void Cancel();
        void Refresh(bool lists = true);
        void Saved(std::string error = {});

        void CreateAssetAt(std::string target)
        {
            preferredTarget_ = std::move(target);
            AddAsset();
        }

    private:
        void Initialize();
        void Menus();
        void BindPointers();
        void Viewport();
        void PaintView();
        void SelectAt(Vector2 point, bool extend);
        void BrushAt(Vector2 point, bool uv);
        bool ObjectSelected(const creation::CreationObject &object) const;
        Vector3 SelectionCenter() const;
        void Command(std::string op, Json args = Json::object());
        winrt::fire_and_forget Action(std::string action);
        winrt::fire_and_forget AddAsset();
        winrt::fire_and_forget ChangeTemplate();
        winrt::fire_and_forget Primitive(std::string type);
        winrt::fire_and_forget BoneAction(std::string action);
        winrt::fire_and_forget TextureAction(std::string action);
        winrt::fire_and_forget AutoSave();
        winrt::fire_and_forget Job(std::string label, std::function<std::function<void()>(std::stop_token)> work);
        winrt::Windows::Foundation::IAsyncOperation<winrt::hstring> PickFile(std::vector<winrt::hstring> extensions);
        winrt::Windows::Foundation::IAsyncOperation<bool> Dialog(winrt::hstring title, xaml::UIElement content);
        creation::CreationAsset &Asset(creation::CreationState &state) const;
        creation::CreationObject &ObjectOf(creation::CreationState &state) const;
        creation::CreationTexture &Texture(creation::CreationState &state) const;
        const creation::CreationObject *SelectedObject() const;
        const creation::CreationTexture *SelectedTexture() const;
        void Message(std::string text);

        template <class T> T Get(const wchar_t *name) const
        {
            return root_.FindName(name).as<T>();
        }

        std::weak_ptr<NativeWindow> owner_;
        std::shared_ptr<creation::CreationProject> project_;
        std::shared_ptr<creation::CreationService> service_;
        std::shared_ptr<const asset::Geometry> reference_;
        creation::FitPreview fit_;
        std::unique_ptr<CreationViewport> viewport_;
        xaml::FrameworkElement root_{nullptr};
        controls::SwapChainPanel panel_{nullptr};
        winrt::Microsoft::UI::Dispatching::DispatcherQueueTimer autosave_{nullptr};
        ComPtr<IDXGISwapChain2> chain_;
        std::string asset_, object_, texture_, lastInstall_;
        std::string preferredTarget_;
        std::vector<std::string> assetIds_, objectIds_, textureIds_;
        creation::MeshSelection selection_;
        std::set<std::string> objectSelection_;
        std::map<std::string, creation::MeshDocument> dragObjects_;
        Vector3 dragPivot_{};
        CreationCamera camera_;
        bool refreshing_ = false, stopped_ = false, working_ = false, pointer_ = false, orbit_ = false, pan_ = false,
             uvPointer_ = false;
        bool savingRecovery_ = false;
        Vector2 last_{}, start_{};
        std::optional<creation::MeshDocument> dragMesh_;
        std::optional<creation::PaintDocument> displayedPaint_;
        std::string displayedUvKey_;
        std::shared_ptr<const asset::Geometry> displayedUvReference_;
        std::shared_ptr<const asset::Geometry> pickedReference_;
        std::vector<creation::MeshDocument> referenceMeshes_;
        std::stop_source cancellation_;
        std::vector<Vector2> stroke_;
        int layer_ = 0, dragAxis_ = 0;
        Vector4 color_{.7f, .25f, .15f, 1};
    };
}
