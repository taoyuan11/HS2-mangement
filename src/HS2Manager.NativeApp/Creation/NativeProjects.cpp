#include "Creation/NativeProjects.h"
#include "Creation/NativeCreation.h"

using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    void NativeWindow::ShowProjects(std::string kind)
    {
        if (busy_ || editor_ || closing_ || dialogOpen_)
            return;
        ClosePreview();
        searchTimer_.Stop();
        if (!projects_)
            projects_ = NativeProjects::Create(shared_from_this());
        model_->Put(L"IsProjects", true);
        model_->Text(L"CurrentNavigation", kind + "-projects");
        updating_ = true;
        Control<ListView>(L"CollectionList").SelectedItem(nullptr);
        Control<TreeView>(L"FolderTree").SelectedNode(nullptr);
        updating_ = false;
        projects_->Show(std::move(kind));
    }

    void NativeWindow::HideProjects()
    {
        if (projects_)
            projects_->Hide();
        model_->Put(L"IsProjects", false);
    }

    std::shared_ptr<NativeProjects> NativeProjects::Create(const std::shared_ptr<NativeWindow> &owner)
    {
        auto self = std::shared_ptr<NativeProjects>(new NativeProjects);
        self->owner_ = owner;
        self->root_ = owner->LoadView(L"ProjectsView");
        self->model_ = make_self<Observable>();
        self->root_.DataContext(self->model_.as<Object>());
        owner->Control<ContentControl>(L"ProjectsHost").Content(self->root_);
        std::weak_ptr<NativeProjects> weak = self;
        for (auto name : {L"New", L"Refresh", L"Open", L"Recover", L"Duplicate", L"Rename"})
            self->Get<Button>(name).Click(
                [weak, action = to_string(hstring(name))](auto &&, auto &&)
                {
                    if (auto view = weak.lock())
                        view->Action(action);
                });
        self->Get<ListView>(L"Projects")
            .SelectionChanged(
                [weak](auto &&, auto &&)
                {
                    if (auto view = weak.lock())
                        view->SyncControls();
                });
        self->Get<ListView>(L"Projects")
            .DoubleTapped(
                [weak](auto &&, Input::DoubleTappedRoutedEventArgs const &args)
                {
                    auto node = args.OriginalSource().try_as<DependencyObject>();
                    while (node && !node.try_as<ListViewItem>())
                        node = Media::VisualTreeHelper::GetParent(node);
                    if (node)
                        if (auto view = weak.lock())
                        {
                            view->Action("Open");
                            args.Handled(true);
                        }
                });
        return self;
    }

    void NativeProjects::Show(std::string kind)
    {
        active_ = true;
        if (kind_ != kind)
        {
            summaries_.clear();
            model_->Set(L"Projects", single_threaded_observable_vector<Object>());
        }
        kind_ = std::move(kind);
        model_->Put(L"Title", kind_ == "mod" ? L"MOD 工程" : L"角色工程");
        model_->Put(L"NewLabel", kind_ == "mod" ? L"新建 MOD 工程" : L"新建角色工程");
        Refresh();
    }

    void NativeProjects::Hide()
    {
        active_ = false;
        ++generation_;
    }

    const creation::ProjectSummary *NativeProjects::Selected() const
    {
        int index = Get<ListView>(L"Projects").SelectedIndex();
        return index >= 0 && index < int(summaries_.size()) ? &summaries_[index] : nullptr;
    }

    void NativeProjects::SyncControls()
    {
        auto owner = owner_.lock();
        bool idle = owner && active_ && !owner->busy_ && !owner->closing_ && !owner->editor_;
        auto selected = Selected();
        bool canOpen = idle && !loading_ && selected && selected->Error.empty();
        model_->Put(L"CanCreate", idle && !loading_);
        model_->Put(L"CanRefresh", idle && !loading_);
        model_->Put(L"CanSelect", idle && !loading_);
        model_->Put(L"CanOpen", canOpen);
        model_->Put(L"CanRecover", canOpen && selected->HasRecovery);
    }

    fire_and_forget NativeProjects::Refresh()
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || !active_ || owner->busy_ || owner->editor_ || owner->closing_ || owner->dialogOpen_)
            co_return;
        auto generation = ++generation_;
        auto kind = kind_;
        auto selected = Selected();
        auto selectedPath = selected ? selected->Path : fs::path{};
        auto root = owner->dataDirectory_ / "Creation" / "Projects";
        loading_ = true;
        SyncControls();
        model_->Put(L"Summary", L"正在读取工程…");
        model_->Put(L"Empty", false);
        model_->Put(L"HasWarning", false);
        apartment_context ui;
        std::vector<creation::ProjectSummary> summaries;
        std::string error;
        co_await resume_background();
        try
        {
            summaries = creation::CreationProject::List(root);
        }
        catch (const std::exception &e)
        {
            error = e.what();
        }
        co_await ui;
        // 页面切换或再次刷新后，旧请求只释放数据，不改写当前界面。
        if (owner->closing_ || !active_ || generation != generation_)
            co_return;
        loading_ = false;
        try
        {
            summaries_.clear();
            auto items = single_threaded_observable_vector<Object>();
            int selectedIndex = -1;
            std::string warning = error;
            for (auto &summary : summaries)
            {
                if (summary.Kind != "character" && summary.Kind != "mod")
                {
                    warning += (warning.empty() ? "" : "\n\n") + std::string("无法识别工程类型：") +
                               Utf8(summary.Path) + "\n" +
                               (summary.Error.empty() ? "未知工程类型：" + summary.Kind : summary.Error);
                    continue;
                }
                if (summary.Kind != kind)
                    continue;
                auto item = make_self<Observable>();
                item->Text(L"Name", summary.Name);
                item->Text(L"Path", Utf8(summary.Path));
                item->Text(L"Updated", summary.UpdatedUtc.empty() ? "未知" : summary.UpdatedUtc);
                item->Text(L"State", !summary.Error.empty() ? summary.Error
                                     : summary.HasRecovery  ? "有恢复快照"
                                                            : "已保存");
                if (summary.Path == selectedPath)
                    selectedIndex = int(summaries_.size());
                items.Append(item.as<Object>());
                summaries_.push_back(std::move(summary));
            }
            model_->Set(L"Projects", items);
            Get<ListView>(L"Projects").SelectedIndex(selectedIndex);
            model_->Text(L"Summary", "共 " + std::to_string(summaries_.size()) + " 个工程 · 最近修改优先");
            model_->Text(L"Warning", warning);
            model_->Put(L"HasWarning", !warning.empty());
            model_->Put(L"Empty", summaries_.empty());
            model_->Put(L"EmptyMessage",
                        error.empty() ? L"暂无工程，点击右上方按钮新建。" : L"工程读取失败，请刷新重试。");
            SyncControls();
        }
        catch (const hresult_error &e)
        {
            owner->Report(to_string(e.message()));
        }
        catch (const std::exception &e)
        {
            owner->Report(e.what());
        }
    }

    fire_and_forget NativeProjects::Action(std::string action)
    {
        auto lifetime = shared_from_this();
        auto owner = owner_.lock();
        if (!owner || !active_ || loading_ || owner->busy_ || owner->editor_ || owner->closing_ || owner->dialogOpen_)
            co_return;
        if (action == "Refresh")
        {
            Refresh();
            co_return;
        }
        if (action == "New")
        {
            owner->OpenCreation(kind_);
            co_return;
        }
        auto selected = Selected();
        if (!selected || !selected->Error.empty() || (action == "Recover" && !selected->HasRecovery))
            co_return;
        auto summary = *selected;
        std::string name, error;
        bool started = false;
        std::shared_ptr<creation::CreationProject> project;
        std::shared_ptr<creation::CreationService> service;
        apartment_context ui;
        try
        {
            if (action == "Duplicate" || action == "Rename")
            {
                auto input = co_await owner->Prompt(action == "Rename" ? L"重命名工程" : L"复制为新工程", L"工程名称",
                                                    to_hstring(summary.Name + (action == "Duplicate" ? " 副本" : "")));
                if (input.empty() || owner->closing_)
                    co_return;
                name = to_string(input);
                if (name.find_first_not_of(" \t\r\n") == std::string::npos)
                    throw std::runtime_error("工程名称不能为空");
            }
            owner->SetBusy(true);
            owner->model_->Put(L"WorkLabel", L"正在打开工程…");
            owner->model_->Put(L"ProgressIndeterminate", true);
            started = true;
            auto game = Path(owner->settings_.GamePath), data = owner->dataDirectory_, runtime = ExecutableDirectory();
            co_await resume_background();
            try
            {
                service = std::make_shared<creation::CreationService>(game, data, runtime);
                service->RecoverInstallations();
                project = action == "Duplicate"
                              ? creation::CreationProject::Duplicate(service->ProjectsRoot(), summary.Path, name)
                              : creation::CreationProject::Open(summary.Path, action == "Recover");
                if (action == "Rename")
                {
                    project->Edit("重命名",
                                  [&](creation::CreationState &state)
                                  {
                                      state.Name = name;
                                  });
                    project->Save();
                    project.reset();
                }
            }
            catch (const std::exception &e)
            {
                error = e.what();
            }
            co_await ui;
            if (project && error.empty() && !owner->closing_)
                owner->creation_ = NativeCreation::Open(owner, project, service);
        }
        catch (const hresult_error &e)
        {
            error = to_string(e.message());
        }
        catch (const std::exception &e)
        {
            error = e.what();
        }
        if (started)
            owner->SetBusy(false);
        if (owner->closing_)
            owner->RequestClose();
        else if (!error.empty())
            owner->ShowReport(L"工程操作失败", to_hstring(Utf8(summary.Path) + "\n" + error));
        else if (action == "Rename")
        {
            owner->Report("工程已重命名");
            Refresh();
        }
    }
}
