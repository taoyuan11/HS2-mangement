#include "Editor/NativeEditor.h"
#include "Preview/NativePreview.h"
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    fire_and_forget NativeEditor::SwitchCard()
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || owner->dialogOpen_ || saving_)
            co_return;
        try
        {
            auto cards = owner->repository_->All();
            std::erase_if(cards,
                          [](auto const &card)
                          {
                              return card.Status != CardStatus::Valid ||
                                     card.Availability != FileAvailability::Available;
                          });
            ListView list;
            list.Width(440);
            list.Height(360);
            for (auto const &card : cards)
                list.Items().Append(box_value(to_hstring(card.Name + " · " + card.FileName())));
            ContentDialog dialog;
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.RequestedTheme());
            dialog.Title(box_value(L"切换人物卡"));
            dialog.Content(list);
            dialog.PrimaryButtonText(L"编辑");
            dialog.CloseButtonText(L"取消");
            owner->dialogOpen_ = true;
            auto result = co_await dialog.ShowAsync();
            owner->dialogOpen_ = false;
            if (result != ContentDialogResult::Primary || list.SelectedIndex() < 0)
                co_return;
            auto card = cards[list.SelectedIndex()];
            if (co_await owner->CloseEditor())
                owner->OpenEditor(card);
        }
        catch (hresult_error const &e)
        {
            owner->dialogOpen_ = false;
            Get<TextBlock>(L"EditorMessage").Text(e.message());
        }
        catch (std::exception const &e)
        {
            owner->dialogOpen_ = false;
            Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
        }
    }

    fire_and_forget NativeEditor::RestoreBackup()
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || owner->dialogOpen_ || saving_)
            co_return;
        try
        {
            auto backups = owner->files_->Operations();
            std::erase_if(backups,
                          [&](auto const &operation)
                          {
                              return operation.BackupPath.empty() || !operation.Card ||
                                     operation.Card->Id != session_.Card.Id || operation.Phase != "Indexed";
                          });
            if (backups.empty())
            {
                Get<TextBlock>(L"EditorMessage").Text(L"当前人物卡没有可恢复的编辑备份。");
                co_return;
            }
            ListView list;
            list.Width(460);
            list.Height(280);
            for (auto const &operation : backups)
                list.Items().Append(
                    box_value(to_hstring(operation.CreatedUtc + " · " + Utf8(Path(operation.Source).filename()))));
            list.SelectedIndex(0);
            ContentDialog dialog;
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.RequestedTheme());
            dialog.Title(box_value(L"恢复编辑备份"));
            dialog.Content(list);
            dialog.PrimaryButtonText(L"恢复所选备份");
            dialog.CloseButtonText(L"取消");
            owner->dialogOpen_ = true;
            auto result = co_await dialog.ShowAsync();
            owner->dialogOpen_ = false;
            if (result != ContentDialogResult::Primary || list.SelectedIndex() < 0)
                co_return;
            auto backup = backups[list.SelectedIndex()];
            if (session_.IsDirty())
            {
                ContentDialog leave;
                leave.XamlRoot(root_.XamlRoot());
                leave.Title(box_value(L"保留当前未保存修改？"));
                leave.Content(
                    box_value(L"保存为新卡后恢复所选原卡备份，或放弃草稿并恢复。恢复操作会先备份当前磁盘文件。"));
                leave.PrimaryButtonText(L"另存新卡后恢复");
                leave.SecondaryButtonText(L"放弃草稿并恢复");
                leave.CloseButtonText(L"取消");
                owner->dialogOpen_ = true;
                auto choice = co_await leave.ShowAsync();
                owner->dialogOpen_ = false;
                if (choice == ContentDialogResult::None)
                    co_return;
                if (choice == ContentDialogResult::Primary && !(co_await Save()))
                    co_return;
            }
            if (stopped_ || owner->closing_)
                co_return;
            saving_ = true;
            owner->SetBusy(true);
            UpdateState();
            apartment_context ui;
            std::optional<CardEditSaveResult> restored;
            std::string error;
            co_await resume_background();
            try
            {
                restored = CardEditService(*owner->repository_).RestoreBackup(backup);
            }
            catch (std::exception const &e)
            {
                error = e.what();
            }
            co_await ui;
            saving_ = false;
            owner->SetBusy(false);
            if (restored)
            {
                session_ = CharacterEditSession(restored->Card, restored->Document);
                if (owner->preview_)
                    owner->preview_->SavedCard(session_.Card);
                std::weak_ptr<NativeEditor> weak = shared_from_this();
                session_.Changed = [weak]
                {
                    if (auto self = weak.lock())
                    {
                        self->UpdateState();
                        self->QueuePreview();
                    }
                };
                Get<TextBlock>(L"EditorTitle").Text(to_hstring("编辑角色 · " + session_.Card.Name));
                RefreshFields();
                QueuePreview();
                owner->Reload();
                Get<TextBlock>(L"EditorMessage").Text(L"已恢复备份；恢复前的人物卡也已备份。");
            }
            else
                Get<TextBlock>(L"EditorMessage").Text(to_hstring("恢复失败：" + error));
            UpdateState();
            if (owner->closing_)
            {
                owner->closing_ = false;
                owner->RequestClose();
            }
        }
        catch (hresult_error const &e)
        {
            owner->dialogOpen_ = false;
            saving_ = false;
            owner->SetBusy(false);
            Get<TextBlock>(L"EditorMessage").Text(e.message());
            UpdateState();
        }
        catch (std::exception const &e)
        {
            owner->dialogOpen_ = false;
            saving_ = false;
            owner->SetBusy(false);
            Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
            UpdateState();
        }
    }
} // 命名空间 hs2::ui
