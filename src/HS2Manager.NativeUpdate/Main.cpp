#include "Update.h"
#include "third_party/json.hpp"
#include <shellapi.h>
#include <stdexcept>

using namespace hs2::update;

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    fs::path request, installation, data;
    bool restartOld = false;
    try
    {
        int count{};
        auto raw = CommandLineToArgvW(GetCommandLineW(), &count);
        std::vector<std::wstring> args(raw, raw + count);
        LocalFree(raw);
        if (args.size() < 3)
            throw std::runtime_error(
                "用法：HS2Updater.exe --recover <transaction.json> 或 --complete <transaction.json>");
        request = args[2];
        auto j = nlohmann::json::parse(ReadText(request));
        installation = Path(j.at("installation"));
        data = Path(j.at("data"));
        auto cache = CacheRoot(installation);
        if (args[1] == L"--recover" || args[1] == L"--complete")
        {
            auto lease = InstallationLease(installation, true);
            if (args[1] == L"--recover")
                Recover(request);
            else
                Complete(request);
            MessageBoxW(nullptr, L"更新恢复记录已处理。现在可以启动程序。", L"HS2 Manager 更新",
                        MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        auto requestName = Utf8(request.filename());
        if (args[1] != L"--install" || args.size() != 5 || args[3] != L"--handoff" || request.parent_path() != cache ||
            !requestName.starts_with("request-") || !requestName.ends_with(".json"))
            throw std::runtime_error("更新请求参数无效。");
        Handle parent(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, j.at("parent").get<DWORD>()));
        FILETIME creation{}, exit{}, kernel{}, user{};
        if (!parent.Value || parent.Value == INVALID_HANDLE_VALUE ||
            !GetProcessTimes(parent.Value, &creation, &exit, &kernel, &user) ||
            ((uint64_t(creation.dwHighDateTime) << 32) | creation.dwLowDateTime) != j.at("created").get<uint64_t>())
            throw std::runtime_error("无法确认发起更新的程序。");
        Handle handoff(OpenEventW(EVENT_MODIFY_STATE, FALSE, args[4].c_str()));
        if (!handoff.Value || !SetEvent(handoff.Value))
            throw std::runtime_error("更新交接失败。");
        if (WaitForSingleObject(parent.Value, 45000) != WAIT_OBJECT_0)
            return 1;
        restartOld = true;
        auto lease = InstallationLease(installation, true);
        auto journal = Install(installation, Path(j.at("package")), data);
        restartOld = false;
        // 只在新程序成功初始化窗口后清理备份，失败时不回滚可能已被新版迁移的数据。
        auto readyName = L"Local\\HS2Manager.UpdateReady." + Path(Id()).wstring();
        Handle ready(CreateEventW(nullptr, TRUE, FALSE, readyName.c_str()));
        lease = Handle();
        auto process = StartApplication(installation, data, readyName);
        HANDLE handles[]{ready.Value, process.Value};
        if (WaitForMultipleObjects(2, handles, FALSE, 60000) != WAIT_OBJECT_0)
            throw std::runtime_error(
                "新版未确认启动成功，旧程序备份已保留。关闭程序后可运行 HS2Updater.exe --recover \"" + Utf8(journal) +
                "\"，或确认新版正常后运行 --complete。");
        Complete(journal);
        std::error_code ignored;
        fs::remove(request, ignored);
        auto package = Path(j.at("package"));
        if (package.parent_path().parent_path() == cache &&
            Utf8(package.parent_path().filename()).starts_with("download-"))
        {
            CheckTreePath(package.parent_path(), {});
            for (auto const &entry : fs::recursive_directory_iterator(package.parent_path()))
                CheckOrdinary(entry.path());
            fs::remove_all(package.parent_path());
        }
        return 0;
    }
    catch (std::exception const &error)
    {
        if (!installation.empty())
        {
            try
            {
                WriteText(CacheRoot(installation) / L"last-error.txt", error.what());
            }
            catch (...)
            {
            }
            if (restartOld)
            {
                try
                {
                    auto process = StartApplication(installation, data);
                }
                catch (...)
                {
                }
            }
        }
        MessageBoxW(nullptr, Path(error.what()).c_str(), L"HS2 Manager 更新未完成", MB_OK | MB_ICONERROR);
        return 1;
    }
}
