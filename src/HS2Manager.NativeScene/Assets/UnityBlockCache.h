#pragma once
#include <cstdint>
#include <filesystem>
#include <list>
#include <memory>
#include <mutex>
#include <span>
#include <stop_token>
#include <string>
#include <unordered_map>
#include <vector>

namespace hs2
{
    // 只共享不可变的解压字节，不共享人物参数、资源读取器或脚本解析回调。
    class UnityBlockCache
    {
    public:
        using Bytes = std::vector<std::uint8_t>;

        struct Statistics
        {
            std::size_t MemoryHits = 0, DiskHits = 0, Decodes = 0, ResidentBytes = 0;
        };

        explicit UnityBlockCache(std::filesystem::path directory = {}, std::size_t memoryBudget = 256ULL * 1024 * 1024,
                                 std::size_t diskBudget = 1024ULL * 1024 * 1024);
        std::shared_ptr<const Bytes> Get(std::span<const std::uint8_t> compressed, std::size_t outputSize,
                                         unsigned compression, std::stop_token cancellation = {});
        Statistics Stats() const;

    private:
        std::filesystem::path directory_;
        std::size_t memoryBudget_, diskBudget_;
        mutable std::mutex mutex_;
        Statistics stats_;
        std::unordered_map<std::string, std::shared_ptr<const Bytes>> blocks_;
        std::list<std::string> recent_;
        void PruneDisk(const std::filesystem::path &keep);
    };
}
