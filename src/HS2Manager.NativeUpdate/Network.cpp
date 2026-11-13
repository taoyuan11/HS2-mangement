#include "Update.h"
#include <winhttp.h>
#include <atomic>
#include <chrono>
#include <fstream>
#include <memory>
#include <stdexcept>

namespace hs2::update
{
    namespace
    {
        struct Internet
        {
            std::atomic<HINTERNET> Value;

            explicit Internet(HINTERNET value) : Value(value)
            {
                if (!value)
                    throw std::runtime_error("无法创建更新网络请求。");
            }

            void Close()
            {
                if (auto value = Value.exchange(nullptr))
                    WinHttpCloseHandle(value);
            }

            ~Internet()
            {
                Close();
            }
        };

        struct AsyncState
        {
            Handle Event{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
            Handle Closed{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
            std::atomic<DWORD> Error{0}, Bytes{0};

            AsyncState()
            {
                if (!Event.Value || !Closed.Value)
                    throw std::runtime_error("无法创建更新网络事件。");
            }

            static void CALLBACK Notify(HINTERNET, DWORD_PTR context, DWORD status, void *info, DWORD length)
            {
                auto self = reinterpret_cast<AsyncState *>(context);
                if (!self)
                    return;
                if (status == WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING)
                {
                    SetEvent(self->Closed.Value);
                    return;
                }
                if (status == WINHTTP_CALLBACK_STATUS_REQUEST_ERROR)
                    self->Error = static_cast<WINHTTP_ASYNC_RESULT *>(info)->dwError;
                else if (status == WINHTTP_CALLBACK_STATUS_READ_COMPLETE)
                    self->Bytes = length;
                else if (status != WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE &&
                         status != WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE)
                    return;
                SetEvent(self->Event.Value);
            }

            void Wait(BOOL started, std::stop_token stop, std::chrono::steady_clock::time_point deadline)
            {
                if (!started && GetLastError() != ERROR_IO_PENDING)
                    throw std::runtime_error("更新网络请求启动失败。");
                while (WaitForSingleObject(Event.Value, 100) != WAIT_OBJECT_0)
                {
                    CheckStop(stop);
                    if (std::chrono::steady_clock::now() > deadline)
                        throw std::runtime_error("更新请求超时，请重试。");
                }
                CheckStop(stop);
                if (Error)
                    throw std::runtime_error("无法连接 GitHub，请检查网络与系统代理（Windows " +
                                             std::to_string(Error.load()) + "）。");
            }
        };

        struct AsyncRequest
        {
            AsyncState State;
            HINTERNET Value = nullptr;

            explicit AsyncRequest(HINTERNET value) : Value(value)
            {
                if (!value)
                    throw std::runtime_error("无法创建更新请求。");
                DWORD_PTR context = reinterpret_cast<DWORD_PTR>(&State);
                if (!WinHttpSetOption(value, WINHTTP_OPTION_CONTEXT_VALUE, &context, sizeof(context)) ||
                    WinHttpSetStatusCallback(value, AsyncState::Notify,
                                             WINHTTP_CALLBACK_FLAG_ALL_COMPLETIONS | WINHTTP_CALLBACK_FLAG_HANDLES |
                                                 WINHTTP_CALLBACK_FLAG_REQUEST_ERROR,
                                             0) == WINHTTP_INVALID_STATUS_CALLBACK)
                {
                    WinHttpCloseHandle(value);
                    throw std::runtime_error("无法初始化异步下载。");
                }
            }

            ~AsyncRequest()
            {
                // 异步句柄关闭后等待最后的 HANDLE_CLOSING 通知，再释放回调上下文。
                WinHttpCloseHandle(Value);
                WaitForSingleObject(State.Closed.Value, INFINITE);
            }
        };

        void Require(BOOL result, std::stop_token stop)
        {
            CheckStop(stop);
            if (!result)
                throw std::runtime_error("无法连接 GitHub，请检查网络和系统代理后重试（Windows " +
                                         std::to_string(GetLastError()) + "）。");
        }

        HttpResponse Request(std::string url, fs::path const &file, uint64_t maximum, std::stop_token stop,
                             Progress progress)
        {
            Internet session(WinHttpOpen(L"HS2Manager-Updater/1", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                         WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, WINHTTP_FLAG_ASYNC));
            WinHttpSetTimeouts(session.Value, 10000, 10000, 15000, 15000);
            auto deadline = std::chrono::steady_clock::now() +
                            (file.empty() ? std::chrono::seconds(30) : std::chrono::seconds(900));
            for (int redirects = 0; redirects <= 5; ++redirects)
            {
                CheckStop(stop);
                auto wide = Path(url).wstring();
                URL_COMPONENTS parts{};
                parts.dwStructSize = sizeof(parts);
                parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = static_cast<DWORD>(-1);
                if (!WinHttpCrackUrl(wide.c_str(), 0, 0, &parts) || parts.nScheme != INTERNET_SCHEME_HTTPS ||
                    parts.nPort != 443)
                    throw std::runtime_error("更新下载只允许 HTTPS 地址。");
                std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
                if (host != L"api.github.com" && host != L"github.com" &&
                    host != L"release-assets.githubusercontent.com" && host != L"objects.githubusercontent.com" &&
                    host != L"github-releases.githubusercontent.com")
                    throw std::runtime_error("更新请求跳转到非 GitHub 地址。");
                std::wstring target(parts.lpszUrlPath, parts.dwUrlPathLength);
                if (parts.dwExtraInfoLength)
                    target.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
                Internet connection(WinHttpConnect(session.Value, host.c_str(), parts.nPort, 0));
                std::array<char, 65536> buffer{};
                AsyncRequest request(WinHttpOpenRequest(connection.Value, L"GET", target.c_str(), nullptr,
                                                        WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                                                        WINHTTP_FLAG_SECURE));
                DWORD policy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
                Require(WinHttpSetOption(request.Value, WINHTTP_OPTION_REDIRECT_POLICY, &policy, sizeof(policy)), stop);
                request.State.Wait(
                    WinHttpSendRequest(
                        request.Value, L"Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n",
                        static_cast<DWORD>(-1), nullptr, 0, 0, reinterpret_cast<DWORD_PTR>(&request.State)),
                    stop, deadline);
                request.State.Wait(WinHttpReceiveResponse(request.Value, nullptr), stop, deadline);
                DWORD status{}, length = sizeof(status);
                Require(WinHttpQueryHeaders(request.Value, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                                            nullptr, &status, &length, nullptr),
                        stop);
                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                {
                    std::wstring location(32768, L'\0');
                    length = static_cast<DWORD>(location.size() * sizeof(wchar_t));
                    Require(WinHttpQueryHeaders(request.Value, WINHTTP_QUERY_LOCATION, nullptr, location.data(),
                                                &length, nullptr),
                            stop);
                    location.resize(length / sizeof(wchar_t));
                    while (!location.empty() && location.back() == 0)
                        location.pop_back();
                    auto utf8 = fs::path(location).u8string();
                    url.assign(reinterpret_cast<char const *>(utf8.data()), utf8.size());
                    continue;
                }
                HttpResponse result{status, {}};
                if (status != 200)
                    return result;
                std::ofstream out;
                if (!file.empty())
                {
                    fs::create_directories(file.parent_path());
                    out.open(file, std::ios::binary | std::ios::trunc);
                    if (!out)
                        throw std::runtime_error("无法写入更新下载目录。");
                }
                uint64_t received = 0;
                while (true)
                {
                    CheckStop(stop);
                    if (std::chrono::steady_clock::now() > deadline)
                        throw std::runtime_error("更新下载超时，请重试。");
                    request.State.Wait(
                        WinHttpReadData(request.Value, buffer.data(), static_cast<DWORD>(buffer.size()), nullptr), stop,
                        deadline);
                    DWORD count = request.State.Bytes;
                    if (!count)
                        break;
                    received += count;
                    if (received > maximum)
                        throw std::runtime_error("下载内容超出预期大小。");
                    if (file.empty())
                        result.Body.append(buffer.data(), count);
                    else
                    {
                        out.write(buffer.data(), count);
                        if (!out)
                            throw std::runtime_error("更新下载写入失败，请检查磁盘空间。");
                    }
                    if (progress)
                        progress(received, maximum);
                }
                return result;
            }
            throw std::runtime_error("更新请求跳转次数过多。");
        }
    }

    HttpResponse Get(std::string const &url, std::stop_token stop)
    {
        return Request(url, {}, 2 * 1024 * 1024, stop, {});
    }

    void Download(Release const &release, fs::path const &archive, std::stop_token stop, Progress progress)
    {
        try
        {
            auto result = Request(release.ArchiveUrl, archive, release.Bytes, stop, progress);
            if (result.Status != 200)
                throw std::runtime_error("下载失败（HTTP " + std::to_string(result.Status) + "）。");
            if (fs::file_size(archive) != release.Bytes || HashFile(archive, stop) != release.SHA256)
                throw std::runtime_error("下载文件校验失败，请重试。");
        }
        catch (...)
        {
            std::error_code ec;
            fs::remove(archive, ec);
            throw;
        }
    }
}
