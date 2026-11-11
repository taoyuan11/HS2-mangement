#include "Assets/UnityBlockCache.h"
#include <chrono>
#include <fstream>
#include <future>
#include <iostream>
#include <stdexcept>

using namespace hs2;
namespace fs = std::filesystem;

namespace
{
    void Check(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }

    template <class F> void Reject(F action)
    {
        try
        {
            action();
        }
        catch (const std::exception &)
        {
            return;
        }
        throw std::runtime_error("Invalid or cancelled block was accepted.");
    }

    std::string Text(const std::shared_ptr<const UnityBlockCache::Bytes> &bytes)
    {
        return {bytes->begin(), bytes->end()};
    }

    std::vector<fs::path> Blocks(const fs::path &root)
    {
        std::vector<fs::path> result;
        if (fs::is_directory(root))
            for (auto &file : fs::directory_iterator(root))
                if (file.path().extension() == ".block")
                    result.push_back(file.path());
        return result;
    }

    const UnityBlockCache::Bytes Hello{0x50, 'h', 'e', 'l', 'l', 'o'};
    const UnityBlockCache::Bytes World{0x50, 'w', 'o', 'r', 'l', 'd'};

    void ReuseAndIntegrity(const fs::path &root)
    {
        UnityBlockCache cache(root, 5, 1024);
        auto first = cache.Get(Hello, 5, 2);
        Check(Text(first) == "hello" && cache.Get(Hello, 5, 2) == first, "Decoded block was not shared.");
        Check(cache.Stats().Decodes == 1 && cache.Stats().MemoryHits == 1 && cache.Stats().ResidentBytes == 5,
              "Memory cache counters differ.");
        UnityBlockCache disk(root, 0, 1024);
        Check(Text(disk.Get(Hello, 5, 2)) == "hello" && disk.Stats().DiskHits == 1 && disk.Stats().Decodes == 0,
              "Persisted block was not reused.");
        auto files = Blocks(root);
        Check(files.size() == 1, "Unexpected persisted block count.");
        {
            std::fstream corrupt(files.front(), std::ios::binary | std::ios::in | std::ios::out);
            corrupt.seekp(-1, std::ios::end);
            corrupt.put('x');
        }
        UnityBlockCache repair(root, 0, 1024);
        Check(Text(repair.Get(Hello, 5, 2)) == "hello" && repair.Stats().Decodes == 1,
              "Corrupt block payload was accepted.");
        fs::resize_file(files.front(), 12);
        UnityBlockCache truncated(root, 0, 1024);
        Check(Text(truncated.Get(Hello, 5, 2)) == "hello" && truncated.Stats().Decodes == 1,
              "Truncated block was accepted.");
        Check(Text(cache.Get(World, 5, 2)) == "world", "Changed input with the same size reused old bytes.");
        Check(cache.Stats().ResidentBytes == 5, "Memory budget exceeded after eviction.");
        Check(Text(first) == "hello", "Eviction invalidated a borrowed immutable block.");
        files = Blocks(root);
        Check(files.size() == 2, "Content identities collided.");
        // 将另一个合法缓存放到错误的内容身份下，头部身份必须阻止错误命中。
        fs::copy_file(files[0], files[1], fs::copy_options::overwrite_existing);
        UnityBlockCache swapped(root, 0, 1024);
        Check(Text(swapped.Get(Hello, 5, 2)) == "hello" && Text(swapped.Get(World, 5, 2)) == "world" &&
                  swapped.Stats().Decodes == 1,
              "Renamed valid cache data bypassed identity validation.");
    }

    void BudgetsAndFailures(const fs::path &root)
    {
        fs::create_directories(root);
        auto unrelated = root / "keep.txt";
        std::ofstream(unrelated) << "user file";
        UnityBlockCache bounded(root, 5, 85);
        bounded.Get(Hello, 5, 2);
        auto previous = Blocks(root).front();
        fs::last_write_time(previous, fs::file_time_type::clock::now() - std::chrono::hours(1));
        bounded.Get(World, 5, 2);
        Check(Blocks(root).size() == 1 && !fs::exists(previous) && fs::exists(unrelated),
              "Disk pruning exceeded its budget or removed an unrelated file.");
        UnityBlockCache noRoom(root / "small", 4, 84);
        Check(Text(noRoom.Get(Hello, 5, 2)) == "hello" && noRoom.Stats().ResidentBytes == 0 &&
                  Blocks(root / "small").empty(),
              "Oversized block was admitted to the cache.");
        UnityBlockCache unavailable(unrelated / "not-a-directory");
        Check(Text(unavailable.Get(Hello, 5, 2)) == "hello", "Cache write failure blocked resource loading.");
        std::stop_source stop;
        stop.request_stop();
        auto count = bounded.Stats().Decodes;
        Reject(
            [&]
            {
                bounded.Get(Hello, 5, 2, stop.get_token());
            });
        Reject(
            [&]
            {
                bounded.Get(World, 5, 2, stop.get_token());
            });
        Check(bounded.Stats().Decodes == count, "Cancelled request decoded a block.");
        UnityBlockCache invalid(root / "invalid");
        Reject(
            [&]
            {
                invalid.Get({Hello.data(), 2}, 5, 2);
            });
        Reject(
            [&]
            {
                invalid.Get(Hello, 5, 4);
            });
        Reject(
            [&]
            {
                invalid.Get(Hello, 1024ULL * 1024 * 1024 + 1, 2);
            });
        Check(Blocks(root / "invalid").empty() && invalid.Stats().ResidentBytes == 0,
              "Failed decode published a cache entry.");
        for (auto &file : fs::recursive_directory_iterator(root))
            Check(file.path().extension() != ".tmp", "Temporary cache file was left behind.");
    }

    void ConcurrentAccess(const fs::path &root)
    {
        UnityBlockCache shared(root / "shared");
        auto first = std::async(std::launch::async,
                                [&]
                                {
                                    return shared.Get(Hello, 5, 2);
                                });
        auto second = std::async(std::launch::async,
                                 [&]
                                 {
                                     return shared.Get(Hello, 5, 2);
                                 });
        Check(first.get() == second.get() && shared.Stats().Decodes == 1, "Concurrent reads duplicated decoding.");
        // 不同实例允许争用同一缓存文件；最终只允许出现完整且校验通过的版本。
        UnityBlockCache a(root / "writers"), b(root / "writers");
        auto writerA = std::async(std::launch::async,
                                  [&]
                                  {
                                      return a.Get(World, 5, 2);
                                  });
        auto writerB = std::async(std::launch::async,
                                  [&]
                                  {
                                      return b.Get(World, 5, 2);
                                  });
        Check(Text(writerA.get()) == "world" && Text(writerB.get()) == "world", "Concurrent writes lost data.");
        UnityBlockCache reader(root / "writers");
        Check(Text(reader.Get(World, 5, 2)) == "world" && reader.Stats().DiskHits == 1,
              "Concurrent write published an incomplete block.");
    }
}

int main()
{
    auto root = fs::temp_directory_path() /
                ("hs2-block-cache-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));

    struct Cleanup
    {
        fs::path Root;

        ~Cleanup()
        {
            std::error_code error;
            fs::remove_all(Root, error);
        }
    } cleanup{root};

    try
    {
        ReuseAndIntegrity(root / "reuse");
        BudgetsAndFailures(root / "limits");
        ConcurrentAccess(root / "concurrent");
        std::cout << "Unity block cache checks passed\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
