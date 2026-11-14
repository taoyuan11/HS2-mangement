#include "Creation/NativeCreation.h"
#include "Editor/NativeEditor.h"
#include "Preview/NativePreview.h"
#include <microsoft.ui.xaml.media.dxinterop.h>
#include <Shobjidl.h>
#include <algorithm>
#include <winrt/Windows.Globalization.NumberFormatting.h>
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    using namespace creation;

    std::shared_ptr<NativeCreation> NativeCreation::Open(std::shared_ptr<NativeWindow> owner,
                                                         std::shared_ptr<CreationProject> project,
                                                         std::shared_ptr<CreationService> service)
    {
        auto self = std::shared_ptr<NativeCreation>(new NativeCreation);
        self->owner_ = owner;
        self->project_ = std::move(project);
        self->service_ = std::move(service);
        self->Initialize();
        return self;
    }

    void NativeCreation::Initialize()
    {
        auto owner = owner_.lock();
        owner->creationKind_ = project_->State().Kind;
        CardRecord card;
        card.Name = project_->State().Name;
        card.Sex = project_->State().Character->Sex();
        card.Path = Utf8(project_->State().Character->SourcePath());
        card.Id = project_->Id();
        owner->editor_ = NativeEditor::Open(owner, card, *project_->State().Character);
        owner->editor_->BindProject(project_);
        auto editorRoot = owner->editor_->Root();
        owner->Control<ContentControl>(L"EditorHost").Content(nullptr);
        root_ = owner->LoadView(L"CreationView");
        root_.DataContext(owner->model_.as<Object>());
        Get<ContentControl>(L"CharacterHost").Content(editorRoot);
        owner->Control<ContentControl>(L"EditorHost").Content(root_);
        panel_ = Get<SwapChainPanel>(L"Viewport");
        for (auto name : {L"X", L"Y", L"Z", L"Snap", L"Radius"})
        {
            winrt::Windows::Globalization::NumberFormatting::DecimalFormatter format;
            format.FractionDigits(3);
            format.IntegerDigits(1);
            winrt::Windows::Globalization::NumberFormatting::IncrementNumberRounder rounder;
            rounder.Increment(.001);
            format.NumberRounder(rounder);
            Get<NumberBox>(name).NumberFormatter(format);
        }
        std::weak_ptr<NativeCreation> weak = shared_from_this();
        for (auto name :
             {L"Close",     L"CharacterTab", L"ModelTab",         L"Save", L"Undo",     L"Redo",           L"Cover",
              L"Build",     L"Cancel",       L"AddAsset",         L"Hide", L"Lock",     L"ObjectRename",   L"Frame",
              L"Transform", L"Half",         L"RefreshReference", L"Fit",  L"ResetFit", L"ChangeTemplate", L"Color",
              L"Material"})
            Get<Primitives::ButtonBase>(name).Click(
                [weak, action = to_string(hstring(name))](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->Action(action);
                });
        for (auto name : {L"Rename", L"ExportCard", L"ExportMod", L"ExportPackage", L"Install", L"Rollback"})
            Get<MenuFlyoutItem>(name).Click(
                [weak, action = to_string(hstring(name))](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->Action(action);
                });
        auto select = [weak](auto &&, auto &&)
        {
            if (auto self = weak.lock())
                if (!self->refreshing_)
                {
                    self->selection_.Mode =
                        static_cast<SelectionMode>(self->Get<ComboBox>(L"Selection").SelectedIndex());
                    self->selection_.Ids.clear();
                    self->Viewport();
                    self->PaintView();
                }
        };
        Get<ComboBox>(L"Selection").SelectionChanged(select);
        Get<ComboBox>(L"Assets").SelectionChanged(
            [weak](auto &&, auto &&)
            {
                if (auto s = weak.lock())
                    if (!s->refreshing_)
                    {
                        int i = s->Get<ComboBox>(L"Assets").SelectedIndex();
                        if (i >= 0 && i < int(s->assetIds_.size()))
                        {
                            s->asset_ = s->assetIds_[i];
                            s->object_.clear();
                            s->objectSelection_.clear();
                            s->Refresh();
                        }
                    }
            });
        Get<ListView>(L"Objects")
            .SelectionChanged(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                        if (!s->refreshing_)
                        {
                            auto list = s->Get<ListView>(L"Objects");
                            s->objectSelection_.clear();
                            for (auto item : list.SelectedItems())
                            {
                                uint32_t index = 0;
                                if (list.Items().IndexOf(item, index) && index < s->objectIds_.size())
                                    s->objectSelection_.insert(s->objectIds_[index]);
                            }
                            if (!s->objectSelection_.contains(s->object_))
                                s->object_ = s->objectSelection_.empty() ? "" : *s->objectSelection_.begin();
                            s->selection_.Ids.clear();
                            s->Refresh(false);
                        }
                });
        Get<ComboBox>(L"Textures")
            .SelectionChanged(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                        if (!s->refreshing_)
                        {
                            int i = s->Get<ComboBox>(L"Textures").SelectedIndex();
                            if (i >= 0 && i < int(s->textureIds_.size()))
                            {
                                s->texture_ = s->textureIds_[i];
                                s->layer_ = 0;
                                s->Refresh();
                            }
                        }
                });
        Get<ComboBox>(L"Layers").SelectionChanged(
            [weak](auto &&, auto &&)
            {
                if (auto s = weak.lock())
                    if (!s->refreshing_)
                    {
                        s->layer_ = s->Get<ComboBox>(L"Layers").SelectedIndex();
                        s->Refresh(false);
                    }
            });
        Get<Slider>(L"Opacity")
            .ValueChanged(
                [weak](auto &&, auto const &args)
                {
                    if (auto s = weak.lock())
                        if (!s->refreshing_ && s->SelectedTexture() && s->layer_ >= 0)
                        {
                            try
                            {
                                s->project_->Edit("图层透明度",
                                                  [&](CreationState &state)
                                                  {
                                                      s->Texture(state).Image.Layers.at(s->layer_).Opacity =
                                                          float(args.NewValue());
                                                  });
                            }
                            catch (const std::exception &e)
                            {
                                s->Message(e.what());
                            }
                        }
                });
        Get<Slider>(L"Opacity")
            .AddHandler(UIElement::PointerPressedEvent(),
                        box_value(Input::PointerEventHandler(
                            [weak](auto &&, auto &&)
                            {
                                if (auto s = weak.lock())
                                    s->project_->BeginGesture();
                            })),
                        true);
        Get<Slider>(L"Opacity")
            .AddHandler(UIElement::PointerReleasedEvent(),
                        box_value(Input::PointerEventHandler(
                            [weak](auto &&, auto &&)
                            {
                                if (auto s = weak.lock())
                                    s->project_->EndGesture();
                            })),
                        true);
        Get<Slider>(L"Opacity")
            .AddHandler(UIElement::PointerCaptureLostEvent(),
                        box_value(Input::PointerEventHandler(
                            [weak](auto &&, auto &&)
                            {
                                if (auto s = weak.lock())
                                    s->project_->EndGesture();
                            })),
                        true);
        for (auto name : {L"Checker", L"Weights", L"UvVisible"})
            Get<Primitives::ToggleButton>(name).Click(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                    {
                        auto show = s->Get<Primitives::ToggleButton>(L"UvVisible").IsChecked().GetBoolean();
                        s->Get<ColumnDefinition>(L"UvColumn")
                            .Width({show ? 1.0 : 0.0, show ? GridUnitType::Star : GridUnitType::Pixel});
                        s->Viewport();
                        s->PaintView();
                    }
                });
        Get<CheckBox>(L"Reference")
            .Click(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                        s->Viewport();
                });
        Get<ComboBox>(L"Clothing")
            .SelectionChanged(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                        s->Viewport();
                });
        Get<ComboBox>(L"Bones").SelectionChanged(
            [weak](auto &&, auto &&)
            {
                if (auto s = weak.lock())
                    s->Viewport();
            });
        Get<Grid>(L"ViewportHost")
            .SizeChanged(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                        s->Viewport();
                });
        panel_.CompositionScaleChanged(
            [weak](auto &&, auto &&)
            {
                if (auto s = weak.lock())
                    s->Viewport();
            });
        Get<Grid>(L"UvHost").SizeChanged(
            [weak](auto &&, auto &&)
            {
                if (auto s = weak.lock())
                    s->PaintView();
            });
        Get<Grid>(L"UvArea").SizeChanged(
            [weak](auto &&, auto &&)
            {
                if (auto s = weak.lock())
                    s->PaintView();
            });
        auto queue = owner->dispatcher_;
        viewport_ = std::make_unique<CreationViewport>(
            [weak, queue](ComPtr<IDXGISwapChain2> chain)
            {
                queue.TryEnqueue(
                    [weak, chain = std::move(chain)]
                    {
                        if (auto s = weak.lock())
                            if (!s->stopped_)
                            {
                                s->chain_ = chain;
                                s->panel_.as<ISwapChainPanelNative>()->SetSwapChain(chain.Get());
                            }
                    });
            },
            [weak, queue](std::string error)
            {
                queue.TryEnqueue(
                    [weak, error]
                    {
                        if (auto s = weak.lock())
                            if (!s->stopped_)
                                s->Message(error);
                    });
            });
        viewport_->Visible(false);
        project_->Changed = [weak]
        {
            if (auto s = weak.lock())
            {
                s->fit_ = {};
                if (auto o = s->owner_.lock())
                    o->editor_->SyncProject();
                s->Refresh(false);
            }
        };
        autosave_ = queue.CreateTimer();
        autosave_.Interval(std::chrono::seconds(20));
        autosave_.Tick(
            [weak](auto &&, auto &&)
            {
                if (auto s = weak.lock())
                    s->AutoSave();
            });
        autosave_.Start();
        Menus();
        BindPointers();
        Refresh();
        Action(project_->State().Kind == "mod" ? "ModelTab" : "CharacterTab");
    }

    void NativeCreation::Menus()
    {
        std::weak_ptr<NativeCreation> weak = shared_from_this();
        auto menu = [&](const wchar_t *name, std::initializer_list<std::pair<const wchar_t *, const char *>> items)
        {
            MenuFlyout fly;
            fly.Placement(std::wstring_view(name) == L"TextureMenu" || std::wstring_view(name) == L"BoneMenu"
                              ? Primitives::FlyoutPlacementMode::TopEdgeAlignedLeft
                              : Primitives::FlyoutPlacementMode::BottomEdgeAlignedLeft);
            Style presenter{xaml_typename<MenuFlyoutPresenter>()};
            presenter.Setters().Append(Setter(FrameworkElement::MaxHeightProperty(), box_value(420.)));
            fly.MenuFlyoutPresenterStyle(presenter);
            for (auto [label, action] : items)
            {
                MenuFlyoutItem item;
                item.Text(label);
                item.Click(
                    [weak, action = std::string(action)](auto &&, auto &&)
                    {
                        if (auto s = weak.lock())
                            s->Action(action);
                    });
                fly.Items().Append(item);
            }
            Get<DropDownButton>(name).Flyout(fly);
        };
        menu(L"CreateMenu", {{L"立方体", "primitive:box"},
                             {L"球体", "primitive:sphere"},
                             {L"圆柱", "primitive:cylinder"},
                             {L"平面", "primitive:plane"},
                             {L"曲线发片", "primitive:ribbon"},
                             {L"曲线管束", "primitive:tube"},
                             {L"编辑曲线控制点", "primitive:edit_curve"},
                             {L"从身体表面生成服装", "primitive:body_surface"},
                             {L"导入 FBX / OBJ / GLB / GLTF", "ImportModel"}});
        menu(L"TopologyMenu", {{L"挤出", "mesh:extrude"},
                               {L"内插", "mesh:inset"},
                               {L"切分", "mesh:cut"},
                               {L"环切", "mesh:loop_cut"},
                               {L"倒角", "mesh:bevel"},
                               {L"细分", "mesh:subdivide"},
                               {L"设置面材质槽", "mesh:material"},
                               {L"清除雕刻遮罩", "mesh:mask_clear"},
                               {L"焊接", "mesh:weld"},
                               {L"删除", "mesh:delete"},
                               {L"溶解", "mesh:dissolve"},
                               {L"补面", "mesh:fill"},
                               {L"镜像", "mesh:mirror"},
                               {L"合并选中对象", "Merge"},
                               {L"分离选中面", "Separate"},
                               {L"翻转法线", "mesh:flip_normals"},
                               {L"平滑法线", "mesh:smooth_normals"},
                               {L"平面法线", "mesh:flat_normals"}});
        menu(L"UvMenu", {{L"标记接缝", "mesh:uv_seam"},
                         {L"清除接缝", "mesh:uv_unseam"},
                         {L"平面投影", "mesh:uv_project"},
                         {L"柱面投影", "mesh:uv_cylinder"},
                         {L"球面投影", "mesh:uv_sphere"},
                         {L"按接缝展开", "mesh:uv_unwrap"},
                         {L"松弛", "mesh:uv_relax"},
                         {L"打包 UV 岛", "mesh:uv_pack"},
                         {L"导出 UV 布局", "ExportUv"}});
        menu(L"BoneMenu", {{L"设为配饰挂点", "bone:parent"},
                           {L"新增骨骼", "bone:add"},
                           {L"编辑绑定姿态", "bone:pose"},
                           {L"自动权重转移", "bone:transfer"},
                           {L"将选区绑定到当前骨骼", "mesh:weight_assign"},
                           {L"平滑选中权重", "mesh:weight_smooth"},
                           {L"归一化权重", "mesh:weight_normalize"},
                           {L"镜像权重", "mesh:weight_mirror"},
                           {L"锁定／解锁骨骼", "bone:lock"},
                           {L"启用／关闭动态链", "bone:dynamic"},
                           {L"动态链参数", "bone:dynamic_settings"}});
        menu(L"TextureMenu", {{L"新建材质贴图", "texture:new"},
                              {L"新建脸部覆盖", "texture:face"},
                              {L"新建身体覆盖", "texture:body"},
                              {L"新建服装覆盖", "texture:clothes"},
                              {L"新建服装遮罩", "texture:mask"},
                              {L"导入图片", "texture:import"},
                              {L"添加空白图层", "texture:layer"},
                              {L"导入图片为图层", "texture:import_layer"},
                              {L"锁定／解锁图层", "texture:lock"},
                              {L"显隐图层", "texture:visible"},
                              {L"删除当前图层", "texture:delete_layer"},
                              {L"导出贴图 PNG", "texture:export"}});
    }

    CreationAsset &NativeCreation::Asset(CreationState &s) const
    {
        auto it = std::find_if(s.Assets.begin(), s.Assets.end(),
                               [&](auto &a)
                               {
                                   return a.Id == asset_;
                               });
        if (it == s.Assets.end())
            throw std::runtime_error("请先添加或选择素材");
        return *it;
    }

    CreationObject &NativeCreation::ObjectOf(CreationState &s) const
    {
        auto &a = Asset(s);
        auto it = std::find_if(a.Objects.begin(), a.Objects.end(),
                               [&](auto &o)
                               {
                                   return o.Id == object_;
                               });
        if (it == a.Objects.end())
            throw std::runtime_error("请先选择网格对象");
        if (it->Locked)
            throw std::runtime_error("对象已锁定");
        return *it;
    }

    CreationTexture &NativeCreation::Texture(CreationState &s) const
    {
        for (auto &t : s.Overlays)
            if (t.Id == texture_)
                return t;
        for (auto &a : s.Assets)
            for (auto &t : a.Textures)
                if (t.Id == texture_)
                    return t;
        throw std::runtime_error("请先选择贴图");
    }

    const CreationObject *NativeCreation::SelectedObject() const
    {
        for (auto &a : project_->State().Assets)
            for (auto &o : a.Objects)
                if (o.Id == object_)
                    return &o;
        return nullptr;
    }

    const CreationTexture *NativeCreation::SelectedTexture() const
    {
        for (auto &t : project_->State().Overlays)
            if (t.Id == texture_)
                return &t;
        for (auto &a : project_->State().Assets)
            for (auto &t : a.Textures)
                if (t.Id == texture_)
                    return &t;
        return nullptr;
    }

    bool NativeCreation::ObjectSelected(const CreationObject &object) const
    {
        return selection_.Mode == SelectionMode::Object
                   ? (objectSelection_.contains(object.Id) || (objectSelection_.empty() && object.Id == object_))
                   : object.Id == object_;
    }

    Vector3 NativeCreation::SelectionCenter() const
    {
        Vector3 center{};
        size_t count = 0;
        for (auto &asset : project_->State().Assets)
            if (asset.Id == asset_)
                for (auto &object : asset.Objects)
                    if (ObjectSelected(object) && !object.Hidden && !object.Locked)
                    {
                        auto ids = object.Mesh.SelectedVertices(selection_);
                        for (auto id : ids)
                        {
                            center.x += object.Mesh.Vertices.at(id).Position.x;
                            center.y += object.Mesh.Vertices.at(id).Position.y;
                            center.z += object.Mesh.Vertices.at(id).Position.z;
                            ++count;
                        }
                    }
        if (count)
        {
            center.x /= float(count);
            center.y /= float(count);
            center.z /= float(count);
        }
        return center;
    }

    void NativeCreation::Message(std::string text)
    {
        if (!stopped_)
            Get<TextBlock>(L"Message").Text(to_hstring(text));
    }

    void NativeCreation::Saved(std::string error)
    {
        Refresh(false);
        Message(error.empty() ? "工程已保存" : "工程保存失败：" + error);
    }

    void NativeCreation::Refresh(bool lists)
    {
        if (stopped_)
            return;
        refreshing_ = true;
        Get<TextBlock>(L"Title").Text(to_hstring(project_->State().Name + " · 创作工作台"));
        Get<TextBlock>(L"State").Text(to_hstring(std::string(project_->IsDirty() ? "● 未保存" : "已保存") + " · r" +
                                                 std::to_string(project_->Revision())));
        Get<Button>(L"Cover").Content(
            box_value(project_->State().CoverSettings.value("mode", "") == "import" ? L"封面：导入图片"
                      : project_->State().CoverToken == project_->State().Token     ? L"封面：当前版本"
                                                                                    : L"封面：待重新拍摄"));
        Get<Button>(L"Undo").IsEnabled(project_->CanUndo());
        Get<Button>(L"Redo").IsEnabled(project_->CanRedo());
        if (lists)
        {
            auto previousAsset = asset_;
            auto assets = Get<ComboBox>(L"Assets");
            assets.Items().Clear();
            assetIds_.clear();
            for (auto &a : project_->State().Assets)
            {
                assetIds_.push_back(a.Id);
                assets.Items().Append(box_value(to_hstring(a.Name)));
            }
            if (std::find(assetIds_.begin(), assetIds_.end(), asset_) == assetIds_.end())
                asset_ = assetIds_.empty() ? "" : assetIds_[0];
            assets.SelectedIndex(
                asset_.empty() ? -1 : int(std::find(assetIds_.begin(), assetIds_.end(), asset_) - assetIds_.begin()));
            auto objects = Get<ListView>(L"Objects");
            objects.Items().Clear();
            objectIds_.clear();
            auto bones = Get<ComboBox>(L"Bones");
            auto previous = unbox_value_or<hstring>(bones.SelectedItem(), L"");
            bones.Items().Clear();
            std::set<std::string> names;
            if (reference_)
                for (auto &b : reference_->Bones)
                    names.insert(b.Name);
            for (auto &a : project_->State().Assets)
                if (a.Id == asset_)
                {
                    Get<TextBlock>(L"HalfStatus")
                        .Text(a.IndependentHalfState ? L"半脱：独立网格" : L"半脱：复用完整网格");
                    for (auto &b : a.Bones)
                        names.insert(b.Name);
                    for (auto &o : a.Objects)
                    {
                        objectIds_.push_back(o.Id);
                        objects.Items().Append(
                            box_value(to_hstring(std::string(o.Hidden ? "○ " : "● ") + (o.Locked ? "🔒 " : "") +
                                                 o.Mesh.Name + (o.HalfState ? " · 半脱" : ""))));
                    }
                }
            for (auto &name : names)
                bones.Items().Append(box_value(to_hstring(name)));
            if (!previous.empty() && names.contains(to_string(previous)))
                bones.SelectedItem(box_value(previous));
            else if (!names.empty())
                bones.SelectedIndex(0);
            if ((!object_.empty() || previousAsset != asset_) &&
                std::find(objectIds_.begin(), objectIds_.end(), object_) == objectIds_.end())
                object_ = objectIds_.empty() ? "" : objectIds_[0];
            std::erase_if(objectSelection_,
                          [&](const auto &id)
                          {
                              return std::find(objectIds_.begin(), objectIds_.end(), id) == objectIds_.end();
                          });
            if (!object_.empty() && !objectSelection_.contains(object_))
                objectSelection_ = {object_};
            for (size_t index = 0; index < objectIds_.size(); ++index)
                if (objectSelection_.contains(objectIds_[index]))
                    objects.SelectedItems().Append(objects.Items().GetAt(uint32_t(index)));
            auto textures = Get<ComboBox>(L"Textures");
            textures.Items().Clear();
            textureIds_.clear();
            textureIds_.push_back("");
            textures.Items().Append(box_value(L"网格 UV（无绘制贴图）"));
            auto append = [&](auto &t)
            {
                textureIds_.push_back(t.Id);
                textures.Items().Append(box_value(to_hstring(t.Name)));
            };
            for (auto &a : project_->State().Assets)
                if (a.Id == asset_)
                    for (auto &t : a.Textures)
                        append(t);
            for (auto &t : project_->State().Overlays)
                append(t);
            if (std::find(textureIds_.begin(), textureIds_.end(), texture_) == textureIds_.end())
                texture_.clear();
            textures.SelectedIndex(
                int(std::find(textureIds_.begin(), textureIds_.end(), texture_) - textureIds_.begin()));
            auto layers = Get<ComboBox>(L"Layers");
            layers.Items().Clear();
            if (auto t = SelectedTexture())
                for (auto &l : t->Image.Layers)
                    layers.Items().Append(
                        box_value(to_hstring(l.Name + (l.Locked ? " · 锁定" : "") + (!l.Visible ? " · 隐藏" : ""))));
            layer_ = std::clamp(layer_, 0, std::max(0, int(layers.Items().Size()) - 1));
            if (layers.Items().Size())
                layers.SelectedIndex(layer_);
        }
        if (auto t = SelectedTexture())
            if (layer_ >= 0 && layer_ < int(t->Image.Layers.size()))
                Get<Slider>(L"Opacity").Value(t->Image.Layers[layer_].Opacity);
        refreshing_ = false;
        Viewport();
        PaintView();
    }

    void NativeCreation::Viewport()
    {
        if (!viewport_ || stopped_)
            return;
        auto host = Get<Grid>(L"ViewportHost");
        float scale = host.XamlRoot() ? float(host.XamlRoot().RasterizationScale()) : 1;
        CreationView view;
        view.State = fit_.State ? fit_.State : project_->Snapshot();
        view.Reference = fit_.Reference ? fit_.Reference : reference_;
        view.Object = object_;
        view.Objects = objectSelection_;
        view.Selection = selection_;
        view.Camera = camera_;
        view.Pivot = SelectionCenter();
        view.Checker = Get<Primitives::ToggleButton>(L"Checker").IsChecked().GetBoolean();
        view.Weights = Get<Primitives::ToggleButton>(L"Weights").IsChecked().GetBoolean();
        view.ShowReference = Get<CheckBox>(L"Reference").IsChecked().GetBoolean();
        view.HalfState = Get<ComboBox>(L"Clothing").SelectedIndex() == 1;
        view.Clothing = Get<ComboBox>(L"Clothing").SelectedIndex();
        view.Bone = to_string(unbox_value_or<hstring>(Get<ComboBox>(L"Bones").SelectedItem(), L""));
        view.Texture = texture_;
        viewport_->Update(std::move(view), {std::max(1, int(host.ActualWidth() * scale)),
                                            std::max(1, int(host.ActualHeight() * scale)), scale});
    }

    void NativeCreation::Cancel()
    {
        cancellation_.request_stop();
    }

    IAsyncAction NativeCreation::Stop()
    {
        auto lifetime = shared_from_this();
        stopped_ = true;
        Cancel();
        autosave_.Stop();
        project_->Changed = {};
        while (working_ || savingRecovery_)
            co_await resume_after(std::chrono::milliseconds(20));
        apartment_context ui;
        co_await resume_background();
        if (viewport_)
            viewport_->Stop();
        co_await ui;
        panel_.as<ISwapChainPanelNative>()->SetSwapChain(nullptr);
        chain_.Reset();
        viewport_.reset();
    }

    fire_and_forget NativeCreation::AutoSave()
    {
        auto lifetime = shared_from_this();
        if (stopped_ || pointer_ || savingRecovery_ || !project_->IsDirty())
            co_return;
        savingRecovery_ = true;
        auto state = project_->Snapshot();
        auto revision = project_->Revision();
        apartment_context ui;
        PreparedProjectSave saved;
        std::string error;
        co_await resume_background();
        try
        {
            saved = project_->PrepareSave(state, revision);
        }
        catch (const std::exception &e)
        {
            error = e.what();
        }
        catch (...)
        {
            error = "无法保存恢复快照";
        }
        co_await ui;
        savingRecovery_ = false;
        if (error.empty())
        {
            try
            {
                project_->CommitSave(saved, true);
            }
            catch (const std::exception &e)
            {
                error = e.what();
            }
        }
        if (!stopped_ && !error.empty() && project_->IsDirty())
            Message("恢复快照保存失败：" + error);
        else if (!stopped_ && error.empty() &&
                 to_string(Get<TextBlock>(L"Message").Text()).starts_with("恢复快照保存失败："))
            Message("恢复快照已保存");
    }

    fire_and_forget NativeCreation::Job(std::string label, std::function<std::function<void()>(std::stop_token)> work)
    {
        auto lifetime = shared_from_this();
        if (working_ || stopped_)
        {
            Message("请等待当前任务完成，或先取消任务");
            co_return;
        }
        working_ = true;
        cancellation_ = std::stop_source{};
        auto token = cancellation_.get_token();
        Get<Button>(L"Cancel").IsEnabled(true);
        Get<Button>(L"Cancel").Visibility(Visibility::Visible);
        Message(label);
        apartment_context ui;
        std::function<void()> finish;
        std::string error;
        co_await resume_background();
        try
        {
            finish = work(token);
        }
        catch (const std::exception &e)
        {
            error = e.what();
        }
        catch (const hresult_error &e)
        {
            error = to_string(e.message());
        }
        catch (...)
        {
            error = "创作任务遇到未知错误";
        }
        co_await ui;
        working_ = false;
        if (stopped_)
            co_return;
        Get<Button>(L"Cancel").IsEnabled(false);
        Get<Button>(L"Cancel").Visibility(Visibility::Collapsed);
        if (error.empty())
        {
            try
            {
                Message(label + "完成");
                if (finish)
                    finish();
                Refresh();
            }
            catch (const std::exception &e)
            {
                Message(e.what());
            }
        }
        else
            Message(token.stop_requested() ? "任务已取消，已提交的工程与上次成品保留" : error);
    }

    IAsyncOperation<bool> NativeCreation::Dialog(hstring title, UIElement content)
    {
        auto owner = owner_.lock();
        if (!owner || owner->dialogOpen_)
            co_return false;
        owner->dialogOpen_ = true;
        ContentDialog dialog;
        dialog.XamlRoot(root_.XamlRoot());
        dialog.RequestedTheme(root_.ActualTheme());
        dialog.Title(box_value(title));
        dialog.Content(content);
        dialog.PrimaryButtonText(L"确定");
        dialog.CloseButtonText(L"取消");
        dialog.DefaultButton(ContentDialogButton::Primary);
        ContentDialogResult result = ContentDialogResult::None;
        try
        {
            result = co_await dialog.ShowAsync();
        }
        catch (...)
        {
            owner->dialogOpen_ = false;
            throw;
        }
        owner->dialogOpen_ = false;
        co_return result == ContentDialogResult::Primary;
    }

    IAsyncOperation<hstring> NativeCreation::PickFile(std::vector<hstring> extensions)
    {
        auto owner = owner_.lock();
        winrt::Windows::Storage::Pickers::FileOpenPicker picker;
        picker.as<IInitializeWithWindow>()->Initialize(owner->Handle());
        for (auto &ext : extensions)
            picker.FileTypeFilter().Append(ext);
        auto file = co_await picker.PickSingleFileAsync();
        co_return file ? file.Path() : L"";
    }
}
