#include "Assets/UnityBlockCache.h"
#include "Assets/AssetReader.h"
#include <Windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <fstream>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        using Digest = std::array<std::uint8_t, 32>;
        constexpr std::size_t HeaderSize = 80;
        constexpr std::array<std::uint8_t, 8> Magic{'H', 'S', '2', 'B', 'L', 'K', '0', '1'};

        void Cancel(std::stop_token token)
        {
            if (token.stop_requested())
                throw std::runtime_error("Resource loading cancelled.");
        }

        struct Hasher
        {
            BCRYPT_ALG_HANDLE Algorithm{};
            BCRYPT_HASH_HANDLE Hash{};

            Hasher()
            {
                if (BCryptOpenAlgorithmProvider(&Algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
                    throw std::runtime_error("Block cache hash initialization failed.");
                if (BCryptCreateHash(Algorithm, &Hash, nullptr, 0, nullptr, 0, 0) < 0)
                {
                    BCryptCloseAlgorithmProvider(Algorithm, 0);
                    throw std::runtime_error("Block cache hash initialization failed.");
                }
            }

            ~Hasher()
            {
                BCryptDestroyHash(Hash);
                BCryptCloseAlgorithmProvider(Algorithm, 0);
            }

            void Add(std::span<const std::uint8_t> bytes, std::stop_token token)
            {
                while (!bytes.empty())
                {
                    Cancel(token);
                    auto count = static_cast<ULONG>(std::min<std::size_t>(bytes.size(), 1024 * 1024));
                    if (BCryptHashData(Hash, const_cast<PUCHAR>(bytes.data()), count, 0) < 0)
                        throw std::runtime_error("Block cache hashing failed.");
                    bytes = bytes.subspan(count);
                }
            }

            Digest Finish()
            {
                Digest result{};
                if (BCryptFinishHash(Hash, result.data(), static_cast<ULONG>(result.size()), 0) < 0)
                    throw std::runtime_error("Block cache hashing failed.");
                return result;
            }
        };

        Digest Hash(std::span<const std::uint8_t> bytes, std::stop_token token)
        {
            Hasher hash;
            hash.Add(bytes, token);
            return hash.Finish();
        }

        std::string Hex(const Digest &digest)
        {
            std::string result;
            constexpr char digits[] = "0123456789abcdef";
            for (auto byte : digest)
            {
                result += digits[byte >> 4];
                result += digits[byte & 15];
            }
            return result;
        }

        std::shared_ptr<const UnityBlockCache::Bytes> Read(const std::filesystem::path &path, const Digest &key,
                                                           std::size_t size, std::stop_token token)
        {
            std::ifstream file(path, std::ios::binary | std::ios::ate);
            if (!file || file.tellg() != static_cast<std::streamoff>(HeaderSize + size))
                return {};
            file.seekg(0);
            std::array<std::uint8_t, HeaderSize> header{};
            file.read(reinterpret_cast<char *>(header.data()), header.size());
            std::uint64_t length = 0;
            std::memcpy(&length, header.data() + 8, sizeof(length));
            if (!file || !std::equal(Magic.begin(), Magic.end(), header.begin()) || length != size ||
                !std::equal(key.begin(), key.end(), header.begin() + 16))
                return {};
            auto result = std::make_shared<UnityBlockCache::Bytes>(size);
            for (std::size_t offset = 0; offset < size;)
            {
                Cancel(token);
                auto count = std::min<std::size_t>(size - offset, 1024 * 1024);
                file.read(reinterpret_cast<char *>(result->data() + offset), count);
                if (!file)
                    return {};
                offset += count;
            }
            auto digest = Hash(*result, token);
            if (!std::equal(digest.begin(), digest.end(), header.begin() + 48))
                return {};
            return result;
        }

        void Write(const std::filesystem::path &path, const Digest &key, std::span<const std::uint8_t> bytes,
                   std::stop_token token)
        {
            std::array<std::uint8_t, HeaderSize> header{};
            std::copy(Magic.begin(), Magic.end(), header.begin());
            std::uint64_t length = bytes.size();
            std::memcpy(header.data() + 8, &length, sizeof(length));
            std::copy(key.begin(), key.end(), header.begin() + 16);
            auto digest = Hash(bytes, token);
            std::copy(digest.begin(), digest.end(), header.begin() + 48);
            static std::atomic_uint64_t sequence{};
            auto temporary = path;
            temporary += "." + std::to_string(GetCurrentProcessId()) + "." +
                         std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + "." +
                         std::to_string(++sequence) + ".tmp";

            struct Cleanup
            {
                std::filesystem::path Path;
                bool Owned = false;

                ~Cleanup()
                {
                    std::error_code error;
                    if (Owned)
                        std::filesystem::remove(Path, error);
                }
            } cleanup{temporary};

            std::filesystem::create_directories(path.parent_path());
            auto handle =
                CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (handle == INVALID_HANDLE_VALUE)
                throw std::runtime_error("Cannot create block cache file.");
            cleanup.Owned = true;
            {
                struct Close
                {
                    HANDLE Handle;

                    ~Close()
                    {
                        CloseHandle(Handle);
                    }
                } close{handle};

                auto write = [&](std::span<const std::uint8_t> data)
                {
                    while (!data.empty())
                    {
                        Cancel(token);
                        auto count = static_cast<DWORD>(std::min<std::size_t>(data.size(), 1024 * 1024));
                        DWORD written = 0;
                        if (!WriteFile(handle, data.data(), count, &written, nullptr) || written != count)
                            throw std::runtime_error("Block cache write failed.");
                        data = data.subspan(count);
                    }
                };
                write(header);
                write(bytes);
            }
            Cancel(token);
            if (!MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
                throw std::runtime_error("Cannot commit block cache file.");
        }
    }

    UnityBlockCache::UnityBlockCache(std::filesystem::path directory, std::size_t memoryBudget, std::size_t diskBudget)
        : directory_(std::move(directory)), memoryBudget_(memoryBudget), diskBudget_(diskBudget)
    {
        if (!directory_.empty())
            try
            {
                PruneDisk({});
            }
            catch (const std::exception &)
            {
            }
    }

    UnityBlockCache::Statistics UnityBlockCache::Stats() const
    {
        std::lock_guard lock(mutex_);
        return stats_;
    }

    void UnityBlockCache::ReleaseMemory()
    {
        std::lock_guard lock(mutex_);
        blocks_.clear();
        recent_.clear();
        stats_.ResidentBytes = 0;
    }

    std::shared_ptr<const UnityBlockCache::Bytes> UnityBlockCache::Get(std::span<const std::uint8_t> compressed,
                                                                       std::size_t outputSize, unsigned compression,
                                                                       std::stop_token token)
    {
        Cancel(token);
        if (outputSize > 1024ULL * 1024 * 1024 || compression > 3)
            throw std::runtime_error("Invalid cached Unity block.");
        // 内容身份包含压缩算法及输出长度；源文件修改但大小和时间未变，也不会复用旧字节。
        std::array<std::uint8_t, 12> format{};
        std::uint64_t size = outputSize;
        std::memcpy(format.data(), &size, sizeof(size));
        std::memcpy(format.data() + 8, &compression, sizeof(compression));
        Hasher hash;
        hash.Add(format, token);
        hash.Add(compressed, token);
        auto digest = hash.Finish();
        auto key = Hex(digest);
        std::lock_guard lock(mutex_);
        Cancel(token);
        if (auto found = blocks_.find(key); found != blocks_.end())
        {
            recent_.remove(key);
            recent_.push_front(key);
            ++stats_.MemoryHits;
            return found->second;
        }
        std::shared_ptr<const Bytes> result;
        auto path = directory_ / (key + ".block");
        if (!directory_.empty() && HeaderSize + outputSize <= diskBudget_)
        {
            try
            {
                result = Read(path, digest, outputSize, token);
                if (result)
                {
                    ++stats_.DiskHits;
                    std::error_code error;
                    std::filesystem::last_write_time(path, std::filesystem::file_time_type::clock::now(), error);
                }
            }
            catch (const std::exception &)
            {
                Cancel(token);
            }
        }
        if (!result)
        {
            Cancel(token);
            result = std::make_shared<const Bytes>(DecompressUnityBlock(compressed, outputSize, compression));
            Cancel(token);
            ++stats_.Decodes;
            if (!directory_.empty() && HeaderSize + outputSize <= diskBudget_)
                try
                {
                    Write(path, digest, *result, token);
                    PruneDisk(path);
                }
                catch (const std::exception &)
                {
                    // 缓存不可写或磁盘不足时，仍使用已经成功解压的游戏资源。
                    Cancel(token);
                }
        }
        Cancel(token);
        if (memoryBudget_ && result->size() <= memoryBudget_)
        {
            while (!recent_.empty() && (stats_.ResidentBytes > memoryBudget_ - result->size() || recent_.size() >= 64))
            {
                auto last = recent_.back();
                recent_.pop_back();
                stats_.ResidentBytes -= blocks_.at(last)->size();
                blocks_.erase(last);
            }
            blocks_.emplace(key, result);
            recent_.push_front(key);
            stats_.ResidentBytes += result->size();
        }
        return result;
    }

    void UnityBlockCache::PruneDisk(const std::filesystem::path &keep)
    {
        struct Entry
        {
            std::filesystem::path Path;
            std::filesystem::file_time_type Time;
            std::uintmax_t Size;
        };

        std::vector<Entry> entries;
        std::uintmax_t total = 0;
        for (auto &file : std::filesystem::directory_iterator(directory_))
        {
            auto name = file.path().filename().string();
            if (name.size() != 70 || !name.ends_with(".block") ||
                !std::all_of(name.begin(), name.begin() + 64,
                             [](char c)
                             {
                                 return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                             }) ||
                !file.is_regular_file())
                continue;
            auto size = file.file_size();
            total += size;
            entries.push_back({file.path(), file.last_write_time(), size});
        }
        std::sort(entries.begin(), entries.end(),
                  [](auto &a, auto &b)
                  {
                      return a.Time < b.Time;
                  });
        for (auto &entry : entries)
        {
            if (total <= diskBudget_)
                break;
            if (entry.Path == keep)
                continue;
            std::error_code error;
            if (std::filesystem::remove(entry.Path, error))
                total -= entry.Size;
        }
    }
}
