#include <windows.h>
#undef GetCurrentTime
#include "Infrastructure/DataLocation.h"
#include "Application/NativeWindow.h"
#include <fstream>
#include <shellapi.h>
#include <shlobj.h>
#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Xaml.XamlTypeInfo.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.h>

using namespace winrt;
using namespace Microsoft::UI::Xaml;

namespace hs2::ui
{
    struct Application : ApplicationT<Application, Markup::IXamlMetadataProvider>
    {
        Microsoft::UI::Xaml::XamlTypeInfo::XamlControlsXamlMetaDataProvider metadata;
        std::shared_ptr<NativeWindow> window;
        std::unique_ptr<CardRepository> repository;
        static inline int exitCode = 0;
        static inline std::filesystem::path dataDirectory;

        explicit Application(std::unique_ptr<CardRepository> value) : repository(std::move(value))
        {
            UnhandledException(
                [](auto &&, UnhandledExceptionEventArgs const &e)
                {
                    std::ofstream(dataDirectory / L"native-error.log", std::ios::app)
                        << UtcNow() << " XAML " << std::hex << static_cast<uint32_t>(e.Exception()) << " "
                        << to_string(e.Message()) << '\n';
                });
        }

        Markup::IXamlType GetXamlType(Windows::UI::Xaml::Interop::TypeName const &type)
        {
            return metadata.GetXamlType(type);
        }

        Markup::IXamlType GetXamlType(hstring const &name)
        {
            return metadata.GetXamlType(name);
        }

        com_array<Markup::XmlnsDefinition> GetXmlnsDefinitions()
        {
            return metadata.GetXmlnsDefinitions();
        }

        void OnLaunched(LaunchActivatedEventArgs const &)
        {
            try
            {
                InitializeResources();
                window = NativeWindow::Create(dataDirectory, std::move(repository));
            }
            catch (hresult_error const &e)
            {
                std::ofstream(dataDirectory / L"native-error.log", std::ios::app)
                    << UtcNow() << " Launch " << std::hex << static_cast<uint32_t>(e.code()) << " "
                    << to_string(e.message()) << '\n';
                MessageBoxW(nullptr, e.message().c_str(), L"HS2 Manager 启动失败", MB_OK | MB_ICONERROR);
                exitCode = 1;
                Exit();
            }
            catch (std::exception const &e)
            {
                std::ofstream(dataDirectory / L"native-error.log", std::ios::app)
                    << UtcNow() << " Launch " << e.what() << '\n';
                MessageBoxW(nullptr, to_hstring(e.what()).c_str(), L"HS2 Manager 启动失败", MB_OK | MB_ICONERROR);
                exitCode = 1;
                Exit();
            }
        }
    };
} // 命名空间 hs2::ui

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    winrt::handle instance;
    try
    {
        init_apartment(apartment_type::single_threaded);
        int argc{};
        auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        std::vector<std::wstring> arguments;
        for (int i = 1; i < argc; ++i)
            arguments.emplace_back(argv[i]);
        LocalFree(argv);
        PWSTR local{};
        check_hresult(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local));
        std::filesystem::path localPath(local);
        CoTaskMemFree(local);
        auto location = hs2::ui::ResolveLibraryLocation(hs2::ui::ExecutableDirectory(), arguments, localPath);
        instance.attach(CreateMutexW(nullptr, FALSE, hs2::ui::LibraryMutexName(location.Directory).c_str()));
        if (!instance)
            throw_last_error();
        if (GetLastError() == ERROR_ALREADY_EXISTS)
            throw std::runtime_error("该资料库已由另一个 HS2 Manager 实例打开。");
        hs2::ui::PrepareLibrary(location);
        std::filesystem::create_directories(location.Directory);
        hs2::ui::Application::dataDirectory = location.Directory;
        // 数据库打开与恢复在启动 XAML 之前完成，错误能由原生对话框明确报告。
        auto repository = hs2::ui::OpenLibrary(location.Directory);
        Microsoft::UI::Xaml::Application::Start(
            [&repository](auto &&)
            {
                make<hs2::ui::Application>(std::move(repository));
            });
        return hs2::ui::Application::exitCode;
    }
    catch (hresult_error const &error)
    {
        std::ofstream(hs2::ui::Application::dataDirectory / L"native-error.log", std::ios::app)
            << hs2::UtcNow() << " Startup " << std::hex << static_cast<uint32_t>(error.code()) << " "
            << to_string(error.message()) << '\n';
        MessageBoxW(nullptr, error.message().c_str(), L"HS2 Manager", MB_OK | MB_ICONERROR);
        return static_cast<int>(error.code());
    }
    catch (std::exception const &error)
    {
        std::ofstream(hs2::ui::Application::dataDirectory / L"native-error.log", std::ios::app)
            << hs2::UtcNow() << " Startup " << error.what() << '\n';
        MessageBoxW(nullptr, to_hstring(error.what()).c_str(), L"HS2 Manager", MB_OK | MB_ICONERROR);
        return 1;
    }
}
