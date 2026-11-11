#include "Editor/NativeEditor.h"
#include "Preview/NativePreview.h"
#include "Application/NativeWindow.h"
#include "Infrastructure/UiEvents.h"
#include <ShlObj.h>
#include <Shobjidl.h>
#include <fstream>
#include <shellapi.h>

using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    namespace
    {
        std::string Trim(std::string text)
        {
            auto first = text.find_first_not_of(" \t\r\n");
            if (first == text.npos)
                return {};
            return text.substr(first, text.find_last_not_of(" \t\r\n") - first + 1);
        }

        std::vector<std::string> ParseTags(std::string text)
        {
            std::vector<std::string> result;
            size_t offset = 0;
            while (offset < text.size())
            {
                auto end = text.find_first_of(",;\r\n", offset);
                auto part = text.substr(offset, end == text.npos ? text.npos : end - offset);
                auto a = part.find_first_not_of(" \t"), b = part.find_last_not_of(" \t");
                if (a != part.npos)
                {
                    part = part.substr(a, b - a + 1);
                    if (std::find(result.begin(), result.end(), part) == result.end())
                        result.push_back(part);
                }
                if (end == text.npos)
                    break;
                offset = end + 1;
            }
            return result;
        }

        std::string Results(std::vector<FileOperationResult> const &results)
        {
            size_t success = 0;
            std::string errors;
            for (auto const &item : results)
            {
                if (item.Success)
                    ++success;
                else
                    errors += '\n' + item.Source + ": " + item.Message;
            }
            return "完成 " + std::to_string(success) + " 项，失败 " + std::to_string(results.size() - success) + " 项" +
                   errors;
        }

        void Reveal(fs::path const &path)
        {
            PIDLIST_ABSOLUTE item{};
            check_hresult(SHParseDisplayName(path.c_str(), nullptr, &item, 0, nullptr));
            auto result = SHOpenFolderAndSelectItems(item, 0, nullptr, 0);
            CoTaskMemFree(item);
            check_hresult(result);
        }
    } // 命名空间

    fire_and_forget NativeWindow::Action(std::wstring name, Object sender)
    {
        auto lifetime = shared_from_this();
        try
        {
            if (closing_ || closePrompt_)
                co_return;
            if (preview_ && preview_->Action(name, sender))
                co_return;
            if (name == L"TemporaryPreview_Click")
            {
                OpenTemporaryPreview();
                co_return;
            }
            if (model_->Bool(L"IsTemporaryPreview"))
            {
                // 临时模式不让快捷键或隐藏菜单落到资料库写入操作。
                bool navigation =
                    name == L"Navigate_Click" || name == L"Collection_SelectionChanged" || name == L"Settings_Click";
                bool allowed = name == L"ClosePreview_Click" || name == L"Cancel_Click" ||
                               name == L"ToggleRail_Click" || name == L"PreviewRail_Selected" ||
                               (name == L"SaveDetails_Click" && preview_) || navigation;
                if (!allowed || (navigation && busy_))
                    co_return;
            }
            if (name == L"CloseEditor_Click")
            {
                co_await CloseEditor();
                co_return;
            }
            if (name == L"ClosePreview_Click")
            {
                ClosePreview();
                co_return;
            }
            if (name == L"ToggleRail_Click")
            {
                model_->Put(L"ShowPreviewRail", !model_->Bool(L"ShowPreviewRail"));
                if (preview_ && model_->Bool(L"ShowPreviewRail"))
                    preview_->SyncRail(true);
                co_return;
            }
            if (name == L"PreviewRail_Selected")
            {
                if (updating_ || editor_ || !preview_ || !model_->Bool(L"IsPreview"))
                    co_return;
                auto item = Control<ListView>(L"PreviewRail").SelectedItem();
                if (item)
                {
                    auto id = to_string(get_self<Observable>(item.as<data::ICustomPropertyProvider>())->String(L"Id"));
                    if (temporaryPreview_)
                    {
                        LoadTemporaryCard(id);
                        co_return;
                    }
                    auto found = std::find_if(cards_.begin(), cards_.end(),
                                              [&](auto const &c)
                                              {
                                                  return c.Id == id;
                                              });
                    if (found != cards_.end() && (!preview_ || preview_->Card().Id != found->Id))
                    {
                        if (!preview_)
                            preview_ = NativePreview::Create(lifetime);
                        preview_->Load(*found, root_, false);
                    }
                }
                co_return;
            }
            if (name == L"Cancel_Click")
            {
                work_.request_stop();
                CancelTemporaryPreview();
                if (editor_)
                    editor_->CancelPending();
                co_return;
            }
            if (name == L"SaveDetails_Click" && editor_)
            {
                co_await editor_->Save();
                co_return;
            }
            if (name == L"SaveDetails_Click" && preview_)
            {
                preview_->Action(L"Screenshot_Click", nullptr);
                co_return;
            }
            if (name == L"Cards_SelectionChanged")
            {
                Select();
                co_return;
            }
            if (name == L"Navigate_Click")
            {
                if (auto button = sender.try_as<Button>())
                    Navigate(to_string(unbox_value<hstring>(button.Tag())));
                co_return;
            }
            if (name == L"Collection_SelectionChanged")
            {
                if (updating_)
                    co_return;
                auto item = Control<ListView>(L"CollectionList").SelectedItem();
                if (item)
                {
                    ClosePreview();
                    collection_ = to_string(unbox_value<hstring>(item));
                    folder_.clear();
                    mode_ = "all";
                    model_->Put(L"CurrentNavigation", L"");
                    model_->Put(L"IsTrash", false);
                    updating_ = true;
                    Control<TreeView>(L"FolderTree").SelectedNode(nullptr);
                    updating_ = false;
                    Filter();
                }
                co_return;
            }
            if (name == L"Settings_Click")
            {
                if (!busy_)
                {
                    ClosePreview();
                    Settings();
                }
                co_return;
            }
            if (name == L"Refresh_Click")
            {
                Refresh();
                co_return;
            }
            if (name == L"Mods_Click")
            {
                Work("更新 MOD 索引",
                     [this](auto stop, auto progress)
                     {
                         ModDependencyService service(*repository_);
                         auto index = service.Scan(Path(settings_.GamePath), progress, stop);
                         return "MOD 索引：" + std::to_string(index.Files.size()) + " 个资源包，" +
                                std::to_string(index.PluginFiles.size()) + " 个插件文件" +
                                (index.Complete ? "" : "；部分清单无法读取，缺失状态保守标为待确认");
                     });
                co_return;
            }
            if (name == L"Logs_Click")
            {
                auto path = dataDirectory_ / L"Logs";
                fs::create_directories(path);
                ShellExecuteW(Handle(), L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
                co_return;
            }
            if (name == L"Operations_Click")
            {
                auto records = files_->Operations();
                std::string message;
                for (auto const &record : records)
                    message += record.Kind + " · " + record.Phase + "\n" + record.Source + "\n" + record.Error + "\n\n";
                if (message.empty())
                    message = "暂无文件操作记录。";
                if (co_await Confirm(L"操作日志", to_hstring(message + "\n是否检查并恢复未完成操作？")))
                    Work("恢复未完成操作",
                         [this](auto stop, auto)
                         {
                             auto records = files_->Recover(stop);
                             std::string result;
                             for (auto const &record : records)
                                 result += record + '\n';
                             return result.empty() ? "没有需要恢复的操作" : result;
                         });
                co_return;
            }
            if (busy_ || closing_)
                co_return;
            if (name == L"Duplicates_Click")
            {
                ClosePreview();
                Work("校验重复文件",
                     [this](auto stop, auto progress)
                     {
                         library_->ComputeHashes(progress, stop);
                         return "重复文件校验完成";
                     });
                mode_ = "duplicates";
                model_->Text(L"CurrentNavigation", mode_);
                folder_.clear();
                collection_.clear();
                model_->Put(L"IsTrash", false);
                updating_ = true;
                Control<ListView>(L"CollectionList").SelectedItem(nullptr);
                Control<TreeView>(L"FolderTree").SelectedNode(nullptr);
                updating_ = false;
                co_return;
            }
            if (name == L"MultiSelect_Click")
            {
                bool enabled = !model_->Bool(L"IsMultiSelectMode");
                model_->Put(L"IsMultiSelectMode", enabled);
                model_->Put(L"MultiSelectLabel", enabled ? L"退出多选" : L"多选");
                auto list = Control<GridView>(L"CardList");
                std::vector<Object> selected;
                for (auto const &item : list.SelectedItems())
                    selected.push_back(item);
                list.SelectionMode(enabled ? ListViewSelectionMode::Multiple : ListViewSelectionMode::Extended);
                for (auto const &item : selected)
                {
                    uint32_t index{};
                    if (!list.SelectedItems().IndexOf(item, index))
                        list.SelectedItems().Append(item);
                }
                co_return;
            }
            if (name == L"SelectAllCards_Click")
            {
                Control<GridView>(L"CardList").SelectAll();
                co_return;
            }
            if (name == L"ClearCardSelection_Click")
            {
                Control<GridView>(L"CardList").SelectedItems().Clear();
                co_return;
            }
            if (name == L"Import_Click")
            {
                winrt::Windows::Storage::Pickers::FileOpenPicker picker;
                picker.ViewMode(winrt::Windows::Storage::Pickers::PickerViewMode::Thumbnail);
                picker.FileTypeFilter().Append(L".png");
                check_hresult(picker.as<IInitializeWithWindow>()->Initialize(Handle()));
                auto files = co_await picker.PickMultipleFilesAsync();
                std::vector<fs::path> paths;
                for (auto const &file : files)
                    paths.emplace_back(file.Path().c_str());
                if (paths.empty())
                    co_return;
                Work("导入人物卡",
                     [this, paths](auto stop, auto progress)
                     {
                         return Results(files_->Import(paths, Path(settings_.GamePath), {}, progress, stop));
                     });
                co_return;
            }
            if (name == L"NewCollection_Click")
            {
                auto text = co_await Prompt(L"新建分类", L"虚拟分类不会移动原始人物卡文件。");
                if (text.empty())
                    co_return;
                auto value = Trim(to_string(text));
                if (value.empty())
                    co_return;
                if (std::find(settings_.Collections.begin(), settings_.Collections.end(), value) ==
                    settings_.Collections.end())
                    settings_.Collections.push_back(value);
                settings_.Save(dataDirectory_);
                Reload();
                co_return;
            }
            if (name == L"RenameCollection_Click" || name == L"RemoveCollection_Click")
            {
                auto item = Control<ListView>(L"CollectionList").SelectedItem();
                if (!item)
                    co_return;
                auto old = to_string(unbox_value<hstring>(item));
                std::string next;
                if (name == L"RenameCollection_Click")
                {
                    auto text = co_await Prompt(L"重命名分类", L"人物卡关联会一起更新。", to_hstring(old));
                    if (text.empty())
                        co_return;
                    next = Trim(to_string(text));
                    if (next.empty())
                        co_return;
                }
                else if (!(co_await Confirm(L"删除虚拟分类",
                                            to_hstring("删除分类“" + old + "”？人物卡文件、收藏和标签都会保留。"))))
                    co_return;
                std::vector<std::string> ids;
                for (auto const &card : cards_)
                    if (card.Collection == old)
                        ids.push_back(card.Id);
                repository_->UpdateMetadata(ids,
                                            [&](auto &card)
                                            {
                                                card.Collection = next;
                                            });
                std::erase(settings_.Collections, old);
                if (!next.empty() && std::find(settings_.Collections.begin(), settings_.Collections.end(), next) ==
                                         settings_.Collections.end())
                    settings_.Collections.push_back(next);
                settings_.Save(dataDirectory_);
                if (collection_ == old)
                    collection_ = next;
                Reload();
                co_return;
            }
            auto cards = Selected();
            if (preview_ && (name == L"EditCharacter_Click" || name == L"ExportModel_Click"))
                cards = {preview_->Card()};
            if (cards.empty())
                co_return;
            auto ids = SelectedIds();
            if (name == L"Preview_Click")
            {
                if (cards.front().Status == CardStatus::Valid &&
                    cards.front().Availability == FileAvailability::Available)
                {
                    if (!preview_)
                        preview_ = NativePreview::Create(lifetime);
                    preview_->Load(cards.front(), root_, false);
                }
                co_return;
            }
            if (name == L"EditCharacter_Click")
            {
                if (cards.front().Status == CardStatus::Valid &&
                    cards.front().Availability == FileAvailability::Available)
                    OpenEditor(cards.front());
                co_return;
            }
            if (name == L"Favorite_Click" || name == L"BatchFavorite_Click")
            {
                if (name == L"Favorite_Click")
                {
                    cards.resize(1);
                    ids.resize(1);
                }
                bool value = std::any_of(cards.begin(), cards.end(),
                                         [](auto const &card)
                                         {
                                             return !card.Favorite;
                                         });
                repository_->UpdateMetadata(ids,
                                            [&](auto &card)
                                            {
                                                card.Favorite = value;
                                            });
                Reload();
                co_return;
            }
            if (name == L"SaveDetails_Click")
            {
                auto tags = ParseTags(to_string(model_->String(L"EditTags")));
                auto notes = to_string(model_->String(L"EditNotes"));
                repository_->UpdateMetadata({cards.front().Id},
                                            [&](auto &card)
                                            {
                                                card.Tags = tags;
                                                card.Notes = notes;
                                            });
                Reload();
                Report("标签与备注已保存");
                co_return;
            }
            if (name == L"BatchTags_Click")
            {
                auto text = co_await Prompt(L"添加标签", L"用逗号分隔，已有标签会保留。");
                if (text.empty())
                    co_return;
                auto tags = ParseTags(to_string(text));
                repository_->UpdateMetadata(ids,
                                            [&](auto &card)
                                            {
                                                for (auto const &tag : tags)
                                                    if (std::find(card.Tags.begin(), card.Tags.end(), tag) ==
                                                        card.Tags.end())
                                                        card.Tags.push_back(tag);
                                            });
                Reload();
                co_return;
            }
            if (name == L"AssignCollection_Click")
            {
                MenuFlyout menu;
                std::weak_ptr<NativeWindow> weak = shared_from_this();
                auto add = [&](hstring label, std::string value)
                {
                    MenuFlyoutItem item;
                    item.Text(label);
                    item.Click(
                        [weak, ids, value](auto &&, auto &&)
                        {
                            if (auto self = weak.lock())
                                if (!self->busy_ && !self->closing_)
                                    try
                                    {
                                        self->repository_->UpdateMetadata(ids,
                                                                          [&](auto &card)
                                                                          {
                                                                              card.Collection = value;
                                                                          });
                                        self->Reload();
                                        self->Report(value.empty() ? "已移出分类" : "已加入分类：" + value);
                                    }
                                    catch (std::exception const &e)
                                    {
                                        self->Report(e.what());
                                    }
                        });
                    menu.Items().Append(item);
                };
                add(L"移出虚拟分类", {});
                std::set<std::string> collections(settings_.Collections.begin(), settings_.Collections.end());
                for (auto const &card : cards_)
                    if (!card.Collection.empty())
                        collections.insert(card.Collection);
                for (auto const &value : collections)
                    add(to_hstring(value), value);
                menu.Items().Append(MenuFlyoutSeparator{});
                MenuFlyoutItem create;
                create.Text(L"新建分类并加入…");
                create.Click(
                    [weak](auto &&, auto &&)
                    {
                        if (auto self = weak.lock())
                            self->Action(L"AssignNewCollection_Click", nullptr);
                    });
                menu.Items().Append(create);
                menu.ShowAt(sender.try_as<FrameworkElement>() ? sender.as<FrameworkElement>()
                                                              : Control<GridView>(L"CardList"));
                co_return;
            }
            if (name == L"AssignNewCollection_Click")
            {
                auto text = co_await Prompt(L"新建分类", L"虚拟分类不会移动原始文件。");
                if (text.empty())
                    co_return;
                auto value = Trim(to_string(text));
                if (value.empty())
                    co_return;
                repository_->UpdateMetadata(ids,
                                            [&](auto &card)
                                            {
                                                card.Collection = value;
                                            });
                if (!value.empty() && std::find(settings_.Collections.begin(), settings_.Collections.end(), value) ==
                                          settings_.Collections.end())
                    settings_.Collections.push_back(value);
                settings_.Save(dataDirectory_);
                Reload();
                co_return;
            }
            if (name == L"ExportModel_Click")
            {
                std::vector<ModelExportSource> sources;
                for (auto const &card : cards)
                    sources.push_back({card, {}});
                ExportModels(std::move(sources));
                co_return;
            }
            if (name == L"Export_Click")
            {
                ExportCards(cards);
                co_return;
            }
            if (name == L"ExportPng_Click")
            {
                auto folder = co_await PickFolder(L"导出原始人物卡");
                if (folder.empty())
                    co_return;
                Work("导出原始 PNG",
                     [this, cards, folder](auto stop, auto progress)
                     {
                         return Results(files_->Export(cards, fs::path(folder.c_str()), progress, stop));
                     });
                co_return;
            }
            if (name == L"Move_Click")
            {
                if (mode_ == "trash")
                    co_return;
                auto folder = co_await PickFolder(L"将选中的人物卡移动到文件夹");
                if (folder.empty())
                    co_return;
                Work("移动人物卡",
                     [this, cards, folder](auto stop, auto)
                     {
                         for (auto const &card : cards)
                         {
                             CheckCancelled(stop);
                             files_->Move(card, fs::path(folder.c_str()), stop);
                         }
                         return "人物卡已移动";
                     });
                co_return;
            }
            if (name == L"Rename_Click")
            {
                if (cards.size() != 1 || mode_ == "trash")
                    co_return;
                auto text = co_await Prompt(L"重命名人物卡", L"仅修改文件名，卡片内容保持完整。",
                                            to_hstring(cards.front().FileName()));
                if (text.empty())
                    co_return;
                Work("重命名人物卡",
                     [this, card = cards.front(), text](auto stop, auto)
                     {
                         files_->Rename(card, to_string(text), stop);
                         return "人物卡已重命名";
                     });
                co_return;
            }
            if (name == L"Quarantine_Click")
            {
                if (mode_ == "trash" || preview_ || editor_ || dialogOpen_)
                    co_return;
                if (!(co_await Confirm(L"移入可恢复隔离区", to_hstring("将选中的 " + std::to_string(cards.size()) +
                                                                       " 项移入隔离区？之后可在可恢复隔离区恢复。"))))
                    co_return;
                if (closing_ || preview_ || editor_)
                    co_return;
                Work("隔离人物卡",
                     [this, cards](auto stop, auto)
                     {
                         for (auto const &card : cards)
                         {
                             CheckCancelled(stop);
                             files_->Quarantine(card, Path(settings_.GamePath), stop);
                         }
                         return "人物卡已移入可恢复隔离区";
                     });
                co_return;
            }
            if (name == L"Restore_Click")
            {
                if (mode_ != "trash")
                    co_return;
                Work("恢复人物卡",
                     [this, cards](auto stop, auto)
                     {
                         for (auto const &card : cards)
                         {
                             CheckCancelled(stop);
                             files_->Restore(card, stop);
                         }
                         return "人物卡已恢复";
                     });
                co_return;
            }
            if (name == L"Delete_Click")
            {
                if (preview_ || editor_ || dialogOpen_)
                    co_return;
                std::string message = "将永久删除以下 " + std::to_string(cards.size()) +
                                      " 个原始文件及管理记录。不经过回收站，无法恢复。\n\n";
                std::vector<CardDeleteRequest> requests;
                for (auto const &card : cards)
                {
                    message += card.FileName() + "\n" + card.Path + "\n\n";
                    requests.push_back(CardDeleteRequest::FromCard(card));
                }
                if (!(co_await Confirm(L"永久删除人物卡", to_hstring(message), true)))
                    co_return;
                if (closing_ || preview_ || editor_)
                    co_return;
                Work("永久删除人物卡",
                     [this, requests](auto stop, auto progress)
                     {
                         return Results(files_->Delete(requests, progress, stop));
                     });
                co_return;
            }
            if (name == L"Reveal_Click")
            {
                Reveal(Path(cards.front().Path));
                co_return;
            }
            if (name == L"More_Click")
            {
                MenuFlyout menu;
                auto add = [&](wchar_t const *label, wchar_t const *action)
                {
                    MenuFlyoutItem item;
                    item.Text(label);
                    std::weak_ptr<NativeWindow> weak = shared_from_this();
                    item.Click(
                        [weak, action](auto const &sender, auto &&)
                        {
                            if (auto self = weak.lock())
                                self->Action(action, sender);
                        });
                    menu.Items().Append(item);
                };
                add(L"仅导出原始 PNG…", L"ExportPng_Click");
                add(L"导出 3D 模型…", L"ExportModel_Click");
                if (mode_ == "trash")
                    add(L"恢复到原目录", L"Restore_Click");
                else
                {
                    add(L"移动到文件夹…", L"Move_Click");
                    if (cards.size() == 1)
                        add(L"重命名…", L"Rename_Click");
                    add(L"移入可恢复隔离区…", L"Quarantine_Click");
                }
                add(L"在资源管理器中显示", L"Reveal_Click");
                menu.ShowAt(sender.try_as<FrameworkElement>() ? sender.as<FrameworkElement>()
                                                              : Control<GridView>(L"CardList"));
                co_return;
            }
        }
        catch (hresult_error const &e)
        {
            Report("操作未完成：" + to_string(e.message()));
        }
        catch (std::exception const &e)
        {
            Report(std::string("操作未完成：") + e.what());
        }
    }

    fire_and_forget NativeWindow::Settings()
    {
        auto lifetime = shared_from_this();
        if (dialogOpen_)
            co_return;
        try
        {
            auto view = LoadView(L"SettingsView");
            view.Width(std::min(850.0, static_cast<double>(root_.XamlRoot().Size().Width) - 160));
            view.MaxHeight(std::min(650.0, static_cast<double>(root_.XamlRoot().Size().Height) - 200));
            auto find = [&]<typename T>(wchar_t const *name)
            {
                return view.FindName(name).as<T>();
            };
            find.operator()<TextBox>(L"GamePathBox").Text(to_hstring(settings_.GamePath));
            find.operator()<TextBox>(L"BlenderPathBox").Text(to_hstring(settings_.BlenderPath));
            find.operator()<TextBox>(L"UnrealPathBox").Text(to_hstring(settings_.UnrealEditorPath));
            find.operator()<CheckBox>(L"RecursiveBox").IsChecked(settings_.RecursiveCardSearch);
            find.operator()<TextBlock>(L"DataPathText").Text(dataDirectory_.wstring());
            find.operator()<TextBlock>(L"CachePathText").Text((dataDirectory_ / L"NativeCpp").wstring());
            find.operator()<RadioButton>(settings_.Theme == "Dark"    ? L"DarkTheme"
                                         : settings_.Theme == "Light" ? L"LightTheme"
                                                                      : L"SystemTheme")
                .IsChecked(true);
            auto roots = single_threaded_observable_vector<Object>();
            for (auto const &root : settings_.ExtraRoots)
                roots.Append(box_value(to_hstring(root)));
            auto rootsList = find.operator()<ListView>(L"RootsList");
            rootsList.ItemsSource(roots);
            auto weakSettings = make_weak(view);
            auto updateRoots = [weakSettings]
            {
                if (auto view = weakSettings.get())
                {
                    auto list = view.FindName(L"RootsList").as<ListView>();
                    auto count = list.Items().Size();
                    list.Height(count ? 124 : 64);
                    view.FindName(L"EmptyRootsHint")
                        .as<UIElement>()
                        .Visibility(count ? Visibility::Collapsed : Visibility::Visible);
                    view.FindName(L"RemoveRootButton").as<Button>().IsEnabled(list.SelectedIndex() >= 0);
                }
            };
            rootsList.SelectionChanged(
                [updateRoots](auto &&, auto &&)
                {
                    updateRoots();
                });
            roots.VectorChanged(
                [updateRoots](auto &&, auto &&)
                {
                    updateRoots();
                });
            updateRoots();
            auto weakView = make_weak(view);
            auto nav = find.operator()<ListView>(L"SettingsNavigation");
            auto change = [weakView](auto const &sender, auto const &)
            {
                auto view = weakView.get();
                if (!view)
                    return;
                auto item = sender.template as<ListView>().SelectedItem().template try_as<ListViewItem>();
                if (!item)
                    return;
                auto section = unbox_value<hstring>(item.Tag());
                view.FindName(L"LibraryPage")
                    .as<UIElement>()
                    .Visibility(section == L"library" ? Visibility::Visible : Visibility::Collapsed);
                view.FindName(L"AppearancePage")
                    .as<UIElement>()
                    .Visibility(section == L"appearance" ? Visibility::Visible : Visibility::Collapsed);
                view.FindName(L"ModelExportPage")
                    .as<UIElement>()
                    .Visibility(section == L"model-export" ? Visibility::Visible : Visibility::Collapsed);
                view.FindName(L"DataPage")
                    .as<UIElement>()
                    .Visibility(section == L"data" ? Visibility::Visible : Visibility::Collapsed);
                view.FindName(L"SectionTitle")
                    .as<TextBlock>()
                    .Text(section == L"library"        ? L"资源库"
                          : section == L"appearance"   ? L"外观主题"
                          : section == L"model-export" ? L"模型导出"
                                                       : L"数据与维护");
                view.FindName(L"SectionDescription")
                    .as<TextBlock>()
                    .Text(section == L"library"        ? L"连接游戏与人物卡目录，设置你的检索范围。"
                          : section == L"appearance"   ? L"让工作空间适应你的使用习惯。"
                          : section == L"model-export" ? L"配置生成原生模型所需的本机软件。"
                                                       : L"管理本地数据、数据库备份与预览缓存。");
            };
            nav.SelectionChanged(change);
            nav.SelectedIndex(0);
            for (auto const &event : UiEvents)
                if (event.View == L"SettingsView" && event.Event == L"Click")
                    if (auto button = view.FindName(hstring(event.Name)).try_as<Button>())
                    {
                        auto handler = std::wstring(event.Handler);
                        button.Click(
                            [this, weakView, roots, handler](auto &&, auto &&) -> fire_and_forget
                            {
                                auto lifetime = shared_from_this();
                                auto view = weakView.get();
                                if (!view)
                                    co_return;
                                try
                                {
                                    if (handler == L"BrowseGame_Click" || handler == L"AddRoot_Click")
                                    {
                                        auto path = co_await PickFolder(
                                            handler == L"BrowseGame_Click" ? L"选择游戏目录" : L"选择人物卡目录");
                                        if (!path.empty())
                                        {
                                            if (handler == L"BrowseGame_Click")
                                                view.FindName(L"GamePathBox").as<TextBox>().Text(path);
                                            else
                                            {
                                                bool exists = false;
                                                for (auto const &value : roots)
                                                    if (SamePath(Path(to_string(unbox_value<hstring>(value))),
                                                                 fs::path(path.c_str())))
                                                        exists = true;
                                                if (!exists)
                                                    roots.Append(box_value(path));
                                            }
                                        }
                                    }
                                    else if (handler == L"RemoveRoot_Click")
                                    {
                                        auto list = view.FindName(L"RootsList").as<ListView>();
                                        if (list.SelectedIndex() >= 0)
                                            roots.RemoveAt(list.SelectedIndex());
                                    }
                                    else if (handler == L"BrowseBlender_Click" || handler == L"BrowseUnreal_Click")
                                    {
                                        winrt::Windows::Storage::Pickers::FileOpenPicker picker;
                                        picker.FileTypeFilter().Append(L".exe");
                                        check_hresult(picker.as<IInitializeWithWindow>()->Initialize(Handle()));
                                        auto file = co_await picker.PickSingleFileAsync();
                                        if (file)
                                            view.FindName(handler == L"BrowseBlender_Click" ? L"BlenderPathBox"
                                                                                            : L"UnrealPathBox")
                                                .as<TextBox>()
                                                .Text(file.Path());
                                    }
                                    else if (handler == L"Backup_Click")
                                    {
                                        winrt::Windows::Storage::Pickers::FileSavePicker picker;
                                        picker.SuggestedFileName(L"HS2Manager-library.db");
                                        picker.FileTypeChoices().Insert(L"SQLite 数据库",
                                                                        single_threaded_vector<hstring>({L".db"}));
                                        check_hresult(picker.as<IInitializeWithWindow>()->Initialize(Handle()));
                                        auto file = co_await picker.PickSaveFileAsync();
                                        if (file)
                                        {
                                            repository_->Backup(fs::path(file.Path().c_str()));
                                            auto bar = view.FindName(L"ResultBar").as<InfoBar>();
                                            bar.Message(L"管理数据库已备份。");
                                            bar.Severity(InfoBarSeverity::Success);
                                            bar.IsOpen(true);
                                        }
                                    }
                                    else if (handler == L"Logs_Click")
                                    {
                                        auto path = dataDirectory_ / L"Logs";
                                        fs::create_directories(path);
                                        ShellExecuteW(Handle(), L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
                                    }
                                }
                                catch (std::exception const &e)
                                {
                                    auto bar = view.FindName(L"ResultBar").as<InfoBar>();
                                    bar.Severity(InfoBarSeverity::Error);
                                    bar.Message(to_hstring(e.what()));
                                    bar.IsOpen(true);
                                }
                                catch (hresult_error const &e)
                                {
                                    auto bar = view.FindName(L"ResultBar").as<InfoBar>();
                                    bar.Severity(InfoBarSeverity::Error);
                                    bar.Message(e.message());
                                    bar.IsOpen(true);
                                }
                            });
                    }
            ContentDialog dialog;
            dialog.Resources().Insert(box_value(L"ContentDialogMaxWidth"), box_value(1000.0));
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.RequestedTheme());
            dialog.Title(box_value(L"设置与连接"));
            dialog.Content(view);
            dialog.PrimaryButtonText(L"保存");
            dialog.CloseButtonText(L"取消");
            dialog.DefaultButton(ContentDialogButton::Primary);
            dialog.PrimaryButtonClick(
                [this, view, roots](auto const &, ContentDialogButtonClickEventArgs const &args)
                {
                    try
                    {
                        auto next = settings_;
                        auto path = Trim(to_string(view.FindName(L"GamePathBox").as<TextBox>().Text()));
                        if (path.empty())
                            throw std::runtime_error("请选择游戏目录。");
                        next.GamePath = Utf8(fs::absolute(Path(path)));
                        next.ExtraRoots.clear();
                        for (auto const &value : roots)
                            next.ExtraRoots.push_back(Utf8(fs::absolute(Path(to_string(unbox_value<hstring>(value))))));
                        next.RecursiveCardSearch =
                            unbox_value_or<bool>(view.FindName(L"RecursiveBox").as<CheckBox>().IsChecked(), false);
                        next.BlenderPath = Trim(to_string(view.FindName(L"BlenderPathBox").as<TextBox>().Text()));
                        next.UnrealEditorPath = Trim(to_string(view.FindName(L"UnrealPathBox").as<TextBox>().Text()));
                        next.Theme =
                            unbox_value_or<bool>(view.FindName(L"DarkTheme").as<RadioButton>().IsChecked(), false)
                                ? "Dark"
                            : unbox_value_or<bool>(view.FindName(L"LightTheme").as<RadioButton>().IsChecked(), false)
                                ? "Light"
                                : "System";
                        next.Save(dataDirectory_);
                        settings_ = std::move(next);
                    }
                    catch (std::exception const &e)
                    {
                        args.Cancel(true);
                        auto bar = view.FindName(L"ResultBar").as<InfoBar>();
                        bar.Severity(InfoBarSeverity::Error);
                        bar.Message(to_hstring(e.what()));
                        bar.IsOpen(true);
                        view.FindName(L"SettingsNavigation").as<ListView>().SelectedIndex(0);
                        view.FindName(L"GamePathBox").as<TextBox>().Focus(FocusState::Programmatic);
                    }
                });
            dialogOpen_ = true;
            auto result = co_await dialog.ShowAsync();
            dialogOpen_ = false;
            if (result == ContentDialogResult::Primary)
            {
                ApplyTheme();
                Reload();
                Refresh();
            }
        }
        catch (hresult_error const &e)
        {
            dialogOpen_ = false;
            Report(to_string(e.message()));
        }
        catch (std::exception const &e)
        {
            dialogOpen_ = false;
            Report(e.what());
        }
    }
} // 命名空间 hs2::ui
