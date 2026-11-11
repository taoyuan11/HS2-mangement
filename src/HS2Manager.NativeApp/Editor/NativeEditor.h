#pragma once
#include "Character/CharacterCatalog.h"
#include "Application/NativeWindow.h"

namespace hs2::ui
{
    struct EditorCatalogState;

    class NativeEditor : public std::enable_shared_from_this<NativeEditor>
    {
    public:
        static std::shared_ptr<NativeEditor> Open(std::shared_ptr<NativeWindow> const &owner, CardRecord card,
                                                  CharacterDocument document);

        bool IsDirty() const
        {
            return session_.IsDirty();
        }

        bool Saving() const
        {
            return saving_;
        }

        winrt::Windows::Foundation::IAsyncOperation<bool> Save(bool overwrite = false, bool chooseFolder = false);
        void Undo();
        void Redo();

        void FocusSearch()
        {
            Get<controls::TextBox>(L"FieldSearch").Focus(xaml::FocusState::Programmatic);
        }

        void Stop();

        void CancelPending()
        {
            catalogCancellation_.request_stop();
        }

        void Definition(std::shared_ptr<const CharacterDefinition> definition);

    private:
        NativeEditor(std::shared_ptr<NativeWindow> const &owner, CardRecord card, CharacterDocument document);
        void Initialize();
        void RefreshFields();
        void BuildParameters();
        void UpdateState();
        void QueuePreview();
        void UpdatePreview();
        winrt::Microsoft::UI::Xaml::FrameworkElement FieldControl(CharacterField field);
        winrt::fire_and_forget Action(std::wstring action, hs2::ui::Object sender);
        winrt::fire_and_forget ChooseColor(CharacterField field);
        winrt::fire_and_forget ChooseAsset(CharacterField field);
        winrt::fire_and_forget ChangeChoice(CharacterField field, MessagePack::Value value);
        winrt::fire_and_forget RestoreBackup();
        winrt::fire_and_forget SwitchCard();
        winrt::fire_and_forget AssetThumbnail(winrt::com_ptr<Observable> row, NativeAssetEntry entry,
                                              std::stop_token token);
        std::string Category() const;

        template <typename T> T Get(wchar_t const *name) const
        {
            return root_.FindName(name).as<T>();
        }

        std::weak_ptr<NativeWindow> owner_;
        CharacterEditSession session_;
        CharacterSliderLimits limits_;
        std::vector<CharacterField> fields_;
        winrt::Microsoft::UI::Xaml::FrameworkElement root_{nullptr};
        bool building_ = false, saving_ = false;
        winrt::Microsoft::UI::Dispatching::DispatcherQueueTimer previewTimer_{nullptr};
        std::shared_ptr<EditorCatalogState> catalog_;
        std::stop_source catalogCancellation_;
        bool stopped_ = false;
        std::shared_ptr<const CharacterDefinition> definition_;
    };
} // 命名空间 hs2::ui
