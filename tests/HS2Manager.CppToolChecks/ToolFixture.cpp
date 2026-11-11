#include "Common/Core.h"
#include <windows.h>
#include <iostream>
#include <thread>
using namespace hs2;

namespace
{
    Json Read(const fs::path &path)
    {
        return Json::parse(ReadFile(path));
    }

    void Text(const fs::path &path, std::string const &text)
    {
        WriteAtomic(path, {reinterpret_cast<const uint8_t *>(text.data()), text.size()});
    }
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc >= 3 && std::wstring_view(argv[1]) == L"--echo")
        {
            Json array = Json::array();
            for (int i = 3; i < argc; ++i)
                array.push_back(Utf8(fs::path(argv[i])));
            WriteJson(argv[2], array);
            return 0;
        }
        if (argc >= 3 && std::wstring_view(argv[1]) == L"--child")
        {
            WriteJson(argv[2], Json{{"Pid", GetCurrentProcessId()}});
            std::this_thread::sleep_for(std::chrono::seconds(30));
            return 0;
        }
        if (argc >= 3 && (std::wstring_view(argv[1]) == L"--spawn" || std::wstring_view(argv[1]) == L"--spawn-exit"))
        {
            std::wstring command = L"\"" + std::wstring(argv[0]) + L"\" --child \"" + std::wstring(argv[2]) + L"\"";
            STARTUPINFOW startup{};
            startup.cb = sizeof(startup);
            PROCESS_INFORMATION process{};
            if (!CreateProcessW(argv[0], command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr,
                                &startup, &process))
                throw std::runtime_error("Spawn test child failed");
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            auto limit = std::chrono::steady_clock::now() + std::chrono::seconds(5);
            while (!fs::exists(argv[2]) && std::chrono::steady_clock::now() < limit)
                std::this_thread::sleep_for(std::chrono::milliseconds(20));
            if (!fs::exists(argv[2]))
                throw std::runtime_error("Child did not start");
            if (std::wstring_view(argv[1]) == L"--spawn")
                std::this_thread::sleep_for(std::chrono::seconds(30));
            return 0;
        }
        fs::path script;
        for (int i = 1; i < argc; ++i)
        {
            std::wstring argument(argv[i]);
            if (argument == L"--python" && i + 1 < argc)
                script = argv[++i];
            else if (argument.starts_with(L"-script="))
                script = argument.substr(8);
            else if (argument.starts_with(L"-ExecutePythonScript="))
                script = argument.substr(21);
        }
        if (script.empty())
            throw std::runtime_error("Missing mock conversion script argument");
        auto request = Read(script.parent_path() / L"request.json");
        auto mode = request.at("Mode").get<std::string>();
        auto output = Path(request.at("Output").get<std::string>());
        bool unreal = script.filename() == L"UnrealNative.py";
        auto entry = output / (unreal ? L"HS2Character.uproject" : L"character.blend");
        if (mode == "probe")
        {
            if (unreal && !fs::is_regular_file(entry))
                throw std::runtime_error("Probe project missing");
        }
        else if (mode == "convert")
        {
            if (request.at("Source").get<std::string>().empty())
                throw std::runtime_error("Conversion source missing");
            if (unreal)
            {
                fs::create_directories(output / L"Saved");
                Text(output / L"Saved" / L"temporary", "owned test data");
            }
            else
                Text(entry, "Mock native asset; process integration fixture only");
        }
        else if (mode == "verify")
        {
            if (!request.at("Source").get<std::string>().empty())
                throw std::runtime_error("Verification was given source resources");
            if (!fs::is_regular_file(entry))
                throw std::runtime_error("Reopen target missing");
        }
        else
            throw std::runtime_error("Unexpected conversion mode");
        WriteJson(Path(request.at("Result").get<std::string>()), Json{{"Passed", true},
                                                                      {"ToolVersion", "test-only native fixture"},
                                                                      {"Warnings", Json::array({"fixture warning"})}});
        return 0;
    }
    catch (std::exception const &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
