#include "Creation/NativeCreation.h"
#include "Editor/NativeEditor.h"
#include "Preview/NativePreview.h"
#include "Animation/SimulationMath.h"
#include "Images/ImageFiles.h"
#include <sstream>
#include <iomanip>
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    using namespace creation;
    using namespace sim;

    void NativeCreation::Command(std::string op, Json args)
    {
        try
        {
            if (fit_.State)
                throw std::runtime_error("请先返回编辑姿态，再修改网格");
            if (op == "mask_clear")
            {
                project_->Edit("清除雕刻遮罩",
                               [&](CreationState &s)
                               {
                                   for (auto &[id, v] : ObjectOf(s).Mesh.Vertices)
                                   {
                                       v.Mask = 0;
                                       (void)id;
                                   }
                               });
                Refresh(false);
                return;
            }
            if (op == "uv_unseam")
            {
                op = "uv_seam";
                args["enabled"] = false;
            }
            if (op == "flip_normals" || op == "smooth_normals" || op == "flat_normals")
            {
                args["mode"] = op == "flip_normals" ? "flip" : op == "smooth_normals" ? "smooth" : "flat";
                op = "normal";
            }
            if (op.starts_with("weight_"))
            {
                args["bone"] = to_string(unbox_value_or<hstring>(Get<ComboBox>(L"Bones").SelectedItem(), L""));
                for (auto &a : project_->State().Assets)
                    if (a.Id == asset_)
                        args["locked_bones"] = a.LockedBones;
                if (op == "weight_smooth")
                {
                    auto object = SelectedObject();
                    if (!object)
                        throw std::runtime_error("请先选择网格");
                    args["center"] = asset::VectorJson(object->Mesh.Center(selection_));
                    args["radius"] = 10000;
                    args["strength"] = Get<Slider>(L"Strength").Value();
                }
            }
            if (op == "transform" && selection_.Mode == SelectionMode::Object)
                args["pivot"] = asset::VectorJson(SelectionCenter());
            project_->Edit(op,
                           [&](CreationState &state)
                           {
                               if (selection_.Mode != SelectionMode::Object)
                               {
                                   ObjectOf(state).Mesh.Execute({op, args}, selection_);
                                   return;
                               }
                               auto &objects = Asset(state).Objects;
                               if (std::none_of(objects.begin(), objects.end(),
                                                [&](auto &o)
                                                {
                                                    return ObjectSelected(o);
                                                }))
                                   throw std::runtime_error("请先选择对象");
                               for (auto &o : objects)
                                   if (ObjectSelected(o) && o.Locked)
                                       throw std::runtime_error("选中的对象已锁定");
                               if (op == "delete")
                                   std::erase_if(objects,
                                                 [&](auto &o)
                                                 {
                                                     return ObjectSelected(o);
                                                 });
                               else
                                   for (auto &o : objects)
                                       if (ObjectSelected(o))
                                           o.Mesh.Execute({op, args}, {});
                           });
            if (!op.starts_with("uv_") && !op.starts_with("weight_") && op != "transform" && op != "normal")
                selection_.Ids.clear();
            Refresh(false);
        }
        catch (const std::exception &e)
        {
            Message(e.what());
        }
    }

    fire_and_forget NativeCreation::Action(std::string action)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || stopped_)
            co_return;
        try
        {
            if (action == "Cancel")
            {
                Cancel();
                co_return;
            }
            if (action == "Close")
            {
                co_await owner->CloseEditor();
                co_return;
            }
            if (action == "CharacterTab" || action == "ModelTab")
            {
                bool model = action == "ModelTab";
                Get<Grid>(L"ModelHost").Visibility(model ? Visibility::Visible : Visibility::Collapsed);
                Get<ContentControl>(L"CharacterHost").Visibility(model ? Visibility::Collapsed : Visibility::Visible);
                viewport_->Visible(model);
                if (owner->preview_)
                    owner->preview_->Visible(!model);
                if (model && !reference_ && !working_)
                    Action("RefreshReference");
                Viewport();
                co_return;
            }
            if (action == "Save")
            {
                co_await owner->editor_->Save();
                Refresh(false);
                if (!project_->IsDirty())
                    Message("工程已保存");
                co_return;
            }
            if (action == "Undo" || action == "Redo")
            {
                if (action == "Undo")
                    project_->Undo();
                else
                    project_->Redo();
                selection_.Ids.clear();
                Refresh();
                co_return;
            }
            if (action == "AddAsset")
            {
                AddAsset();
                co_return;
            }
            if (action == "ChangeTemplate")
            {
                ChangeTemplate();
                co_return;
            }
            if (action == "ResetFit")
            {
                fit_ = {};
                Viewport();
                Message("已返回工程编辑姿态");
                co_return;
            }
            if (action == "Fit")
            {
                StackPanel content;
                content.Spacing(8);
                NumberBox time;
                time.Header(box_value(L"姿态采样时间（0–5 秒）"));
                time.Minimum(0);
                time.Maximum(5);
                time.Value(.5);
                content.Children().Append(time);
                ComboBox field;
                field.Header(box_value(L"检查的体型参数"));
                field.Items().Append(box_value(L"保持当前体型"));
                std::vector<std::string> paths{""};
                for (auto &f : CharacterEditSchema::Build(*project_->State().Character))
                    if (f.Path.starts_with("body.shapeValueBody["))
                    {
                        paths.push_back(f.Path);
                        field.Items().Append(box_value(to_hstring(f.Label)));
                    }
                field.SelectedIndex(0);
                content.Children().Append(field);
                Slider value;
                value.Header(box_value(L"体型参数值"));
                value.Minimum(0);
                value.Maximum(1);
                value.Value(.8);
                content.Children().Append(value);
                if (!(co_await Dialog(L"人物适配检查", content)))
                    co_return;
                auto state = project_->Snapshot();
                auto revision = project_->Revision();
                auto sample = float(time.Value()), bodyValue = float(value.Value());
                auto path = paths.at(field.SelectedIndex());
                Job("采样姿态与体型",
                    [lifetime, state, revision, sample, path, bodyValue](std::stop_token token)
                    {
                        auto fit = lifetime->service_->PreviewFit(*state, sample, path, bodyValue, token);
                        return [lifetime, revision, fit]
                        {
                            if (lifetime->project_->Revision() != revision)
                                throw std::runtime_error("工程已改变，旧适配预览未应用");
                            lifetime->fit_ = fit;
                            lifetime->Viewport();
                            lifetime->Message("正在查看适配结果；可旋转缩放，返回编辑姿态后继续修改");
                        };
                    });
                co_return;
            }
            if (action.starts_with("primitive:"))
            {
                Primitive(action.substr(10));
                co_return;
            }
            if (action.starts_with("bone:"))
            {
                BoneAction(action.substr(5));
                co_return;
            }
            if (action.starts_with("texture:"))
            {
                TextureAction(action.substr(8));
                co_return;
            }
            if (action.starts_with("mesh:"))
            {
                auto op = action.substr(5);
                Json args = Json::object();
                if (op == "material")
                {
                    NumberBox slot;
                    slot.Header(box_value(L"面材质槽（从 0 开始）"));
                    slot.Minimum(0);
                    slot.Maximum(63);
                    slot.Value(0);
                    if (!(co_await Dialog(L"设置面材质", slot)))
                        co_return;
                    args["slot"] = int(slot.Value());
                }
                if (op == "uv_cylinder" || op == "uv_sphere")
                {
                    args["mode"] = op == "uv_cylinder" ? "cylinder" : "sphere";
                    op = "uv_project";
                }
                if (op == "extrude" || op == "inset" || op == "bevel" || op == "weld" || op == "loop_cut")
                {
                    NumberBox value;
                    value.Header(box_value(op == "extrude" || op == "weld" ? L"距离"
                                           : op == "loop_cut"              ? L"切分位置（0–1）"
                                                                           : L"比例（0–1）"));
                    value.Value(op == "weld" ? .001 : op == "loop_cut" ? .5 : .1);
                    if (!(co_await Dialog(L"网格操作", value)))
                        co_return;
                    args[op == "extrude" || op == "weld" ? "distance"
                         : op == "loop_cut"              ? "ratio"
                                                         : "amount"] = value.Value();
                }
                if (op == "mirror")
                    args["axis"] = std::max(0, Get<ComboBox>(L"Axis").SelectedIndex() - 1);
                Command(op, args);
                co_return;
            }
            if (action == "Rename" || action == "ObjectRename")
            {
                auto value = co_await owner->Prompt(L"重命名", L"输入名称",
                                                    to_hstring(action == "Rename" ? project_->State().Name
                                                               : SelectedObject() ? SelectedObject()->Mesh.Name
                                                                                  : ""));
                if (value.empty())
                    co_return;
                project_->Edit("重命名",
                               [&](CreationState &s)
                               {
                                   if (action == "Rename")
                                       s.Name = to_string(value);
                                   else
                                       ObjectOf(s).Mesh.Name = to_string(value);
                               });
                Refresh();
                co_return;
            }
            if (action == "Hide" || action == "Lock")
            {
                project_->Edit(action,
                               [&](CreationState &s)
                               {
                                   auto &a = Asset(s);
                                   for (auto &o : a.Objects)
                                       if (ObjectSelected(o))
                                       {
                                           if (action == "Hide")
                                               o.Hidden = !o.Hidden;
                                           else
                                               o.Locked = !o.Locked;
                                       }
                               });
                Refresh();
                co_return;
            }
            if (action == "Frame" || action == "FrameReference")
            {
                if (action == "Frame" && SelectedObject())
                {
                    camera_.Center = SelectionCenter();
                    float radius = .1f;
                    for (auto &asset : project_->State().Assets)
                        if (asset.Id == asset_)
                            for (auto &object : asset.Objects)
                                if (ObjectSelected(object))
                                    for (auto &[id, v] : object.Mesh.Vertices)
                                    {
                                        radius = std::max(radius, sim::Length(v.Position - camera_.Center));
                                        (void)id;
                                    }
                    camera_.Scale = radius * 1.2f;
                }
                else if (reference_)
                {
                    Vector3 low{INFINITY, INFINITY, INFINITY}, high{-INFINITY, -INFINITY, -INFINITY};
                    for (auto &m : reference_->Meshes)
                        for (auto &v : m.Vertices)
                        {
                            low = {std::min(low.x, v.Position.x), std::min(low.y, v.Position.y),
                                   std::min(low.z, v.Position.z)};
                            high = {std::max(high.x, v.Position.x), std::max(high.y, v.Position.y),
                                    std::max(high.z, v.Position.z)};
                        }
                    if (std::isfinite(low.y))
                    {
                        camera_.Center = (low + high) * .5f;
                        camera_.Scale = sim::Length(high - low) * .6f;
                    }
                }
                Viewport();
                co_return;
            }
            if (action == "Transform")
            {
                auto mode = Get<ComboBox>(L"Tool").SelectedIndex();
                Json values = Json::array(
                    {Get<NumberBox>(L"X").Value(), Get<NumberBox>(L"Y").Value(), Get<NumberBox>(L"Z").Value()});
                Command("transform", {{mode == 3   ? "rotation"
                                       : mode == 4 ? "scale"
                                                   : "position",
                                       values},
                                      {"snap", Get<NumberBox>(L"Snap").Value()}});
                co_return;
            }
            if (action == "Merge")
            {
                project_->Edit("合并对象",
                               [&](CreationState &s)
                               {
                                   auto &a = Asset(s);
                                   auto &target = ObjectOf(s);
                                   size_t count = 0;
                                   for (auto &o : a.Objects)
                                       if (objectSelection_.contains(o.Id))
                                       {
                                           ++count;
                                           if (o.Locked || o.HalfState != target.HalfState)
                                               throw std::runtime_error("不能合并锁定对象或不同穿着状态");
                                           if (o.Id != object_)
                                               target.Mesh.Append(o.Mesh);
                                       }
                                   if (count < 2)
                                       throw std::runtime_error("请按住 Ctrl 或 Shift 在对象列表中选择至少两个对象");
                                   std::erase_if(a.Objects,
                                                 [&](auto &o)
                                                 {
                                                     return o.Id != object_ && objectSelection_.contains(o.Id);
                                                 });
                               });
                objectSelection_ = {object_};
                Refresh();
                co_return;
            }
            if (action == "Separate")
            {
                project_->Edit("分离对象",
                               [&](CreationState &s)
                               {
                                   auto &o = ObjectOf(s);
                                   CreationObject part;
                                   part.Mesh = o.Mesh.Extract(selection_);
                                   part.Mesh.Name += " · 分离";
                                   part.HalfState = o.HalfState;
                                   o.Mesh.Execute({"delete"}, selection_);
                                   Asset(s).Objects.push_back(part);
                                   object_ = part.Id;
                               });
                selection_.Ids.clear();
                Refresh();
                co_return;
            }
            if (action == "Half")
            {
                project_->Edit("独立半脱网格",
                               [&](CreationState &s)
                               {
                                   auto &a = Asset(s);
                                   if (a.Kind != "clothes")
                                       throw std::runtime_error("只有服装包含半脱状态");
                                   if (a.IndependentHalfState)
                                       throw std::runtime_error("已有独立半脱网格，请在对象列表中编辑");
                                   auto objects = a.Objects;
                                   for (auto &o : objects)
                                   {
                                       o.Id = NewId();
                                       o.HalfState = true;
                                       a.Objects.push_back(std::move(o));
                                   }
                                   a.IndependentHalfState = true;
                               });
                Get<ComboBox>(L"Clothing").SelectedIndex(1);
                Refresh();
                co_return;
            }
            if (action == "RefreshReference")
            {
                auto state = project_->Snapshot();
                auto revision = project_->Revision();
                Job("读取当前体型参考",
                    [lifetime, state, revision](std::stop_token token)
                    {
                        auto geometry = std::make_shared<asset::Geometry>(lifetime->service_->Reference(*state, token));
                        return [lifetime, geometry, revision]
                        {
                            if (lifetime->project_->Revision() != revision)
                                throw std::runtime_error("角色已改变，请重新更新参考");
                            bool first = !lifetime->reference_;
                            lifetime->reference_ = geometry;
                            if (first)
                                lifetime->Action("FrameReference");
                        };
                    });
                co_return;
            }
            if (action == "ImportModel")
            {
                auto file = co_await PickFile({L".fbx", L".obj", L".glb", L".gltf"});
                if (file.empty())
                    co_return;
                auto targetAsset = asset_;
                auto importRevision = project_->Revision();
                auto source = project_->ImportSource(fs::path(file.c_str()));
                auto runtime = ExecutableDirectory();
                Job("导入模型",
                    [lifetime, source, runtime, targetAsset, importRevision](std::stop_token token)
                    {
                        auto geometry =
                            asset::ImportModel(source, runtime / "native/win-x64/HS2Manager.FbxBridge.dll", token);
                        return [lifetime, geometry = std::move(geometry), targetAsset, importRevision]
                        {
                            if (lifetime->project_->Revision() != importRevision)
                                throw std::runtime_error("工程已修改，旧导入结果未应用");
                            lifetime->project_->Edit("导入模型",
                                                     [&](CreationState &s)
                                                     {
                                                         auto it = std::find_if(s.Assets.begin(), s.Assets.end(),
                                                                                [&](auto &a)
                                                                                {
                                                                                    return a.Id == targetAsset;
                                                                                });
                                                         if (it == s.Assets.end())
                                                             throw std::runtime_error("导入目标素材已移除");
                                                         for (auto &m : geometry.Meshes)
                                                         {
                                                             CreationObject o;
                                                             o.Mesh = MeshDocument::FromMesh(m);
                                                             it->Objects.push_back(std::move(o));
                                                         }
                                                         for (auto &bone : geometry.Bones)
                                                         {
                                                             auto b = std::find_if(it->Bones.begin(), it->Bones.end(),
                                                                                   [&](auto &old)
                                                                                   {
                                                                                       return old.Name == bone.Name;
                                                                                   });
                                                             if (b == it->Bones.end())
                                                                 it->Bones.push_back(bone);
                                                             else if (asset::VectorJson(b->Position) !=
                                                                      asset::VectorJson(bone.Position))
                                                                 throw std::runtime_error("导入骨骼与工程绑定姿态冲突");
                                                         }
                                                     });
                            if (!geometry.Warnings.empty())
                                lifetime->Message(Json(geometry.Warnings).dump());
                        };
                    });
                co_return;
            }
            if (action == "Color")
            {
                ColorPicker picker;
                picker.Color({uint8_t(color_.w * 255), uint8_t(color_.x * 255), uint8_t(color_.y * 255),
                              uint8_t(color_.z * 255)});
                picker.IsAlphaEnabled(true);
                if (co_await Dialog(L"画笔颜色", picker))
                {
                    auto c = picker.Color();
                    color_ = {c.R / 255.f, c.G / 255.f, c.B / 255.f, c.A / 255.f};
                }
                co_return;
            }
            if (action == "Build")
            {
                owner->editor_->EndGesture();
                auto request =
                    BuildRequest{project_->Snapshot(), project_->Directory(), project_->Guid(), project_->Revision()};
                Job("构建与原生试渲染",
                    [lifetime, request](std::stop_token token)
                    {
                        auto result = lifetime->service_->Build(request, {}, token);
                        return [lifetime, result = std::move(result)]
                        {
                            lifetime->project_->AcceptBuild(result.Report, *result.Character, result.Revision);
                            if (auto o = lifetime->owner_.lock())
                            {
                                auto card = o->preview_->Card();
                                o->preview_->Load(card, o->editor_->Root(), true, *result.Character, {}, true,
                                                  lifetime->service_->Resources(&lifetime->project_->State()));
                            }
                        };
                    });
                co_return;
            }
            if (action == "ExportCard" || action == "ExportPackage" || action == "ExportMod")
            {
                if (working_)
                    throw std::runtime_error("请等待当前任务完成再导出");
                StackPanel content;
                content.Spacing(8);
                TextBox directory;
                directory.Header(box_value(L"导出目录（可粘贴本机或 NAS 完整路径）"));
                directory.MinWidth(380);
                directory.Text(to_hstring(Utf8(project_->Directory() / "Exports")));
                content.Children().Append(directory);
                Button browse;
                browse.Content(box_value(L"浏览文件夹…"));
                browse.Click(
                    [owner, directory](auto &&, auto &&) -> fire_and_forget
                    {
                        auto window = owner;
                        auto input = directory;
                        try
                        {
                            auto selected = co_await window->PickFolder(L"选择成品导出目录");
                            if (!selected.empty())
                                input.Text(selected);
                        }
                        catch (const hresult_error &)
                        {
                            input.PlaceholderText(L"请选择目录或直接输入路径");
                        }
                    });
                content.Children().Append(browse);
                if (!(co_await Dialog(L"导出成品", content)))
                    co_return;
                auto output = fs::path(directory.Text().c_str());
                if (output.empty() || !output.is_absolute())
                    throw std::runtime_error("请输入完整的导出目录路径");
                auto state = project_->Snapshot();
                if ((!state->Assets.empty() || !state->Overlays.empty()) &&
                    (!state->LastBuild.is_object() || state->LastBuild.value("state_token", "") != state->Token))
                    throw std::runtime_error("素材或贴图已有修改，请先构建当前版本再导出");
                Job("导出成品",
                    [lifetime, state, output, action](std::stop_token token)
                    {
                        std::string message;
                        if (action == "ExportCard")
                            message = "角色卡已导出：" + lifetime->service_->ExportCard(*state, output, token).Path;
                        else if (action == "ExportPackage")
                        {
                            auto report = lifetime->service_->ExportPackage(*state, output, token);
                            message = report.Summary() + "；报告：" + report.ReportPath;
                        }
                        else
                            message = "Mod 已导出：" + Utf8(lifetime->service_->ExportMod(*state, output, token));
                        return std::function<void()>(
                            [lifetime, message]
                            {
                                lifetime->Message(message);
                            });
                    });
                co_return;
            }
            if (action == "Cover")
            {
                StackPanel content;
                content.Spacing(8);
                ComboBox mode;
                mode.Header(box_value(L"封面来源"));
                for (auto name : {L"当前角色渲染", L"导入图片"})
                    mode.Items().Append(box_value(name));
                mode.SelectedIndex(0);
                ComboBox view;
                view.Header(box_value(L"取景"));
                for (auto name : {L"正面全身", L"面部", L"三分之四", L"侧面", L"背面"})
                    view.Items().Append(box_value(name));
                view.SelectedIndex(0);
                NumberBox zoom;
                zoom.Header(box_value(L"缩放"));
                zoom.Minimum(.2);
                zoom.Maximum(5);
                zoom.Value(1);
                NumberBox vertical;
                vertical.Header(box_value(L"垂直偏移"));
                vertical.Value(0);
                content.Children().Append(mode);
                content.Children().Append(view);
                content.Children().Append(zoom);
                content.Children().Append(vertical);
                if (!(co_await Dialog(L"封面取景与重新拍摄", content)))
                    co_return;
                if (mode.SelectedIndex() == 1)
                {
                    auto file = co_await PickFile({L".png", L".jpg", L".jpeg"});
                    if (file.empty())
                        co_return;
                    auto source = project_->ImportSource(fs::path(file.c_str()));
                    auto image = PaintDocument::Import(source);
                    auto path = project_->Directory() / "ImportedCover.png";
                    image.Save(path);
                    auto bytes = ReadFile(path, 32 * 1024 * 1024);
                    project_->AcceptCover(project_->State().Character->WithCover(bytes), {{"mode", "import"}},
                                          project_->Revision());
                }
                else
                {
                    const char *views[]{"front", "face", "three_quarter", "side", "back"};
                    auto state = std::make_shared<CreationState>(project_->State());
                    state->CoverSettings = {{"mode", "render"},
                                            {"view", views[view.SelectedIndex()]},
                                            {"zoom", zoom.Value()},
                                            {"vertical_offset", vertical.Value()}};
                    auto revision = project_->Revision();
                    auto path = project_->Directory() / "Cover" / Path(NewId());
                    Job("重新拍摄封面",
                        [lifetime, state, revision, path](std::stop_token token)
                        {
                            auto document = lifetime->service_->Cover(*state, path, token);
                            return [lifetime, revision, document, state]
                            {
                                lifetime->project_->AcceptCover(document, state->CoverSettings, revision);
                            };
                        });
                }
                co_return;
            }
            if (action == "Install")
            {
                if (working_)
                    throw std::runtime_error("请等待当前任务完成");
                auto state = project_->Snapshot();
                auto revision = project_->Revision();
                InstallPlan plan;
                std::string error;
                apartment_context ui;
                working_ = true;
                cancellation_ = std::stop_source{};
                auto token = cancellation_.get_token();
                Get<Button>(L"Cancel").IsEnabled(true);
                co_await resume_background();
                try
                {
                    plan = service_->PlanInstall(*state, project_->Id(), token);
                }
                catch (const std::exception &e)
                {
                    error = e.what();
                }
                catch (...)
                {
                    error = "安装检查失败";
                }
                co_await ui;
                working_ = false;
                if (stopped_)
                    co_return;
                Get<Button>(L"Cancel").IsEnabled(false);
                if (!error.empty())
                    throw std::runtime_error(error);
                if (!plan.Conflicts.empty())
                {
                    owner->ShowReport(L"安装冲突", to_hstring(plan.Summary()));
                    co_return;
                }
                if (!(co_await owner->Confirm(L"安装到游戏", to_hstring(plan.Summary()))))
                    co_return;
                if (project_->Revision() != revision)
                    throw std::runtime_error("工程已改变，请重新检查安装计划");
                Job("安装到游戏",
                    [lifetime, plan](std::stop_token token)
                    {
                        auto result = lifetime->service_->Install(plan, {}, token);
                        return [lifetime, result]
                        {
                            if (result.Status != "installed")
                                throw std::runtime_error(result.Status + "：" + Json(result.Messages).dump());
                            if (auto o = lifetime->owner_.lock())
                            {
                                for (auto &p : result.Cards)
                                    o->repository_->SaveScan(CardParser().Parse(p));
                                o->mods_ = std::make_unique<ModDependencyService>(*o->repository_);
                                o->Reload();
                            }
                        };
                    });
                co_return;
            }
            if (action == "Rollback")
            {
                auto installs = service_->Installations();
                ComboBox choice;
                std::vector<std::string> ids;
                for (auto &i : installs)
                    if (i.value("project_id", "") == project_->Id() && i.value("status", "") == "installed")
                    {
                        ids.push_back(i.at("id"));
                        choice.Items().Append(box_value(to_hstring(i.value("started_utc", "") + " · " + ids.back())));
                    }
                if (ids.empty())
                    throw std::runtime_error("此工程没有可回退的安装");
                choice.SelectedIndex(int(ids.size()) - 1);
                if (!(co_await Dialog(L"回退本次安装", choice)))
                    co_return;
                auto id = ids[choice.SelectedIndex()];
                Job("回退安装",
                    [lifetime, id](std::stop_token token)
                    {
                        auto result = lifetime->service_->Rollback(id, token);
                        return [lifetime, result]
                        {
                            if (auto o = lifetime->owner_.lock())
                            {
                                o->mods_ = std::make_unique<ModDependencyService>(*o->repository_);
                                o->Reload();
                                o->ShowReport(L"安装回退",
                                              to_hstring(result.Status + "\n" + Json(result.Messages).dump()));
                            }
                        };
                    });
                co_return;
            }
            if (action == "ExportUv")
            {
                auto o = SelectedObject();
                if (!o)
                    throw std::runtime_error("请先选择网格");
                auto folder = co_await owner->PickFolder(L"导出 UV 布局");
                if (folder.empty())
                    co_return;
                std::ostringstream svg;
                svg << "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1024\" height=\"1024\" viewBox=\"0 0 1024 "
                       "1024\"><rect width=\"1024\" height=\"1024\" fill=\"white\"/>";
                for (auto &[id, f] : o->Mesh.Faces)
                {
                    svg << "<polygon fill=\"none\" stroke=\"black\" points=\"";
                    for (auto &c : f.Corners)
                        svg << c.Uv.x * 1024 << ',' << (1 - c.Uv.y) * 1024 << ' ';
                    svg << "\"/>";
                    (void)id;
                }
                svg << "</svg>";
                auto text = svg.str();
                WriteAtomic(fs::path(folder.c_str()) / "uv-layout.svg", Bytes(text.begin(), text.end()));
                Message("UV 布局已导出");
                co_return;
            }
            if (action == "Material")
            {
                TextureAction("material");
                co_return;
            }
        }
        catch (const std::exception &e)
        {
            Message(e.what());
        }
        catch (const hresult_error &e)
        {
            Message(to_string(e.message()));
        }
    }
}
