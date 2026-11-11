#include "Assets/AssetStore.h"
#include <chrono>
#include <fstream>
#include <iostream>
#include <limits>

using namespace hs2;

namespace
{
    using Bytes = std::vector<uint8_t>;

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
        throw std::runtime_error("Invalid resource was accepted.");
    }

    void Number(Bytes &bytes, uint64_t value, size_t count, bool big = true)
    {
        for (size_t i = 0; i < count; ++i)
            bytes.push_back(static_cast<uint8_t>(value >> (8 * (big ? count - i - 1 : i))));
    }

    void String(Bytes &bytes, std::string_view value)
    {
        bytes.insert(bytes.end(), value.begin(), value.end());
        bytes.push_back(0);
    }

    Bytes Bundle(bool atEnd, bool corrupt = false, bool singleBlock = false)
    {
        // 两个目录项共用三个块，中间块使用 LZ4，覆盖跨块及跨目录项的区间读取。
        Bytes directory(16);
        Number(directory, singleBlock ? 1 : 3, 4);
        for (int i = 0; i < (singleBlock ? 1 : 3); ++i)
        {
            Number(directory, singleBlock ? 12 : 4, 4);
            Number(directory, singleBlock ? 12 : i == 1 ? 5 : 4, 4);
            Number(directory, i == 1 ? 2 : 0, 2);
        }
        Number(directory, 2, 4);
        Number(directory, 0, 8);
        Number(directory, 5, 8);
        Number(directory, 0, 4);
        String(directory, "CAB-test");
        Number(directory, 5, 8);
        Number(directory, 7, 8);
        Number(directory, 0, 4);
        String(directory, "CAB-test.resS");
        Bytes result;
        String(result, "UnityFS");
        Number(result, 6, 4);
        String(result, "5.x.x");
        String(result, "2018.4.11f1");
        auto totalOffset = result.size();
        Number(result, 0, 8);
        Number(result, directory.size(), 4);
        Number(result, directory.size(), 4);
        Number(result, atEnd ? 0x80 : 0, 4);
        Bytes payload{'a', 'b', 'c', 'd', static_cast<uint8_t>(corrupt ? 0xf0 : 0x40), 'e', 'f', 'g', 'h',
                      'i', 'j', 'k', 'l'};
        if (singleBlock)
            payload.erase(payload.begin() + 4);
        auto &first = atEnd ? payload : directory;
        auto &second = atEnd ? directory : payload;
        result.insert(result.end(), first.begin(), first.end());
        result.insert(result.end(), second.begin(), second.end());
        for (size_t i = 0; i < 8; ++i)
            result[totalOffset + i] = static_cast<uint8_t>(result.size() >> (8 * (7 - i)));
        return result;
    }

    std::string Text(std::span<const uint8_t> bytes)
    {
        return {bytes.begin(), bytes.end()};
    }

    void BundleRanges()
    {
        for (bool atEnd : {false, true})
        {
            UnityBundle single(Bundle(atEnd, false, true));
            Check(Text(single.Entry(0)) == "abcde", "Single block serialized entry mismatch.");
            Check(Text(single.ReadEntry("CAB-test.resS", 0, 7)) == "fghijkl", "Single block reuse failed.");
            Check(Text(single.ReadEntry("CAB-test", 1, 3)) == "bcd", "Single block backward range failed.");
            UnityBundle bundle(Bundle(atEnd));
            Check(Text(bundle.ReadEntry("CAB-test.resS", 1, 5)) == "ghijk", "Cross-block range mismatch.");
            auto first = bundle.Entry(0);
            Check(Text(first) == "abcde", "Serialized entry mismatch.");
            Check(Text(bundle.Entry("CAB-test.resS")) == "fghijkl", "Stream entry mismatch.");
            Check(Text(first) == "abcde", "Entry span invalidated by another read.");
            Check(bundle.ReadEntry("CAB-test.resS", 7, 0).empty(), "Empty end range failed.");
            Reject(
                [&]
                {
                    bundle.ReadEntry("CAB-test.resS", 7, 1);
                });
            Reject(
                [&]
                {
                    bundle.ReadEntry("CAB-test.resS", UINT64_MAX, 1);
                });
            Reject(
                [&]
                {
                    bundle.ReadEntry("missing", 0, 0);
                });
            auto truncated = Bundle(atEnd);
            truncated.pop_back();
            Reject(
                [&]
                {
                    UnityBundle invalid(truncated);
                });
            UnityBundle corrupt(Bundle(atEnd, true));
            Check(Text(corrupt.ReadEntry("CAB-test", 0, 4)) == "abcd", "Unrequested block was decoded.");
            Reject(
                [&]
                {
                    corrupt.Entry(0);
                });
            Reject(
                [&]
                {
                    corrupt.Entry(0);
                });
            Check(Text(corrupt.ReadEntry("CAB-test.resS", 3, 4)) == "ijkl", "Failed block poisoned other reads.");
        }
    }

    Bytes SerializedFile(std::string_view external = {})
    {
        Bytes metadata;
        String(metadata, "2018.4.11f1");
        Number(metadata, 0, 4, false);
        Number(metadata, 1, 1, false);
        for (int i = 0; i < 3; ++i)
            Number(metadata, 0, 4, false);
        Number(metadata, external.empty() ? 0 : 1, 4, false);
        if (!external.empty())
        {
            String(metadata, "");
            metadata.resize(metadata.size() + 20);
            String(metadata, external);
        }
        String(metadata, "");
        Bytes file;
        Number(file, metadata.size(), 4);
        Number(file, 20 + metadata.size(), 4);
        Number(file, 17, 4);
        Number(file, 20 + metadata.size(), 4);
        Number(file, 0, 4);
        file.insert(file.end(), metadata.begin(), metadata.end());
        return file;
    }

    void Write(const std::filesystem::path &path, const Bytes &bytes)
    {
        std::ofstream file(path, std::ios::binary);
        file.write(reinterpret_cast<const char *>(bytes.data()), bytes.size());
        Check(bool(file), "Fixture write failed.");
    }

    void ReaderCacheAndStreams()
    {
        auto root =
            std::filesystem::temp_directory_path() /
            ("hs2-asset-loading-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
        std::filesystem::create_directory(root);

        struct Cleanup
        {
            std::filesystem::path Root;

            ~Cleanup()
            {
                std::error_code error;
                std::filesystem::remove_all(Root, error);
            }
        } cleanup{root};

        NativeGpuAssetStore store({}, {}, {});
        std::vector<std::filesystem::path> paths;
        for (int i = 0; i < 66; ++i)
        {
            paths.push_back(root / (i == 0 ? "target" : std::to_string(i) + ".assets"));
            Write(paths.back(), SerializedFile());
        }
        for (int pass = 0; pass < 3; ++pass)
            for (int i = 0; i < 8; ++i)
                store.Reader(paths[i]);
        Check(store.ReaderStats().Loads == 8 && store.ReaderStats().Hits == 16,
              "Small resource set was repeatedly reopened.");
        auto borrowed = store.Reader(paths[0]);
        store.AddPath(paths[0]);
        Check(!store.FindShader(paths[0], "missing"), "Empty shader index returned an asset.");
        for (int i = 8; i < 66; ++i)
            store.Reader(paths[i]);
        store.Reader(paths[65]);
        Check(store.ReaderStats().Evictions >= 2, "Reader count limit was not enforced.");
        auto loads = store.ReaderStats().Loads;
        Check(!store.FindShader(paths[0], "another missing") && store.ReaderStats().Loads == loads,
              "Shader name lookup reopened an evicted resource.");
        auto origin = root / "origin";
        Write(origin, SerializedFile("target"));
        auto resolved = store.Resolve({origin, 1, 17});
        Check(resolved.Bundle == paths[0] && resolved.FileId == 0 && resolved.PathId == 17 &&
                  store.ReaderStats().Loads == loads + 1,
              "CAB identity lookup reopened the dependency payload.");
        Check(borrowed->UnityVersion() == "2018.4.11f1", "Eviction invalidated a borrowed reader.");
        Write(root / "stream.resS", {'0', '1', '2', '3', '4', '5', '6', '7'});
        Check(Text(borrowed->ResourceStream("archive:/stream.resS", 2, 3)) == "234", "External range mismatch.");
        Check(borrowed->ResourceStream("stream.resS", 8, 0).empty(), "Empty external range failed.");
        Reject(
            [&]
            {
                borrowed->ResourceStream("stream.resS", 7, 2);
            });
        Reject(
            [&]
            {
                borrowed->ResourceStream("stream.resS", UINT64_MAX, 1);
            });
        Reject(
            [&]
            {
                borrowed->ResourceStream("stream.resS", 0, 513ULL * 1024 * 1024);
            });
        Reject(
            [&]
            {
                borrowed->ResourceStream("..", 0, 0);
            });
        std::stop_source cancellation;
        NativeGpuAssetStore cancelled({}, {}, {}, cancellation.get_token());
        cancelled.Reader(paths[0]);
        cancelled.FindShader(paths[0], "missing");
        cancellation.request_stop();
        Reject(
            [&]
            {
                cancelled.Reader(paths[0]);
            });
        Reject(
            [&]
            {
                cancelled.FindShader(paths[0], "missing");
            });
        // 新加载建立独立读取器，不能沿用上一次准备的已删除或损坏资源。
        Write(paths[0], {1, 2, 3});
        NativeGpuAssetStore fresh({}, {}, {});
        Reject(
            [&]
            {
                fresh.Reader(paths[0]);
            });
    }

    void RealShaderNames(const std::filesystem::path &bundle, const std::filesystem::path &tpk)
    {
        UnityClassDatabase database(tpk);
        NativeGpuAssetReader reader(bundle, &database);
        auto names = reader.ShaderNames();
        Check(!names.empty(), "Real shader fixture has no shaders.");
        for (auto &[name, ids] : names)
        {
            auto found = reader.TryFind(name, 48);
            Check(found && found->PathId == ids.front(), "Shader name index changed alias precedence.");
        }
        std::cout << "Real shader names and aliases checked: " << names.size() << '\n';
    }
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        BundleRanges();
        ReaderCacheAndStreams();
        if (argc == 3)
            RealShaderNames(argv[1], argv[2]);
        std::cout << "Asset loading checks passed\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
