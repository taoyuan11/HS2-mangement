#include "Creation/NativeCreation.h"
#include "Animation/SimulationMath.h"
#include <robuffer.h>
#include <winrt/Microsoft.UI.Input.h>
#include <algorithm>
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    using namespace creation;
    using namespace sim;

    namespace
    {
        float Distance(Vector2 a, Vector2 b)
        {
            return std::hypot(a.x - b.x, a.y - b.y);
        }

        float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            float x = b.x - a.x, y = b.y - a.y,
                  t = std::clamp(((p.x - a.x) * x + (p.y - a.y) * y) / std::max(1e-6f, x * x + y * y), 0.f, 1.f);
            return Distance(p, {a.x + t * x, a.y + t * y});
        }

        bool InPolygon(Vector2 p, const MeshFace &f)
        {
            bool inside = false;
            for (size_t i = 0, j = f.Corners.size() - 1; i < f.Corners.size(); j = i++)
            {
                auto a = f.Corners[i].Uv, b = f.Corners[j].Uv;
                if (((a.y > p.y) != (b.y > p.y)) && (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x))
                    inside = !inside;
            }
            return inside;
        }
    }

    void NativeCreation::PaintView()
    {
        if (stopped_ || !Get<Primitives::ToggleButton>(L"UvVisible").IsChecked().GetBoolean())
            return;
        auto area = Get<Grid>(L"UvArea"), host = Get<Grid>(L"UvHost");
        auto texture = SelectedTexture();
        double ratio = texture ? double(texture->Image.Width) / texture->Image.Height : 1;
        double width = std::min(area.ActualWidth(), area.ActualHeight() * ratio), height = width / ratio;
        if (width <= 0 || height <= 0)
            return;
        if (host.Width() != width)
            host.Width(width);
        if (host.Height() != height)
            host.Height(height);
        float w = float(width), h = float(height);
        auto clip = host.Clip().try_as<Media::RectangleGeometry>();
        if (!clip)
        {
            clip = Media::RectangleGeometry();
            host.Clip(clip);
        }
        if (clip.Rect().Width != w || clip.Rect().Height != h)
            clip.Rect({0, 0, w, h});
        auto image = Get<Image>(L"TextureImage");
        if (auto t = SelectedTexture())
        {
            if (!displayedPaint_ || !t->Image.SamePixels(*displayedPaint_))
            {
                auto pixels = t->Image.Composite(true);
                Media::Imaging::WriteableBitmap bitmap(t->Image.Width, t->Image.Height);
                auto buffer = bitmap.PixelBuffer().as<::Windows::Storage::Streams::IBufferByteAccess>();
                byte *data = nullptr;
                check_hresult(buffer->Buffer(&data));
                std::copy(pixels.begin(), pixels.end(), data);
                bitmap.Invalidate();
                image.Source(bitmap);
                displayedPaint_ = t->Image;
            }
        }
        else
        {
            image.Source(nullptr);
            displayedPaint_.reset();
        }
        auto key = std::to_string(w) + ":" + std::to_string(h) + ":" + object_;
        if (texture && texture->Kind == "overlay")
            key += texture->Target + texture->ClothingName + project_->State().Token +
                   std::to_string(Get<ComboBox>(L"Clothing").SelectedIndex());
        else
            key += project_->State().Token + Json(selection_.Ids).dump();
        if (key == displayedUvKey_ && displayedUvReference_ == reference_)
            return;
        displayedUvKey_ = key;
        displayedUvReference_ = reference_;
        auto canvas = Get<Canvas>(L"UvCanvas");
        canvas.Children().Clear();
        if (texture && texture->Kind == "overlay" && reference_)
        {
            Media::PathGeometry geometry;
            auto polygon = [&](const std::vector<Vector2> &points)
            {
                if (points.empty())
                    return;
                Media::PathFigure figure;
                figure.StartPoint({points.front().x * w, (1 - points.front().y) * h});
                figure.IsClosed(true);
                figure.IsFilled(false);
                for (size_t i = 1; i < points.size(); ++i)
                {
                    Media::LineSegment line;
                    line.Point({points[i].x * w, (1 - points[i].y) * h});
                    figure.Segments().Append(line);
                }
                geometry.Figures().Append(figure);
            };
            bool authored = false;
            if (texture->Target == "clothes")
                for (const auto &asset : project_->State().Assets)
                    if (asset.Kind == "clothes" &&
                        asset.TargetPath ==
                            "clothes.parts[" + std::to_string(ClothingOverlaySlot(texture->ClothingName)) + "].id")
                    {
                        authored = true;
                        int state = Get<ComboBox>(L"Clothing").SelectedIndex();
                        for (const auto &object : asset.Objects)
                            if (!object.Hidden && state != 2 &&
                                object.HalfState == (state == 1 && asset.IndependentHalfState))
                                for (const auto &[id, face] : object.Mesh.Faces)
                                {
                                    std::vector<Vector2> points;
                                    for (const auto &corner : face.Corners)
                                        points.push_back(corner.Uv);
                                    polygon(points);
                                }
                    }
            for (const auto &mesh : reference_->Meshes)
                if (!authored && OverlayMatches(mesh, *texture))
                {
                    for (size_t i = 0; i + 2 < mesh.Indices.size(); i += 3)
                    {
                        polygon({mesh.Vertices.at(mesh.Indices[i]).Uv, mesh.Vertices.at(mesh.Indices[i + 1]).Uv,
                                 mesh.Vertices.at(mesh.Indices[i + 2]).Uv});
                    }
                }
            Shapes::Path lines;
            lines.Data(geometry);
            lines.Stroke(Media::SolidColorBrush(winrt::Windows::UI::Color{95, 170, 190, 210}));
            lines.StrokeThickness(.5);
            canvas.Children().Append(lines);
        }
        else if (auto object = SelectedObject())
            for (auto &[id, f] : object->Mesh.Faces)
            {
                Shapes::Polygon polygon;
                auto selected = selection_.Ids.contains(id);
                polygon.Stroke(Media::SolidColorBrush(selected ? winrt::Windows::UI::Color{255, 255, 160, 40}
                                                               : winrt::Windows::UI::Color{200, 170, 190, 210}));
                polygon.StrokeThickness(selected ? 2 : 1);
                for (auto &c : f.Corners)
                    polygon.Points().Append({c.Uv.x * w, (1 - c.Uv.y) * h});
                canvas.Children().Append(polygon);
            }
    }

    void NativeCreation::SelectAt(Vector2 point, bool extend)
    {
        auto host = Get<Grid>(L"ViewportHost");
        auto [origin, direction] = camera_.Ray(point, float(host.ActualWidth()), float(host.ActualHeight()));
        std::optional<MeshHit> hit;
        std::string object, asset;
        for (auto &a : project_->State().Assets)
            for (auto &o : a.Objects)
            {
                bool half =
                    a.Kind == "clothes" && Get<ComboBox>(L"Clothing").SelectedIndex() == 1 && a.IndependentHalfState;
                if (a.Kind == "clothes" && Get<ComboBox>(L"Clothing").SelectedIndex() == 2)
                    continue;
                if (o.Hidden || o.Locked || o.HalfState != half)
                    continue;
                auto candidate = o.Mesh.Pick(origin, direction, true);
                if (candidate && (!hit || candidate->Distance < hit->Distance))
                {
                    hit = candidate;
                    object = o.Id;
                    asset = a.Id;
                }
            }
        if (!extend)
            selection_.Ids.clear();
        if (!hit)
        {
            if (!extend && selection_.Mode == SelectionMode::Object)
            {
                objectSelection_.clear();
                object_.clear();
            }
            Refresh(selection_.Mode == SelectionMode::Object);
            return;
        }
        bool changed = object_ != object;
        if (selection_.Mode == SelectionMode::Object)
        {
            if (!extend || asset_ != asset)
                objectSelection_.clear();
            if (extend && objectSelection_.contains(object))
            {
                objectSelection_.erase(object);
                object_ = objectSelection_.empty() ? "" : *objectSelection_.begin();
                Refresh();
                return;
            }
            objectSelection_.insert(object);
        }
        else
            objectSelection_ = {object};
        object_ = object;
        asset_ = asset;
        auto &mesh = SelectedObject()->Mesh;
        if (selection_.Mode == SelectionMode::Face || selection_.Mode == SelectionMode::Uv)
        {
            if (selection_.Mode == SelectionMode::Uv)
            {
                for (auto &island : mesh.UvIslands())
                    if (std::find(island.begin(), island.end(), hit->Face) != island.end())
                        selection_.Ids.insert(island.begin(), island.end());
            }
            else if (extend && selection_.Ids.contains(hit->Face))
                selection_.Ids.erase(hit->Face);
            else
                selection_.Ids.insert(hit->Face);
        }
        else if (selection_.Mode == SelectionMode::Vertex)
        {
            float best = 15;
            ElementId id = 0;
            for (auto &[key, v] : mesh.Vertices)
            {
                float distance =
                    Distance(point, camera_.Project(v.Position, float(host.ActualWidth()), float(host.ActualHeight())));
                if (distance < best)
                {
                    best = distance;
                    id = key;
                }
            }
            if (id)
            {
                if (extend && selection_.Ids.contains(id))
                    selection_.Ids.erase(id);
                else
                    selection_.Ids.insert(id);
            }
        }
        else if (selection_.Mode == SelectionMode::Edge)
        {
            float best = 12;
            ElementId id = 0;
            for (auto &[key, e] : mesh.Edges)
            {
                auto a = camera_.Project(mesh.Vertices.at(e.A).Position, float(host.ActualWidth()),
                                         float(host.ActualHeight())),
                     b = camera_.Project(mesh.Vertices.at(e.B).Position, float(host.ActualWidth()),
                                         float(host.ActualHeight()));
                float distance = Segment(point, a, b);
                if (distance < best)
                {
                    best = distance;
                    id = e.Id;
                }
                (void)key;
            }
            if (id)
            {
                if (extend && selection_.Ids.contains(id))
                    selection_.Ids.erase(id);
                else
                    selection_.Ids.insert(id);
            }
        }
        Refresh(changed);
    }

    void NativeCreation::BrushAt(Vector2 point, bool uv)
    {
        int tool = Get<ComboBox>(L"Tool").SelectedIndex();
        auto object = SelectedObject();
        auto texture = SelectedTexture();
        std::optional<MeshHit> hit;
        Vector2 at{};
        if (uv)
        {
            auto host = Get<Grid>(L"UvHost");
            if (!texture)
                return;
            at = {point.x / float(host.ActualWidth()) * texture->Image.Width,
                  point.y / float(host.ActualHeight()) * texture->Image.Height};
        }
        else
        {
            auto host = Get<Grid>(L"ViewportHost");
            auto [o, d] = camera_.Ray(point, float(host.ActualWidth()), float(host.ActualHeight()));
            if (tool >= 13 && texture && texture->Kind == "overlay")
            {
                if (texture->Target == "clothes" && Get<ComboBox>(L"Clothing").SelectedIndex() == 2)
                    return;
                if (reference_ != pickedReference_)
                {
                    referenceMeshes_.clear();
                    if (reference_)
                        for (auto &m : reference_->Meshes)
                            referenceMeshes_.push_back(MeshDocument::FromMesh(m));
                    pickedReference_ = reference_;
                }
                bool authoredClothes =
                    texture->Target == "clothes" &&
                    std::any_of(project_->State().Assets.begin(), project_->State().Assets.end(),
                                [&](const auto &a)
                                {
                                    return a.Kind == "clothes" &&
                                           a.TargetPath ==
                                               "clothes.parts[" +
                                                   std::to_string(ClothingOverlaySlot(texture->ClothingName)) + "].id";
                                });
                for (size_t index = 0; index < referenceMeshes_.size(); ++index)
                {
                    auto &mesh = referenceMeshes_[index];
                    if (authoredClothes)
                        continue;
                    bool matches = OverlayMatches(reference_->Meshes[index], *texture);
                    if (!matches)
                        continue;
                    auto candidate = mesh.Pick(o, d, true);
                    if (candidate && (!hit || candidate->Distance < hit->Distance))
                        hit = candidate;
                }
                if (texture->Target == "clothes" && Get<ComboBox>(L"Clothing").SelectedIndex() != 2)
                    for (const auto &a : project_->State().Assets)
                        if (a.Kind == "clothes" &&
                            a.TargetPath ==
                                "clothes.parts[" + std::to_string(ClothingOverlaySlot(texture->ClothingName)) + "].id")
                            for (const auto &mesh : a.Objects)
                                if (!mesh.Hidden && !mesh.Locked &&
                                    mesh.HalfState ==
                                        (a.IndependentHalfState && Get<ComboBox>(L"Clothing").SelectedIndex() == 1))
                                {
                                    auto candidate = mesh.Mesh.Pick(o, d, true);
                                    if (candidate && (!hit || candidate->Distance < hit->Distance))
                                        hit = candidate;
                                }
            }
            else
            {
                if (!object || object->Locked)
                    return;
                hit = object->Mesh.Pick(o, d, true);
            }
            if (!hit)
                return;
            if (texture)
                at = {hit->Uv.x * texture->Image.Width, (1 - hit->Uv.y) * texture->Image.Height};
        }
        float strength = float(Get<Slider>(L"Strength").Value()), radius = float(Get<NumberBox>(L"Radius").Value());
        if (tool >= 13)
        {
            if (!texture)
                throw std::runtime_error("请先新建或选择贴图");
            if (tool == 16)
            {
                color_ = texture->Image.Sample(at);
                return;
            }
            project_->Edit("贴图笔画",
                           [&](CreationState &s)
                           {
                               auto &paint = Texture(s).Image;
                               if (tool == 15)
                                   paint.Fill(layer_, at, color_);
                               else
                               {
                                   std::vector<Vector2> points;
                                   if (!stroke_.empty() &&
                                       Distance(stroke_.back(), at) < std::max(paint.Width, paint.Height) * .25f)
                                       points.push_back(stroke_.back());
                                   points.push_back(at);
                                   paint.Stroke(layer_, points, color_, std::max(1.f, radius * paint.Width), strength,
                                                float(Get<Slider>(L"Hardness").Value()), tool == 14);
                               }
                           });
            stroke_.push_back(at);
        }
        else if (hit && tool >= 5 && tool <= 12)
        {
            auto host = Get<Grid>(L"ViewportHost");
            auto delta = camera_.Right() * ((point.x - last_.x) * 2 * camera_.Scale / float(host.ActualHeight())) +
                         camera_.Up() * ((last_.y - point.y) * 2 * camera_.Scale / float(host.ActualHeight()));
            const char *ops[]{"sculpt_move",    "sculpt_inflate", "sculpt_inflate", "sculpt_smooth",
                              "sculpt_flatten", "sculpt_pinch",   "mask",           "weight_paint"};
            auto bone = to_string(unbox_value_or<hstring>(Get<ComboBox>(L"Bones").SelectedItem(), L""));
            Json args{{"center", asset::VectorJson(hit->Position)},
                      {"normal", asset::VectorJson(hit->Normal)},
                      {"delta", asset::VectorJson(delta)},
                      {"radius", radius},
                      {"strength", strength * (tool == 7 ? -1 : 1)},
                      {"falloff", 1 + 4 * (1 - Get<Slider>(L"Hardness").Value())},
                      {"symmetry", Get<CheckBox>(L"Symmetry").IsChecked().GetBoolean() ? 0 : -1},
                      {"bone", bone}};
            if (GetKeyState(VK_CONTROL) & 0x8000)
            {
                if (tool == 12)
                    args["value"] = 0;
                else
                    args["strength"] = -args["strength"].get<float>();
            }
            project_->Edit("网格笔画",
                           [&](CreationState &s)
                           {
                               args["locked_bones"] = Asset(s).LockedBones;
                               ObjectOf(s).Mesh.Execute({ops[tool - 5], args}, {});
                           });
        }
    }

    void NativeCreation::BindPointers()
    {
        std::weak_ptr<NativeCreation> weak = shared_from_this();
        root_.KeyDown(
            [weak](auto &&, Input::KeyRoutedEventArgs const &args)
            {
                auto s = weak.lock();
                if (!s || s->stopped_ || s->Get<Grid>(L"ModelHost").Visibility() != Visibility::Visible)
                    return;
                auto owner = s->owner_.lock();
                if (!owner || owner->dialogOpen_)
                    return;
                auto focus = Input::FocusManager::GetFocusedElement(s->root_.XamlRoot()).try_as<DependencyObject>();
                while (focus)
                {
                    if (focus.try_as<TextBox>() || focus.try_as<RichEditBox>() || focus.try_as<NumberBox>())
                        return;
                    focus = Media::VisualTreeHelper::GetParent(focus);
                }
                int key = static_cast<int>(args.Key());
                bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                if (ctrl)
                {
                    if (key == 'Z' || key == 'Y')
                    {
                        s->Action(key == 'Y' || (GetKeyState(VK_SHIFT) & 0x8000) ? "Redo" : "Undo");
                        args.Handled(true);
                    }
                    return;
                }
                if (key == VK_ESCAPE)
                {
                    if (s->pointer_)
                    {
                        s->pointer_ = false;
                        s->project_->CancelGesture();
                        s->dragMesh_.reset();
                    }
                    else
                    {
                        s->selection_.Ids.clear();
                        if (s->selection_.Mode == SelectionMode::Object)
                        {
                            s->objectSelection_.clear();
                            s->object_.clear();
                        }
                    }
                    s->Refresh(s->selection_.Mode == SelectionMode::Object);
                }
                else if (key == 'G' || key == 'R' || key == 'S' || key == 'B')
                    s->Get<ComboBox>(L"Tool").SelectedIndex(key == 'G' ? 2 : key == 'R' ? 3 : key == 'S' ? 4 : 1);
                else if (key == 'X' || key == 'Y' || key == 'Z')
                    s->Get<ComboBox>(L"Axis").SelectedIndex(key == 'X' ? 1 : key == 'Y' ? 2 : 3);
                else if (key == 'F')
                    s->Action("Frame");
                else if (key == VK_DELETE)
                    s->Command("delete");
                else if (key == 'A')
                {
                    if (s->selection_.Mode == SelectionMode::Object)
                    {
                        s->objectSelection_.clear();
                        for (auto &a : s->project_->State().Assets)
                            if (a.Id == s->asset_)
                                for (auto &o : a.Objects)
                                    if (!o.Hidden && !o.Locked)
                                        s->objectSelection_.insert(o.Id);
                        if (!s->objectSelection_.empty())
                            s->object_ = *s->objectSelection_.begin();
                        s->Refresh();
                    }
                    else if (auto o = s->SelectedObject())
                    {
                        s->selection_.Ids.clear();
                        if (s->selection_.Mode == SelectionMode::Vertex)
                            for (auto &[id, v] : o->Mesh.Vertices)
                                s->selection_.Ids.insert(id);
                        else if (s->selection_.Mode == SelectionMode::Edge)
                            for (auto &[edgeKey, e] : o->Mesh.Edges)
                                s->selection_.Ids.insert(e.Id);
                        else
                            for (auto &[id, f] : o->Mesh.Faces)
                                s->selection_.Ids.insert(id);
                        s->Refresh(false);
                    }
                }
                else
                    return;
                args.Handled(true);
            });
        auto bind = [&](Grid host, bool uv)
        {
            host.PointerPressed(
                [weak, uv](auto const &sender, Input::PointerRoutedEventArgs const &args)
                {
                    if (auto s = weak.lock())
                    {
                        auto host = sender.template as<Grid>();
                        auto point = args.GetCurrentPoint(host);
                        auto p = point.Position();
                        s->start_ = s->last_ = {p.X, p.Y};
                        s->Get<Button>(L"Frame").Focus(FocusState::Programmatic);
                        s->dragAxis_ = s->Get<ComboBox>(L"Axis").SelectedIndex();
                        auto props = point.Properties();
                        s->orbit_ = props.IsRightButtonPressed();
                        s->pan_ = props.IsMiddleButtonPressed();
                        s->pointer_ = true;
                        s->uvPointer_ = uv;
                        s->stroke_.clear();
                        s->dragMesh_.reset();
                        s->dragObjects_.clear();
                        host.CapturePointer(args.Pointer());
                        int tool = s->Get<ComboBox>(L"Tool").SelectedIndex();
                        try
                        {
                            if (s->orbit_ || s->pan_)
                            {
                                args.Handled(true);
                                return;
                            }
                            if (s->fit_.State)
                            {
                                s->pointer_ = false;
                                host.ReleasePointerCaptures();
                                s->Message("请先返回编辑姿态，再选择或修改网格");
                                args.Handled(true);
                                return;
                            }
                            if (uv && tool < 13 && s->SelectedTexture() && s->SelectedTexture()->Kind == "overlay")
                            {
                                s->pointer_ = false;
                                host.ReleasePointerCaptures();
                                s->Message("覆盖贴图参考 UV 仅用于绘制，请选择贴图画笔、橡皮、填充或取色");
                                args.Handled(true);
                                return;
                            }
                            bool extend = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                            if (!uv && tool == 0)
                                s->SelectAt(s->start_, extend);
                            if (!uv && tool >= 2 && tool <= 4)
                                if (auto object = s->SelectedObject())
                                {
                                    auto center = s->SelectionCenter();
                                    auto a = s->camera_.Project(center, float(host.ActualWidth()),
                                                                float(host.ActualHeight()));
                                    float best = 9;
                                    int axis = 1;
                                    for (auto direction :
                                         {Vector3{s->camera_.Scale * .2f, 0, 0}, Vector3{0, s->camera_.Scale * .2f, 0},
                                          Vector3{0, 0, s->camera_.Scale * .2f}})
                                    {
                                        auto b = s->camera_.Project(center + direction, float(host.ActualWidth()),
                                                                    float(host.ActualHeight()));
                                        float distance = Segment(s->start_, a, b);
                                        if (Distance(a, b) > 8 && Distance(s->start_, a) > 5 && distance < best)
                                        {
                                            best = distance;
                                            s->dragAxis_ = axis;
                                        }
                                        ++axis;
                                    }
                                }
                            if (uv && tool < 5)
                            {
                                auto o = s->SelectedObject();
                                if (o)
                                {
                                    Vector2 at{p.X / float(host.ActualWidth()), 1 - p.Y / float(host.ActualHeight())};
                                    if (!extend)
                                        s->selection_.Ids.clear();
                                    s->selection_.Mode = SelectionMode::Uv;
                                    s->Get<ComboBox>(L"Selection").SelectedIndex(4);
                                    for (auto &[id, face] : o->Mesh.Faces)
                                        if (InPolygon(at, face))
                                        {
                                            for (auto &island : o->Mesh.UvIslands())
                                                if (std::find(island.begin(), island.end(), id) != island.end())
                                                    s->selection_.Ids.insert(island.begin(), island.end());
                                            break;
                                        }
                                    s->Refresh(false);
                                }
                            }
                            if (tool >= 2)
                            {
                                s->project_->BeginGesture();
                                if (auto o = s->SelectedObject())
                                    s->dragMesh_ = o->Mesh;
                                s->dragPivot_ = s->SelectionCenter();
                                if (!uv && s->selection_.Mode == SelectionMode::Object)
                                    for (auto &a : s->project_->State().Assets)
                                        if (a.Id == s->asset_)
                                            for (auto &o : a.Objects)
                                                if (s->ObjectSelected(o) && !o.Locked)
                                                    s->dragObjects_[o.Id] = o.Mesh;
                                if (tool >= 5)
                                    s->BrushAt(s->start_, uv);
                            }
                        }
                        catch (const std::exception &e)
                        {
                            s->Message(e.what());
                        }
                        args.Handled(true);
                    }
                });
            host.PointerMoved(
                [weak, uv](auto const &sender, Input::PointerRoutedEventArgs const &args)
                {
                    auto s = weak.lock();
                    if (!s || !s->pointer_ || s->uvPointer_ != uv)
                        return;
                    auto host = sender.template as<Grid>();
                    auto point = args.GetCurrentPoint(host).Position();
                    Vector2 p{point.X, point.Y};
                    int tool = s->Get<ComboBox>(L"Tool").SelectedIndex();
                    try
                    {
                        if (s->orbit_)
                        {
                            if (!uv)
                            {
                                s->camera_.Yaw += (p.x - s->last_.x) * .008f;
                                s->camera_.Pitch =
                                    std::clamp(s->camera_.Pitch + (p.y - s->last_.y) * .008f, -1.5f, 1.5f);
                                s->Viewport();
                            }
                        }
                        else if (s->pan_)
                        {
                            if (!uv)
                            {
                                s->camera_.Center -= s->camera_.Right() * ((p.x - s->last_.x) * 2 * s->camera_.Scale /
                                                                           float(host.ActualHeight()));
                                s->camera_.Center += s->camera_.Up() * ((p.y - s->last_.y) * 2 * s->camera_.Scale /
                                                                        float(host.ActualHeight()));
                                s->Viewport();
                            }
                        }
                        else if (tool == 1)
                        {
                            auto canvas = s->Get<Canvas>(uv ? L"UvSelectionCanvas" : L"SelectionCanvas");
                            canvas.Children().Clear();
                            Shapes::Rectangle box;
                            box.Width(std::abs(p.x - s->start_.x));
                            box.Height(std::abs(p.y - s->start_.y));
                            box.Stroke(Media::SolidColorBrush(winrt::Windows::UI::Color{255, 255, 170, 40}));
                            box.StrokeThickness(1);
                            Canvas::SetLeft(box, std::min(p.x, s->start_.x));
                            Canvas::SetTop(box, std::min(p.y, s->start_.y));
                            canvas.Children().Append(box);
                        }
                        else if (tool >= 2 && tool <= 4 && s->dragMesh_)
                        {
                            auto base = *s->dragMesh_;
                            auto dx = p.x - s->start_.x, dy = s->start_.y - p.y;
                            Json transform;
                            if (uv)
                            {
                                transform = {{"position", {dx / host.ActualWidth(), dy / host.ActualHeight()}}};
                                if (tool == 3)
                                    transform = {{"rotation", dx * .5f}};
                                if (tool == 4)
                                {
                                    float v = std::exp(dx * .01f);
                                    transform = {{"scale", {v, v}}};
                                }
                            }
                            else
                            {
                                auto delta = (s->camera_.Right() * dx + s->camera_.Up() * dy) *
                                             (2 * s->camera_.Scale / float(host.ActualHeight()));
                                int axis = s->dragAxis_;
                                if (axis && tool == 2)
                                {
                                    Vector3 direction{};
                                    (&direction.x)[axis - 1] = 1;
                                    auto center = base.Center(s->selection_);
                                    auto a = s->camera_.Project(center, float(host.ActualWidth()),
                                                                float(host.ActualHeight())),
                                         b = s->camera_.Project(center + direction, float(host.ActualWidth()),
                                                                float(host.ActualHeight()));
                                    float x = b.x - a.x, y = b.y - a.y;
                                    delta = direction * ((dx * x - dy * y) / std::max(1e-6f, x * x + y * y));
                                }
                                if (axis == 1)
                                    delta = {delta.x, 0, 0};
                                if (axis == 2)
                                    delta = {0, delta.y, 0};
                                if (axis == 3)
                                    delta = {0, 0, delta.z};
                                if (s->Get<CheckBox>(L"VertexSnap").IsChecked().GetBoolean() && tool == 2)
                                {
                                    auto pivot = base.Center(s->selection_);
                                    float best = 15;
                                    auto selected = base.SelectedVertices(s->selection_);
                                    for (auto &a : s->project_->State().Assets)
                                        for (auto &o : a.Objects)
                                            if (!o.Hidden)
                                                for (auto &vertex : o.Mesh.Vertices)
                                                    if (o.Id != s->object_ || !selected.contains(vertex.first))
                                                    {
                                                        auto screen = s->camera_.Project(vertex.second.Position,
                                                                                         float(host.ActualWidth()),
                                                                                         float(host.ActualHeight()));
                                                        auto d = Distance(screen, p);
                                                        if (d < best)
                                                        {
                                                            best = d;
                                                            delta = vertex.second.Position - pivot;
                                                        }
                                                    }
                                }
                                transform = {{"position", asset::VectorJson(delta)},
                                             {"snap", s->Get<NumberBox>(L"Snap").Value()}};
                                if (tool == 3)
                                    transform = {{"rotation", axis == 1   ? Json::array({dx, 0, 0})
                                                              : axis == 3 ? Json::array({0, 0, dx})
                                                                          : Json::array({0, dx, 0})}};
                                if (tool == 4)
                                {
                                    float v = std::exp(dx * .01f);
                                    transform = {{"scale",
                                                  {axis == 0 || axis == 1 ? v : 1, axis == 0 || axis == 2 ? v : 1,
                                                   axis == 0 || axis == 3 ? v : 1}}};
                                }
                            }
                            s->project_->Edit("变换",
                                              [&](CreationState &state)
                                              {
                                                  if (!uv && s->selection_.Mode == SelectionMode::Object)
                                                  {
                                                      transform["pivot"] = asset::VectorJson(s->dragPivot_);
                                                      for (auto &o : s->Asset(state).Objects)
                                                          if (s->dragObjects_.contains(o.Id))
                                                          {
                                                              o.Mesh = s->dragObjects_.at(o.Id);
                                                              o.Mesh.Execute({"transform", transform}, {});
                                                          }
                                                  }
                                                  else
                                                  {
                                                      auto &mesh = s->ObjectOf(state).Mesh;
                                                      mesh = base;
                                                      mesh.Execute({uv ? "uv_transform" : "transform", transform},
                                                                   s->selection_);
                                                  }
                                              });
                        }
                        else if (tool >= 5 && Distance(p, s->last_) >= 1)
                            s->BrushAt(p, uv);
                    }
                    catch (const std::exception &e)
                    {
                        s->Message(e.what());
                    }
                    s->last_ = p;
                    args.Handled(true);
                });
            host.PointerReleased(
                [weak, uv](auto const &sender, Input::PointerRoutedEventArgs const &args)
                {
                    if (auto s = weak.lock())
                        if (s->pointer_ && s->uvPointer_ == uv)
                        {
                            auto host = sender.template as<Grid>();
                            auto p = args.GetCurrentPoint(host).Position();
                            int tool = s->Get<ComboBox>(L"Tool").SelectedIndex();
                            if (tool == 1 && !s->orbit_ && !s->pan_)
                            {
                                if (!uv && s->selection_.Mode == SelectionMode::Object)
                                {
                                    if (!(GetKeyState(VK_SHIFT) & 0x8000))
                                        s->objectSelection_.clear();
                                    for (auto &a : s->project_->State().Assets)
                                        if (a.Id == s->asset_)
                                            for (auto &o : a.Objects)
                                            {
                                                bool half = a.Kind == "clothes" && a.IndependentHalfState &&
                                                            s->Get<ComboBox>(L"Clothing").SelectedIndex() == 1;
                                                if (o.Hidden || o.Locked || o.HalfState != half ||
                                                    (a.Kind == "clothes" &&
                                                     s->Get<ComboBox>(L"Clothing").SelectedIndex() == 2))
                                                    continue;
                                                if (std::any_of(o.Mesh.Vertices.begin(), o.Mesh.Vertices.end(),
                                                                [&](auto &vertex)
                                                                {
                                                                    auto q =
                                                                        s->camera_.Project(vertex.second.Position,
                                                                                           float(host.ActualWidth()),
                                                                                           float(host.ActualHeight()));
                                                                    return q.x >= std::min(p.X, s->start_.x) &&
                                                                           q.x <= std::max(p.X, s->start_.x) &&
                                                                           q.y >= std::min(p.Y, s->start_.y) &&
                                                                           q.y <= std::max(p.Y, s->start_.y);
                                                                }))
                                                    s->objectSelection_.insert(o.Id);
                                            }
                                    s->object_ = s->objectSelection_.empty() ? "" : *s->objectSelection_.begin();
                                }
                                else if (auto o = s->SelectedObject())
                                {
                                    if (!(GetKeyState(VK_SHIFT) & 0x8000))
                                        s->selection_.Ids.clear();
                                    std::set<ElementId> vertices;
                                    if (uv)
                                    {
                                        s->selection_.Mode = SelectionMode::Uv;
                                        for (auto &[id, f] : o->Mesh.Faces)
                                            if (std::any_of(f.Corners.begin(), f.Corners.end(),
                                                            [&](auto &c)
                                                            {
                                                                Vector2 q{c.Uv.x * float(host.ActualWidth()),
                                                                          (1 - c.Uv.y) * float(host.ActualHeight())};
                                                                return q.x >= std::min(p.X, s->start_.x) &&
                                                                       q.x <= std::max(p.X, s->start_.x) &&
                                                                       q.y >= std::min(p.Y, s->start_.y) &&
                                                                       q.y <= std::max(p.Y, s->start_.y);
                                                            }))
                                                for (auto &island : o->Mesh.UvIslands())
                                                    if (std::find(island.begin(), island.end(), id) != island.end())
                                                        s->selection_.Ids.insert(island.begin(), island.end());
                                    }
                                    else
                                        for (const auto &vertex : o->Mesh.Vertices)
                                        {
                                            auto q =
                                                s->camera_.Project(vertex.second.Position, float(host.ActualWidth()),
                                                                   float(host.ActualHeight()));
                                            if (q.x >= std::min(p.X, s->start_.x) &&
                                                q.x <= std::max(p.X, s->start_.x) &&
                                                q.y >= std::min(p.Y, s->start_.y) && q.y <= std::max(p.Y, s->start_.y))
                                                vertices.insert(vertex.first);
                                        }
                                    if (s->selection_.Mode == SelectionMode::Vertex)
                                        s->selection_.Ids.insert(vertices.begin(), vertices.end());
                                    else if (s->selection_.Mode == SelectionMode::Edge)
                                        for (auto &edge : o->Mesh.Edges)
                                        {
                                            if (vertices.contains(edge.second.A) && vertices.contains(edge.second.B))
                                                s->selection_.Ids.insert(edge.second.Id);
                                        }
                                    else
                                        for (auto &face : o->Mesh.Faces)
                                            if (std::all_of(face.second.Corners.begin(), face.second.Corners.end(),
                                                            [&](auto &c)
                                                            {
                                                                return vertices.contains(c.Vertex);
                                                            }))
                                                s->selection_.Ids.insert(face.first);
                                }
                            }
                            s->pointer_ = false;
                            s->project_->EndGesture();
                            s->dragMesh_.reset();
                            s->Get<Canvas>(uv ? L"UvSelectionCanvas" : L"SelectionCanvas").Children().Clear();
                            host.ReleasePointerCaptures();
                            s->Refresh(tool == 1 && s->selection_.Mode == SelectionMode::Object);
                            args.Handled(true);
                        }
                });
            host.PointerCanceled(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                    {
                        s->pointer_ = false;
                        s->project_->CancelGesture();
                        s->dragMesh_.reset();
                    }
                });
            host.PointerCaptureLost(
                [weak](auto &&, auto &&)
                {
                    if (auto s = weak.lock())
                        if (s->pointer_)
                        {
                            s->pointer_ = false;
                            s->project_->CancelGesture();
                            s->dragMesh_.reset();
                        }
                });
            host.PointerWheelChanged(
                [weak, uv](auto const &sender, Input::PointerRoutedEventArgs const &args)
                {
                    if (auto s = weak.lock())
                        if (!uv)
                        {
                            auto point = args.GetCurrentPoint(sender.template as<Grid>());
                            s->camera_.Scale =
                                std::clamp(s->camera_.Scale * std::exp(-point.Properties().MouseWheelDelta() * .001f),
                                           .005f, 100.f);
                            s->Viewport();
                            args.Handled(true);
                        }
                });
        };
        bind(Get<Grid>(L"ViewportHost"), false);
        bind(Get<Grid>(L"UvHost"), true);
    }
}
