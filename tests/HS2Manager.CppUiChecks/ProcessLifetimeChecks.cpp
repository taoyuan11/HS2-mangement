#include "Infrastructure/ProcessLifetime.h"
#include "Tools/OwnedToolProcess.h"
#include <tlhelp32.h>
#include <werapi.h>
#include <fstream>
#include <iostream>
#include <thread>

using namespace hs2;
using namespace hs2::ui;

namespace
{
    void Require(bool value, char const *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }

    std::wstring Quote(fs::path const &path)
    {
        return L"\"" + path.wstring() + L"\"";
    }

    struct Child
    {
        HANDLE Job = CreateJobObjectW(nullptr, nullptr);
        PROCESS_INFORMATION Process{};

        Child(fs::path const &executable, std::wstring arguments)
        {
            Require(Job != nullptr, "Create test containment job failed");
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            Require(SetInformationJobObject(Job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)),
                    "Set test containment job failed");
            auto command = Quote(executable) + L" " + arguments;
            STARTUPINFOW startup{sizeof(startup)};
            Require(CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
                                   CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, nullptr, &startup, &Process),
                    "Start lifetime fixture failed");
            if (!AssignProcessToJobObject(Job, Process.hProcess) || ResumeThread(Process.hThread) == DWORD(-1))
            {
                TerminateProcess(Process.hProcess, 1);
                WaitForSingleObject(Process.hProcess, 10000);
                CloseHandle(Process.hThread);
                CloseHandle(Process.hProcess);
                CloseHandle(Job);
                throw std::runtime_error("Contain/resume lifetime fixture failed");
            }
        }

        ~Child()
        {
            TerminateJobObject(Job, 1);
            WaitForSingleObject(Process.hProcess, 10000);
            CloseHandle(Process.hThread);
            CloseHandle(Process.hProcess);
            CloseHandle(Job);
        }

        void Exited(DWORD expected, DWORD milliseconds = 10000)
        {
            Require(WaitForSingleObject(Process.hProcess, milliseconds) == WAIT_OBJECT_0,
                    "Fixture retained a process after exit");
            DWORD code{};
            Require(GetExitCodeProcess(Process.hProcess, &code), "Cannot read fixture exit code");
            if (code != expected)
                throw std::runtime_error("Unexpected fixture exit code: " + std::to_string(code) + ", expected " +
                                         std::to_string(expected));
        }
    };

    void NoRemainingImage(fs::path const &executable)
    {
        auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        Require(snapshot != INVALID_HANDLE_VALUE, "Cannot verify remaining processes");
        PROCESSENTRY32W entry{sizeof(entry)};
        bool found = false;
        if (Process32FirstW(snapshot, &entry))
            do
            {
                if (executable.filename() == entry.szExeFile)
                    found = true;
            } while (Process32NextW(snapshot, &entry));
        CloseHandle(snapshot);
        Require(!found, "A process or WER snapshot retained the fixture image");
    }

    [[noreturn]] void Fixture(std::wstring_view mode, fs::path const &directory, fs::path const &tool)
    {
        InitializeProcessLifetime();
        SetProcessErrorDirectory(directory);
        DWORD flags{};
        constexpr DWORD required =
            WER_FAULT_REPORTING_DISABLE_SNAPSHOT_CRASH | WER_FAULT_REPORTING_DISABLE_SNAPSHOT_HANG;
        Require(SUCCEEDED(WerGetFlags(GetCurrentProcess(), &flags)) && (flags & required) == required,
                "WER snapshots remain enabled");
        if (mode == L"detach")
        {
            Require(LoadLibraryW((directory / L"exit-blocking.dll").c_str()) != nullptr, "Cannot load blocking DLL");
            FinishProcess(23);
        }
        if (mode == L"timeout")
        {
            BeginProcessShutdown(120);
            // 重复关闭不得把期限延后。
            BeginProcessShutdown(30000);
            Sleep(30000);
        }
        if (mode == L"cancel")
        {
            BeginProcessShutdown(120);
            CancelProcessShutdown();
            Sleep(300);
            FinishProcess(0);
        }
        if (mode == L"crash")
            RaiseException(0xe0424242, EXCEPTION_NONCONTINUABLE, 0, nullptr);
        if (mode == L"fastfail")
            RaiseFailFastException(nullptr, nullptr, 0);
        if (mode == L"tool")
            RunOwnedTool(tool, {L"--spawn", (directory / L"tool-child.json").wstring()}, directory,
                         std::chrono::seconds(30), directory / L"tool.log");
        FinishProcess(1);
    }
}

