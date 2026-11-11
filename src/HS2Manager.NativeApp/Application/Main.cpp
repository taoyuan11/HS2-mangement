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
        static inline int exitCode = 0;
        static inline std::filesystem::path dataDirectory;

        Application()
        {
            UnhandledException(
                [](auto &&, UnhandledExceptionEventArgs const &e)
                {
                    std::ofstream(dataDirectory / L"native-error.log", std::ios::app) << to_string(e.Message()) << '\n';
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
                window = NativeWindow::Create(dataDirectory);
            }
            catch (hresult_error const &e)
            {
                std::ofstream(dataDirectory / L"native-error.log", std::ios::app)
                    << std::hex << static_cast<uint32_t>(e.code()) << " " << to_string(e.message()) << '\n';
                exitCode = 1;
                Exit();
            }
            catch (std::exception const &e)
            {
                std::ofstream(dataDirectory / L"native-error.log", std::ios::app) << e.what() << '\n';
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
        Microsoft::UI::Xaml::Application::Start(
            [](auto &&)
            {
                make<hs2::ui::Application>();
            });
        return hs2::ui::Application::exitCode;
    }
    catch (hresult_error const &error)
    {
        MessageBoxW(nullptr, error.message().c_str(), L"HS2 Manager", MB_OK | MB_ICONERROR);
        return static_cast<int>(error.code());
    }
    catch (std::exception const &error)
    {
        MessageBoxW(nullptr, to_hstring(error.what()).c_str(), L"HS2 Manager", MB_OK | MB_ICONERROR);
        return 1;
    }
}
