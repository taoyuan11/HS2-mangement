#include "Assets/AssetStore.h"
#include "Character/CharacterAssembly.h"
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

    void Align(Bytes &bytes)
    {
        bytes.resize((bytes.size() + 3) & ~size_t(3));
    }

    void FieldString(Bytes &bytes, std::string_view value)
    {
        Number(bytes, value.size(), 4, false);
        bytes.insert(bytes.end(), value.begin(), value.end());
        Align(bytes);
    }

    void StringSchema(std::vector<UnityTypeNode> &nodes, std::string name, uint8_t level)
    {
        nodes.push_back({"string", std::move(name), -1, 0, level});
        nodes.push_back({"Array", "Array", -1, 0, static_cast<uint8_t>(level + 1)});
        nodes.push_back({"int", "size", 4, 0, static_cast<uint8_t>(level + 2)});
        nodes.push_back({"char", "data", 1, 0, static_cast<uint8_t>(level + 2)});
    }

    struct Object
    {
        int64_t Id;
        int Type;
        Bytes Data;
    };

    Bytes ObjectFile(const std::vector<std::pair<int, std::vector<UnityTypeNode>>> &types,
                     const std::vector<Object> &objects)
    {
        Bytes metadata;
        String(metadata, "2018.4.11f1");
        Number(metadata, 0, 4, false);
        Number(metadata, 1, 1, false);
        Number(metadata, types.size(), 4, false);
        for (auto &[id, nodes] : types)
        {
            Number(metadata, id, 4, false);
            Number(metadata, 0, 1, false);
            Number(metadata, 0xffff, 2, false);
            metadata.resize(metadata.size() + (id == 114 ? 32 : 16));
            Bytes tree, strings;
            for (auto &node : nodes)
            {
                Number(tree, 1, 2, false);
                Number(tree, node.Level, 1, false);
                Number(tree, 0, 1, false);
                Number(tree, strings.size(), 4, false);
                String(strings, node.Type);
                Number(tree, strings.size(), 4, false);
                String(strings, node.Name);
                Number(tree, node.Size, 4, false);
                Number(tree, 0, 4, false);
                Number(tree, node.Flags, 4, false);
            }
            Number(metadata, nodes.size(), 4, false);
            Number(metadata, strings.size(), 4, false);
            metadata.insert(metadata.end(), tree.begin(), tree.end());
            metadata.insert(metadata.end(), strings.begin(), strings.end());
        }
        Number(metadata, objects.size(), 4, false);
        Bytes payload;
        for (auto &object : objects)
        {
            Align(metadata);
            Align(payload);
            Number(metadata, object.Id, 8, false);
            Number(metadata, payload.size(), 4, false);
            Number(metadata, object.Data.size(), 4, false);
            Number(metadata, object.Type, 4, false);
            payload.insert(payload.end(), object.Data.begin(), object.Data.end());
        }
        Number(metadata, 0, 4, false);
        Number(metadata, 0, 4, false);
        String(metadata, "");
        Align(metadata);
        Bytes file;
        Number(file, metadata.size(), 4);
        Number(file, 20 + metadata.size() + payload.size(), 4);
        Number(file, 17, 4);
        Number(file, 20 + metadata.size(), 4);
        Number(file, 0, 4);
        file.insert(file.end(), metadata.begin(), metadata.end());
        file.insert(file.end(), payload.begin(), payload.end());
        return file;
    }

    void StructuredValues()
    {
        std::vector<UnityTypeNode> schema{{"Test", "Base", -1, 0, 0},   {"vector", "values", -1, 0, 1},
                                          {"Array", "Array", -1, 0, 2}, {"int", "size", 4, 0, 3},
                                          {"Pair", "data", -1, 0, 3},   {"int", "number", 4, 0, 4}};
        StringSchema(schema, "label", 4);
        schema.push_back({"int", "tail", 4, 0, 1});
        Bytes data;
        Number(data, 2, 4, false);
        Number(data, 17, 4, false);
        FieldString(data, "first");
        Number(data, 29, 4, false);
        FieldString(data, "second");
        Number(data, 71, 4, false);
        UnitySerializedFile file(ObjectFile({{114, schema}}, {{7, 0, data}}));
        for (auto value : {file.Read(7), file.ReadScript(7, schema)})
        {
            auto rows = value.Get("values").Array();
            Check(rows.size() == 2 && rows[0].Int("number") == 17 && rows[1].String("label") == "second" &&
                      value.Int("tail") == 71,
                  "Compiled type layout changed nested arrays or following fields.");
        }
        auto shortSchema = schema;
        shortSchema.pop_back();
        Reject(
            [&]
            {
                file.ReadScript(7, shortSchema);
            });
        data.pop_back();
        UnitySerializedFile truncated(ObjectFile({{114, schema}}, {{7, 0, data}}));
        Reject(
            [&]
            {
                truncated.Read(7);
            });
        Bytes empty;
        Number(empty, 0, 4, false);
        Number(empty, 91, 4, false);
        UnitySerializedFile zero(ObjectFile({{114, schema}}, {{7, 0, empty}}));
        Check(zero.Read(7).Get("values").Array().empty() && zero.Read(7).Int("tail") == 91,
              "Empty array consumed the next field.");
    }

    void Write(const std::filesystem::path &path, const Bytes &bytes)
    {
        std::ofstream file(path, std::ios::binary);
        file.write(reinterpret_cast<const char *>(bytes.data()), bytes.size());
        Check(bool(file), "Fixture write failed.");
    }

    void ExportAliases(const std::filesystem::path &root)
    {
        std::vector<UnityTypeNode> texture{{"Texture2D", "Base", -1, 0, 0}};
        StringSchema(texture, "m_Name", 1);
        texture.push_back({"int", "m_Width", 4, 0, 1});
        texture.push_back({"int", "m_Height", 4, 0, 1});
        std::vector<UnityTypeNode> container{{"AssetBundle", "Base", -1, 0, 0},
                                             {"map", "m_Container", -1, 0, 1},
                                             {"Array", "Array", -1, 0, 2},
                                             {"int", "size", 4, 0, 3},
                                             {"pair", "data", -1, 0, 3}};
        StringSchema(container, "first", 4);
        container.insert(container.end(), {{"AssetInfo", "second", -1, 0, 4},
                                           {"PPtr<Object>", "asset", -1, 0, 5},
                                           {"int", "m_FileID", 4, 0, 6},
                                           {"SInt64", "m_PathID", 8, 0, 6}});
        Bytes exports;
        Number(exports, 3, 4, false);
        for (int i = 0; i < 3; ++i)
        {
            FieldString(exports, i == 2 ? "external.png" : "Assets/Shared.PNG");
            Number(exports, i == 2 ? 1 : 0, 4, false);
            Number(exports, i == 0 ? 11 : 12, 8, false);
        }
        std::vector<Object> objects{{1, 0, exports}};
        for (int i = 0; i < 3; ++i)
        {
            Bytes bytes;
            FieldString(bytes, i == 2 ? "shared" : "original" + std::to_string(i));
            Number(bytes, 16u << i, 4, false);
            Number(bytes, 16u << i, 4, false);
            objects.push_back({11 + i, 1, std::move(bytes)});
        }
        auto path = root / "aliases.assets";
        Write(path, ObjectFile({{142, container}, {28, texture}}, objects));
        NativeGpuAssetReader reader(path);
        for (int pass = 0; pass < 2; ++pass)
        {
            for (auto name : {"assets/shared.png", "SHARED.PNG", "shared"})
                Check(reader.Find(name, 28).PathId == 11, "Export alias lost precedence or case folding.");
            Check(reader.Find("shared", 28, 32).PathId == 12 && reader.Find("shared", 28, 64).PathId == 13,
                  "Texture size filtering lost duplicate exports or object-name fallback.");
            Check(reader.Find("original0", 28).PathId == 11 && !reader.TryFind("external", 28) &&
                      !reader.TryFind("shared", 48) && !reader.TryFind("missing", 28),
                  "Name index changed class filtering, external exports, or missing-name behavior.");
        }

        // 组件转换保留数值、字节与层级；仅将对象引用转换为名称，不修改缓存的原始组件。
        Parameter field;
        field.Name = "value";
        field.Encoding = "uint";
        field.Text = "saved";
        field.Number = 3.5;
        field.Integer = -17;
        field.Unsigned = UINT64_MAX;
        field.Bytes = {1, 2, 3};
        Parameter reference;
        reference.Name = "reference";
        Parameter fileId, pathId;
        fileId.Name = "m_FileID";
        pathId.Name = "m_PathID";
        pathId.Encoding = "int";
        pathId.Integer = 11;
        reference.Children = {fileId, pathId};
        Parameter nested;
        nested.Name = "nested";
        nested.Children = {field, reference};
        GpuPrefabData prefab;
        prefab.Nodes.emplace_back();
        prefab.Nodes.back().Id = 1;
        prefab.Nodes.back().Name = "node";
        prefab.Components.push_back({1, 2, 137, nested});
        std::vector<std::string> diagnostics;
        auto converted = ReadCharacterComponents(reader, prefab, diagnostics);
        Check(diagnostics.empty() && converted.size() == 1, "Component conversion failed.");
        auto &value = converted[0].Data.Get("value");
        Check(value.Encoding == field.Encoding && value.Text == field.Text && value.Number == field.Number &&
                  value.Integer == field.Integer && value.Unsigned == field.Unsigned && value.Bytes == field.Bytes,
              "Component conversion changed a scalar field.");
        Check(converted[0].Data.Get("reference").Text == "original0" &&
                  converted[0].Data.Get("reference").Children.empty() &&
                  prefab.Components[0].Data.Get("reference").Children.size() == 2,
              "Component conversion changed object references or its immutable source.");
    }

    void TransientMesh(const std::filesystem::path &root)
    {
        std::vector<UnityTypeNode> schema{{"Mesh", "Base", -1, 0, 0}};
        StringSchema(schema, "m_Name", 1);
        schema.push_back({"int", "m_MeshCompression", 4, 0, 1});
        schema.push_back({"TypelessData", "unused", -1, 0, 1});
        Bytes payload;
        FieldString(payload, "empty mesh");
        Number(payload, 0, 4, false);
        Number(payload, 1024 * 1024, 4, false);
        payload.resize(payload.size() + 1024 * 1024, 7);
        auto path = root / "mesh.assets";
        Write(path, ObjectFile({{43, schema}}, {{7, 0, payload}}));
        NativeGpuAssetReader reader(path);
        auto bytes = reader.ResidentBytes();
        Parameter reference, fileId, pathId;
        reference.Name = "m_Mesh";
        fileId.Name = "m_FileID";
        pathId.Name = "m_PathID";
        pathId.Encoding = "int";
        pathId.Integer = 7;
        reference.Children = {fileId, pathId};
        GpuPrefabData prefab;
        prefab.Nodes.emplace_back();
        prefab.Nodes.back().Id = 1;
        prefab.Nodes.back().Name = "node";
        prefab.Components.push_back({1, 2, 137, reference});
        std::vector<std::string> diagnostics;
        auto components = ReadCharacterComponents(reader, prefab, diagnostics);
        Check(diagnostics.empty() && components.size() == 1 && components[0].Data.Text == "empty mesh" &&
                  reader.ResidentBytes() == bytes,
              "Mesh reference expanded its payload instead of reading the name prefix.");
        Check(reader.Mesh(7).Name == "empty mesh" && reader.ResidentBytes() == bytes,
              "Mesh conversion retained its temporary metadata tree.");
        const auto &borrowed = reader.Metadata(7);
        bytes = reader.ResidentBytes();
        auto mesh = reader.Mesh(7);
        Check(mesh.Name == "empty mesh" && mesh.VertexCount == 0 && reader.ResidentBytes() == bytes &&
                  borrowed.Get("unused").Bytes.size() == 1024 * 1024 && borrowed.Get("unused").Bytes.back() == 7,
              "Mesh conversion invalidated previously borrowed metadata.");
        NativeGpuAssetStore store({}, {}, {});
        Check(store.Mesh({path, 0, 7}) == store.Mesh({path, 0, 7}), "Converted mesh was not reused.");
    }

    void ParallelShaderNames(const std::filesystem::path &root)
    {
        std::vector<UnityTypeNode> schema{{"Shader", "Base", -1, 0, 0}};
        StringSchema(schema, "m_Name", 1);
        Bytes payload;
        FieldString(payload, "CaseSensitive/Shader");
        auto file = ObjectFile({{48, schema}}, {{17, 0, payload}});
        std::vector<std::filesystem::path> paths;
        for (int i = 0; i < 70; ++i)
        {
            paths.push_back(root / ("shader-" + std::to_string(i) + ".assets"));
            Write(paths.back(), file);
        }
        NativeGpuAssetStore store(paths, {}, {});
        auto borrowed = store.Reader(paths[0]);
        auto expected = std::vector<std::string>(paths.size(), "CaseSensitive/Shader");
        Check(store.ShaderNames() == expected && store.ReaderStats().Loads == paths.size() &&
                  store.ReaderStats().Evictions > 0,
              "Parallel shader scan lost names, reopened a resident reader, or ignored the reader budget.");
        auto loads = store.ReaderStats().Loads;
        Check(store.ShaderNames() == expected && store.ReaderStats().Loads == loads &&
                  store.FindShader(paths[0], "casesensitive/shader")->PathId == 17 &&
                  borrowed->AssetName(17) == expected.front(),
              "Shader scan did not retain case, lookup aliases, or borrowed readers after eviction.");
        auto missing = root / "missing-shader.assets";
        store.AddPath(missing);
        Reject(
            [&]
            {
                store.ShaderNames();
            });
        Write(missing, file);
        expected.push_back(expected.front());
        Check(store.ShaderNames() == expected && store.ReaderStats().Loads == loads + 1,
              "Failed incremental shader scan poisoned its cache or reopened old resources.");
        // 同批次中前一个成功、后一个失败时，重试也不能重复提交名称。
        auto missingBatch = root / "missing-batch.assets";
        NativeGpuAssetStore retry({paths[0], missingBatch, paths[1]}, {}, {});
        Reject(
            [&]
            {
                retry.ShaderNames();
            });
        Write(missingBatch, file);
        Check(retry.ShaderNames() == std::vector<std::string>(3, expected.front()),
              "Failed shader batch changed name order or duplicated entries on retry.");
        std::stop_source stop;
        NativeGpuAssetStore cancelled(paths, {}, {}, stop.get_token());
        cancelled.ShaderNames();
        stop.request_stop();
        Reject(
            [&]
            {
                cancelled.ShaderNames();
            });
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

        ExportAliases(root);
        TransientMesh(root);
        ParallelShaderNames(root);

        for (bool atEnd : {false, true})
        {
            auto path = root / (atEnd ? "tail.bundle" : "head.bundle");
            Write(path, Bundle(atEnd));
            UnityBundle streamed(path);
            Check(streamed.ResidentBytes() == 0, "Streamed bundle retained an entire source file.");
            Check(Text(streamed.ReadEntry("CAB-test.resS", 1, 5)) == "ghijk", "Streamed cross-block range mismatch.");
            Check(Text(streamed.Entry(0)) == "abcde", "Streamed serialized entry mismatch.");
            Check(Text(streamed.ReadEntry("CAB-test.resS", 0, 7)) == "fghijkl", "Streamed backward range mismatch.");
            auto modified = std::filesystem::last_write_time(path);
            std::filesystem::last_write_time(path, modified + std::chrono::hours(1));
            Reject(
                [&]
                {
                    streamed.ReadEntry("CAB-test", 0, 4);
                });
            std::filesystem::last_write_time(path, modified);
            std::stop_source cancellation;
            UnityBundle cancelled(path, {}, cancellation.get_token());
            cancellation.request_stop();
            Reject(
                [&]
                {
                    cancelled.ReadEntry("CAB-test", 0, 4);
                });
        }

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

        // 未读取的依赖跨多批并行建立索引；路径去重、顺序、淘汰和增量添加保持原语义。
        NativeGpuAssetStore parallel(paths, {}, {});
        parallel.AddPath(root / "." / "target");
        Check(parallel.Paths().size() == paths.size(), "Equivalent dependency paths were not deduplicated.");
        auto indexed = parallel.Resolve({origin, 1, 17});
        Check(indexed.Bundle == paths[0] && parallel.ReaderStats().Loads == 1 &&
                  parallel.ReaderStats().CabIndexLoads == paths.size(),
              "Parallel CAB index lost a dependency or loaded an unused payload.");
        Check(parallel.ReaderStats().Evictions == 0, "CAB indexing evicted a live reader.");
        auto later = root / "later", laterOrigin = root / "later-origin";
        Write(later, SerializedFile());
        Write(laterOrigin, SerializedFile("later"));
        parallel.AddPath(later);
        Check(parallel.Resolve({laterOrigin, 1, 19}).Bundle == later && parallel.ReaderStats().Loads == 2 &&
                  parallel.ReaderStats().CabIndexLoads == paths.size() + 1,
              "Incremental CAB indexing reread old dependencies.");

        // 目录可读但对象数据无效的包仍可索引；只有实际请求对象时才解析并拒绝无效数据。
        auto lazyBundle = root / "lazy.bundle", lazyOrigin = root / "lazy-origin";
        Write(lazyBundle, Bundle(false, true));
        Write(lazyOrigin, SerializedFile("CAB-test"));
        NativeGpuAssetStore lazy({lazyBundle}, {}, {});
        Check(lazy.Resolve({lazyOrigin, 1, 23}).Bundle == lazyBundle && lazy.ReaderStats().Loads == 1,
              "CAB lookup decompressed an unrequested payload.");
        Reject(
            [&]
            {
                lazy.Reader(lazyBundle);
            });

        auto broken = root / "broken";
        NativeGpuAssetStore retry({paths[0], broken, paths[1]}, {}, {});
        Reject(
            [&]
            {
                retry.Resolve({origin, 1, 17});
            });
        // 失败批次已提交的索引不能重复提交，未完成的读取器必须能安全重试。
        Write(broken, SerializedFile());
        Check(retry.Resolve({origin, 1, 17}).Bundle == paths[0], "Failed CAB batch poisoned a later retry.");
        auto duplicate = root / "duplicate" / "target";
        std::filesystem::create_directory(duplicate.parent_path());
        Write(duplicate, SerializedFile());
        NativeGpuAssetStore ambiguous({paths[0], duplicate}, {}, {});
        Reject(
            [&]
            {
                ambiguous.Resolve({origin, 1, 17});
            });

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
        StructuredValues();
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
