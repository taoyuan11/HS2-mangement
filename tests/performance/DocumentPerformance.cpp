#include "Common/Core.h"
#include <windows.h>
#include <psapi.h>
#include <chrono>
#include <iostream>

using namespace hs2;

namespace
{
    std::size_t PrivateBytes()
    {
        PROCESS_MEMORY_COUNTERS_EX counters{};
        if (!GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&counters),
                                  sizeof(counters)))
            throw std::runtime_error("Cannot read process memory.");
        return counters.PrivateUsage;
    }
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc != 3)
            throw std::runtime_error("Usage: hs2_document_performance <card.png> <report.json>");
        auto start = std::chrono::steady_clock::now();
        auto document = CharacterDocument::Open(argv[1]);
        auto opened = std::chrono::steady_clock::now();
        auto before = PrivateBytes();
        // 保留独立修订，覆盖撤销历史的实际存活方式，不把序列化计入编辑耗时。
        std::vector<CharacterDocument> history{document};
        history.reserve(101);
        for (int i = 0; i < 100; ++i)
            history.push_back(history.back().WithValue("face.shapeValueFace[0]", (i + 1) / 101.f));
        auto edited = std::chrono::steady_clock::now();
        auto after = PrivateBytes();
        auto encoded = history.back().ToBytes();
        auto restored = CharacterDocument::FromBytes(encoded, argv[1]);
        if (!document.IsOriginal() || restored.Node("face.shapeValueFace[0]").ScalarValue !=
                                          history.back().Node("face.shapeValueFace[0]").ScalarValue)
            throw std::runtime_error("Edit roundtrip changed the value or source.");
        auto milliseconds = [](auto duration)
        {
            return std::chrono::duration<double, std::milli>(duration).count();
        };
        Json report{{"OpenMs", milliseconds(opened - start)},
                    {"Edit100Ms", milliseconds(edited - opened)},
                    {"PrivateBeforeBytes", before},
                    {"PrivateAfterBytes", after},
                    {"HistoryGrowthBytes", after > before ? after - before : 0},
                    {"SourceSHA256", document.SourceHash()},
                    {"ResultSHA256", Sha256(encoded)}};
        auto text = report.dump(2);
        WriteAtomic(argv[2], std::span(reinterpret_cast<const std::uint8_t *>(text.data()), text.size()));
        std::cout << text << '\n';
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
