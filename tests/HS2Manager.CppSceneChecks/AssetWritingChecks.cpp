#include "Assets/AssetWriter.h"
#include <iostream>
#include <stdexcept>

using namespace hs2;

namespace
{
    void Check(bool ok, const char *message)
    {
        if (!ok)
            throw std::runtime_error(message);
    }

    template <class F> void Throws(F f)
    {
        try
        {
            f();
        }
        catch (const std::exception &)
        {
            return;
        }
        throw std::runtime_error("Expected invalid output rejection");
    }
}

int main()
{
    try
    {
        UnitySerializedType type;
        type.ClassId = 49;
        type.TypeHash[0] = 19;
        type.Nodes = {{"TextAsset", "Base", -1, 0, 0},
                      {"string", "m_Name", -1, 0, 1},
                      {"TypelessData", "m_Script", -1, 0x4000, 1},
                      {"SInt64", "wide", 8, 0, 1},
                      {"PPtr<Object>", "link", -1, 0, 1},
                      {"int", "m_FileID", 4, 0, 2},
                      {"SInt64", "m_PathID", 8, 0, 2}};
        auto value = DefaultUnityValue(type.Nodes);
        value.Children[0].Text = "测试-name";
        value.Children[1].Bytes = {1, 2, 3};
        value.Children[2].Integer = 0x123456789abcdef;
        VisitUnityPointers(value,
                           [](auto &file, auto &path)
                           {
                               file = 1;
                               path = 0x123456789abcde;
                           });
        for (bool big : {false, true})
        {
            UnitySerializedDocument document;
            document.BigEndian = big;
            document.UnityVersion = "2018.4.11f1";
            document.Types = {type};
            document.Objects = {{-0x123456789abcdef, 0, WriteUnityValue(type.Nodes, value, big)}};
            document.Externals = {{"", "archive:/CAB-test/CAB-test", {}, 0}};
            document.ScriptReferences = {{0, -0x123456789abcdef}};
            auto bytes = WriteUnitySerializedFile(document);
            UnitySerializedFile reopened(bytes);
            auto roundtrip = reopened.Read(document.Objects[0].PathId);
            Check(roundtrip.String("m_Name") == value.String("m_Name"), "UTF-8 name changed");
            Check(roundtrip.Int("wide") == value.Children[2].Integer, "64-bit integer lost precision");
            Check(roundtrip.Get("link").Int("m_PathID") == 0x123456789abcde, "64-bit pointer changed");
            auto snapshot = reopened.Document();
            Check(snapshot.ScriptReferences == document.ScriptReferences &&
                      snapshot.Types[0].TypeHash == type.TypeHash &&
                      snapshot.Externals[0].Path == document.Externals[0].Path,
                  "Metadata was discarded");
            Check(WriteUnitySerializedFile(snapshot) == bytes, "Document serialization is not deterministic");
            std::vector<UnityBundleOutput> entries{{"CAB-test", bytes, 4},
                                                   {"CAB-test.resS", std::vector<std::uint8_t>(300000, 42), 0}};
            auto bundleBytes = WriteUnityBundle(document.UnityVersion, entries);
            UnityBundle bundle(bundleBytes);
            Check(bundle.Entries().size() == 2, "Bundle directory changed");
            Check(bundle.ReadEntry("CAB-test.resS", 120000, 150000) == std::vector<std::uint8_t>(150000, 42),
                  "Cross-block resource changed");
            Check(std::vector<std::uint8_t>(bundle.Entry(0).begin(), bundle.Entry(0).end()) == bytes,
                  "Serialized bundle payload changed");
            auto broken = bundleBytes;
            broken.resize(broken.size() / 2);
            Throws(
                [&]
                {
                    UnityBundle invalid(broken);
                    invalid.Entry(0);
                });
            document.Objects.push_back(document.Objects[0]);
            Throws(
                [&]
                {
                    WriteUnitySerializedFile(document);
                });
        }
        std::stop_source cancellation;
        cancellation.request_stop();
        std::vector<UnityBundleOutput> entries{{"CAB-test", {1, 2, 3}, 4}};
        Throws(
            [&]
            {
                WriteUnityBundle("2018.4.11f1", entries, cancellation.get_token());
            });
        std::cout << "PASS native serialized writing, metadata, pointers, LZ4 blocks, cancellation\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
