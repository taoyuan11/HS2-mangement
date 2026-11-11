#include "Archive/PackageArchive.h"
#include <windows.h>
#include <wrl/client.h>
#include "Common/MyInitGuid.h"
#include "7zip/Archive/IArchive.h"
#include "7zip/IPassword.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cwctype>
#include <memory>
#include <mutex>
#include <set>
#include <system_error>
#include <vector>

namespace hs2
{
    namespace
    {
        namespace fs = std::filesystem;
        using Microsoft::WRL::ComPtr;

        void Require(bool valid, const char *message)
        {
            if (!valid)
                throw std::runtime_error(message);
        }

        void Check(std::stop_token token)
        {
            if (token.stop_requested())
                throw std::system_error(std::make_error_code(std::errc::operation_canceled));
        }

        void Win32(bool success)
        {
            if (!success)
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category());
        }

        std::wstring Key(std::wstring text)
        {
            if (!text.empty())
                CharLowerBuffW(text.data(), static_cast<DWORD>(text.size()));
            return text;
        }

        struct Handle
        {
            HANDLE Value = INVALID_HANDLE_VALUE;

            ~Handle()
            {
                if (Value != INVALID_HANDLE_VALUE)
                    CloseHandle(Value);
            }
        };

        struct Module
        {
            HMODULE Value = nullptr;

            ~Module()
            {
                if (Value)
                    FreeLibrary(Value);
            }
        };

        struct Property
        {
            PROPVARIANT Value{};

            ~Property()
            {
                PropVariantClear(&Value);
            }

            bool Flag() const
            {
                return Value.vt == VT_BOOL && Value.boolVal != VARIANT_FALSE;
            }

            uint64_t Number() const
            {
                if (Value.vt == VT_UI8)
                    return Value.uhVal.QuadPart;
                if (Value.vt == VT_UI4)
                    return Value.ulVal;
                return 0;
            }

            std::wstring Text() const
            {
                if (Value.vt != VT_BSTR || !Value.bstrVal)
                    return {};
                return {Value.bstrVal, SysStringLen(Value.bstrVal)};
            }
        };

        template <class Interface> class RefCount : public Interface
        {
        public:
            ULONG STDMETHODCALLTYPE AddRef() noexcept override
            {
                return ++references_;
            }

            ULONG STDMETHODCALLTYPE Release() noexcept override
            {
                auto remaining = --references_;
                if (!remaining)
                    delete this;
                return remaining;
            }

        protected:
            virtual ~RefCount() = default;

        private:
            std::atomic<ULONG> references_{1};
        };

        struct State
        {
            const std::optional<std::wstring> &Password;
            std::stop_token Cancellation;
            std::function<void(uint64_t, uint64_t)> Progress;
            std::atomic<bool> PasswordAsked{false}, PasswordMissing{false}, WrongPassword{false};
            std::mutex Gate;
            std::exception_ptr Error;
            uint64_t Total = 0;

            template <class F> HRESULT Guard(F action) noexcept
            {
                try
                {
                    Check(Cancellation);
                    action();
                    return S_OK;
                }
                catch (...)
                {
                    std::lock_guard lock(Gate);
                    if (!Error)
                        Error = std::current_exception();
                    return E_ABORT;
                }
            }

            HRESULT GetPassword(BSTR *value) noexcept
            {
                *value = nullptr;
                PasswordAsked = true;
                if (Cancellation.stop_requested())
                    return E_ABORT;
                if (!Password)
                {
                    PasswordMissing = true;
                    return E_ABORT;
                }
                *value = SysAllocStringLen(Password->data(), static_cast<UINT>(Password->size()));
                return *value ? S_OK : E_OUTOFMEMORY;
            }

            void Rethrow()
            {
                Check(Cancellation);
                if (Error)
                    std::rethrow_exception(Error);
                if (PasswordMissing || WrongPassword)
                    throw ArchivePasswordRequired(!PasswordMissing);
            }
        };

        class Input final : public RefCount<IInStream>
        {
        public:
            Input(const fs::path &path, std::stop_token token) : token_(token)
            {
                file_.Value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                          FILE_ATTRIBUTE_NORMAL, nullptr);
                Win32(file_.Value != INVALID_HANDLE_VALUE);
            }

            HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **value) noexcept override
            {
                *value = nullptr;
                if (id != IID_IUnknown && id != IID_IInStream && id != IID_ISequentialInStream)
                    return E_NOINTERFACE;
                *value = static_cast<IInStream *>(this);
                AddRef();
                return S_OK;
            }

            HRESULT STDMETHODCALLTYPE Read(void *data, UInt32 size, UInt32 *read) noexcept override
            {
                if (read)
                    *read = 0;
                if (token_.stop_requested())
                    return E_ABORT;
                DWORD completed = 0;
                if (!ReadFile(file_.Value, data, size, &completed, nullptr))
                    return HRESULT_FROM_WIN32(GetLastError());
                if (read)
                    *read = completed;
                return S_OK;
            }

            HRESULT STDMETHODCALLTYPE Seek(Int64 offset, UInt32 origin, UInt64 *position) noexcept override
            {
                LARGE_INTEGER result{}, distance{};
                distance.QuadPart = offset;
                if (!SetFilePointerEx(file_.Value, distance, &result, origin))
                    return HRESULT_FROM_WIN32(GetLastError());
                if (position)
                    *position = static_cast<UInt64>(result.QuadPart);
                return S_OK;
            }

        private:
            Handle file_;
            std::stop_token token_;
        };

        // 即使临时目录被外部替换，也不沿重解析点向会话目录外写入。
        void SafeDirectory(const fs::path &directory)
        {
            auto full = fs::absolute(directory);
            fs::path current = full.root_path();
            for (auto &part : full.relative_path())
            {
                current /= part;
                auto attributes = GetFileAttributesW(current.c_str());
                if (attributes == INVALID_FILE_ATTRIBUTES)
                {
                    Win32(CreateDirectoryW(current.c_str(), nullptr) != FALSE);
                    attributes = GetFileAttributesW(current.c_str());
                }
                Require(attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) &&
                            !(attributes & FILE_ATTRIBUTE_REPARSE_POINT),
                        "解压目标目录不能包含链接。");
            }
        }

        class Output final : public RefCount<ISequentialOutStream>
        {
        public:
            Output(const fs::path &path, uint64_t expected, State &state) : expected_(expected), state_(state)
            {
                SafeDirectory(path.parent_path());
                file_.Value = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                          FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
                Win32(file_.Value != INVALID_HANDLE_VALUE);
            }

            HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **value) noexcept override
            {
                *value = nullptr;
                if (id != IID_IUnknown && id != IID_ISequentialOutStream)
                    return E_NOINTERFACE;
                *value = static_cast<ISequentialOutStream *>(this);
                AddRef();
                return S_OK;
            }

            HRESULT STDMETHODCALLTYPE Write(const void *data, UInt32 size, UInt32 *written) noexcept override
            {
                if (written)
                    *written = 0;
                return state_.Guard(
                    [&]
                    {
                        Require(size <= expected_ - completed_, "解压内容超出声明大小。");
                        DWORD count = 0;
                        Win32(WriteFile(file_.Value, data, size, &count, nullptr) != FALSE);
                        Require(count == size, "解压文件写入不完整，请检查剩余磁盘空间。");
                        completed_ += count;
                        if (written)
                            *written = count;
                    });
            }

            void Finish(const std::optional<FILETIME> &time)
            {
                Require(completed_ == expected_, "压缩包中的文件长度校验失败。");
                if (time)
                    Win32(SetFileTime(file_.Value, nullptr, nullptr, &*time) != FALSE);
            }

        private:
            Handle file_;
            uint64_t expected_, completed_ = 0;
            State &state_;
        };

        class OpenCallback final : public RefCount<IArchiveOpenCallback>, public ICryptoGetTextPassword
        {
        public:
            explicit OpenCallback(State &state) : state_(state)
            {
            }

            ULONG STDMETHODCALLTYPE AddRef() noexcept override
            {
                return RefCount::AddRef();
            }

            ULONG STDMETHODCALLTYPE Release() noexcept override
            {
                return RefCount::Release();
            }

            HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **value) noexcept override
            {
                *value = nullptr;
                if (id == IID_IUnknown || id == IID_IArchiveOpenCallback)
                    *value = static_cast<IArchiveOpenCallback *>(this);
                else if (id == IID_ICryptoGetTextPassword)
                    *value = static_cast<ICryptoGetTextPassword *>(this);
                else
                    return E_NOINTERFACE;
                AddRef();
                return S_OK;
            }

            HRESULT STDMETHODCALLTYPE SetTotal(const UInt64 *, const UInt64 *) noexcept override
            {
                return state_.Cancellation.stop_requested() ? E_ABORT : S_OK;
            }

            HRESULT STDMETHODCALLTYPE SetCompleted(const UInt64 *, const UInt64 *) noexcept override
            {
                return state_.Cancellation.stop_requested() ? E_ABORT : S_OK;
            }

            HRESULT STDMETHODCALLTYPE CryptoGetTextPassword(BSTR *value) noexcept override
            {
                return state_.GetPassword(value);
            }

        private:
            State &state_;
        };

        struct Entry
        {
            fs::path Relative;
            uint64_t Size = 0;
            bool Directory = false, Encrypted = false;
            std::optional<FILETIME> Modified;
        };

        class ExtractCallback final : public RefCount<IArchiveExtractCallback>, public ICryptoGetTextPassword
        {
        public:
            ExtractCallback(const fs::path &root, const std::vector<Entry> &entries, State &state)
                : root_(root), entries_(entries), state_(state)
            {
            }

            ULONG STDMETHODCALLTYPE AddRef() noexcept override
            {
                return RefCount::AddRef();
            }

            ULONG STDMETHODCALLTYPE Release() noexcept override
            {
                return RefCount::Release();
            }

            HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **value) noexcept override
            {
                *value = nullptr;
                if (id == IID_IUnknown || id == IID_IArchiveExtractCallback || id == IID_IProgress)
                    *value = static_cast<IArchiveExtractCallback *>(this);
                else if (id == IID_ICryptoGetTextPassword)
                    *value = static_cast<ICryptoGetTextPassword *>(this);
                else
                    return E_NOINTERFACE;
                AddRef();
                return S_OK;
            }

            HRESULT STDMETHODCALLTYPE SetTotal(UInt64) noexcept override
            {
                return state_.Cancellation.stop_requested() ? E_ABORT : S_OK;
            }

            HRESULT STDMETHODCALLTYPE SetCompleted(const UInt64 *value) noexcept override
            {
                return state_.Guard(
                    [&]
                    {
                        if (value && state_.Progress)
                            state_.Progress(std::min<uint64_t>(*value, state_.Total), state_.Total);
                    });
            }

            HRESULT STDMETHODCALLTYPE GetStream(UInt32 index, ISequentialOutStream **stream,
                                                Int32 mode) noexcept override
            {
                *stream = nullptr;
                output_.Reset();
                return state_.Guard(
                    [&]
                    {
                        Require(index < entries_.size(), "无效的压缩包条目。");
                        current_ = index;
                        if (mode != NArchive::NExtract::NAskMode::kExtract)
                            return;
                        auto &entry = entries_[index];
                        if (entry.Directory)
                            SafeDirectory(root_ / entry.Relative);
                        else
                        {
                            output_.Attach(new Output(root_ / entry.Relative, entry.Size, state_));
                            *stream = output_.Get();
                            output_->AddRef();
                        }
                    });
            }

            HRESULT STDMETHODCALLTYPE PrepareOperation(Int32) noexcept override
            {
                return state_.Cancellation.stop_requested() ? E_ABORT : S_OK;
            }

            HRESULT STDMETHODCALLTYPE SetOperationResult(Int32 result) noexcept override
            {
                auto status = state_.Guard(
                    [&]
                    {
                        if (result != NArchive::NExtract::NOperationResult::kOK)
                        {
                            auto encrypted = entries_[current_].Encrypted;
                            if (result == NArchive::NExtract::NOperationResult::kWrongPassword ||
                                (encrypted && (result == NArchive::NExtract::NOperationResult::kCRCError ||
                                               result == NArchive::NExtract::NOperationResult::kDataError)))
                                state_.WrongPassword = true;
                            else
                                throw std::runtime_error(
                                    "压缩包解压或校验失败，文件可能损坏或使用了不支持的压缩方式。");
                        }
                        else if (output_)
                            output_->Finish(entries_[current_].Modified);
                    });
                output_.Reset();
                return state_.WrongPassword ? E_ABORT : status;
            }

            HRESULT STDMETHODCALLTYPE CryptoGetTextPassword(BSTR *value) noexcept override
            {
                return state_.GetPassword(value);
            }

        private:
            fs::path root_;
            const std::vector<Entry> &entries_;
            State &state_;
            UInt32 current_ = 0;
            ComPtr<Output> output_;
        };
    }

    fs::path ValidatePackagePath(std::wstring name)
    {
        std::replace(name.begin(), name.end(), L'\\', L'/');
        Require(!name.empty() && name.front() != L'/', "压缩包包含绝对路径或空路径。");
        if (name.back() == L'/')
            name.pop_back();
        Require(!name.empty() && name.size() < 30000, "压缩包路径长度无效。");
        fs::path relative;
        for (size_t start = 0; start < name.size();)
        {
            auto end = name.find(L'/', start);
            if (end == std::wstring::npos)
                end = name.size();
            auto part = name.substr(start, end - start);
            Require(!part.empty() && part != L"." && part != L".." && part.back() != L'.' && part.back() != L' ' &&
                        part.size() <= 255,
                    "压缩包包含不安全的文件名或目录穿越路径。");
            Require(std::none_of(part.begin(), part.end(),
                                 [](wchar_t c)
                                 {
                                     return c < 32 || std::wstring_view(L":*?\"<>|").find(c) != std::wstring_view::npos;
                                 }),
                    "压缩包包含非法文件名或备用数据流。");
            auto base = Key(part.substr(0, part.find(L'.')));
            Require(base != L"con" && base != L"prn" && base != L"aux" && base != L"nul" && base != L"clock$" &&
                        base != L"conin$" && base != L"conout$" &&
                        !(base.size() == 4 && (base.starts_with(L"com") || base.starts_with(L"lpt")) &&
                          std::wstring_view(L"123456789¹²³").find(base.back()) != std::wstring_view::npos),
                    "压缩包包含 Windows 保留文件名。");
            relative /= part;
            start = end + 1;
        }
        Require(!relative.has_root_path(), "压缩包包含绝对路径。");
        return relative;
    }

    void ExtractPackage(const fs::path &source, const fs::path &destination, const fs::path &decoder,
                        const std::optional<std::wstring> &password, std::function<void(uint64_t, uint64_t)> progress,
                        std::stop_token cancellation, PackageLimits limits)
    {
        Check(cancellation);
        SafeDirectory(destination);
        Require(fs::is_empty(destination), "解压目标必须是空的临时目录。");
        Module module;
        module.Value = LoadLibraryExW(fs::absolute(decoder).c_str(), nullptr,
                                      LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(module.Value != nullptr, "压缩包解码组件缺失，请重新完整解压 HS2 Manager 程序包。");
        auto create = reinterpret_cast<HRESULT(WINAPI *)(const GUID *, const GUID *, void **)>(
            GetProcAddress(module.Value, "CreateObject"));
        Require(create != nullptr, "压缩包解码组件版本无效。");
        ComPtr<Input> input;
        input.Attach(new Input(source, cancellation));
        std::array<uint8_t, 8> signature{};
        UInt32 read = 0;
        Require(input->Read(signature.data(), static_cast<UInt32>(signature.size()), &read) == S_OK && read >= 6,
                "压缩包为空或无法读取。");
        uint8_t format = 0;
        if (signature[0] == 'P' && signature[1] == 'K')
            format = 1;
        else if (std::equal(signature.begin(), signature.begin() + 6,
                            std::array<uint8_t, 6>{'7', 'z', 0xbc, 0xaf, 0x27, 0x1c}.begin()))
            format = 7;
        else if (std::equal(signature.begin(), signature.begin() + 6,
                            std::array<uint8_t, 6>{'R', 'a', 'r', '!', 0x1a, 7}.begin()))
            format = signature[6] == 1 ? 0xcc : 3;
        Require(format != 0, "仅支持完整的 ZIP、7Z 或 RAR 压缩包；分卷包请先手动解压。");
        Require(input->Seek(0, FILE_BEGIN, nullptr) == S_OK, "无法读取压缩包。");
        const GUID classId{0x23170f69, 0x40c1, 0x278a, {0x10, 0, 0, 1, 0x10, format, 0, 0}};
        ComPtr<IInArchive> archive;
        Require(create(&classId, &IID_IInArchive, reinterpret_cast<void **>(archive.GetAddressOf())) == S_OK,
                "解码组件不支持此压缩格式。");
        State state{password, cancellation, std::move(progress)};
        ComPtr<OpenCallback> open;
        open.Attach(new OpenCallback(state));
        UInt64 start = 0;
        auto opened = archive->Open(input.Get(), &start, open.Get());
        state.Rethrow();
        if (opened != S_OK && state.PasswordAsked)
            throw ArchivePasswordRequired(true);
        Require(opened == S_OK, "压缩包无法打开，可能损坏或属于分卷包；请先手动解压检查。");
        Property volume, countVolumes, errors;
        archive->GetArchiveProperty(kpidIsVolume, &volume.Value);
        archive->GetArchiveProperty(kpidNumVolumes, &countVolumes.Value);
        archive->GetArchiveProperty(kpidErrorFlags, &errors.Value);
        Require(!volume.Flag() && countVolumes.Number() <= 1, "分卷压缩包请先手动解压，再选择角色卡。");
        Require(errors.Number() == 0, "压缩包目录损坏或不完整，请先手动解压检查。");
        UInt32 count = 0;
        Require(archive->GetNumberOfItems(&count) == S_OK && count <= limits.Entries,
                "压缩包条目数量无效或超过临时预览限制。");
        std::vector<Entry> entries;
        std::set<std::wstring> names, files;
        for (UInt32 i = 0; i < count; ++i)
        {
            Check(cancellation);
            auto property = [&](PROPID id, Property &value)
            {
                Require(archive->GetProperty(i, id, &value.Value) == S_OK, "无法读取压缩包条目。");
            };
            Property name, directory, size, encrypted, modified, attributes, posix;
            property(kpidPath, name);
            property(kpidIsDir, directory);
            property(kpidSize, size);
            property(kpidEncrypted, encrypted);
            property(kpidMTime, modified);
            property(kpidAttrib, attributes);
            property(kpidPosixAttrib, posix);
            for (auto id : {kpidSymLink, kpidHardLink, kpidIsAltStream, kpidIsAnti, kpidSplitBefore, kpidSplitAfter})
            {
                Property unsafe;
                property(id, unsafe);
                Require(!unsafe.Flag() && unsafe.Text().empty(), "压缩包包含链接、备用数据流、删除条目或分卷文件。");
            }
            auto mode = posix.Number() & 0170000;
            Require(!(attributes.Number() & FILE_ATTRIBUTE_REPARSE_POINT) &&
                        (mode == 0 || mode == 0100000 || mode == 0040000),
                    "压缩包包含链接或特殊设备文件。");
            Entry entry{ValidatePackagePath(name.Text()), size.Number(), directory.Flag(), encrypted.Flag(), {}};
            Require(entry.Directory || size.Value.vt == VT_UI8 || size.Value.vt == VT_UI4, "压缩包条目缺少解压大小。");
            auto key = Key(entry.Relative.generic_wstring());
            Require(names.insert(key).second, "压缩包包含规范化后重名的文件。");
            if (!entry.Directory)
                files.insert(key);
            Require(entry.Size <= limits.FileBytes && entry.Size <= limits.TotalBytes - state.Total,
                    "压缩包解压大小超过临时预览限制（单文件 8 GiB，总计 32 GiB）。");
            state.Total += entry.Size;
            if (modified.Value.vt == VT_FILETIME)
                entry.Modified = modified.Value.filetime;
            entries.push_back(std::move(entry));
        }
        for (auto &entry : entries)
            for (auto parent = entry.Relative.parent_path(); !parent.empty(); parent = parent.parent_path())
                Require(!files.contains(Key(parent.generic_wstring())), "压缩包中存在文件与目录路径冲突。");
        Require(fs::space(destination).available >= state.Total + 128ULL * 1024 * 1024,
                "本机临时目录的剩余磁盘空间不足。");
        ComPtr<ExtractCallback> extract;
        extract.Attach(new ExtractCallback(destination, entries, state));
        auto result = archive->Extract(nullptr, static_cast<UInt32>(-1), 0, extract.Get());
        state.Rethrow();
        Require(result == S_OK, "压缩包解压失败，请检查文件是否完整和磁盘空间是否充足。");
        Require(archive->Close() == S_OK, "压缩包读取未能正常结束。");
        if (state.Progress)
            state.Progress(state.Total, state.Total);
    }
}
