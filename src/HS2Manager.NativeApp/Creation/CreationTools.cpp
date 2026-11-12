#include "Creation/NativeCreation.h"
#include "Animation/SimulationMath.h"
#include <sstream>
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    using namespace creation;

    namespace
    {
        NumberBox Number(StackPanel &panel, const wchar_t *label, double value)
        {
            NumberBox box;
            box.Header(box_value(label));
            box.Value(value);
            panel.Children().Append(box);
            return box;
        }

        TextBox Input(StackPanel &panel, const wchar_t *label, hstring value)
        {
            TextBox box;
            box.Header(box_value(label));
            box.Text(value);
            panel.Children().Append(box);
            return box;
        }
    }

    fire_and_forget NativeCreation::AddAsset()
    {
        auto lifetime = shared_from_this();
        if (working_ || stopped_)
        {
            Message("请等待当前任务完成");
            co_return;
        }
        try
        {
            StackPanel content;
            content.Spacing(8);
            auto name = Input(content, L"素材名称", L"新素材");
            ComboBox kind;
            kind.Header(box_value(L"类别"));
            for (auto label : {L"配饰", L"发型", L"服装"})
                kind.Items().Append(box_value(label));
            kind.SelectedIndex(0);
            content.Children().Append(kind);
            ComboBox slots;
            slots.Header(box_value(L"角色素材槽"));
            content.Children().Append(slots);
            ComboBox category;
            category.Header(box_value(L"配饰分类"));
            auto types = CharacterEditSchema::AccessoryTypes();
            for (size_t i = 1; i < types.size(); ++i)
                category.Items().Append(box_value(to_hstring(types[i].Label)));
            category.SelectedIndex(0);
            content.Children().Append(category);
            std::vector<std::string> paths;
            auto update = [&]
            {
                slots.Items().Clear();
                paths.clear();
                auto k = kind.SelectedIndex();
                auto prefix = k == 0 ? "accessory" : k == 1 ? "hair" : "clothes";
                for (size_t i = 0;
                     i < project_->State().Character->Node(std::string(prefix) + ".parts").Children.size(); ++i)
                {
                    std::string path = std::string(prefix) + ".parts[" + std::to_string(i) + "].id";
                    if (!project_->State().Character->Contains(path))
                        continue;
                    paths.push_back(path);
                    auto label = k == 0   ? "配饰 " + std::to_string(i + 1)
                                 : k == 1 ? CharacterEditSchema::HairNames().at(i)
                                          : CharacterEditSchema::ClothesNames().at(i);
                    slots.Items().Append(box_value(to_hstring(label)));
                }
                slots.SelectedIndex(0);
                category.Visibility(k == 0 ? Visibility::Visible : Visibility::Collapsed);
            };
            update();
            kind.SelectionChanged(
                [&](auto &&, auto &&)
                {
                    update();
                });
            auto preferred = std::exchange(preferredTarget_, {});
            if (!preferred.empty())
            {
                kind.SelectedIndex(preferred.starts_with("hair.") ? 1 : preferred.starts_with("clothes.") ? 2 : 0);
                update();
                auto slot = std::find(paths.begin(), paths.end(), preferred);
                if (slot != paths.end())
                    slots.SelectedIndex(int(slot - paths.begin()));
                if (preferred.starts_with("accessory."))
                {
                    auto type =
                        project_->State().Character->Node(preferred.substr(0, preferred.size() - 2) + "type").Integer();
                    for (size_t i = 1; i < types.size(); ++i)
                        if (auto value = std::get_if<std::int64_t>(&types[i].Value); value && *value == type)
                            category.SelectedIndex(int(i) - 1);
                }
            }
            if (!(co_await Dialog(L"添加素材", content)))
                co_return;
            if (slots.SelectedIndex() < 0 || name.Text().empty())
                throw std::runtime_error("请填写名称并选择素材槽");
            auto target = paths.at(slots.SelectedIndex());
            auto state = *project_->Snapshot();
            auto revision = project_->Revision();
            auto kindName = kind.SelectedIndex() == 0 ? "accessory" : kind.SelectedIndex() == 1 ? "hair" : "clothes";
            if (kind.SelectedIndex() == 0)
                state.Character = state.Character->WithValue(target.substr(0, target.size() - 2) + "type",
                                                             types.at(category.SelectedIndex() + 1).Value);
            Json templates;
            std::string error;
            apartment_context ui;
            working_ = true;
            cancellation_ = std::stop_source{};
            auto token = cancellation_.get_token();
            Get<Button>(L"Cancel").IsEnabled(true);
            Message("读取本机游戏材质模板…");
            co_await resume_background();
            try
            {
                templates = service_->Templates(state, target, token);
            }
            catch (const std::exception &e)
            {
                error = e.what();
            }
            catch (...)
            {
                error = "读取模板失败";
            }
            co_await ui;
            working_ = false;
            if (!stopped_)
                Get<Button>(L"Cancel").IsEnabled(false);
            if (stopped_)
                co_return;
            if (!error.empty())
                throw std::runtime_error(error);
            if (templates.empty())
                throw std::runtime_error("此位置没有可用的游戏模板，请检查资源与分类");
            ComboBox templateChoice;
            templateChoice.Header(box_value(L"材质与组件模板"));
            templateChoice.HorizontalAlignment(HorizontalAlignment::Stretch);
            for (auto &t : templates)
                templateChoice.Items().Append(box_value(to_hstring(t.at("name").get<std::string>())));
            templateChoice.SelectedIndex(0);
            if (!(co_await Dialog(L"选择游戏模板", templateChoice)))
                co_return;
            auto assetName = to_string(name.Text());
            auto templateId = templates.at(templateChoice.SelectedIndex()).at("asset_id").get<std::string>();
            CreationAsset created;
            working_ = true;
            cancellation_ = std::stop_source{};
            auto createToken = cancellation_.get_token();
            Get<Button>(L"Cancel").IsEnabled(true);
            co_await resume_background();
            try
            {
                created = service_->NewAsset(state, kindName, target, assetName, templateId, createToken);
            }
            catch (const std::exception &e)
            {
                error = e.what();
            }
            catch (...)
            {
                error = "创建素材失败";
            }
            co_await ui;
            working_ = false;
            if (stopped_)
                co_return;
            Get<Button>(L"Cancel").IsEnabled(false);
            if (!error.empty())
                throw std::runtime_error(error);
            if (project_->Revision() != revision)
                throw std::runtime_error("工程已改变，请重新选择创建素材");
            if (reference_)
                created.Bones = reference_->Bones;
            project_->Edit("添加素材",
                           [&](CreationState &s)
                           {
                               s.Character = state.Character;
                               s.Assets.push_back(created);
                           });
            asset_ = created.Id;
            object_.clear();
            Refresh();
            Message("素材已创建，请添加网格或导入模型");
            Action("ModelTab");
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

    fire_and_forget NativeCreation::ChangeTemplate()
    {
        auto lifetime = shared_from_this();
        if (working_ || stopped_)
        {
            Message("请等待当前任务完成");
            co_return;
        }
        try
        {
            auto state = project_->Snapshot();
            auto revision = project_->Revision();
            auto selected = Asset(*std::make_shared<CreationState>(*state));
            Json templates;
            std::string error;
            apartment_context ui;
            working_ = true;
            cancellation_ = std::stop_source{};
            auto token = cancellation_.get_token();
            Get<Button>(L"Cancel").IsEnabled(true);
            co_await resume_background();
            try
            {
                templates = service_->Templates(*state, selected.TargetPath, token);
            }
            catch (const std::exception &e)
            {
                error = e.what();
            }
            catch (...)
            {
                error = "读取模板失败";
            }
            co_await ui;
            working_ = false;
            if (stopped_)
                co_return;
            Get<Button>(L"Cancel").IsEnabled(false);
            if (!error.empty())
                throw std::runtime_error(error);
            ComboBox choice;
            choice.Header(box_value(L"选择兼容的游戏模板"));
            for (auto &item : templates)
                choice.Items().Append(box_value(to_hstring(item.at("name").get<std::string>())));
            if (templates.empty())
                throw std::runtime_error("没有可用模板");
            choice.SelectedIndex(0);
            for (size_t i = 0; i < templates.size(); ++i)
                if (templates[i].at("asset_id").get<std::string>() == selected.TemplateId)
                    choice.SelectedIndex(int(i));
            if (!(co_await Dialog(L"重新选择并确认模板", choice)))
                co_return;
            auto templateId = templates.at(choice.SelectedIndex()).at("asset_id").get<std::string>();
            Job("验证模板通道",
                [lifetime, state, selected, revision, templateId](std::stop_token token)
                {
                    auto replacement = lifetime->service_->NewAsset(*state, selected.Kind, selected.TargetPath,
                                                                    selected.Name, templateId, token);
                    auto materials = lifetime->service_->MaterialProperties(*state, replacement, token);
                    std::set<std::pair<int, std::string>> channels;
                    for (auto &material : materials)
                        for (auto &value : material.at("values"))
                            channels.emplace(material.at("slot").get<int>(), value.at("property").get<std::string>());
                    for (auto &texture : selected.Textures)
                        if (texture.Kind == "material" && !channels.contains({texture.MaterialSlot, texture.Property}))
                            throw std::runtime_error("新模板缺少已使用的贴图通道：" + texture.Property);
                    for (auto &value : selected.MaterialValues)
                        if (!channels.contains({value.at("slot").get<int>(), value.at("property").get<std::string>()}))
                            throw std::runtime_error("新模板缺少已使用的材质参数");
                    return [lifetime, revision, selected, replacement]
                    {
                        if (lifetime->project_->Revision() != revision)
                            throw std::runtime_error("工程已改变，请重新选择模板");
                        lifetime->project_->Edit("更换游戏模板",
                                                 [&](CreationState &s)
                                                 {
                                                     auto a = std::find_if(s.Assets.begin(), s.Assets.end(),
                                                                           [&](auto &item)
                                                                           {
                                                                               return item.Id == selected.Id;
                                                                           });
                                                     if (a == s.Assets.end())
                                                         throw std::runtime_error("目标素材已移除");
                                                     a->TemplateId = replacement.TemplateId;
                                                     a->TemplateSource = replacement.TemplateSource;
                                                 });
                    };
                });
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

    fire_and_forget NativeCreation::Primitive(std::string type)
    {
        auto lifetime = shared_from_this();
        try
        {
            if (asset_.empty())
                throw std::runtime_error("请先添加素材");
            bool editing = type == "edit_curve";
            Json recipe = Json::object();
            std::string editedId;
            if (editing)
            {
                auto selected = SelectedObject();
                if (!selected || !selected->Generator.is_object() || selected->Generator.value("op", "") != "curve")
                    throw std::runtime_error("请选择由曲线生成的发片或管束");
                if (selected->Locked)
                    throw std::runtime_error("对象已锁定");
                auto json = selected->Mesh.ToJson().dump();
                if (Sha256(Bytes(json.begin(), json.end())) != selected->GeneratorMeshHash)
                    throw std::runtime_error(
                        "此曲线已进行网格编辑，继续修改控制点会丢失编辑；请撤销网格修改后再编辑曲线");
                recipe = selected->Generator;
                editedId = selected->Id;
                type = recipe.at("mode");
            }
            StackPanel content;
            content.Spacing(6);
            auto name = Input(content, L"对象名称", to_hstring(type));
            auto size = Number(content,
                               type == "body_surface"               ? L"衣物表面偏移"
                               : type == "ribbon" || type == "tube" ? L"发束宽度"
                                                                    : L"大小",
                               type == "body_surface" ? .1 : 1);
            auto segments = Number(content, L"细分段数", 12);
            segments.Minimum(3);
            segments.Maximum(128);
            TextBox points;
            NumberBox low, high;
            if (type == "ribbon" || type == "tube")
            {
                Vector3 head{0, 15, 0};
                if (reference_)
                    for (auto &bone : reference_->Bones)
                        if (bone.Name == "cf_J_Head" || bone.Name == "cm_J_Head")
                            head = bone.Position;
                std::ostringstream coords;
                for (auto d : {Vector3{-.6f, 2, .7f}, Vector3{-1.05f, 1, .9f}, Vector3{-1.15f, -.2f, .8f}})
                    coords << head.x + d.x << ' ' << head.y + d.y << ' ' << head.z + d.z << '\n';
                points = Input(content, L"控制点（每行 X Y Z，游戏单位）", L"");
                points.AcceptsReturn(true);
                points.Text(to_hstring(coords.str()));
                points.MinHeight(110);
            }
            if (editing)
            {
                name.Text(to_hstring(recipe.value("name", "曲线")));
                size.Value(recipe.value("width", .1));
                segments.Value(recipe.value("subdivisions", 12));
                std::ostringstream text;
                for (auto &p : recipe.at("points"))
                    text << p[0].get<float>() << ' ' << p[1].get<float>() << ' ' << p[2].get<float>() << '\n';
                points.Text(to_hstring(text.str()));
            }
            if (type == "body_surface")
            {
                low = Number(content, L"裁剪最低 Y", 7);
                high = Number(content, L"裁剪最高 Y", 14);
            }
            if (!(co_await Dialog(L"添加网格", content)))
                co_return;
            Json op{{"name", to_string(name.Text())}};
            if (type == "ribbon" || type == "tube")
            {
                Json list = Json::array();
                auto coordinates = to_string(points.Text());
                std::replace(coordinates.begin(), coordinates.end(), '\r', '\n');
                std::istringstream stream(coordinates);
                std::string line;
                while (std::getline(stream, line))
                {
                    if (line.find_first_not_of(" \t") == std::string::npos)
                        continue;
                    std::istringstream row(line);
                    float x, y, z;
                    std::string extra;
                    if (!(row >> x >> y >> z) || row >> extra)
                        throw std::runtime_error("每行需要三个有效坐标");
                    list.push_back({x, y, z});
                }
                op.update({{"op", "curve"},
                           {"mode", type},
                           {"points", list},
                           {"width", size.Value()},
                           {"subdivisions", int(segments.Value())}});
            }
            else if (type == "body_surface")
            {
                if (!reference_)
                    throw std::runtime_error("请先更新当前体型参考");
                op.update({{"op", "body_surface"},
                           {"minimum", {-100, low.Value(), -100}},
                           {"maximum", {100, high.Value(), 100}},
                           {"offset", size.Value()}});
            }
            else
                op.update({{"op", "primitive"},
                           {"primitive", type},
                           {"size", {size.Value(), size.Value(), size.Value()}},
                           {"radius", size.Value() * .5},
                           {"height", size.Value()},
                           {"segments", int(segments.Value())},
                           {"position", asset::VectorJson(camera_.Center)}});
            asset::Geometry empty;
            auto geometry = asset::BuildRecipe({{"operations", Json::array({op})}}, reference_ ? *reference_ : empty);
            project_->Edit("添加网格",
                           [&](CreationState &s)
                           {
                               auto &a = Asset(s);
                               for (auto &mesh : geometry.Meshes)
                               {
                                   CreationObject object;
                                   object.Mesh = MeshDocument::FromMesh(mesh);
                                   // 基本体与曲线的成对三角面恢复四边形，保留每个面角的 UV。
                                   if (type != "body_surface")
                                   {
                                       auto faces = object.Mesh.Faces;
                                       std::vector<ElementId> ids;
                                       for (auto &[id, f] : faces)
                                           ids.push_back(id);
                                       for (size_t i = 0; i + 1 < ids.size(); i += 2)
                                       {
                                           auto &x = faces.at(ids[i]);
                                           auto &y = faces.at(ids[i + 1]);
                                           if (x.Corners.size() != 3 || y.Corners.size() != 3)
                                               continue;
                                           ElementId edge = 0;
                                           for (auto &[key, e] : object.Mesh.Edges)
                                           {
                                               int count = 0;
                                               for (auto &c : x.Corners)
                                                   count += c.Vertex == e.A || c.Vertex == e.B;
                                               for (auto &c : y.Corners)
                                                   count += c.Vertex == e.A || c.Vertex == e.B;
                                               if (count == 4)
                                               {
                                                   edge = e.Id;
                                                   break;
                                               }
                                               (void)key;
                                           }
                                           if (edge)
                                           {
                                               try
                                               {
                                                   object.Mesh.Execute({"dissolve"}, {SelectionMode::Edge, {edge}});
                                               }
                                               catch (const std::exception &)
                                               {
                                               }
                                           }
                                       }
                                   }
                                   if (type == "ribbon" || type == "tube")
                                   {
                                       object.Generator = op;
                                       auto text = object.Mesh.ToJson().dump();
                                       object.GeneratorMeshHash = Sha256(Bytes(text.begin(), text.end()));
                                   }
                                   if (editing)
                                   {
                                       auto old = std::find_if(a.Objects.begin(), a.Objects.end(),
                                                               [&](auto &o)
                                                               {
                                                                   return o.Id == editedId;
                                                               });
                                       if (old == a.Objects.end())
                                           throw std::runtime_error("曲线对象已移除");
                                       object.Id = old->Id;
                                       object.HalfState = old->HalfState;
                                       *old = std::move(object);
                                       object_ = editedId;
                                   }
                                   else
                                   {
                                       object_ = object.Id;
                                       a.Objects.push_back(std::move(object));
                                   }
                               }
                               if (a.Bones.empty() && reference_)
                                   a.Bones = reference_->Bones;
                           });
            selection_.Ids.clear();
            Refresh();
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

    fire_and_forget NativeCreation::BoneAction(std::string action)
    {
        auto lifetime = shared_from_this();
        try
        {
            auto bone = to_string(unbox_value_or<hstring>(Get<ComboBox>(L"Bones").SelectedItem(), L""));
            if (action == "transfer")
            {
                if (!reference_)
                    throw std::runtime_error("请先读取当前体型参考");
                project_->Edit("权重转移",
                               [&](CreationState &s)
                               {
                                   service_->TransferWeights(Asset(s), *reference_);
                               });
                Refresh();
                co_return;
            }
            if (bone.empty())
                throw std::runtime_error("请选择骨骼");
            if (action == "dynamic_settings")
            {
                auto state = project_->State();
                auto &asset = Asset(state);
                auto settings = asset.DynamicSettings.value(bone, Json::object());
                StackPanel panel;
                panel.Spacing(6);
                std::vector<std::pair<std::string, NumberBox>> inputs;
                for (auto [key, label, fallback] : {std::tuple{"damping", L"阻尼", .1},
                                                    {"elasticity", L"弹性", .1},
                                                    {"stiffness", L"刚度", .1},
                                                    {"inertia", L"惯性", 0.},
                                                    {"radius", L"碰撞半径", 0.}})
                {
                    auto box = Number(panel, label, settings.value(key, fallback));
                    box.Minimum(0);
                    box.Maximum(std::string(key) == "radius" ? 10 : 1);
                    inputs.emplace_back(key, box);
                }
                if (!(co_await Dialog(L"动态链参数", panel)))
                    co_return;
                project_->Edit("动态链参数",
                               [&](CreationState &s)
                               {
                                   auto &a = Asset(s);
                                   for (auto &[key, box] : inputs)
                                       a.DynamicSettings[bone][key] = box.Value();
                                   if (std::find(a.DynamicRoots.begin(), a.DynamicRoots.end(), bone) ==
                                       a.DynamicRoots.end())
                                       a.DynamicRoots.push_back(bone);
                               });
            }
            else if (action == "add" || action == "pose")
            {
                StackPanel content;
                content.Spacing(6);
                auto name = Input(content, L"骨骼名称", action == "add" ? L"authored_bone" : to_hstring(bone));
                name.IsReadOnly(action == "pose");
                auto parent = Input(content, L"父级骨骼", to_hstring(bone));
                Vector3 position{};
                Quaternion rotation{0, 0, 0, 1};
                if (action == "pose")
                    for (auto &a : project_->State().Assets)
                        if (a.Id == asset_)
                            for (auto &b : a.Bones)
                                if (b.Name == bone)
                                {
                                    parent.Text(to_hstring(b.Parent));
                                    position = b.Position;
                                    rotation = b.Rotation;
                                }
                auto x = Number(content, L"绑定位置 X", position.x), y = Number(content, L"绑定位置 Y", position.y),
                     z = Number(content, L"绑定位置 Z", position.z);
                auto rx = Number(content, L"旋转四元数 X", rotation.x),
                     ry = Number(content, L"旋转四元数 Y", rotation.y),
                     rz = Number(content, L"旋转四元数 Z", rotation.z),
                     rw = Number(content, L"旋转四元数 W", rotation.w);
                ScrollViewer scroll;
                scroll.Content(content);
                scroll.MaxHeight(500);
                if (!(co_await Dialog(action == "add" ? L"新增骨骼" : L"编辑绑定姿态", scroll)))
                    co_return;
                project_->Edit("骨骼绑定",
                               [&](CreationState &s)
                               {
                                   auto &a = Asset(s);
                                   if (a.Bones.empty() && reference_)
                                       a.Bones = reference_->Bones;
                                   auto target = std::find_if(a.Bones.begin(), a.Bones.end(),
                                                              [&](auto &b)
                                                              {
                                                                  return b.Name == to_string(name.Text());
                                                              });
                                   if (action == "add")
                                   {
                                       if (target != a.Bones.end())
                                           throw std::runtime_error("骨骼名称已存在");
                                       asset::Bone b;
                                       b.Name = to_string(name.Text());
                                       b.Added = true;
                                       a.Bones.push_back(b);
                                       target = std::prev(a.Bones.end());
                                   }
                                   if (target == a.Bones.end() || !target->Added)
                                       throw std::runtime_error("原版参考骨骼不可改变绑定姿态，请新增骨骼");
                                   auto parentName = to_string(parent.Text());
                                   if (std::none_of(a.Bones.begin(), a.Bones.end(),
                                                    [&](auto &b)
                                                    {
                                                        return b.Name == parentName;
                                                    }))
                                       throw std::runtime_error("父级骨骼不存在");
                                   target->Parent = parentName;
                                   target->Position = {float(x.Value()), float(y.Value()), float(z.Value())};
                                   target->Rotation = sim::Normalize(
                                       {float(rx.Value()), float(ry.Value()), float(rz.Value()), float(rw.Value())});
                                   target->HasMatrix = false;
                               });
            }
            else
                project_->Edit("骨骼设置",
                               [&](CreationState &s)
                               {
                                   auto &a = Asset(s);
                                   if (action == "parent")
                                   {
                                       if (a.Kind != "accessory")
                                           throw std::runtime_error("仅配饰使用挂点，发型与服装通过蒙皮绑定");
                                       a.Parent = bone;
                                   }
                                   else if (action == "lock")
                                   {
                                       if (!a.LockedBones.erase(bone))
                                           a.LockedBones.insert(bone);
                                   }
                                   else if (action == "dynamic")
                                   {
                                       if (std::find(a.DynamicRoots.begin(), a.DynamicRoots.end(), bone) ==
                                           a.DynamicRoots.end())
                                           a.DynamicRoots.push_back(bone);
                                       else
                                           std::erase(a.DynamicRoots, bone);
                                   }
                               });
            Refresh();
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

    fire_and_forget NativeCreation::TextureAction(std::string action)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (working_ || stopped_)
        {
            Message("请等待当前任务完成");
            co_return;
        }
        try
        {
            if (action == "export")
            {
                auto selected = SelectedTexture();
                if (!selected)
                    throw std::runtime_error("请选择贴图");
                auto image = selected->Image;
                auto folder = co_await owner->PickFolder(L"导出贴图 PNG");
                if (!folder.empty())
                    image.Save(CardFileService::UniqueDestination(fs::path(folder.c_str()), L"texture.png"));
                co_return;
            }
            if (action == "layer" || action == "lock" || action == "visible" || action == "delete_layer" ||
                action == "import_layer")
            {
                fs::path source;
                if (action == "import_layer")
                {
                    auto file = co_await PickFile({L".png", L".jpg", L".jpeg"});
                    if (file.empty())
                        co_return;
                    source = project_->ImportSource(fs::path(file.c_str()));
                }
                project_->Edit("贴图图层",
                               [&](CreationState &s)
                               {
                                   auto &image = Texture(s).Image;
                                   if (action == "layer")
                                   {
                                       image.AddLayer("图层 " + std::to_string(image.Layers.size() + 1));
                                       layer_ = int(image.Layers.size()) - 1;
                                   }
                                   else if (action == "import_layer")
                                   {
                                       image.ImportLayer(source);
                                       layer_ = int(image.Layers.size()) - 1;
                                   }
                                   else if (action == "lock")
                                       image.Layers.at(layer_).Locked = !image.Layers.at(layer_).Locked;
                                   else if (action == "visible")
                                       image.Layers.at(layer_).Visible = !image.Layers.at(layer_).Visible;
                                   else
                                   {
                                       if (image.Layers.size() == 1)
                                           throw std::runtime_error("至少保留一个图层");
                                       image.Layers.erase(image.Layers.begin() + layer_);
                                       layer_ = 0;
                                   }
                               });
                Refresh();
                co_return;
            }
            bool overlay = action == "face" || action == "body" || action == "clothes";
            Json properties = Json::array();
            if (!overlay && action != "mask")
            {
                auto state = project_->Snapshot();
                auto it = std::find_if(state->Assets.begin(), state->Assets.end(),
                                       [&](auto &a)
                                       {
                                           return a.Id == asset_;
                                       });
                if (it == state->Assets.end())
                    throw std::runtime_error("请选择素材");
                auto selected = *it;
                std::string error;
                apartment_context ui;
                working_ = true;
                cancellation_ = std::stop_source{};
                auto token = cancellation_.get_token();
                Get<Button>(L"Cancel").IsEnabled(true);
                Message("读取模板材质通道…");
                co_await resume_background();
                try
                {
                    properties = service_->MaterialProperties(*state, selected, token);
                }
                catch (const std::exception &e)
                {
                    error = e.what();
                }
                catch (...)
                {
                    error = "读取材质失败";
                }
                co_await ui;
                working_ = false;
                if (!stopped_)
                    Get<Button>(L"Cancel").IsEnabled(false);
                if (stopped_)
                    co_return;
                if (!error.empty())
                    throw std::runtime_error(error);
            }
            StackPanel content;
            content.Spacing(7);
            auto name = Input(content, L"名称", action == "material" ? L"材质参数" : L"新贴图");
            ComboBox channel;
            channel.Header(box_value(L"模板通道"));
            std::vector<Json> channels;
            for (auto &material : properties)
                for (auto &p : material.at("values"))
                    if ((action == "material") == (p.at("kind") != "texture"))
                    {
                        auto value = p;
                        value["slot"] = material.at("slot");
                        channels.push_back(value);
                        channel.Items().Append(box_value(to_hstring(std::to_string(material.at("slot").get<int>()) +
                                                                    " · " + p.at("property").get<std::string>())));
                    }
            if (!overlay && action != "mask")
            {
                if (channels.empty())
                    throw std::runtime_error("模板未提供此类可编辑通道");
                channel.SelectedIndex(0);
                content.Children().Append(channel);
            }
            NumberBox dimension, number;
            TextBox clothing;
            ComboBox mask;
            if (action == "material")
                number = Number(content, L"数值（颜色通道使用当前画笔颜色）", .5);
            else
                dimension = Number(content, L"贴图边长", 1024);
            if (action == "clothes")
                clothing = Input(content, L"服装对象名称", L"ct_clothesTop");
            if (action == "mask")
            {
                mask.Header(box_value(L"遮罩类型"));
                for (auto n : {L"身体遮罩", L"内衣遮罩", L"内裤遮罩"})
                    mask.Items().Append(box_value(n));
                mask.SelectedIndex(0);
                content.Children().Append(mask);
            }
            if (!(co_await Dialog(action == "material" ? L"材质参数" : L"新建贴图", content)))
                co_return;
            if (action == "material")
            {
                auto value = channels.at(channel.SelectedIndex());
                auto data = value.at("kind") == "color" ? asset::VectorJson(color_) : Json(number.Value());
                project_->Edit("材质参数",
                               [&](CreationState &s)
                               {
                                   auto &a = Asset(s);
                                   std::erase_if(a.MaterialValues.get_ref<Json::array_t &>(),
                                                 [&](auto &p)
                                                 {
                                                     return p.at("slot") == value.at("slot") &&
                                                            p.at("property") == value.at("property");
                                                 });
                                   a.MaterialValues.push_back({{"slot", value.at("slot")},
                                                               {"property", value.at("property")},
                                                               {"value", data}});
                               });
                co_return;
            }
            CreationTexture texture;
            texture.Name = to_string(name.Text());
            texture.Image = PaintDocument::Blank(int(dimension.Value()), int(dimension.Value()));
            if (action == "import")
            {
                auto file = co_await PickFile({L".png", L".jpg", L".jpeg"});
                if (file.empty())
                    co_return;
                auto source = project_->ImportSource(fs::path(file.c_str()));
                texture.Image = PaintDocument::Import(source);
            }
            if (overlay)
            {
                texture.Kind = "overlay";
                texture.Target = action;
                if (action == "clothes")
                    texture.ClothingName = to_string(clothing.Text());
            }
            else if (action == "mask")
            {
                const char *names[]{"body", "bra", "inner"};
                texture.Kind = "mask";
                texture.Property = names[mask.SelectedIndex()];
                texture.Image.Fill(0, {0, 0}, {1, 1, 1, 1});
            }
            else
            {
                auto value = channels.at(channel.SelectedIndex());
                texture.Property = value.at("property");
                texture.MaterialSlot = value.at("slot");
            }
            project_->Edit("新建贴图",
                           [&](CreationState &s)
                           {
                               if (overlay)
                                   s.Overlays.push_back(texture);
                               else
                                   Asset(s).Textures.push_back(texture);
                           });
            texture_ = texture.Id;
            layer_ = 0;
            Get<Primitives::ToggleButton>(L"UvVisible").IsChecked(true);
            Get<ColumnDefinition>(L"UvColumn").Width({1, GridUnitType::Star});
            Refresh();
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
