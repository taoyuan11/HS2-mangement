#include "NativeWindow.h"
#include "Layout.h"
#include "NativeEditor.h"
#include "NativePreview.h"
#include "UiEvents.h"
#include <Shobjidl.h>
#include <dwmapi.h>
#include <fstream>
#include <microsoft.ui.xaml.window.h>
#include <shellapi.h>
#include <sstream>
#include <unordered_map>

using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;
using namespace winrt::Microsoft::UI::Xaml::Media;
namespace hs2::ui
{
namespace
{
hstring ReadText(fs::path const &path)
{
    auto bytes = ReadFile(path);
    std::string text(bytes.begin(), bytes.end());
    if (text.starts_with("\xef\xbb\xbf"))
        text.erase(0, 3);
    return to_hstring(text);
}
std::string Join(std::vector<std::string> const &values, std::string_view separator)
{
    std::string result;
    for (auto const &value : values)
    {
        if (!result.empty())
            result += separator;
        result += value;
    }
    return result;
}
std::vector<std::string> Tags(std::string text)
{
    std::vector<std::string> result;
    std::string line;
    std::replace(text.begin(), text.end(), ';', ',');
    std::istringstream input(text);
    while (std::getline(input, line, ','))
    {
        auto first = line.find_first_not_of(" \r\n\t");
        if (first == line.npos)
            continue;
        line = line.substr(first, line.find_last_not_of(" \r\n\t") - first + 1);
        if (std::find(result.begin(), result.end(), line) == result.end())
            result.push_back(line);
    }
    return result;
}
bool Contains(std::string const &haystack, std::string const &needle)
{
    auto h = to_hstring(haystack), n = to_hstring(needle);
    return FindNLSStringEx(LOCALE_NAME_INVARIANT, FIND_FROMSTART | NORM_IGNORECASE, h.c_str(),
                           static_cast<int>(h.size()), n.c_str(), static_cast<int>(n.size()), nullptr, nullptr, nullptr,
                           0) >= 0;
}
bool LessText(std::string const &a, std::string const &b)
{
    auto aa = to_hstring(a), bb = to_hstring(b);
    return CompareStringEx(LOCALE_NAME_USER_DEFAULT, NORM_IGNORECASE, aa.c_str(), -1, bb.c_str(), -1, nullptr, nullptr,
                           0) == CSTR_LESS_THAN;
}
bool TextInputFocused(XamlRoot const &root)
{
    auto focus = Input::FocusManager::GetFocusedElement(root).try_as<DependencyObject>();
    while (focus)
    {
        if (focus.try_as<TextBox>() || focus.try_as<RichEditBox>() || focus.try_as<PasswordBox>() ||
            focus.try_as<AutoSuggestBox>())
            return true;
        focus = VisualTreeHelper::GetParent(focus);
    }
    return false;
}
void MinimumWindowSize(xaml::Window const &window, HWND handle)
{
    using namespace winrt::Microsoft::UI::Windowing;
    auto app = window.AppWindow();
    if (auto presenter = app.Presenter().try_as<OverlappedPresenter>())
    {
        auto work = DisplayArea::GetFromWindowId(app.Id(), DisplayAreaFallback::Primary).WorkArea();
        auto scale = GetDpiForWindow(handle) / 96.0;
        presenter.PreferredMinimumWidth(static_cast<int>(std::min(1100 * scale, static_cast<double>(work.Width))));
        presenter.PreferredMinimumHeight(static_cast<int>(std::min(700 * scale, static_cast<double>(work.Height))));
    }
}
std::string ModifiedLabel(int64_t ticks)
{
    if (ticks < 504911232000000000LL)
        return {};
    ULARGE_INTEGER value;
    value.QuadPart = static_cast<uint64_t>(ticks - 504911232000000000LL);
    FILETIME utc{value.LowPart, value.HighPart}, local{};
    SYSTEMTIME time{};
    if (!FileTimeToLocalFileTime(&utc, &local) || !FileTimeToSystemTime(&local, &time))
        return {};
    char text[32];
    sprintf_s(text, "%04u-%02u-%02u %02u:%02u", time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute);
    return text;
}
SolidColorBrush Brush(wchar_t const *key)
{
    return Application::Current().Resources().Lookup(box_value(key)).as<SolidColorBrush>();
}
auto Strings(std::vector<std::string> const &values)
{
    auto result = single_threaded_observable_vector<Object>();
    for (auto const &value : values)
        result.Append(box_value(to_hstring(value)));
    return result;
}
void OpenDirectory(fs::path const &path, HWND window)
{
    fs::create_directories(path);
    auto result = ShellExecuteW(window, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    if (reinterpret_cast<INT_PTR>(result) <= 32)
        throw std::runtime_error("无法打开目录");
}
} // namespace
fs::path ExecutableDirectory()
{
    std::wstring value(32768, L'\0');
    auto size = GetModuleFileNameW(nullptr, value.data(), static_cast<DWORD>(value.size()));
    if (!size || size == value.size())
        throw_last_error();
    value.resize(size);
    return fs::path(value).parent_path();
}
void InitializeResources()
{
    auto resources = Application::Current().Resources();
    resources.MergedDictionaries().Append(XamlControlsResources{});
    resources.Insert(box_value(L"BoolVisible"), make<VisibilityConverter>());
    resources.Insert(box_value(L"NullVisible"), make<VisibilityConverter>(true));
    resources.Insert(box_value(L"NavigationBackground"), make<NavigationConverter>());
    resources.MergedDictionaries().Append(
        Markup::XamlReader::Load(ReadText(ExecutableDirectory() / L"Styles.xaml")).as<ResourceDictionary>());
}
std::shared_ptr<NativeWindow> NativeWindow::Create(fs::path directory)
{
    auto result = std::shared_ptr<NativeWindow>(new NativeWindow(std::move(directory)));
    result->Initialize();
    return result;
}
NativeWindow::NativeWindow(fs::path directory)
    : dataDirectory_(std::move(directory)), settings_(AppSettings::Load(dataDirectory_)),
      model_(make_self<Observable>())
{
    repository_ = std::make_unique<CardRepository>(dataDirectory_);
    library_ = std::make_unique<CardLibraryService>(*repository_);
    files_ = std::make_unique<CardFileService>(*repository_);
}
NativeWindow::~NativeWindow()
{
    preview_.reset();
    if (preferenceToken_.value)
        preferences_.ColorValuesChanged(preferenceToken_);
    work_.request_stop();
    if (work_.joinable())
        work_.join();
}
HWND NativeWindow::Handle() const
{
    HWND handle{};
    check_hresult(window_.as<IWindowNative>()->get_WindowHandle(&handle));
    return handle;
}
FrameworkElement NativeWindow::LoadView(std::wstring_view view)
{
    auto root = Markup::XamlReader::Load(ReadText(ExecutableDirectory() / (std::wstring(view) + L".xaml")))
                    .as<FrameworkElement>();
    for (auto const &definition : UiWraps)
        if (definition.View == view)
        {
            auto grid = root.FindName(hstring(definition.Name)).try_as<Grid>();
            if (!grid)
                continue;
            auto panel = make<WrapPanel>();
            while (grid.Children().Size())
            {
                auto child = grid.Children().GetAt(0);
                grid.Children().RemoveAt(0);
                panel.Children().Append(child);
            }
            grid.Children().Append(panel);
        }
    return root;
}
void NativeWindow::Initialize()
{
    window_ = xaml::Window();
    window_.Title(L"HS2 Manager");
    window_.AppWindow().SetIcon((ExecutableDirectory() / L"Assets/Brand/hs2-manager.ico").wstring());
    dispatcher_ = window_.DispatcherQueue();
    root_ = LoadView(L"MainWindow");
    window_.Content(root_);
    model_->Put(L"IsEditing", false);
    model_->Put(L"IsPreview", false);
    model_->Put(L"IsIdle", true);
    model_->Put(L"IsBusy", false);
    model_->Put(L"CanModify", false);
    model_->Put(L"HasSelection", false);
    model_->Put(L"SearchText", L"");
    model_->Put(L"GenderFilter", L"全部性别");
    model_->Put(L"TagFilter", L"全部标签");
    model_->Text(L"SortOrder", settings_.Sort);
    model_->Set(L"GenderChoices", Strings({"全部性别", "女性", "男性"}), false);
    model_->Set(L"SortChoices", Strings({"名称", "最近修改", "文件大小"}), false);
    model_->Put(L"Status", L"就绪");
    model_->Put(L"IsMultiSelectMode", false);
    model_->Put(L"MultiSelectLabel", L"多选");
    model_->Put(L"CurrentNavigation", L"all");
    auto settings = make_self<Observable>();
    settings->Text(L"GamePath", settings_.GamePath);
    model_->Set(L"Settings", settings.as<Object>(), false);
    root_.DataContext(model_.as<Object>());
    Control<ComboBox>(L"GenderFilterBox").SelectedIndex(0);
    Control<ComboBox>(L"SortOrderBox")
        .SelectedIndex(settings_.Sort == "最近修改"   ? 1
                       : settings_.Sort == "文件大小" ? 2
                                                      : 0);
    std::weak_ptr<NativeWindow> weak = shared_from_this();
    model_->Changed = [weak](hstring const &property) {
        if (auto self = weak.lock())
        {
            if (self->updating_)
                return;
            if (property == L"PreviewLightIntensity" || property == L"PreviewBackgroundIndex")
            {
                if (self->preview_)
                    self->preview_->Environment();
                return;
            }
            if (property == L"SearchText" || property == L"GenderFilter" || property == L"TagFilter" ||
                property == L"SortOrder")
            {
                try
                {
                    if (property == L"SortOrder")
                    {
                        self->settings_.Sort = to_string(self->model_->String(property));
                        self->settings_.Save(self->dataDirectory_);
                    }
                    self->Filter();
                }
                catch (std::exception const &e)
                {
                    self->Report(e.what());
                }
            }
        }
    };
    BindEvents(root_, L"MainWindow");
    preferenceToken_ = preferences_.ColorValuesChanged([weak](auto &&, auto &&) {
        if (auto self = weak.lock())
            self->dispatcher_.TryEnqueue([weak] {
                if (auto self = weak.lock())
                    if (self->settings_.Theme == "System")
                        self->ApplyTheme();
            });
    });
    window_.AppWindow().Closing([weak](auto const &, auto const &args) {
        if (auto self = weak.lock())
        {
            if (self->busy_)
            {
                args.Cancel(true);
                self->closing_ = true;
                self->work_.request_stop();
                if (self->editor_)
                    self->editor_->CancelPending();
                self->Report("正在取消当前操作，完成清理后关闭…");
                return;
            }
            if (self->editor_ && !self->closing_)
            {
                args.Cancel(true);
                self->RequestClose();
                return;
            }
            try
            {
                double scale = self->root_.XamlRoot() ? self->root_.XamlRoot().RasterizationScale() : 1;
                auto size = self->window_.AppWindow().Size();
                self->settings_.WindowWidth = size.Width / scale;
                self->settings_.WindowHeight = size.Height / scale;
                self->settings_.Save(self->dataDirectory_);
            }
            catch (std::exception const &e)
            {
                self->Report(e.what());
            }
            self->preview_.reset();
            self->closing_ = true;
        }
    });
    window_.VisibilityChanged([weak](auto &&, WindowVisibilityChangedEventArgs const &args) {
        if (auto self = weak.lock())
            if (self->preview_)
                self->preview_->Visible(args.Visible());
    });
    window_.AppWindow().Changed([weak](auto &&, auto &&) {
        if (auto self = weak.lock())
            if (self->preview_)
                self->preview_->Visible(self->window_.Visible() && !IsIconic(self->Handle()));
    });
    double scale = GetDpiForWindow(Handle()) / 96.0;
    auto appWindow = window_.AppWindow();
    auto workArea = winrt::Microsoft::UI::Windowing::DisplayArea::GetFromWindowId(
                        appWindow.Id(), winrt::Microsoft::UI::Windowing::DisplayAreaFallback::Primary)
                        .WorkArea();
    auto width = static_cast<int>(
        std::min(std::max(1100.0, settings_.WindowWidth) * scale, static_cast<double>(workArea.Width)));
    auto height = static_cast<int>(
        std::min(std::max(700.0, settings_.WindowHeight) * scale, static_cast<double>(workArea.Height)));
    MinimumWindowSize(window_, Handle());
    appWindow.MoveAndResize(
        {workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2, width, height});
    root_.Loaded([weak](auto &&, auto &&) {
        if (auto self = weak.lock())
            self->root_.XamlRoot().Changed([weak](auto &&, auto &&) {
                if (auto current = weak.lock())
                    MinimumWindowSize(current->window_, current->Handle());
            });
    });
    ApplyTheme();
    Reload();
    window_.Activate();
    Work("检查未完成操作", [this](auto stop, auto progress) {
        auto recovered = files_->Recover(stop);
        auto scan = library_->Scan(settings_.ScanRoots(), progress, stop, settings_.RecursiveCardSearch);
        return "扫描完成：" + std::to_string(scan.Valid) + " 张人物卡，" + std::to_string(scan.Warnings.size()) +
               " 项目录/文件警告" + (recovered.empty() ? "" : "；" + Join(recovered, "；"));
    });
}
void NativeWindow::ApplyTheme()
{
    bool dark = settings_.Theme == "Dark";
    if (settings_.Theme == "System")
    {
        auto foreground = preferences_.GetColorValue(winrt::Windows::UI::ViewManagement::UIColorType::Foreground);
        dark = (5 * foreground.G + 2 * foreground.R + foreground.B) > 8 * 128;
    }
    wchar_t const *keys[] = {L"PageBrush",    L"SidebarBrush", L"SurfaceBrush", L"HoverBrush",      L"BorderBrush",
                             L"TextBrush",    L"MutedBrush",   L"AccentBrush",  L"AccentTextBrush", L"SelectedBrush",
                             L"SuccessBrush", L"WarningBrush", L"DangerBrush"};
    uint32_t light[] = {0xFAFAF9, 0xF1F1EF, 0xFFFFFF, 0xEAEAE7, 0xE3E3DF, 0x242723, 0x62685F,
                        0x282E29, 0xFFFFFF, 0xE1E8DE, 0x35704F, 0x92601D, 0xB42318};
    uint32_t night[] = {0x1C1D1F, 0x151618, 0x252729, 0x303335, 0x383B3C, 0xE8EAE6, 0xA9B1AA,
                        0xE0E8DD, 0x1C261F, 0x344239, 0x86B69A, 0xD1AB70, 0xFFB4AB};
    for (size_t i = 0; i < std::size(keys); ++i)
    {
        auto rgb = dark ? night[i] : light[i];
        Brush(keys[i]).Color(
            {255, static_cast<uint8_t>(rgb >> 16), static_cast<uint8_t>(rgb >> 8), static_cast<uint8_t>(rgb)});
    }
    root_.RequestedTheme(settings_.Theme == "System" ? ElementTheme::Default
                         : dark                      ? ElementTheme::Dark
                                                     : ElementTheme::Light);
    BOOL value = dark;
    DwmSetWindowAttribute(Handle(), 20, &value, sizeof(value));
}
void NativeWindow::Report(std::string message)
{
    model_->Text(L"Status", message);
}
void NativeWindow::SetBusy(bool value)
{
    busy_ = value;
    model_->Put(L"IsBusy", value);
    model_->Put(L"IsIdle", !value);
    auto selected = Selected();
    model_->Put(L"CanModify", !value && !selected.empty());
    model_->Put(L"CanPreview", !value && !selected.empty() && selected.front().Status == CardStatus::Valid &&
                                   selected.front().Availability == FileAvailability::Available);
    model_->Put(L"EditorBusy", value && static_cast<bool>(editor_));
    if (editor_)
        Control<ContentControl>(L"EditorHost").IsEnabled(!value);
}
void NativeWindow::Work(std::string label, std::function<std::string(std::stop_token, Progress)> operation)
{
    if (busy_ || closing_)
        return;
    if (work_.joinable())
        work_.join();
    SetBusy(true);
    model_->Text(L"WorkLabel", label);
    model_->Put(L"ProgressIndeterminate", true);
    model_->Put(L"ProgressValue", 0.0);
    std::weak_ptr<NativeWindow> weak = shared_from_this();
    auto queue = dispatcher_;
    work_ = std::jthread([weak, queue, operation = std::move(operation)](std::stop_token stop) {
        std::string result;
        Progress progress = [weak, queue](WorkProgress const &p) {
            queue.TryEnqueue([weak, p] {
                if (auto self = weak.lock())
                {
                    self->model_->Text(L"WorkLabel", p.Message);
                    self->model_->Put(L"ProgressIndeterminate", p.Total <= 0);
                    self->model_->Put(L"ProgressValue", p.Total > 0 ? 100.0 * p.Completed / p.Total : 0);
                }
            });
        };
        try
        {
            result = operation(stop, progress);
        }
        catch (Cancelled const &)
        {
            result = "操作已取消；已完成的结果已保留。";
        }
        catch (std::exception const &e)
        {
            result = std::string("操作未完成：") + e.what();
        }
        catch (hresult_error const &e)
        {
            result = "操作未完成：" + to_string(e.message());
        }
        queue.TryEnqueue([weak, result] {
            if (auto self = weak.lock())
            {
                self->SetBusy(false);
                try
                {
                    self->Reload();
                }
                catch (std::exception const &e)
                {
                    self->Report(e.what());
                    return;
                }
                self->Report(result);
                if (self->closing_)
                {
                    if (self->editor_)
                    {
                        self->closing_ = false;
                        self->RequestClose();
                    }
                    else
                        self->window_.Close();
                }
            }
        });
    });
}
void NativeWindow::Refresh()
{
    Work("刷新人物卡索引", [this](auto stop, auto progress) {
        auto scan = library_->Scan(settings_.ScanRoots(), progress, stop, settings_.RecursiveCardSearch);
        if (!scan.Warnings.empty())
        {
            fs::create_directories(dataDirectory_ / L"Logs");
            std::ofstream(dataDirectory_ / L"Logs" / L"native-scan.log", std::ios::app)
                << Join(scan.Warnings, "\n") << '\n';
        }
        return "扫描完成：" + std::to_string(scan.Valid) + " 张人物卡，" + std::to_string(scan.Invalid) +
               " 个其他或异常文件，" + std::to_string(scan.Unchanged) + " 项无需更新" +
               (scan.Warnings.empty() ? "" : "；警告详见日志");
    });
}
void NativeWindow::Reload()
{
    auto selection = SelectedIds();
    auto currentId = selectedCardId_;
    cards_ = repository_->All();
    mods_ = std::make_unique<ModDependencyService>(*repository_);
    duplicates_.clear();
    std::unordered_map<std::string, std::vector<std::string>> hashes;
    std::set<std::string> collections(settings_.Collections.begin(), settings_.Collections.end()), tags;
    int total = 0, favorite = 0, trash = 0, issues = 0;
    for (auto const &card : cards_)
    {
        if (card.Availability == FileAvailability::Quarantined)
        {
            ++trash;
            continue;
        }
        if (card.Status == CardStatus::Valid)
            ++total;
        if (card.Favorite)
            ++favorite;
        if (card.Status != CardStatus::Valid || card.Availability != FileAvailability::Available)
            ++issues;
        if (!card.Collection.empty())
            collections.insert(card.Collection);
        tags.insert(card.Tags.begin(), card.Tags.end());
        if (!card.Hash.empty())
            hashes[card.Hash].push_back(card.Id);
    }
    for (auto const &[hash, ids] : hashes)
        if (ids.size() > 1)
            duplicates_.insert(ids.begin(), ids.end());
    model_->Put(L"TotalCount", total);
    model_->Put(L"FavoriteCount", favorite);
    model_->Put(L"TrashCount", trash);
    model_->Put(L"IssueCount", issues);
    model_->Put(L"DuplicateCount", static_cast<int>(duplicates_.size()));
    model_->Text(L"LibrarySummary", std::to_string(total) + " 张人物卡 · 你的本地角色资源库");
    bool connected = fs::is_directory(Path(settings_.GamePath));
    model_->Put(L"GameConnection", connected ? L"游戏目录已连接" : L"游戏目录离线");
    model_->Text(L"GameFolderName", Utf8(Path(settings_.GamePath).filename()));
    model_->Set(L"GameConnectionBrush", Brush(connected ? L"SuccessBrush" : L"WarningBrush"), false);
    updating_ = true;
    model_->Set(L"Collections", Strings({collections.begin(), collections.end()}), false);
    auto tagValues = std::vector<std::string>{"全部标签"};
    tagValues.insert(tagValues.end(), tags.begin(), tags.end());
    auto chosen = model_->String(L"TagFilter");
    model_->Set(L"Tags", Strings(tagValues), false);
    model_->Put(L"TagFilter", std::find(tagValues.begin(), tagValues.end(), to_string(chosen)) != tagValues.end()
                                  ? chosen
                                  : hstring(L"全部标签"));
    // TreeViewNode uses native ItemsSource, retaining virtualized folder
    // expansion.
    auto tree = Control<TreeView>(L"FolderTree");
    tree.RootNodes().Clear();
    std::map<std::string, TreeViewNode> nodes;
    auto ensure = [&](auto &&ensure, fs::path path, fs::path root) -> TreeViewNode {
        auto key = Utf8(path);
        if (auto it = nodes.find(key); it != nodes.end())
            return it->second;
        TreeViewNode node;
        auto value = make_self<Observable>();
        value->Text(L"Name", Utf8(path.filename()));
        value->Text(L"Path", key);
        node.Content(value.as<Object>());
        nodes.emplace(key, node);
        if (SamePath(path, root))
        {
            node.IsExpanded(true);
            tree.RootNodes().Append(node);
        }
        else
            ensure(ensure, path.parent_path(), root).Children().Append(node);
        return node;
    };
    for (auto const &card : cards_)
        if (!card.Root.empty() && card.Availability != FileAvailability::Quarantined &&
            Within(Path(card.Path), Path(card.Root)))
            ensure(ensure, Path(card.Path).parent_path(), Path(card.Root));
    auto tagIndex = std::find(tagValues.begin(), tagValues.end(), to_string(chosen));
    Control<ComboBox>(L"TagFilterBox")
        .SelectedIndex(tagIndex == tagValues.end() ? 0 : static_cast<int>(tagIndex - tagValues.begin()));
    updating_ = false;
    Filter();
    auto list = Control<GridView>(L"CardList");
    for (uint32_t i = 0; i < list.Items().Size(); ++i)
        if (std::find(selection.begin(), selection.end(), visible_[i].Id) != selection.end())
            list.SelectedItems().Append(list.Items().GetAt(i));
    selectedCardId_ = std::move(currentId);
    Select();
    if (preview_)
        preview_->SyncRail(false);
}
void NativeWindow::Navigate(std::string mode)
{
    preview_.reset();
    model_->Put(L"IsPreview", false);
    mode_ = std::move(mode);
    folder_.clear();
    collection_.clear();
    model_->Text(L"CurrentNavigation", mode_);
    model_->Put(L"IsTrash", mode_ == "trash");
    updating_ = true;
    Control<ListView>(L"CollectionList").SelectedItem(nullptr);
    Control<TreeView>(L"FolderTree").SelectedNode(nullptr);
    updating_ = false;
    Filter();
}
void NativeWindow::Filter()
{
    if (updating_)
        return;
    visible_.clear();
    auto search = to_string(model_->String(L"SearchText")), gender = to_string(model_->String(L"GenderFilter")),
         tag = to_string(model_->String(L"TagFilter")), sort = to_string(model_->String(L"SortOrder"));
    for (auto const &card : cards_)
    {
        bool include = mode_ == "trash" ? card.Availability == FileAvailability::Quarantined
                                        : card.Availability != FileAvailability::Quarantined;
        if (mode_ == "issues")
            include = include && (card.Status != CardStatus::Valid || card.Availability != FileAvailability::Available);
        else if (mode_ == "favorites")
            include = include && card.Favorite;
        else if (mode_ == "duplicates")
            include = include && duplicates_.contains(card.Id);
        else if (mode_ == "all")
            include = include && card.Status == CardStatus::Valid;
        if (!include || (!folder_.empty() && !Within(Path(card.Path), Path(folder_))) ||
            (!collection_.empty() && card.Collection != collection_) ||
            (!gender.empty() && gender != "全部性别" && card.SexLabel() != gender) ||
            (!tag.empty() && tag != "全部标签" &&
             std::find(card.Tags.begin(), card.Tags.end(), tag) == card.Tags.end()))
            continue;
        if (!search.empty() && !Contains(card.Name, search) && !Contains(card.FileName(), search) &&
            !Contains(card.Notes, search) && !Contains(Join(card.Tags, ","), search))
            continue;
        visible_.push_back(card);
    }
    std::stable_sort(visible_.begin(), visible_.end(), [&](auto const &a, auto const &b) {
        if (sort == "最近修改")
            return a.ModifiedUtcTicks > b.ModifiedUtcTicks;
        if (sort == "文件大小")
            return a.Size > b.Size;
        if (a.Name == b.Name)
            return a.Path < b.Path;
        return LessText(a.Name, b.Name);
    });
    auto items = single_threaded_observable_vector<Object>();
    for (auto const &card : visible_)
    {
        auto item = make_self<Observable>();
        item->Text(L"Name", card.Name);
        item->Text(L"Id", card.Id);
        item->Text(L"Subtitle", card.SexLabel() + " · " + std::to_string(card.Size / 1024) + " KB");
        item->Put(L"Favorite", card.Favorite);
        item->Text(L"Status",
                   card.Availability == FileAvailability::Available ? card.StatusLabel() : card.AvailabilityLabel());
        item->Set(L"StatusBrush",
                  Brush(card.Status == CardStatus::Valid && card.Availability == FileAvailability::Available
                            ? L"SuccessBrush"
                            : L"WarningBrush"),
                  false);
        item->Text(L"DetailLine", card.FileName());
        item->Text(L"Modified", ModifiedLabel(card.ModifiedUtcTicks));
        item->Text(L"Folder", card.Folder());
        auto record = make_self<Observable>();
        record->Text(L"Path", card.Path);
        record->Text(L"Error", card.Error);
        item->Set(L"Record", record.as<Object>(), false);
        items.Append(item.as<Object>());
    }
    model_->Set(L"VisibleCards", items, false);
    model_->Put(L"Empty", visible_.empty());
    model_->Text(L"ResultSummary", std::to_string(visible_.size()) + " 个结果");
    model_->Put(L"EmptyTitle", L"没有匹配的人物卡");
    model_->Put(L"EmptyDescription", L"可刷新人物库、导入 PNG，或调整搜索与筛选条件。");
    model_->Text(L"PageTitle", !collection_.empty()    ? collection_
                               : !folder_.empty()      ? Utf8(Path(folder_).filename())
                               : mode_ == "favorites"  ? "我的收藏"
                               : mode_ == "duplicates" ? "重复文件"
                               : mode_ == "issues"     ? "需要关注"
                               : mode_ == "trash"      ? "可恢复隔离区"
                                                       : "人物卡");
    Select();
}
std::vector<CardRecord> NativeWindow::Selected() const
{
    std::vector<CardRecord> result;
    if (!root_)
        return result;
    auto list = Control<GridView>(L"CardList");
    for (auto const &item : list.SelectedItems())
    {
        auto property = item.try_as<data::ICustomPropertyProvider>();
        if (!property)
            continue;
        auto id = to_string(get_self<Observable>(property)->String(L"Id"));
        auto it = std::find_if(visible_.begin(), visible_.end(), [&](auto const &c) { return c.Id == id; });
        if (it != visible_.end())
            result.push_back(*it);
    }
    auto current =
        std::find_if(result.begin(), result.end(), [&](auto const &card) { return card.Id == selectedCardId_; });
    if (current != result.end())
        std::rotate(result.begin(), current, current + 1);
    return result;
}
std::vector<std::string> NativeWindow::SelectedIds() const
{
    std::vector<std::string> ids;
    for (auto const &card : Selected())
        ids.push_back(card.Id);
    return ids;
}
void NativeWindow::Select()
{
    if (updating_)
        return;
    auto selected = Selected();
    Object current{nullptr};
    if (!selected.empty())
    {
        selectedCardId_ = selected.front().Id;
        for (auto const &item : Control<GridView>(L"CardList").SelectedItems())
            if (to_string(get_self<Observable>(item.as<data::ICustomPropertyProvider>())->String(L"Id")) ==
                selectedCardId_)
            {
                current = item;
                break;
            }
    }
    model_->Set(L"SelectedCard", current, false);
    model_->Put(L"HasSelection", !selected.empty());
    model_->Put(L"CanModify", !busy_ && !selected.empty());
    model_->Put(L"CanPreview", !busy_ && !selected.empty() && selected.front().Status == CardStatus::Valid &&
                                   selected.front().Availability == FileAvailability::Available);
    model_->Text(L"ResultSummary", std::to_string(visible_.size()) + " 个结果" +
                                       (selected.empty() ? "" : " · 已选 " + std::to_string(selected.size()) + " 项"));
    if (selected.empty())
    {
        model_->Put(L"EditTags", L"");
        model_->Put(L"EditNotes", L"");
        return;
    }
    auto const &card = selected.front();
    model_->Text(L"EditTags", Join(card.Tags, ", "));
    model_->Text(L"EditNotes", card.Notes);
    model_->Put(L"FavoriteText", card.Favorite ? L"已收藏" : L"收藏人物卡");
    model_->Put(L"FavoriteGlyph", card.Favorite ? L"\uE735" : L"\uE734");
    auto deps = single_threaded_observable_vector<Object>();
    int missing = 0, unknown = 0;
    for (auto const &dep : mods_->Diagnose(card, Path(settings_.GamePath), fs::is_directory(Path(settings_.GamePath))))
    {
        auto item = make_self<Observable>();
        item->Put(L"Label", dep.State == DependencyState::Found     ? L"已找到"
                            : dep.State == DependencyState::Missing ? L"缺失"
                                                                    : L"无法判断");
        item->Set(L"Color",
                  Brush(dep.State == DependencyState::Found     ? L"SuccessBrush"
                        : dep.State == DependencyState::Missing ? L"WarningBrush"
                                                                : L"MutedBrush"),
                  false);
        item->Text(L"Guid", dep.Guid);
        item->Text(L"Detail", dep.Detail);
        deps.Append(item.as<Object>());
        if (dep.State == DependencyState::Missing)
            ++missing;
        else if (dep.State == DependencyState::Unknown)
            ++unknown;
    }
    model_->Set(L"DetailDependencies", deps, false);
    model_->Text(L"DependencySummary", std::to_string(missing) + " 项缺失 · " + std::to_string(unknown) + " 项待确认");
}
fire_and_forget NativeWindow::LoadCover(com_ptr<Observable> item, CardRecord card)
{
    auto lifetime = shared_from_this();
    try
    {
        if (card.CoverFile.empty())
            co_return;
        auto file = co_await winrt::Windows::Storage::StorageFile::GetFileFromPathAsync(
            (dataDirectory_ / Path(card.CoverFile)).wstring());
        auto stream = co_await file.OpenReadAsync();
        Media::Imaging::BitmapImage bitmap;
        bitmap.DecodePixelWidth(360);
        co_await bitmap.SetSourceAsync(stream);
        item->Set(L"Cover", bitmap, false);
    }
    catch (hresult_error const &)
    {
    }
}
void NativeWindow::BindEvents(FrameworkElement const &root, std::wstring_view view)
{
    std::weak_ptr<NativeWindow> weak = shared_from_this();
    for (auto const &event : UiEvents)
        if (event.View == view)
        {
            auto element = root.FindName(hstring(event.Name));
            if (!element)
                continue;
            std::wstring handler(event.Handler);
            if (event.Event == L"Click")
            {
                auto callback = [weak, handler](auto const &sender, auto const &) {
                    if (auto self = weak.lock())
                        self->Action(handler, sender);
                };
                if (auto button = element.try_as<Primitives::ButtonBase>())
                    button.Click(callback);
                else if (auto menu = element.try_as<MenuFlyoutItem>())
                    menu.Click(callback);
            }
            else if (event.Event == L"SelectionChanged")
            {
                if (auto list = element.try_as<Primitives::Selector>())
                    list.SelectionChanged([weak, handler](auto const &sender, SelectionChangedEventArgs const &args) {
                        if (auto self = weak.lock())
                        {
                            if (handler == L"Cards_SelectionChanged" && args.AddedItems().Size())
                            {
                                auto item = args.AddedItems().GetAt(args.AddedItems().Size() - 1);
                                self->selectedCardId_ = to_string(
                                    get_self<Observable>(item.as<data::ICustomPropertyProvider>())->String(L"Id"));
                            }
                            self->Action(handler, sender);
                        }
                    });
                else if (auto tree = element.try_as<TreeView>())
                    tree.SelectionChanged([weak](auto const &, auto const &) {
                        if (auto self = weak.lock())
                        {
                            if (self->updating_)
                                return;
                            auto node = self->Control<TreeView>(L"FolderTree").SelectedNode();
                            if (node)
                            {
                                auto value = get_self<Observable>(node.Content().as<data::ICustomPropertyProvider>());
                                self->preview_.reset();
                                self->model_->Put(L"IsPreview", false);
                                self->folder_ = to_string(value->String(L"Path"));
                                self->mode_ = "all";
                                self->collection_.clear();
                                self->model_->Put(L"CurrentNavigation", L"");
                                self->model_->Put(L"IsTrash", false);
                                self->updating_ = true;
                                self->Control<ListView>(L"CollectionList").SelectedItem(nullptr);
                                self->updating_ = false;
                                self->Filter();
                            }
                        }
                    });
            }
        }
    if (view != L"MainWindow")
        return;
    auto list = Control<GridView>(L"CardList");
    list.DoubleTapped([weak](auto &&, Input::DoubleTappedRoutedEventArgs const &args) {
        if (auto self = weak.lock())
            if (!self->model_->Bool(L"IsMultiSelectMode"))
            {
                auto node = args.OriginalSource().try_as<DependencyObject>();
                while (node && !node.try_as<GridViewItem>())
                    node = VisualTreeHelper::GetParent(node);
                if (node)
                    self->Action(L"Preview_Click", nullptr);
            }
    });
    list.ContainerContentChanging([weak](auto const &, ContainerContentChangingEventArgs const &args) {
        if (args.InRecycleQueue())
            return;
        if (auto self = weak.lock())
        {
            auto item = args.Item().try_as<data::ICustomPropertyProvider>();
            if (!item)
                return;
            auto object = get_self<Observable>(item);
            if (object->Bool(L"CoverLoading"))
                return;
            object->Put(L"CoverLoading", true);
            auto id = to_string(object->String(L"Id"));
            auto it = std::find_if(self->visible_.begin(), self->visible_.end(),
                                   [&](auto const &card) { return card.Id == id; });
            if (it != self->visible_.end())
                self->LoadCover(object->get_strong(), *it);
        }
    });
    Control<ListView>(L"PreviewRail")
        .ContainerContentChanging([weak](auto const &, ContainerContentChangingEventArgs const &args) {
            if (args.InRecycleQueue())
                return;
            if (auto self = weak.lock())
            {
                auto item = args.Item().try_as<data::ICustomPropertyProvider>();
                if (!item)
                    return;
                auto object = get_self<Observable>(item);
                if (object->Bool(L"CoverLoading"))
                    return;
                object->Put(L"CoverLoading", true);
                auto id = to_string(object->String(L"Id"));
                auto it = std::find_if(self->visible_.begin(), self->visible_.end(),
                                       [&](auto const &card) { return card.Id == id; });
                if (it != self->visible_.end())
                    self->LoadCover(object->get_strong(), *it);
            }
        });
    list.RightTapped([weak](auto const &, Input::RightTappedRoutedEventArgs const &args) {
        if (auto self = weak.lock())
        {
            auto node = args.OriginalSource().try_as<DependencyObject>();
            while (node)
            {
                if (auto container = node.try_as<GridViewItem>())
                {
                    if (!container.IsSelected())
                    {
                        if (!self->model_->Bool(L"IsMultiSelectMode"))
                            self->Control<GridView>(L"CardList").SelectedItems().Clear();
                        container.IsSelected(true);
                    }
                    break;
                }
                node = VisualTreeHelper::GetParent(node);
            }
        }
    });
    root_.KeyDown([weak](auto const &, Input::KeyRoutedEventArgs const &args) {
        if (auto self = weak.lock())
        {
            bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0, shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            auto key = static_cast<int>(args.Key());
            if (self->dialogOpen_)
                return;
            if (key == VK_ESCAPE)
            {
                if (self->editor_)
                    self->Action(L"CloseEditor_Click", nullptr);
                else if (self->preview_)
                    self->Action(L"ClosePreview_Click", nullptr);
                else if (self->model_->Bool(L"IsMultiSelectMode"))
                    self->Action(L"MultiSelect_Click", nullptr);
                args.Handled(true);
            }
            else if (self->editor_ && ctrl && (key == 'Z' || key == 'Y'))
            {
                if (key == 'Z')
                    self->editor_->Undo();
                else
                    self->editor_->Redo();
                args.Handled(true);
            }
            else if (ctrl && key == 'F')
            {
                if (self->editor_)
                    self->editor_->FocusSearch();
                else if (!self->preview_)
                {
                    self->Control<TextBox>(L"SearchBox").Focus(FocusState::Programmatic);
                    self->Control<TextBox>(L"SearchBox").SelectAll();
                }
                args.Handled(true);
            }
            else if (!self->editor_ && !self->preview_ && ctrl && key == 'R')
            {
                self->Refresh();
                args.Handled(true);
            }
            else if (ctrl && key == 'S')
            {
                self->Action(L"SaveDetails_Click", nullptr);
                args.Handled(true);
            }
            else if (!self->editor_ && !self->preview_ && !self->busy_ && key == VK_F2 &&
                     !TextInputFocused(self->root_.XamlRoot()))
            {
                self->Action(L"Rename_Click", nullptr);
                args.Handled(true);
            }
            else if (!self->editor_ && !self->preview_ && !self->busy_ && !self->closing_ && key == VK_DELETE &&
                     VisualTreeHelper::GetOpenPopupsForXamlRoot(self->root_.XamlRoot()).Size() == 0)
            {
                if (!TextInputFocused(self->root_.XamlRoot()) && (shift || self->mode_ != "trash") &&
                    !self->Selected().empty())
                {
                    self->Action(shift ? L"Delete_Click" : L"Quarantine_Click", nullptr);
                    args.Handled(true);
                }
            }
        }
    });
    root_.DragOver([weak](auto const &, DragEventArgs const &args) {
        if (auto self = weak.lock())
        {
            args.AcceptedOperation(
                !self->busy_ && !self->editor_ && !self->closing_ &&
                        args.DataView().Contains(
                            winrt::Windows::ApplicationModel::DataTransfer::StandardDataFormats::StorageItems())
                    ? winrt::Windows::ApplicationModel::DataTransfer::DataPackageOperation::Copy
                    : winrt::Windows::ApplicationModel::DataTransfer::DataPackageOperation::None);
            args.Handled(true);
        }
    });
    root_.Drop([weak](auto const &, DragEventArgs const &args) -> fire_and_forget {
        if (auto self = weak.lock())
        {
            auto lifetime = self;
            auto deferral = args.GetDeferral();
            try
            {
                if (!self->busy_ && !self->editor_ && !self->closing_ &&
                    args.DataView().Contains(
                        winrt::Windows::ApplicationModel::DataTransfer::StandardDataFormats::StorageItems()))
                {
                    auto items = co_await args.DataView().GetStorageItemsAsync();
                    if (self->busy_ || self->editor_ || self->closing_)
                    {
                        deferral.Complete();
                        co_return;
                    }
                    std::vector<fs::path> paths;
                    for (auto const &item : items)
                        paths.emplace_back(item.Path().c_str());
                    self->Work("导入人物卡", [self, paths](auto stop, auto progress) {
                        std::vector<fs::path> sources;
                        for (auto const &path : paths)
                        {
                            if (fs::is_directory(path))
                            {
                                auto children = CardLibraryService::EnumerateCards(path, true, stop);
                                sources.insert(sources.end(), children.begin(), children.end());
                            }
                            else if (_wcsicmp(path.extension().c_str(), L".png") == 0)
                                sources.push_back(path);
                        }
                        auto results =
                            self->files_->Import(sources, Path(self->settings_.GamePath), {}, progress, stop);
                        return "已处理 " + std::to_string(results.size()) + " 个导入文件";
                    });
                }
            }
            catch (hresult_error const &e)
            {
                self->Report(to_string(e.message()));
            }
            catch (std::exception const &e)
            {
                self->Report(e.what());
            }
            deferral.Complete();
        }
    });
}
IAsyncOperation<bool> NativeWindow::Confirm(hstring title, hstring message, bool destructive)
{
    if (dialogOpen_)
        co_return false;
    dialogOpen_ = true;
    ContentDialog dialog;
    dialog.XamlRoot(root_.XamlRoot());
    dialog.RequestedTheme(root_.RequestedTheme());
    dialog.Title(box_value(title));
    TextBlock content;
    content.Text(message);
    content.TextWrapping(TextWrapping::Wrap);
    content.IsTextSelectionEnabled(true);
    ScrollViewer scroll;
    scroll.Content(content);
    scroll.MaxHeight(360);
    scroll.HorizontalScrollBarVisibility(ScrollBarVisibility::Disabled);
    scroll.VerticalScrollBarVisibility(ScrollBarVisibility::Auto);
    dialog.Content(scroll);
    dialog.PrimaryButtonText(destructive ? L"永久删除" : L"确定");
    dialog.CloseButtonText(L"取消");
    dialog.DefaultButton(ContentDialogButton::Close);
    try
    {
        auto result = co_await dialog.ShowAsync();
        dialogOpen_ = false;
        co_return result == ContentDialogResult::Primary;
    }
    catch (...)
    {
        dialogOpen_ = false;
        throw;
    }
}
IAsyncOperation<hstring> NativeWindow::Prompt(hstring title, hstring message, hstring initial)
{
    if (dialogOpen_)
        co_return L"";
    dialogOpen_ = true;
    ContentDialog dialog;
    dialog.XamlRoot(root_.XamlRoot());
    dialog.RequestedTheme(root_.RequestedTheme());
    dialog.Title(box_value(title));
    StackPanel panel;
    panel.Spacing(12);
    TextBlock description;
    description.Text(message);
    description.TextWrapping(TextWrapping::Wrap);
    panel.Children().Append(description);
    TextBox input;
    input.Text(initial);
    panel.Children().Append(input);
    dialog.Content(panel);
    dialog.PrimaryButtonText(L"确定");
    dialog.CloseButtonText(L"取消");
    try
    {
        auto result = co_await dialog.ShowAsync();
        dialogOpen_ = false;
        co_return result == ContentDialogResult::Primary ? input.Text() : L"";
    }
    catch (...)
    {
        dialogOpen_ = false;
        throw;
    }
}
IAsyncOperation<hstring> NativeWindow::PickFolder(hstring title)
{
    winrt::Windows::Storage::Pickers::FolderPicker picker;
    picker.CommitButtonText(title);
    picker.FileTypeFilter().Append(L"*");
    check_hresult(picker.as<IInitializeWithWindow>()->Initialize(Handle()));
    auto folder = co_await picker.PickSingleFolderAsync();
    co_return folder ? folder.Path() : L"";
}
} // namespace hs2::ui
