#include "Preview/NativePreview.h"
#include "Editor/NativeEditor.h"
#include <Shobjidl.h>
#include <cmath>
#include <fstream>
#include <microsoft.ui.xaml.media.dxinterop.h>
#include <winrt/Microsoft.UI.Input.h>
using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    namespace
    {
        constexpr double Pi = 3.14159265358979323846;

        std::string JoinLines(std::vector<std::string> const &lines)
        {
            std::string result;
            for (auto const &line : lines)
            {
                if (!result.empty())
                    result += '\n';
                result += line;
            }
            return result;
        }
    } // 命名空间

    std::shared_ptr<NativePreview> NativePreview::Create(std::shared_ptr<NativeWindow> const &owner)
    {
        auto self = std::shared_ptr<NativePreview>(new NativePreview(owner));
        self->Initialize();
        return self;
    }

    NativePreview::NativePreview(std::shared_ptr<NativeWindow> const &owner) : owner_(owner)
    {
    }

    NativePreview::~NativePreview()
    {
        Stop();
    }

    void NativePreview::Initialize()
    {
        auto owner = owner_.lock();
        std::weak_ptr<NativePreview> weak = shared_from_this();
        auto queue = owner->dispatcher_;
        auto epoch = ++epoch_;
        PreviewCallbacks callbacks;
        callbacks.Ready = [weak, queue, epoch](PreviewReady result)
        {
            queue.TryEnqueue(
                [weak, epoch, result = std::move(result)]
                {
                    if (auto self = weak.lock())
                        if (auto owner = self->owner_.lock())
                            if (self->epoch_ == epoch && self->generation_ == result.Generation && self->panel_)
                            {
                                try
                                {
                                    check_hresult(
                                        self->panel_.as<ISwapChainPanelNative>()->SetSwapChain(result.Chain.Get()));
                                    if (self->editor_ && owner->editor_)
                                        owner->editor_->Definition(result.Definition);
                                    self->body_ = result.Body;
                                    self->face_ = result.Face;
                                    if (self->first_)
                                    {
                                        self->Frame();
                                        self->first_ = false;
                                    }
                                    else
                                        self->Camera();
                                    owner->model_->Put(L"PreviewBusy", false);
                                    owner->model_->Put(L"CanControlPreview", true);
                                    owner->model_->Text(L"PreviewWarnings", JoinLines(result.Diagnostics));
                                    owner->model_->Put(L"HasPreviewWarnings", !result.Diagnostics.empty());
                                    owner->model_->Put(L"PreviewStatus", L"原生 Direct3D 11 · 游戏环境");
                                }
                                catch (hresult_error const &e)
                                {
                                    owner->model_->Text(L"PreviewError", to_string(e.message()));
                                    owner->model_->Put(L"HasPreviewError", true);
                                }
                            }
                });
        };
        callbacks.Progress = [weak, queue, epoch](uint64_t generation, std::string message)
        {
            queue.TryEnqueue(
                [weak, epoch, generation, message = std::move(message)]
                {
                    if (auto self = weak.lock())
                        if (self->epoch_ == epoch && self->generation_ == generation)
                            if (auto owner = self->owner_.lock())
                                owner->model_->Text(L"PreviewStatus", message);
                });
        };
        callbacks.Failure = [weak, queue, epoch](uint64_t generation, std::string message)
        {
            queue.TryEnqueue(
                [weak, epoch, generation, message = std::move(message)]
                {
                    if (auto self = weak.lock())
                        if (self->epoch_ == epoch && self->generation_ == generation)
                            if (auto owner = self->owner_.lock())
                            {
                                owner->model_->Put(L"PreviewBusy", false);
                                owner->model_->Put(L"CanControlPreview", false);
                                owner->model_->Text(L"PreviewError", message);
                                owner->model_->Put(L"HasPreviewError", true);
                                owner->model_->Put(L"PreviewStatus", L"预览未就绪，可重试");
                                fs::create_directories(owner->dataDirectory_ / L"Logs");
                                std::ofstream(owner->dataDirectory_ / L"Logs" / L"native-preview.log", std::ios::app)
                                    << message << '\n';
                            }
                });
        };
        callbacks.Screenshot = [weak, queue, epoch](fs::path path, std::string error)
        {
            queue.TryEnqueue(
                [weak, epoch, path = std::move(path), error = std::move(error)]
                {
                    if (auto self = weak.lock())
                        if (auto owner = self->owner_.lock())
                            owner->Report(error.empty() ? "截图已保存：" + Utf8(path) : "截图未保存：" + error);
                });
        };
        worker_ = std::make_unique<NativePreviewWorker>(Path(owner->settings_.GamePath), owner->dataDirectory_,
                                                        ExecutableDirectory() / L"classdata.tpk", std::move(callbacks));
    }

    void NativePreview::Attach(FrameworkElement view, bool editor)
    {
        if (worker_)
            worker_->Quiesce();
        for (auto &undo : unbind_)
            undo();
        unbind_.clear();
        if (panel_)
            check_hresult(panel_.as<ISwapChainPanelNative>()->SetSwapChain(nullptr));
        view_ = view;
        editor_ = editor;
        viewport_ = view.FindName(editor ? L"EditorViewport" : L"Viewport").as<FrameworkElement>();
        panel_ = view.FindName(editor ? L"EditorCompositionViewport" : L"CompositionViewport").as<SwapChainPanel>();
        std::weak_ptr<NativePreview> weak = shared_from_this();
        auto viewport = viewport_;
        auto size = viewport.SizeChanged(
            [weak](auto &&, auto &&)
            {
                if (auto self = weak.lock())
                    self->Resize();
            });
        unbind_.push_back(
            [viewport, size]
            {
                viewport.SizeChanged(size);
            });
        auto loaded = viewport.Loaded(
            [weak](auto &&, auto &&)
            {
                if (auto self = weak.lock())
                    self->Resize();
            });
        unbind_.push_back(
            [viewport, loaded]
            {
                viewport.Loaded(loaded);
            });
        auto pressed = viewport.PointerPressed(
            [weak](auto &&, Input::PointerRoutedEventArgs const &args)
            {
                if (auto self = weak.lock())
                {
                    auto point = args.GetCurrentPoint(self->viewport_);
                    self->dragging_ = true;
                    self->pan_ =
                        point.Properties().IsRightButtonPressed() || point.Properties().IsMiddleButtonPressed();
                    self->pointer_ = point.Position();
                    self->viewport_.CapturePointer(args.Pointer());
                    self->Camera();
                    args.Handled(true);
                }
            });
        unbind_.push_back(
            [viewport, pressed]
            {
                viewport.PointerPressed(pressed);
            });
        auto released = [weak](auto &&, Input::PointerRoutedEventArgs const &args)
        {
            if (auto self = weak.lock())
            {
                self->dragging_ = false;
                self->viewport_.ReleasePointerCapture(args.Pointer());
                self->Camera();
                args.Handled(true);
            }
        };
        auto up = viewport.PointerReleased(released);
        unbind_.push_back(
            [viewport, up]
            {
                viewport.PointerReleased(up);
            });
        auto lost = viewport.PointerCaptureLost(released);
        unbind_.push_back(
            [viewport, lost]
            {
                viewport.PointerCaptureLost(lost);
            });
        auto cancelled = viewport.PointerCanceled(released);
        unbind_.push_back(
            [viewport, cancelled]
            {
                viewport.PointerCanceled(cancelled);
            });
        auto moved = viewport.PointerMoved(
            [weak](auto &&, Input::PointerRoutedEventArgs const &args)
            {
                if (auto self = weak.lock())
                    if (self->dragging_)
                    {
                        auto point = args.GetCurrentPoint(self->viewport_).Position();
                        auto dx = point.X - self->pointer_.X, dy = point.Y - self->pointer_.Y;
                        self->pointer_ = point;
                        self->frame_ = false;
                        if (self->pan_)
                        {
                            using namespace DirectX;
                            auto forward = XMVector3Normalize(XMVectorSubtract(XMLoadFloat3(&self->camera_.Target),
                                                                               XMLoadFloat3(&self->camera_.Position)));
                            auto up = XMLoadFloat3(&self->camera_.Up);
                            auto right = XMVector3Normalize(XMVector3Cross(forward, up));
                            up = XMVector3Cross(right, forward);
                            auto units = 2 * self->distance_ * std::tan(35 * Pi / 360) * self->aspect_ / self->width_;
                            auto center = XMVectorAdd(XMLoadFloat3(&self->center_),
                                                      XMVectorAdd(XMVectorScale(right, static_cast<float>(-dx * units)),
                                                                  XMVectorScale(up, static_cast<float>(dy * units))));
                            XMStoreFloat3(&self->center_, center);
                        }
                        else
                        {
                            self->yaw_ += dx * .45;
                            self->pitch_ = std::clamp(self->pitch_ + dy * .3, -80.0, 80.0);
                        }
                        self->Camera();
                        args.Handled(true);
                    }
            });
        unbind_.push_back(
            [viewport, moved]
            {
                viewport.PointerMoved(moved);
            });
        auto wheel = viewport.PointerWheelChanged(
            [weak](auto &&, Input::PointerRoutedEventArgs const &args)
            {
                if (auto self = weak.lock())
                {
                    auto delta = args.GetCurrentPoint(self->viewport_).Properties().MouseWheelDelta();
                    self->distance_ = std::clamp(self->distance_ * std::exp(-delta / 120.0 * .12), .1, 500.0);
                    self->frame_ = false;
                    self->Camera();
                    args.Handled(true);
                }
            });
        unbind_.push_back(
            [viewport, wheel]
            {
                viewport.PointerWheelChanged(wheel);
            });
        auto doubleTap = viewport.DoubleTapped(
            [weak](auto &&, Input::DoubleTappedRoutedEventArgs const &args)
            {
                if (auto self = weak.lock())
                {
                    self->Frame();
                    args.Handled(true);
                }
            });
        unbind_.push_back(
            [viewport, doubleTap]
            {
                viewport.DoubleTapped(doubleTap);
            });
        Resize();
    }

    void NativePreview::Load(CardRecord card, FrameworkElement view, bool editor,
                             std::optional<CharacterDocument> draft, CharacterPreviewVisibility visibility,
                             bool preserveView)
    {
        auto owner = owner_.lock();
        card_ = std::move(card);
        draft_ = std::move(draft);
        visibility_ = visibility;
        if (!preserveView)
        {
            first_ = true;
            playing_ = true;
            frame_ = true;
            owner->model_->Put(L"PreviewLightIntensity", 1.0);
            owner->model_->Put(L"PreviewBackgroundIndex", 0);
        }
        owner->model_->Put(L"IsPreview", true);
        owner->model_->Put(L"ShowNativeViewport", true);
        if (editor || (!view_ && !preserveView))
            owner->model_->Put(L"ShowPreviewRail", false);
        owner->model_->Put(L"PreviewBusy", true);
        owner->model_->Put(L"CanControlPreview", false);
        owner->model_->Put(L"HasPreviewError", false);
        owner->model_->Put(L"HasPreviewWarnings", false);
        owner->model_->Put(L"PreviewError", L"");
        owner->model_->Put(L"PreviewWarnings", L"");
        owner->model_->Put(L"PreviewStatus", L"正在读取人物资源…");
        owner->model_->Put(L"PlaybackLabel", playing_ ? L"暂停" : L"播放");
        owner->model_->Text(L"PreviewName", card_.Name);
        owner->model_->Text(L"PreviewFileName", card_.FileName());
        owner->model_->Text(L"PreviewFilePath", card_.Path);
        Attach(view, editor);
        Environment();
        worker_->Paused(!playing_);
        generation_ = worker_->Load(card_, draft_, visibility_);
        SyncRail(true);
    }

    void NativePreview::SyncRail(bool reveal)
    {
        auto owner = owner_.lock();
        if (!owner || editor_)
            return;
        std::weak_ptr<NativePreview> weak = shared_from_this();
        owner->dispatcher_.TryEnqueue(winrt::Microsoft::UI::Dispatching::DispatcherQueuePriority::Low,
                                      [weak, reveal]
                                      {
                                          auto self = weak.lock();
                                          if (!self || self->editor_ || !self->worker_)
                                              return;
                                          auto owner = self->owner_.lock();
                                          if (!owner || !owner->model_->Bool(L"IsPreview"))
                                              return;
                                          auto rail = owner->Control<ListView>(L"PreviewRail");
                                          Object selected{nullptr};
                                          for (auto const &item : rail.Items())
                                              if (auto properties = item.try_as<data::ICustomPropertyProvider>())
                                                  if (to_string(get_self<Observable>(properties)->String(L"Id")) ==
                                                      self->card_.Id)
                                                  {
                                                      selected = item;
                                                      break;
                                                  }
                                          rail.SelectedItem(selected);
                                          if (reveal && selected && owner->model_->Bool(L"ShowPreviewRail"))
                                          {
                                              owner->root_.UpdateLayout();
                                              rail.ScrollIntoView(selected, ScrollIntoViewAlignment::Leading);
                                          }
                                      });
    }

    void NativePreview::Update(CharacterDocument draft, CharacterPreviewVisibility visibility)
    {
        draft_ = std::move(draft);
        visibility_ = visibility;
        if (auto owner = owner_.lock())
        {
            owner->model_->Put(L"PreviewBusy", true);
            owner->model_->Put(L"HasPreviewError", false);
        }
        generation_ = worker_->Load(card_, draft_, visibility_, true);
    }

    void NativePreview::Stop()
    {
        ++epoch_;
        ++generation_;
        if (worker_)
            worker_->Quiesce();
        for (auto &undo : unbind_)
            undo();
        unbind_.clear();
        if (panel_)
        {
            try
            {
                check_hresult(panel_.as<ISwapChainPanelNative>()->SetSwapChain(nullptr));
            }
            catch (hresult_error const &)
            {
            }
            panel_ = nullptr;
        }
        view_ = nullptr;
        viewport_ = nullptr;
        if (worker_)
            worker_->Unload();
        worker_.reset();
    }

    void NativePreview::Resize()
    {
        if (!worker_ || !viewport_)
            return;
        auto width = viewport_.ActualWidth(), height = viewport_.ActualHeight();
        if (width <= 0 || height <= 0)
            return;
        width_ = width;
        aspect_ = width / height;
        auto scale = viewport_.XamlRoot() ? viewport_.XamlRoot().RasterizationScale() : 1;
        worker_->Resize({std::clamp(static_cast<int>(std::round(width * scale)), 1, 8192),
                         std::clamp(static_cast<int>(std::round(height * scale)), 1, 8192), static_cast<float>(scale)});
        if (frame_)
            Frame();
    }

    void NativePreview::Frame(bool face)
    {
        auto b = face ? face_ : body_;
        if (b.Extents.x <= 0 || b.Extents.y <= 0)
            return;
        center_ = b.Center;
        distance_ = std::max(b.Extents.x * 2 / aspect_, static_cast<double>(b.Extents.y * 2)) /
                        (2 * std::tan(35 * Pi / 360)) * (face ? 1.25 : 1.12) +
                    b.Extents.z * 2;
        yaw_ = 0;
        pitch_ = face ? 0 : 5;
        frame_ = !face;
        Camera();
    }

    void NativePreview::Camera()
    {
        if (!worker_)
            return;
        auto yaw = yaw_ * Pi / 180, pitch = pitch_ * Pi / 180;
        camera_.Target = center_;
        camera_.Position = {center_.x + static_cast<float>(std::sin(yaw) * std::cos(pitch) * distance_),
                            center_.y + static_cast<float>(std::sin(pitch) * distance_),
                            center_.z + static_cast<float>(std::cos(yaw) * std::cos(pitch) * distance_)};
        camera_.FieldOfView = 35;
        worker_->View(camera_, dragging_);
    }

    void NativePreview::Environment()
    {
        if (!worker_)
            return;
        if (auto owner = owner_.lock())
        {
            auto light = unbox_value_or<double>(owner->model_->Get(L"PreviewLightIntensity"), 1);
            auto index = unbox_value_or<int>(owner->model_->Get(L"PreviewBackgroundIndex"), 0);
            Vector4 color = index == 2   ? Vector4{105 / 255.f, 108 / 255.f, 112 / 255.f, 1}
                            : index == 3 ? Vector4{220 / 255.f, 223 / 255.f, 225 / 255.f, 1}
                                         : Vector4{41 / 255.f, 44 / 255.f, 48 / 255.f, 1};
            NativeFrameOptions options;
            options.UseGameBackground = index == 0;
            options.Background = color;
            options.KeyLightMultiplier = static_cast<float>(light);
            worker_->Environment(options);
        }
    }

    bool NativePreview::Action(std::wstring_view name, Object sender)
    {
        if (name == L"Angle_Click")
        {
            yaw_ = std::stod(to_string(unbox_value<hstring>(sender.as<Button>().Tag())));
            pitch_ = 0;
            frame_ = false;
            Camera();
        }
        else if (name == L"FocusFace_Click" || name == L"Face_Click")
            Frame(true);
        else if (name == L"ResetCamera_Click" || name == L"Frame_Click")
            Frame();
        else if (name == L"Playback_Click" || name == L"Play_Click")
        {
            playing_ = !playing_;
            worker_->Paused(!playing_);
            if (auto owner = owner_.lock())
                owner->model_->Put(L"PlaybackLabel", playing_ ? L"暂停" : L"播放");
        }
        else if (name == L"RestoreEnvironment_Click" || name == L"Environment_Click")
        {
            if (auto owner = owner_.lock())
            {
                owner->model_->Put(L"PreviewLightIntensity", 1.0);
                owner->model_->Put(L"PreviewBackgroundIndex", 0);
            }
            Environment();
        }
        else if (name == L"RetryPreview_Click" || name == L"Retry_Click")
        {
            auto view = view_;
            auto editor = editor_;
            auto card = card_;
            auto draft = draft_;
            auto visibility = visibility_;
            Stop();
            Initialize();
            Load(card, view, editor, draft, visibility, true);
        }
        else if (name == L"Screenshot_Click")
            Screenshot();
        else
            return false;
        return true;
    }

    ModelExportView NativePreview::ExportView() const
    {
        ModelExportView view;
        view.HasCamera = body_.Extents.y > 0;
        view.Position = camera_.Position;
        view.Target = camera_.Target;
        view.Up = camera_.Up;
        view.FieldOfView = camera_.FieldOfView;
        if (viewport_)
        {
            auto scale = viewport_.XamlRoot() ? viewport_.XamlRoot().RasterizationScale() : 1;
            view.Width = std::max(1, static_cast<int>(viewport_.ActualWidth() * scale));
            view.Height = std::max(1, static_cast<int>(viewport_.ActualHeight() * scale));
        }
        if (auto owner = owner_.lock())
        {
            view.KeyLightMultiplier =
                static_cast<float>(unbox_value_or<double>(owner->model_->Get(L"PreviewLightIntensity"), 1));
            auto index = unbox_value_or<int>(owner->model_->Get(L"PreviewBackgroundIndex"), 0);
            view.UseGameBackground = index == 0;
            view.Background = index == 2   ? Vector4{105 / 255.f, 108 / 255.f, 112 / 255.f, 1}
                              : index == 3 ? Vector4{220 / 255.f, 223 / 255.f, 225 / 255.f, 1}
                                           : Vector4{41 / 255.f, 44 / 255.f, 48 / 255.f, 1};
        }
        return view;
    }

    fire_and_forget NativePreview::Screenshot()
    {
        auto lifetime = shared_from_this();
        try
        {
            auto owner = owner_.lock();
            if (!owner || !worker_)
                co_return;
            winrt::Windows::Storage::Pickers::FileSavePicker picker;
            picker.SuggestedFileName(to_hstring(card_.Name + "-预览"));
            picker.FileTypeChoices().Insert(L"PNG 图片", single_threaded_vector<hstring>({L".png"}));
            check_hresult(picker.as<IInitializeWithWindow>()->Initialize(owner->Handle()));
            auto file = co_await picker.PickSaveFileAsync();
            if (file && worker_)
            {
                auto path = fs::path(file.Path().c_str());
                auto attrs = GetFileAttributesW(path.c_str());
                if (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_REPARSE_POINT))
                    throw std::runtime_error("截图目标不能是链接。");
                ScreenshotTarget target{path, {}};
                if (fs::exists(path))
                    target.ExpectedHash = HashFile(path);
                worker_->Screenshot(std::move(target));
            }
        }
        catch (hresult_error const &e)
        {
            if (auto owner = owner_.lock())
                owner->Report(to_string(e.message()));
        }
        catch (std::exception const &e)
        {
            if (auto owner = owner_.lock())
                owner->Report(e.what());
        }
    }
} // 命名空间 hs2::ui
