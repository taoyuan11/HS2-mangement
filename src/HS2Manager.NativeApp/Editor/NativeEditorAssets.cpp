#include "Character/CharacterCatalog.h"
#include "Editor/NativeEditor.h"
#include "CreationService.h"
#include "PostProcessing/NativeFullscreenPass.h"
#include <cstring>
#include <robuffer.h>
#include <winrt/Microsoft.UI.Xaml.Automation.h>
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    struct EditorCatalogState
    {
        std::mutex Gate;
        std::unique_ptr<CharacterCatalog> Catalog;
        fs::path Game, Data, Tpk;
    };

    fire_and_forget NativeEditor::LoadInformationChoices()
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner)
            co_return;
        auto game = Path(owner->settings_.GamePath);
        auto tpk = ExecutableDirectory() / "classdata.tpk";
        auto token = catalogCancellation_.get_token();
        apartment_context ui;
        std::map<std::string, std::vector<CharacterChoice>> choices;
        std::string error;
        co_await resume_background();
        try
        {
            choices = creation::InformationChoices(game, tpk, token);
        }
        catch (const std::exception &e)
        {
            error = e.what();
        }
        co_await ui;
        if (stopped_ || token.stop_requested())
            co_return;
        informationChoices_ = std::move(choices);
        RefreshFields();
        if (!error.empty())
            Get<TextBlock>(L"EditorMessage").Text(to_hstring(error));
    }

    void NativeEditor::Stop()
    {
        stopped_ = true;
        catalogCancellation_.request_stop();
        if (previewTimer_)
            previewTimer_.Stop();
    }

    fire_and_forget NativeEditor::ChangeChoice(CharacterField field, MessagePack::Value value)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || saving_ || stopped_)
            co_return;
        if (!field.Path.starts_with("accessory.") || !field.Path.ends_with(".type"))
        {
            try
            {
                session_.SetValue(field.Path, value);
                RefreshFields();
            }
            catch (std::exception const &e)
            {
                Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
            }
            co_return;
        }
        BeginGesture();
        try
        {
            session_.SetValue(field.Path, value);
            auto category = static_cast<int>(session_.Document.Node(field.Path).Integer());
            if (category > 350)
            {
                if (!catalog_)
                    catalog_ = std::make_shared<EditorCatalogState>();
                auto state = catalog_;
                state->Game = Path(owner->settings_.GamePath);
                state->Data = owner->dataDirectory_;
                state->Tpk = ExecutableDirectory() / L"classdata.tpk";
                auto document = session_.Document;
                auto path = field.Path.substr(0, field.Path.size() - 4) + "id";
                std::optional<CharacterDocument> changed;
                std::string error;
                apartment_context ui;
                saving_ = true;
                owner->model_->Put(L"WorkLabel", L"正在初始化配饰默认素材…");
                owner->model_->Put(L"ProgressIndeterminate", true);
                owner->SetBusy(true);
                UpdateState();
                catalogCancellation_ = std::stop_source{};
                auto token = catalogCancellation_.get_token();
                co_await resume_background();
                try
                {
                    std::lock_guard guard(state->Gate);
                    if (!state->Catalog)
                        state->Catalog = std::make_unique<CharacterCatalog>(state->Game, state->Data, state->Tpk);
                    state->Catalog->Load({}, token);
                    auto entries = state->Catalog->Query(category, {}, 1);
                    auto initial = std::find_if(entries.begin(), entries.end(),
                                                [](auto const &e)
                                                {
                                                    return e.Id == 0;
                                                });
                    if (initial != entries.end())
                        changed = state->Catalog->Select(document, path, *initial);
                }
                catch (std::exception const &e)
                {
                    error = e.what();
                }
                co_await ui;
                if (changed)
                    session_.Apply(*changed);
                else if (!error.empty())
                    Get<TextBlock>(L"EditorMessage").Text(to_hstring("配饰类别已更改；默认素材无法初始化：" + error));
            }
        }
        catch (std::exception const &e)
        {
            Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
        }
        catch (hresult_error const &e)
        {
            Get<TextBlock>(L"EditorMessage").Text(e.message());
        }
        EndGesture();
        saving_ = false;
        owner->SetBusy(false);
        RefreshFields();
        UpdateState();
        QueuePreview();
        if (owner->closing_)
        {
            owner->closing_ = false;
            owner->RequestClose();
        }
    }

    fire_and_forget NativeEditor::ChooseAsset(CharacterField field)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || owner->dialogOpen_ || saving_ || stopped_)
            co_return;
        catalogCancellation_ = std::stop_source{};
        owner->dialogOpen_ = true;
        saving_ = true;
        owner->model_->Put(L"WorkLabel", L"正在读取原版与 MOD 素材目录…");
        owner->model_->Put(L"ProgressIndeterminate", true);
        owner->SetBusy(true);
        UpdateState();
        try
        {
            if (!catalog_)
                catalog_ = std::make_shared<EditorCatalogState>();
            auto state = catalog_;
            state->Game = Path(owner->settings_.GamePath);
            state->Data = owner->dataDirectory_;
            state->Tpk = ExecutableDirectory() / L"classdata.tpk";
            auto doc = session_.Document;
            int category;
            if (field.Kind == CharacterFieldKind::Preset)
                category = field.Path == "face.moleLayout" ? 6 : 7;
            else
            {
                auto binding = CharacterAssetReferences::Describe(doc, field.Path);
                if (!binding)
                    throw std::runtime_error("此字段没有可用素材类别。");
                category = binding->Category;
            }
            std::vector<NativeAssetEntry> entries;
            std::optional<NativeAssetEntry> current;
            std::string error;
            apartment_context ui;
            auto token = catalogCancellation_.get_token();
            co_await resume_background();
            try
            {
                std::lock_guard guard(state->Gate);
                CheckCancelled(token);
                if (!state->Catalog)
                    state->Catalog = std::make_unique<CharacterCatalog>(state->Game, state->Data, state->Tpk);
                state->Catalog->Load({}, token);
                entries = state->Catalog->Query(category);
                if (field.Kind != CharacterFieldKind::Preset)
                    current = state->Catalog->Selected(doc, field.Path);
            }
            catch (std::exception const &e)
            {
                error = e.what();
            }
            co_await ui;
            saving_ = false;
            owner->SetBusy(false);
            UpdateState();
            if (owner->closing_)
            {
                owner->dialogOpen_ = false;
                owner->closing_ = false;
                owner->RequestClose();
                co_return;
            }
            if (!error.empty())
                throw std::runtime_error(error);
            TextBox query;
            query.PlaceholderText(L"搜索名称、编号或来源");
            query.Width(280);
            ComboBox source;
            source.Items().Append(box_value(L"全部来源"));
            source.Items().Append(box_value(L"原版"));
            source.Items().Append(box_value(L"MOD"));
            source.SelectedIndex(0);
            source.Width(120);
            Button locate;
            locate.Content(box_value(L"定位当前"));
            StackPanel filters;
            filters.Orientation(Orientation::Horizontal);
            filters.Spacing(8);
            filters.Children().Append(query);
            filters.Children().Append(source);
            filters.Children().Append(locate);
            TextBlock info;
            info.Text(to_hstring(current ? current->Editable().Name + " · " + current->Source
                                         : "当前素材：" + (field.Kind == CharacterFieldKind::Preset
                                                               ? std::string("自定义布局")
                                                               : std::to_string(doc.Node(field.Path).Integer()))));
            info.TextWrapping(TextWrapping::Wrap);
            ListView list;
            list.Height(360);
            list.SelectionMode(ListViewSelectionMode::Single);
            list.ItemTemplate(
                Markup::XamlReader::Load(
                    LR"(<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid Padding="4" ColumnSpacing="10"><Grid.ColumnDefinitions><ColumnDefinition Width="56"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Border Width="56" Height="56" Background="#203C4444" CornerRadius="4"><Image Source="{Binding Image}" Stretch="Uniform"/></Border><StackPanel Grid.Column="1" VerticalAlignment="Center"><TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding Source}" FontSize="12" Opacity="0.65" TextTrimming="CharacterEllipsis"/></StackPanel></Grid></DataTemplate>)")
                    .as<DataTemplate>());
            auto values = std::make_shared<std::vector<NativeAssetEntry>>(std::move(entries));
            auto weakList = make_weak(list);
            auto weakQuery = make_weak(query);
            auto weakSource = make_weak(source);
            auto filter = [weakList, weakQuery, weakSource, values]
            {
                auto list = weakList.get();
                auto query = weakQuery.get();
                auto source = weakSource.get();
                if (!list || !query || !source)
                    return;
                auto text = query.Text();
                auto rows = single_threaded_observable_vector<Object>();
                for (size_t i = 0; i < values->size(); ++i)
                {
                    auto const &entry = (*values)[i];
                    if (source.SelectedIndex() == 1 && !entry.Guid.empty() ||
                        source.SelectedIndex() == 2 && entry.Guid.empty())
                        continue;
                    auto label = entry.Editable().Name;
                    auto haystack = to_hstring(label + " " + entry.Source + " " + std::to_string(entry.Id));
                    if (!text.empty() &&
                        FindNLSStringEx(LOCALE_NAME_INVARIANT, FIND_FROMSTART | NORM_IGNORECASE, haystack.c_str(),
                                        static_cast<int>(haystack.size()), text.c_str(), static_cast<int>(text.size()),
                                        nullptr, nullptr, nullptr, 0) < 0)
                        continue;
                    auto row = make_self<Observable>();
                    row->Text(L"Label", label);
                    row->Text(L"Source", entry.Source + " · " + std::to_string(entry.Id));
                    row->Put(L"Index", static_cast<int>(i));
                    rows.Append(row.as<Object>());
                }
                list.ItemsSource(rows);
            };
            query.TextChanged(
                [filter](auto &&, auto &&)
                {
                    filter();
                });
            source.SelectionChanged(
                [filter](auto &&, auto &&)
                {
                    filter();
                });
            auto locateCurrent = [weakList, weakQuery, weakSource, values, current, filter]
            {
                auto list = weakList.get();
                auto query = weakQuery.get();
                auto source = weakSource.get();
                if (!list || !query || !source)
                    return;
                query.Text(L"");
                source.SelectedIndex(0);
                filter();
                if (current)
                    for (auto const &item : list.Items())
                    {
                        auto index = unbox_value<int>(
                            get_self<Observable>(item.as<data::ICustomPropertyProvider>())->Get(L"Index"));
                        auto const &candidate = (*values)[index];
                        if (candidate.Id == current->Id && candidate.Guid == current->Guid)
                        {
                            list.SelectedItem(item);
                            list.ScrollIntoView(item);
                            break;
                        }
                    }
            };
            locate.Click(
                [locateCurrent](auto &&, auto &&)
                {
                    locateCurrent();
                });
            std::weak_ptr<NativeEditor> weak = shared_from_this();
            auto thumbnails = std::make_shared<std::stop_source>();
            list.ContainerContentChanging(
                [weak, values, thumbnails](auto &&, ContainerContentChangingEventArgs const &args)
                {
                    if (args.InRecycleQueue())
                        return;
                    if (auto self = weak.lock())
                    {
                        auto row = get_self<Observable>(args.Item().as<data::ICustomPropertyProvider>());
                        if (row->Bool(L"Requested"))
                            return;
                        row->Put(L"Requested", true);
                        auto index = unbox_value<int>(row->Get(L"Index"));
                        self->AssetThumbnail(row->get_strong(), (*values)[index], thumbnails->get_token());
                    }
                });
            StackPanel panel;
            panel.Spacing(12);
            panel.Width(540);
            panel.Children().Append(filters);
            panel.Children().Append(info);
            panel.Children().Append(list);
            ContentDialog dialog;
            dialog.Resources().Insert(box_value(L"ContentDialogMaxWidth"), box_value(640.0));
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.RequestedTheme());
            dialog.Title(box_value(to_hstring(field.Label)));
            dialog.Content(panel);
            dialog.PrimaryButtonText(L"使用素材");
            dialog.CloseButtonText(L"取消");
            dialog.IsPrimaryButtonEnabled(false);
            auto weakDialog = make_weak(dialog);
            list.SelectionChanged(
                [weakDialog, weakList](auto &&, auto &&)
                {
                    auto dialog = weakDialog.get();
                    auto list = weakList.get();
                    if (dialog && list)
                        dialog.IsPrimaryButtonEnabled(static_cast<bool>(list.SelectedItem()));
                });
            locateCurrent();
            auto result = co_await dialog.ShowAsync();
            thumbnails->request_stop();
            owner->dialogOpen_ = false;
            if (result != ContentDialogResult::Primary || !list.SelectedItem())
                co_return;
            auto row = get_self<Observable>(list.SelectedItem().as<data::ICustomPropertyProvider>());
            auto entry = (*values)[unbox_value<int>(row->Get(L"Index"))];
            std::optional<CharacterDocument> changed;
            saving_ = true;
            owner->model_->Put(L"WorkLabel", L"正在应用所选素材…");
            owner->model_->Put(L"ProgressIndeterminate", true);
            owner->SetBusy(true);
            UpdateState();
            co_await resume_background();
            try
            {
                std::lock_guard guard(state->Gate);
                CheckCancelled(token);
                changed = state->Catalog->Select(doc, field.Path, entry);
            }
            catch (std::exception const &e)
            {
                error = e.what();
            }
            co_await ui;
            saving_ = false;
            owner->SetBusy(false);
            if (changed)
            {
                session_.Apply(*changed);
                RefreshFields();
            }
            else
                Get<TextBlock>(L"EditorMessage").Text(to_hstring("素材未应用：" + error));
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

    fire_and_forget NativeEditor::AssetThumbnail(com_ptr<Observable> row, NativeAssetEntry entry, std::stop_token token)
    {
        auto weak = weak_from_this();
        auto state = catalog_;
        if (!state || stopped_)
            co_return;
        // 界面为后续对话框替换取消源时，已取得的令牌仍可安全使用。
        auto editorToken = catalogCancellation_.get_token();
        apartment_context ui;
        std::vector<uint8_t> pixels;
        int width = 0, height = 0;
        bool ok = false;
        co_await resume_background();
        try
        {
            std::lock_guard guard(state->Gate);
            CheckCancelled(token);
            CheckCancelled(editorToken);
            auto reference = state->Catalog->Thumbnail(entry);
            if (reference)
            {
                NativeGpuAssetReader reader(reference->Bundle, state->Catalog->Database().get());
                auto pointer = reader.Find(reference->Asset, 28);
                auto texture = reader.Texture(pointer.PathId);
                GraphicsDevice graphics(true);
                GpuTexture image(graphics, texture);
                auto scale = std::min(96.0 / texture.Width, 192.0 / texture.Height);
                width = std::max(1, static_cast<int>(texture.Width * scale + .5));
                height = std::max(1, static_cast<int>(texture.Height * scale + .5));
                GpuTexture output(graphics, width, height, true,
                                  texture.Srgb ? DXGI_FORMAT_B8G8R8A8_UNORM_SRGB : DXGI_FORMAT_B8G8R8A8_UNORM, false);
                // Unity 贴图按从下到上的行顺序存储，XAML 则按从上到下读取像素。
                GpuCopyPass(graphics).Copy(image, output, true);
                pixels = graphics.Readback(output.Texture.Get());
                // WriteableBitmap 需要预乘透明度的 BGRA 像素，与源贴图格式不同。
                for (size_t i = 0; i < pixels.size(); i += 4)
                    for (size_t channel = 0; channel < 3; ++channel)
                        pixels[i + channel] = static_cast<uint8_t>((pixels[i + channel] * pixels[i + 3] + 127) / 255);
                ok = true;
            }
        }
        catch (std::exception const &)
        {
        }
        try
        {
            co_await ui;
        }
        catch (hresult_error const &)
        {
            // 缩略图读取取消后，调度器仍可能继续关闭；
            // 此时不得持有编辑器、访问 XAML 或让即发即弃协程异常终止。
            co_return;
        }
        auto self = weak.lock();
        if (!self || !ok || self->stopped_ || token.stop_requested() || editorToken.stop_requested())
            co_return;
        try
        {
            Media::Imaging::WriteableBitmap bitmap(width, height);
            auto buffer = bitmap.PixelBuffer();
            com_ptr<::Windows::Storage::Streams::IBufferByteAccess> access =
                buffer.as<::Windows::Storage::Streams::IBufferByteAccess>();
            byte *data{};
            check_hresult(access->Buffer(&data));
            if (buffer.Capacity() < pixels.size())
                co_return;
            std::memcpy(data, pixels.data(), pixels.size());
            bitmap.Invalidate();
            row->Set(L"Image", bitmap, false);
        }
        catch (hresult_error const &)
        {
        }
    }
} // 命名空间 hs2::ui