int wmain(int argc, wchar_t **argv)
{
    fs::path root;
    try
    {
        if (argc == 5 && std::wstring_view(argv[1]) == L"--fixture")
            Fixture(argv[2], argv[3], argv[4]);
        Require(argc == 3, "Usage: lifetime checks <blocking dll> <tool fixture>");
        root = fs::temp_directory_path() / Path("hs2-process-lifetime-" + NewId());
        fs::create_directories(root);
        auto executable = root / Path("hs2-lifetime-" + NewId() + ".exe");
        fs::copy_file(fs::absolute(argv[0]), executable);
        fs::copy_file(argv[1], root / L"exit-blocking.dll");
        auto tool = fs::absolute(argv[2]);
        auto run = [&](std::wstring_view mode, DWORD code)
        {
            {
                Child child(executable, L"--fixture " + std::wstring(mode) + L" " + Quote(root) + L" " + Quote(tool));
                child.Exited(code);
            }
            NoRemainingImage(executable);
            std::cout << "PASS process lifetime: " << Utf8(fs::path(mode)) << '\n';
        };
        run(L"detach", 23);
        run(L"cancel", 0);
        Require(!fs::exists(root / L"native-error.log"), "Cancelled close triggered a shutdown timeout");
        run(L"timeout", WAIT_TIMEOUT);
        run(L"crash", 0xe0424242);
        run(L"fastfail", 0xc0000602);
        auto log = ReadFile(root / L"native-error.log");
        auto text = std::string(log.begin(), log.end());
        Require(text.find("Shutdown timeout") != text.npos && text.find("Unhandled native exception") != text.npos,
                "Fatal/timeout diagnostics were not saved");
        {
            Child parent(executable, L"--fixture tool " + Quote(root) + L" " + Quote(tool));
            auto pidFile = root / L"tool-child.json";
            auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
            while (!fs::exists(pidFile) && std::chrono::steady_clock::now() < deadline)
                std::this_thread::sleep_for(std::chrono::milliseconds(20));
            Require(fs::exists(pidFile), "Export descendant did not start");
            auto bytes = ReadFile(pidFile);
            DWORD pid = Json::parse(bytes).at("Pid");
            auto descendant = OpenProcess(SYNCHRONIZE, FALSE, pid);
            Require(descendant != nullptr, "Cannot observe export descendant");
            auto stopped = TerminateProcess(parent.Process.hProcess, 37);
            auto state = WaitForSingleObject(descendant, 10000);
            CloseHandle(descendant);
            Require(stopped && state == WAIT_OBJECT_0, "Forced parent exit left an export descendant running");
            parent.Exited(37);
        }
        NoRemainingImage(executable);
        Require(Within(root, fs::temp_directory_path()) &&
                    root.filename().wstring().starts_with(L"hs2-process-lifetime-"),
                "Invalid test cleanup path");
        fs::remove_all(root);
        std::cout << "PASS forced parent termination, export job cleanup and deletion of executable/DLL directory\n";
        return 0;
    }
    catch (std::exception const &error)
    {
        std::cerr << "FAIL process lifetime: " << error.what() << "; fixture: " << Utf8(root) << '\n';
        return 1;
    }
}
