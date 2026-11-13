#include "Editor/NativeEditor.h"
#include "Creation/NativeCreation.h"
#include "Infrastructure/ProcessLifetime.h"
#include "Editing/CharacterEditComponents.h"
#include "Preview/NativePreview.h"
#include "Infrastructure/UiEvents.h"
#include <cmath>
#include <utility>
#include <winrt/Microsoft.UI.Xaml.Automation.h>
#include <winrt/Windows.Globalization.NumberFormatting.h>
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    namespace
    {
        double Numeric(MessagePack::Value const &value)
        {
            return std::visit(
                [](auto const &v) -> double
                {
                    using T = std::decay_t<decltype(v)>;
                    if constexpr (std::is_arithmetic_v<T>)
                        return static_cast<double>(v);
                    else
                        throw std::runtime_error("预期数值参数");
                },
                value);
        }

        winrt::Windows::UI::Color Color(std::vector<float> const &value)
        {
            auto channel = [&](size_t i)
            {
                return static_cast<uint8_t>(std::clamp(i < value.size() ? value[i] * 255.f : 255.f, 0.f, 255.f));
            };
            return {channel(3), channel(0), channel(1), channel(2)};
        }

        TextBlock Text(std::string const &value)
        {
            TextBlock label;
            label.Text(to_hstring(value));
            label.TextWrapping(TextWrapping::Wrap);
            return label;
        }
    } // 命名空间

    std::shared_ptr<NativeEditor> NativeEditor::Open(std::shared_ptr<NativeWindow> const &owner, CardRecord card,
                                                     CharacterDocument document)
    {
        auto editor = std::shared_ptr<NativeEditor>(new NativeEditor(owner, std::move(card), std::move(document)));
        editor->Initialize();
        return editor;
    }

    NativeEditor::NativeEditor(std::shared_ptr<NativeWindow> const &owner, CardRecord card, CharacterDocument document)
        : owner_(owner), session_(std::move(card), std::move(document)),
          limits_(CharacterSliderLimits::Read(Path(owner->settings_.GamePath)))
    {
    }

    void NativeEditor::Initialize()
    {
        auto owner = owner_.lock();
        root_ = owner->LoadView(L"CharacterEditorView");
        root_.DataContext(owner->model_.as<Object>());
        Get<TextBlock>(L"EditorTitle").Text(to_hstring("编辑角色 · " + session_.Card.Name));
        auto categories = single_threaded_observable_vector<Object>();
        for (auto const &[id, label] : CharacterEditSchema::Categories())
        {
            auto item = make_self<Observable>();
            item->Text(L"Id", id);
            item->Text(L"Label", label);
            categories.Append(item.as<Object>());
        }
        Get<ListView>(L"Categories").ItemsSource(categories);
        std::weak_ptr<NativeEditor> weak = shared_from_this();
        session_.Changed = [weak]
        {
            if (auto self = weak.lock())
            {
                if (self->project_ && !self->project_->State().Character->ContentEquals(self->session_.Document))
                    self->project_->Edit("角色外观",
                                         [&](creation::CreationState &s)
                                         {
                                             s.Character = self->session_.Document;
                                         });
                self->UpdateState();
                self->QueuePreview();
            }
        };
        for (auto const &event : UiEvents)
            if (event.View == L"CharacterEditorView")
            {
                auto element = root_.FindName(hstring(event.Name));
                if (!element)
                    continue;
                std::wstring action(event.Handler);
                if (event.Event == L"Click")
                {
                    auto callback = [weak, action](auto const &sender, auto &&)
                    {
                        if (auto self = weak.lock())
                            self->Action(action, sender);
                    };
                    if (auto button = element.try_as<Primitives::ButtonBase>())
                        button.Click(callback);
                    else if (auto item = element.try_as<MenuFlyoutItem>())
                        item.Click(callback);
                }
                else if (event.Event == L"SelectionChanged")
                {
                    if (auto selection = element.try_as<Primitives::Selector>())
                        selection.SelectionChanged(
                            [weak, action](auto const &sender, auto &&)
                            {
                                if (auto self = weak.lock())
                                    self->Action(action, sender);
                            });
                }
                else if (event.Event == L"Toggled")
                    element.as<ToggleSwitch>().Toggled(
                        [weak, action](auto const &sender, auto &&)
                        {
                            if (auto self = weak.lock())
                                self->Action(action, sender);
                        });
                else if (event.Event == L"TextChanged")
                    if (auto input = element.try_as<TextBox>())
                        input.TextChanged(
                            [weak](auto &&, auto &&)
                            {
                                if (auto self = weak.lock())
                                    if (!self->building_)
                                        self->BuildParameters();
                            });
            }
        building_ = true;
        Get<ListView>(L"Categories").SelectedIndex(0);
        building_ = false;
        RefreshFields();
        UpdateState();
        owner->Control<ContentControl>(L"EditorHost").Content(root_);
        owner->model_->Put(L"IsEditing", true);
        Get<TextBlock>(L"EditorMessage").Text(L"封面保持原始字节 · 原生编辑草稿");
        previewTimer_ = owner->dispatcher_.CreateTimer();
        previewTimer_.Interval(std::chrono::milliseconds(33));
        previewTimer_.IsRepeating(false);
        previewTimer_.Tick(
            [weak](auto &&, auto &&)
            {
                if (auto self = weak.lock())
                    self->UpdatePreview();
            });
        owner->preview_ = NativePreview::Create(owner);
        owner->preview_->Load(session_.Card, root_, true, session_.Document);
        LoadInformationChoices();
    }

    void NativeEditor::BindProject(std::shared_ptr<creation::CreationProject> project)
    {
        project_ = std::move(project);
        root_.as<Grid>().Children().GetAt(0).Visibility(Visibility::Collapsed);
        root_.as<Grid>().RowDefinitions().GetAt(0).Height({0, GridUnitType::Pixel});
        Get<TextBlock>(L"EditorMessage").Text(L"角色外观纳入工程历史 · 导出时生成当前版本封面");
        Get<Button>(L"SaveButton").Content(box_value(L"保存工程"));
        for (const auto &event : UiEvents)
            if (event.View == L"CharacterEditorView" &&
                (event.Handler == L"SwitchCard_Click" || event.Handler == L"RestoreBackup_Click" ||
                 event.Handler == L"Overwrite_Click" || event.Handler == L"SaveFolder_Click"))
                if (auto control = root_.FindName(hstring(event.Name)).try_as<Control>())
                    control.IsEnabled(false);
        UpdateState();
    }

    void NativeEditor::SyncProject()
    {
        if (!project_)
            return;
        bool changed = !session_.Document.ContentEquals(*project_->State().Character);
        session_.Document = *project_->State().Character;
        session_.Card.Name = project_->State().Name;
        if (changed)
        {
            RefreshFields();
            QueuePreview();
        }
        UpdateState();
    }

    void NativeEditor::BeginGesture()
    {
        session_.BeginGesture();
        if (project_)
            project_->BeginGesture();
    }

    void NativeEditor::EndGesture()
    {
        session_.EndGesture();
        if (project_)
            project_->EndGesture();
    }

    void NativeEditor::QueuePreview()
    {
        if (!stopped_ && previewTimer_ && !previewTimer_.IsRunning())
            previewTimer_.Start();
    }

    void NativeEditor::Definition(std::shared_ptr<const CharacterDefinition> definition)
    {
        definition_ = std::move(definition);
        auto current = CharacterEditSchema::Build(session_.Document, limits_);
        std::erase_if(current,
                      [&](auto const &field)
                      {
                          return !CharacterFieldCapabilities::Visible(field, definition_.get());
                      });
        if (current.size() != fields_.size() ||
            !std::equal(current.begin(), current.end(), fields_.begin(), fields_.end(),
                        [](auto const &a, auto const &b)
                        {
                            return a.Path == b.Path;
                        }))
            RefreshFields();
    }

    void NativeEditor::UpdatePreview()
    {
        if (stopped_)
            return;
        if (auto owner = owner_.lock())
            if (owner->preview_)
            {
                owner->preview_->Update(session_.Document, UpdateVisibility());
            }
    }

    CharacterPreviewVisibility NativeEditor::UpdateVisibility()
    {
        auto choice = Get<ComboBox>(L"ClothingVisibility").SelectedIndex();
        CharacterPreviewVisibility visibility;
        visibility.Clothing =
            choice == 0
                ? CharacterEditSchema::AutoClothingState(
                      Category(), to_string(unbox_value_or<hstring>(Get<ListView>(L"Groups").SelectedItem(), L"")))
                : choice - 1;
        visibility.Accessories = Get<ToggleSwitch>(L"AccessoryVisibility").IsOn();
        Get<TextBlock>(L"VisibilityStatus")
            .Text(to_hstring(std::string(choice == 0 ? "自动 · " : "") +
                             (visibility.Clothing == 0   ? "完整着装"
                              : visibility.Clothing == 1 ? "内着"
                                                         : "不穿衣物") +
                             (visibility.Accessories ? "" : " · 隐藏配饰")));
        if (auto owner = owner_.lock())
            if (owner->preview_)
                owner->preview_->Visibility(visibility);
        return visibility;
    }

    std::string NativeEditor::Category() const
    {
        auto item = Get<ListView>(L"Categories").SelectedItem();
        if (!item)
            return "face";
        return to_string(get_self<Observable>(item.as<data::ICustomPropertyProvider>())->String(L"Id"));
    }

    void NativeEditor::UpdateState()
    {
        Get<TextBlock>(L"SaveState")
            .Text(to_hstring(std::string(IsDirty() ? "● 未保存" : "已保存") + " · 草稿 r" +
                             std::to_string(session_.Revision) + " · " + session_.Card.FileName()));
        Get<Button>(L"UndoButton").IsEnabled(!saving_ && (project_ ? project_->CanUndo() : session_.CanUndo()));
        Get<Button>(L"RedoButton").IsEnabled(!saving_ && (project_ ? project_->CanRedo() : session_.CanRedo()));
        if (auto owner = owner_.lock())
            owner->Control<ContentControl>(L"EditorHost").IsEnabled(!saving_ && !owner->busy_);
    }

    void NativeEditor::RefreshFields()
    {
        if (building_)
            return;
        building_ = true;
        fields_ = CharacterEditSchema::Build(session_.Document, limits_);
        for (auto &field : fields_)
            if (field.Category == "parameter" && field.Kind == CharacterFieldKind::Choice)
            {
                auto key = field.Path.substr(field.Path.rfind('.') + 1);
                if (informationChoices_.contains(key))
                    field.Choices = informationChoices_.at(key);
            }
        std::erase_if(fields_,
                      [&](auto const &field)
                      {
                          return !CharacterFieldCapabilities::Visible(field, definition_.get());
                      });
        building_ = false;
        RefreshGroups();
    }

    void NativeEditor::RefreshGroups()
    {
        if (building_)
            return;
        building_ = true;
        auto group = unbox_value_or<hstring>(Get<ListView>(L"Groups").SelectedItem(), L"");
        auto category = Category();
        std::vector<std::string> groups;
        for (auto const &field : fields_)
            if (field.Category == category && std::find(groups.begin(), groups.end(), field.Group) == groups.end())
                groups.push_back(field.Group);
        auto items = single_threaded_observable_vector<Object>();
        int index = 0;
        for (size_t i = 0; i < groups.size(); ++i)
        {
            items.Append(box_value(to_hstring(groups[i])));
            if (groups[i] == to_string(group))
                index = static_cast<int>(i);
        }
        Get<ListView>(L"Groups").ItemsSource(items);
        Get<ListView>(L"Groups").SelectedIndex(groups.empty() ? -1 : index);
        building_ = false;
        BuildParameters();
    }

    void NativeEditor::BuildParameters()
    {
        if (building_)
            return;
        building_ = true;
        try
        {
            EndGesture();
            auto panel = Get<StackPanel>(L"Parameters");
            panel.Children().Clear();
            auto group = to_string(unbox_value_or<hstring>(Get<ListView>(L"Groups").SelectedItem(), L"")),
                 search = to_string(Get<TextBox>(L"FieldSearch").Text());
            auto category = Category();
            Get<TextBlock>(L"GroupTitle").Text(to_hstring(search.empty() ? group : "搜索结果"));
            for (auto const &field : fields_)
                if (field.Category == category &&
                    (search.empty() ? field.Group == group
                                    : (field.Label + field.Group).find(search) != std::string::npos))
                    panel.Children().Append(FieldControl(field));
            if (!panel.Children().Size())
                panel.Children().Append(Text("此角色没有相应参数"));
        }
        catch (std::exception const &e)
        {
            Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
        }
        building_ = false;
    }

    FrameworkElement NativeEditor::FieldControl(CharacterField field)
    {
        std::weak_ptr<NativeEditor> weak = shared_from_this();
        StackPanel panel;
        panel.Spacing(6);
        Grid header;
        ColumnDefinition fill;
        fill.Width({1, GridUnitType::Star});
        header.ColumnDefinitions().Append(fill);
        ColumnDefinition autoWidth;
        autoWidth.Width(GridLengthHelper::Auto());
        header.ColumnDefinitions().Append(autoWidth);
        header.Children().Append(Text(field.Label));
        Button restore;
        restore.Content(box_value(L"↶"));
        restore.MinWidth(28);
        restore.Padding({6, 2, 6, 2});
        restore.IsEnabled(session_.Baseline.Contains(field.Path));
        Automation::AutomationProperties::SetName(restore, to_hstring("恢复" + field.Label));
        restore.Click(
            [weak, field](auto &&, auto &&)
            {
                if (auto self = weak.lock())
                {
                    self->session_.Restore(field.Path);
                    self->RefreshFields();
                }
            });
        Grid::SetColumn(restore, 1);
        header.Children().Append(restore);
        panel.Children().Append(header);
        if (field.Kind == CharacterFieldKind::Text)
        {
            TextBox input;
            input.Text(to_hstring(session_.Document.Node(field.Path).Text()));
            input.MaxLength(256);
            Automation::AutomationProperties::SetName(input, to_hstring(field.Label));
            input.LostFocus(
                [weak, field](auto const &sender, auto &&)
                {
                    if (auto self = weak.lock())
                        if (!self->building_ && !self->saving_)
                        {
                            try
                            {
                                self->session_.SetValue(field.Path, to_string(sender.template as<TextBox>().Text()));
                            }
                            catch (const std::exception &e)
                            {
                                self->Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
                            }
                        }
                });
            panel.Children().Append(input);
        }
        else if (field.Kind == CharacterFieldKind::Number)
        {
            double value = Numeric(session_.Document.Node(field.Path).ScalarValue) * field.DisplayScale;
            Slider slider;
            slider.Minimum(std::min(field.Minimum * field.DisplayScale, value));
            slider.Maximum(std::max(field.Maximum * field.DisplayScale, value));
            slider.Value(value);
            slider.StepFrequency(field.DisplayScale == 1 ? 1 : .1);
            slider.IsThumbToolTipEnabled(false);
            NumberBox input;
            winrt::Windows::Globalization::NumberFormatting::DecimalFormatter formatter;
            formatter.FractionDigits(2);
            formatter.IntegerDigits(1);
            winrt::Windows::Globalization::NumberFormatting::IncrementNumberRounder rounder;
            rounder.Increment(.01);
            formatter.NumberRounder(rounder);
            input.NumberFormatter(formatter);
            bool extended = field.Path.find("shapeValue") != std::string::npos ||
                            field.Path.find("dictBundle") != std::string::npos;
            input.Minimum(std::min((extended ? limits_.InputMinimum : field.Minimum) * field.DisplayScale, value));
            input.Maximum(std::max((extended ? limits_.InputMaximum : field.Maximum) * field.DisplayScale, value));
            input.Value(value);
            input.SpinButtonPlacementMode(NumberBoxSpinButtonPlacementMode::Compact);
            auto syncing = std::make_shared<bool>(false);
            auto weakInput = make_weak(input);
            auto weakSlider = make_weak(slider);
            Automation::AutomationProperties::SetName(slider, to_hstring(field.Label));
            Automation::AutomationProperties::SetName(input, to_hstring(field.Label + "数值"));
            slider.AddHandler(UIElement::PointerPressedEvent(),
                              box_value(Input::PointerEventHandler(
                                  [weak](auto &&, auto &&)
                                  {
                                      if (auto self = weak.lock())
                                          self->BeginGesture();
                                  })),
                              true);
            slider.AddHandler(UIElement::PointerCaptureLostEvent(),
                              box_value(Input::PointerEventHandler(
                                  [weak](auto &&, auto &&)
                                  {
                                      if (auto self = weak.lock())
                                          self->EndGesture();
                                  })),
                              true);
            slider.AddHandler(UIElement::PointerReleasedEvent(),
                              box_value(Input::PointerEventHandler(
                                  [weak](auto &&, auto &&)
                                  {
                                      if (auto self = weak.lock())
                                          self->EndGesture();
                                  })),
                              true);
            slider.LostFocus(
                [weak](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->EndGesture();
                });
            slider.ValueChanged(
                [weak, field, syncing, weakInput](auto &&, auto const &args)
                {
                    if (*syncing)
                        return;
                    if (auto self = weak.lock())
                    {
                        if (self->building_ || self->saving_)
                            return;
                        *syncing = true;
                        try
                        {
                            if (auto input = weakInput.get())
                                input.Value(args.NewValue());
                            self->session_.SetValue(field.Path, args.NewValue() / field.DisplayScale);
                        }
                        catch (std::exception const &e)
                        {
                            self->Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
                        }
                        *syncing = false;
                    }
                });
            input.ValueChanged(
                [weak, field, syncing, weakSlider](auto &&, auto const &args)
                {
                    if (*syncing || !std::isfinite(args.NewValue()))
                        return;
                    if (auto self = weak.lock())
                    {
                        if (self->building_ || self->saving_)
                            return;
                        *syncing = true;
                        try
                        {
                            self->EndGesture();
                            if (auto slider = weakSlider.get())
                            {
                                slider.Minimum(std::min(slider.Minimum(), args.NewValue()));
                                slider.Maximum(std::max(slider.Maximum(), args.NewValue()));
                                slider.Value(args.NewValue());
                            }
                            self->session_.SetValue(field.Path, args.NewValue() / field.DisplayScale);
                        }
                        catch (std::exception const &e)
                        {
                            self->Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
                        }
                        *syncing = false;
                    }
                });
            panel.Children().Append(slider);
            panel.Children().Append(input);
        }
        else if (field.Kind == CharacterFieldKind::Toggle)
        {
            ToggleSwitch toggle;
            toggle.IsOn(std::get<bool>(session_.Document.Node(field.Path).ScalarValue));
            toggle.OnContent(box_value(L"开启"));
            toggle.OffContent(box_value(L"关闭"));
            toggle.Toggled(
                [weak, field](auto const &sender, auto &&)
                {
                    if (auto self = weak.lock())
                        if (!self->building_)
                            self->session_.SetValue(field.Path, sender.template as<ToggleSwitch>().IsOn());
                });
            panel.Children().Append(toggle);
        }
        else if (field.Kind == CharacterFieldKind::Choice)
        {
            ComboBox choice;
            choice.HorizontalAlignment(HorizontalAlignment::Stretch);
            auto current = session_.Document.Node(field.Path).ScalarValue;
            auto values = field.Choices;
            int index = -1;
            for (size_t i = 0; i < values.size(); ++i)
            {
                choice.Items().Append(box_value(to_hstring(values[i].Label)));
                if (values[i].Value == current)
                    index = static_cast<int>(i);
            }
            if (index < 0)
            {
                values.push_back({current, "原值"});
                choice.Items().Append(box_value(L"原值"));
                index = static_cast<int>(values.size() - 1);
            }
            choice.SelectedIndex(index);
            choice.SelectionChanged(
                [weak, field, values](auto const &sender, auto &&)
                {
                    if (auto self = weak.lock())
                    {
                        auto index = sender.template as<ComboBox>().SelectedIndex();
                        if (!self->building_ && index >= 0)
                        {
                            try
                            {
                                self->ChangeChoice(field, values[index].Value);
                            }
                            catch (std::exception const &e)
                            {
                                self->Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
                            }
                        }
                    }
                });
            panel.Children().Append(choice);
        }
        else if (field.Kind == CharacterFieldKind::Color)
        {
            Button button;
            button.Content(box_value(L"选择颜色"));
            button.Click(
                [weak, field](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->ChooseColor(field);
                });
            panel.Children().Append(button);
        }
        else
        {
            Button button;
            button.HorizontalAlignment(HorizontalAlignment::Stretch);
            button.HorizontalContentAlignment(HorizontalAlignment::Left);
            auto label = field.Kind == CharacterFieldKind::Preset
                             ? std::string("选择布局预设")
                             : "选择素材 · 当前编号 " + std::to_string(session_.Document.Node(field.Path).Integer());
            button.Content(Text(label));
            Automation::AutomationProperties::SetName(button, to_hstring("选择" + field.Label));
            button.Click(
                [weak, field](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->ChooseAsset(field);
                });
            panel.Children().Append(button);
            if (project_ && field.Kind == CharacterFieldKind::Asset &&
                (field.Path.starts_with("hair.") || field.Path.starts_with("clothes.") ||
                 field.Path.starts_with("accessory.")))
            {
                Button create;
                create.Content(box_value(L"从此槽位创建 Mod"));
                create.Click(
                    [weak, field](auto &&, auto &&)
                    {
                        if (auto self = weak.lock())
                            if (auto owner = self->owner_.lock())
                                if (owner->creation_)
                                    owner->creation_->CreateAssetAt(field.Path);
                    });
                panel.Children().Append(create);
            }
        }
        return panel;
    }

    fire_and_forget NativeEditor::ChooseColor(CharacterField field)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || owner->dialogOpen_ || saving_)
            co_return;
        owner->dialogOpen_ = true;
        auto before = session_.Document;
        try
        {
            ColorPicker picker;
            picker.Color(Color(CharacterEditSchema::ColorValue(before, field.Path)));
            picker.IsAlphaEnabled(true);
            BeginGesture();
            std::weak_ptr<NativeEditor> weak = shared_from_this();
            picker.ColorChanged(
                [weak, field](auto &&, auto const &args)
                {
                    if (auto self = weak.lock())
                    {
                        auto c = args.NewColor();
                        self->session_.SetColor(field.Path, {c.R / 255.f, c.G / 255.f, c.B / 255.f, c.A / 255.f});
                    }
                });
            ContentDialog dialog;
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.RequestedTheme());
            dialog.Title(box_value(to_hstring(field.Label)));
            dialog.Content(picker);
            dialog.PrimaryButtonText(L"应用");
            dialog.CloseButtonText(L"取消");
            auto result = co_await dialog.ShowAsync();
            if (result != ContentDialogResult::Primary)
                session_.Apply(before);
            EndGesture();
            RefreshFields();
        }
        catch (hresult_error const &e)
        {
            Get<TextBlock>(L"EditorMessage").Text(e.message());
        }
        catch (std::exception const &e)
        {
            Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
        }
        owner->dialogOpen_ = false;
    }

    void NativeEditor::Undo()
    {
        auto owner = owner_.lock();
        if (!owner || saving_ || stopped_ || owner->busy_ || owner->dialogOpen_ || owner->closing_)
            return;
        if (project_)
        {
            EndGesture();
            project_->Undo();
            SyncProject();
            return;
        }
        session_.Undo();
        RefreshFields();
    }

    void NativeEditor::Redo()
    {
        auto owner = owner_.lock();
        if (!owner || saving_ || stopped_ || owner->busy_ || owner->dialogOpen_ || owner->closing_)
            return;
        if (project_)
        {
            EndGesture();
            project_->Redo();
            SyncProject();
            return;
        }
        session_.Redo();
        RefreshFields();
    }

    fire_and_forget NativeEditor::Action(std::wstring action, Object sender)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || saving_ || stopped_ || owner->busy_ || owner->closing_)
            co_return;
        try
        {
            if (owner->preview_ && owner->preview_->Action(action, sender))
                co_return;
            if (action == L"Back_Click")
            {
                co_await owner->CloseEditor();
            }
            else if (action == L"SwitchCard_Click")
                SwitchCard();
            else if (action == L"RestoreBackup_Click")
                RestoreBackup();
            else if (action == L"Undo_Click")
                Undo();
            else if (action == L"Redo_Click")
                Redo();
            else if (action == L"RestoreAll_Click")
            {
                session_.RestoreAll();
                RefreshFields();
            }
            else if (action == L"RestoreCategory_Click")
            {
                session_.RestoreCategory(Category());
                RefreshFields();
            }
            else if (action == L"Category_Changed")
            {
                if (!building_)
                {
                    building_ = true;
                    Get<TextBox>(L"FieldSearch").Text(L"");
                    building_ = false;
                    RefreshGroups();
                    UpdateVisibility();
                }
            }
            else if (action == L"ClothingVisibility_Changed" || action == L"AccessoryVisibility_Toggled")
                UpdateVisibility();
            else if (action == L"Group_Changed")
            {
                if (!building_)
                {
                    BuildParameters();
                    UpdateVisibility();
                }
            }
            else if (action == L"Save_Click")
                co_await Save();
            else if (action == L"SaveFolder_Click")
                co_await Save(false, true);
            else if (action == L"Overwrite_Click")
                co_await Save(true);
            else if (action == L"ExportModel_Click")
            {
                auto choice = Get<ComboBox>(L"ClothingVisibility").SelectedIndex();
                auto clothing =
                    choice == 0 ? CharacterEditSchema::AutoClothingState(
                                      Category(),
                                      to_string(unbox_value_or<hstring>(Get<ListView>(L"Groups").SelectedItem(), L"")))
                                : choice - 1;
                EndGesture();
                owner->ExportModels({{session_.Card, session_.Document, static_cast<int64_t>(session_.Revision)}},
                                    clothing, Get<ToggleSwitch>(L"AccessoryVisibility").IsOn());
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
    }

    IAsyncOperation<bool> NativeEditor::Save(bool overwrite, bool chooseFolder)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || saving_ || stopped_ || owner->busy_ || owner->dialogOpen_ || owner->closing_)
            co_return false;
        if (project_)
        {
            try
            {
                EndGesture();
                project_->Save();
                UpdateState();
                if (owner->creation_)
                    owner->creation_->Saved();
                co_return true;
            }
            catch (const std::exception &e)
            {
                Get<TextBlock>(L"EditorMessage").Text(to_hstring(e.what()));
                if (owner->creation_)
                    owner->creation_->Saved(e.what());
                co_return false;
            }
        }
        std::optional<fs::path> destination;
        if (chooseFolder)
        {
            auto folder = co_await owner->PickFolder(L"选择编辑卡片保存目录");
            if (folder.empty())
                co_return false;
            destination = CardFileService::UniqueDestination(
                fs::path(folder.c_str()), Path(Utf8(Path(session_.Card.FileName()).stem()) + "-编辑.png"));
        }
        if (overwrite &&
            !(co_await owner->Confirm(L"备份后覆盖原卡", L"将校验并备份原卡，然后写入当前修改。原 PNG 封面保持完整。")))
            co_return false;
        if (saving_ || stopped_ || owner->busy_ || owner->closing_)
            co_return false;
        EndGesture();
        auto document = session_.Document;
        auto card = session_.Card;
        auto revision = session_.Revision;
        saving_ = true;
        owner->model_->Put(L"WorkLabel", L"正在校验并保存人物卡…");
        owner->model_->Put(L"ProgressIndeterminate", true);
        owner->SetBusy(true);
        UpdateState();
        apartment_context ui;
        std::optional<CardEditSaveResult> result;
        std::string error;
        co_await resume_background();
        try
        {
            CardEditService service(*owner->repository_);
            result = service.Save(document, card, overwrite, destination);
        }
        catch (std::exception const &e)
        {
            error = e.what();
        }
        co_await ui;
        saving_ = false;
        owner->SetBusy(false);
        if (result)
        {
            session_.AcceptSaved(result->Card, result->Document, revision);
            if (owner->preview_)
                owner->preview_->SavedCard(session_.Card);
            owner->Reload();
            Get<TextBlock>(L"EditorMessage").Text(to_hstring("已保存：" + result->Card.Path));
        }
        else
            Get<TextBlock>(L"EditorMessage").Text(to_hstring("保存未完成：" + error));
        UpdateState();
        if (owner->closing_)
        {
            if (result)
            {
                // 先让 CloseEditor 或 RestoreBackup 等待本次保存完成，再关闭窗口；
                // 嵌套调用 CloseEditor 会重置 editor_。
                auto weakOwner = owner->weak_from_this();
                owner->dispatcher_.TryEnqueue(
                    [weakOwner]
                    {
                        if (auto owner = weakOwner.lock())
                            if (owner->closing_)
                            {
                                owner->closing_ = false;
                                owner->RequestClose();
                            }
                    });
            }
            else
            {
                CancelProcessShutdown();
                owner->closing_ = false;
            }
        }
        co_return result.has_value();
    }

    fire_and_forget NativeWindow::OpenEditor(CardRecord card)
    {
        auto lifetime = shared_from_this();
        if (busy_ || editor_ || closing_)
            co_return;
        SetBusy(true);
        apartment_context ui;
        std::optional<CharacterDocument> document;
        std::string error;
        co_await resume_background();
        try
        {
            document = CharacterDocument::Open(Path(card.Path));
        }
        catch (std::exception const &e)
        {
            error = e.what();
        }
        co_await ui;
        if (closing_)
        {
            SetBusy(false);
            RequestClose();
            co_return;
        }
        if (!document)
        {
            SetBusy(false);
            Report(error);
            co_return;
        }
        try
        {
            // 先停止渲染并解除旧面板的交换链，再创建和显示编辑器。
            // 驱动清理可能需要 UI 消息循环，不能在 Initialize 中同步析构旧预览。
            auto previous = std::exchange(preview_, nullptr);
            if (previous)
                co_await previous->StopAsync();
            if (!closing_)
                editor_ = NativeEditor::Open(lifetime, card, std::move(*document));
        }
        catch (std::exception const &e)
        {
            error = e.what();
        }
        catch (hresult_error const &e)
        {
            error = to_string(e.message());
        }
        if (!editor_)
        {
            // 初始化失败或等待期间关闭窗口时，回收部分创建的预览及编辑器视图。
            auto incomplete = std::exchange(preview_, nullptr);
            if (incomplete)
                co_await incomplete->StopAsync();
            Control<ContentControl>(L"EditorHost").Content(nullptr);
            model_->Put(L"IsEditing", false);
            model_->Put(L"IsPreview", false);
        }
        SetBusy(false);
        if (closing_)
            RequestClose();
        else if (!error.empty())
            Report(error);
    }

    IAsyncOperation<bool> NativeWindow::CloseEditor()
    {
        auto lifetime = shared_from_this();
        if (!editor_)
            co_return true;
        auto editor = editor_;
        if (busy_ || editor->Saving() || dialogOpen_)
            co_return false;
        if (editor->IsDirty())
        {
            dialogOpen_ = true;
            ContentDialog dialog;
            dialog.XamlRoot(root_.XamlRoot());
            dialog.Title(box_value(L"保留未保存修改？"));
            dialog.Content(box_value(creation_ ? L"保存创作工程、放弃本次修改，或取消并继续编辑。"
                                               : L"保存为新卡、放弃本次修改，或取消并继续编辑。"));
            dialog.PrimaryButtonText(L"保存");
            dialog.SecondaryButtonText(L"放弃修改");
            dialog.CloseButtonText(L"取消");
            dialog.DefaultButton(ContentDialogButton::Close);
            auto result = co_await dialog.ShowAsync();
            dialogOpen_ = false;
            if (result == ContentDialogResult::None)
                co_return false;
            if (closePrompt_)
                BeginProcessShutdown();
            if (result == ContentDialogResult::Primary && !(co_await editor->Save()))
                co_return false;
        }
        if (closePrompt_)
            BeginProcessShutdown();
        if (creation_)
        {
            co_await creation_->Stop();
            creation_.reset();
        }
        editor_->Stop();
        if (preview_)
        {
            SetBusy(true);
            co_await preview_->StopAsync();
            SetBusy(false);
        }
        preview_.reset();
        model_->Put(L"IsPreview", false);
        Control<ContentControl>(L"EditorHost").Content(nullptr);
        editor_.reset();
        model_->Put(L"IsEditing", false);
        if (!creationKind_.empty())
        {
            auto kind = std::exchange(creationKind_, {});
            if (!closing_ && !closePrompt_)
                ShowProjects(std::move(kind));
        }
        co_return true;
    }

    fire_and_forget NativeWindow::RequestClose()
    {
        auto lifetime = shared_from_this();
        if (closePrompt_ || busy_)
        {
            installRequested_ = false;
            co_return;
        }
        // 不给保存/放弃/取消对话框计时；用户决定离开后再启动清理期限。
        CancelProcessShutdown();
        closePrompt_ = true;
        try
        {
            if (co_await CloseEditor())
            {
                LaunchPendingUpdate();
                BeginProcessShutdown();
                closing_ = true;
                CancelTemporaryPreview();
                if (preview_)
                    co_await preview_->StopAsync();
                ClosePreview();
                try
                {
                    double scale = root_.XamlRoot() ? root_.XamlRoot().RasterizationScale() : 1;
                    auto size = window_.AppWindow().Size();
                    settings_.WindowWidth = size.Width / scale;
                    settings_.WindowHeight = size.Height / scale;
                    settings_.Save(dataDirectory_);
                }
                catch (std::exception const &e)
                {
                    Report(e.what());
                }
                closeReady_ = true;
                Shutdown();
                window_.Close();
            }
            else
            {
                CancelProcessShutdown();
                closing_ = false;
            }
        }
        catch (hresult_error const &e)
        {
            // 已决定退出后，清理失败也必须结束；不能撤销兜底并留下失去业务资源的窗口。
            if (closing_ || closeReady_)
                FinishProcess(static_cast<DWORD>(e.code()));
            CancelProcessShutdown();
            closing_ = false;
            Report(to_string(e.message()));
        }
        catch (std::exception const &e)
        {
            if (closing_ || closeReady_)
                FinishProcess(ERROR_PROCESS_ABORTED);
            CancelProcessShutdown();
            closing_ = false;
            Report(e.what());
        }
        installRequested_ = false;
        closePrompt_ = false;
    }
} // 命名空间 hs2::ui
