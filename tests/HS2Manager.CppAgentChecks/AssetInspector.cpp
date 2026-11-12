#include "Common/Core.h"
#include "Assets/AssetWriter.h"
#include "Assets/MonoSchema.h"
#include "Character/CharacterCatalog.h"
#include "AssetBundleBuild.h"
#include <iostream>
using namespace hs2;

namespace
{
    Json Value(const Parameter &p)
    {
        if (p.Encoding == "string" || p.Encoding == "float-special")
            return p.Text;
        if (p.Encoding == "bytes")
            return Json{{"bytes", p.Bytes.size()}};
        if (p.Encoding == "int")
            return p.Integer;
        if (p.Encoding == "uint")
            return p.Unsigned;
        if (p.Encoding == "float" || p.Encoding == "bool")
            return p.Number;
        if (p.Children.empty())
            return Json::array();
        bool array = p.Encoding == "array" || (p.Children.size() > 1 && p.Children[0].Name == p.Children[1].Name);
        Json result = array ? Json::array() : Json::object();
        for (std::size_t i = 0; i < p.Children.size(); ++i)
        {
            if (array)
            {
                if (i == 30)
                {
                    result.push_back({{"remaining", p.Children.size() - i}});
                    break;
                }
                result.push_back(Value(p.Children[i]));
            }
            else
                result[p.Children[i].Name] = Value(p.Children[i]);
        }
        return result;
    }
}

int main(int argc, char **argv)
{
    try
    {
        if (argc == 3 && std::string(argv[1]) == "--plugin")
        {
            auto p = PluginFileIndex::Read(Path(argv[2]));
            Json result = {{"assembly", p.AssemblyName},
                           {"version", p.Version},
                           {"plugins", Json::array()},
                           {"references", Json::array()}};
            for (const auto &r : p.References)
                result["references"].push_back({{"name", r.Name}, {"version", r.Version}});
            for (const auto &plugin : p.Plugins)
            {
                Json d = Json::array();
                for (const auto &dep : plugin.Dependencies)
                    d.push_back({{"guid", dep.Guid}, {"optional", dep.Optional}});
                result["plugins"].push_back({{"guid", plugin.Guid}, {"version", plugin.Version}, {"dependencies", d}});
            }
            std::cout << result.dump(2) << '\n';
            return 0;
        }
        if (argc == 8 && (std::string(argv[1]) == "--build" || std::string(argv[1]) == "--fingerprints"))
        {
            auto specBytes = ReadFile(Path(argv[6]));
            auto spec = Json::parse(specBytes);
            auto document = CharacterDocument::Open(Path(argv[5]));
            if (spec.at("kind") == "accessory")
                document = CharacterEditRules::SetValue(document, "accessory.parts[0].type", std::int64_t(351));
            CharacterCatalog catalog(Path(argv[2]), Path(argv[4]), Path(argv[3]));
            catalog.Load();
            auto source = asset::ResolveTemplate(catalog, document, spec, Path(argv[2]));
            asset::Geometry reference;
            if (spec.at("kind") != "accessory")
            {
                auto card = CardParser().Parse(Path(argv[5]));
                NativeCharacterService service(Path(argv[2]), Path(argv[4]), Path(argv[3]));
                reference = asset::CaptureReference(service.Prepare(card, &document, {2, false}));
                asset::WriteReference(reference, Path(argv[7]) / "Reference");
            }
            auto geometry = asset::BuildRecipe(spec.at("recipe"), reference);
            asset::AdaptGeometry(geometry, reference, spec);
            auto result = asset::BuildGameBundle(geometry, source, spec, Path(argv[2]), Path(argv[7]));
            if (std::string(argv[1]) == "--fingerprints")
            {
                auto baseline = result.at("hash");
                auto repeat = asset::BuildGameBundle(geometry, source, spec, Path(argv[2]), Path(argv[7]));
                if (repeat.at("hash") != baseline)
                    throw std::runtime_error("Unchanged build fingerprint is not deterministic");
                auto changedSpec = spec;
                changedSpec["name"] = spec.value("name", "fixture") + " changed";
                auto description = asset::BuildGameBundle(geometry, source, changedSpec, Path(argv[2]), Path(argv[7]));
                auto changedGeometry = geometry;
                changedGeometry.InputHashes.push_back(Sha256(Bytes{1, 2, 3}));
                auto input = asset::BuildGameBundle(changedGeometry, source, spec, Path(argv[2]), Path(argv[7]));
                auto changedTemplate = source;
                changedTemplate.Entry.Values["Possess"] = source.Entry.Get("Possess") == "1" ? "0" : "1";
                auto metadata = asset::BuildGameBundle(geometry, changedTemplate, spec, Path(argv[2]), Path(argv[7]));
                if (description.at("hash") == baseline || input.at("hash") == baseline ||
                    metadata.at("hash") == baseline)
                    throw std::runtime_error(
                        "Changed description, input contents or template metadata reused a fingerprint");
                result = {{"passed", true},
                          {"builder_version", asset::BuilderVersion},
                          {"baseline", baseline},
                          {"repeat", repeat.at("hash")},
                          {"description", description.at("hash")},
                          {"input_contents", input.at("hash")},
                          {"template_metadata", metadata.at("hash")}};
            }
            WriteJson(Path(argv[7]) / "build-result.json", result);
            std::cout << result.dump(2) << '\n';
            return 0;
        }
        if (argc == 7 && std::string(argv[1]) == "--catalog")
        {
            CharacterCatalog catalog(Path(argv[2]), Path(argv[4]), Path(argv[3]));
            catalog.Load();
            Json entries = Json::array();
            for (const auto &entry : catalog.Query(std::stoi(argv[5]), {}, 1))
                entries.push_back({{"id", entry.Id},
                                   {"category", entry.Category},
                                   {"bundle", entry.Bundle},
                                   {"asset", entry.Asset},
                                   {"male", entry.MaleAsset},
                                   {"values", entry.Values}});
            WriteJson(Path(argv[6]), entries);
            std::cout << entries.size() << " entries\n";
            return 0;
        }
        if (argc != 6)
            throw std::runtime_error("Usage: --inspect/--rewrite game classdata bundle output");
        auto database = std::make_shared<UnityClassDatabase>(Path(argv[3]));
        auto schemas = std::make_shared<NativeMonoSchema>(Path(argv[2]) / "HoneySelect2_Data/Managed", database);
        NativeGpuAssetReader reader(Path(argv[4]), database.get());
        reader.ScriptSchema = [&](const GpuAssetPointer &ptr, std::string_view version)
        {
            if (ptr.FileId)
                throw std::runtime_error("External script needs a store");
            auto script = reader.Metadata(ptr.PathId);
            return schemas->Type(script.String("m_AssemblyName"), script.String("m_Namespace"),
                                 script.String("m_ClassName"), version);
        };
        if (std::string(argv[1]) == "--rewrite")
        {
            UnityBundle source(Path(argv[4]));
            std::vector<UnityBundleOutput> entries;
            std::size_t checked = 0;
            for (std::size_t i = 0; i < source.Entries().size(); ++i)
            {
                const auto &entry = source.Entries()[i];
                auto bytes = source.Entry(i);
                UnityBundleOutput output{entry.Name, {bytes.begin(), bytes.end()}, entry.Flags};
                if (entry.Flags & 4)
                {
                    UnitySerializedFile serialized(bytes, database.get());
                    auto doc = serialized.Document();
                    for (auto &object : doc.Objects)
                    {
                        auto schema = reader.TypeOf(object.PathId).Nodes;
                        if (reader.AssetClass(object.PathId) == 114)
                        {
                            auto metadata = reader.Metadata(object.PathId);
                            auto script = reader.Metadata(metadata.Get("m_Script").Int("m_PathID"));
                            if (schema.size() <= database->Type(114, source.UnityVersion).size())
                                schema = schemas->Type(script.String("m_AssemblyName"), script.String("m_Namespace"),
                                                       script.String("m_ClassName"), source.UnityVersion);
                        }
                        auto encoded = WriteUnityValue(schema, reader.Metadata(object.PathId), doc.BigEndian);
                        if (encoded != object.Data)
                            throw std::runtime_error("Field roundtrip changed object " + std::to_string(object.PathId) +
                                                     " class " +
                                                     std::to_string(reader.AssetClass(object.PathId).value_or(-1)));
                        object.Data = std::move(encoded);
                        ++checked;
                    }
                    output.Data = WriteUnitySerializedFile(doc);
                }
                entries.push_back(std::move(output));
            }
            WriteAtomic(Path(argv[5]), WriteUnityBundle(source.UnityVersion, entries));
            std::cout << "PASS " << checked << " exact object roundtrips\n";
            return 0;
        }
        Json result = {{"version", reader.UnityVersion()},
                       {"cab", reader.CabName},
                       {"externals", reader.ExternalNames},
                       {"objects", Json::array()}};
        if (std::string(argv[1]) == "--inspect-raw")
        {
            UnitySerializedFile file(ReadFile(Path(argv[4])), database.get());
            result["version"] = file.UnityVersion;
            for (const auto &a : file.Assets())
                if (a.ClassId == 48 &&
                    WriteUnityValue(file.TypeOf(a.PathId).Nodes, file.Read(a.PathId)) != reader.RawObject(a.PathId))
                    throw std::runtime_error("Shader roundtrip failed: " + std::to_string(a.PathId));
        }
        else
        {
            UnityBundle inspectBundle(Path(argv[4]));
            for (std::size_t i = 0; i < inspectBundle.Entries().size(); ++i)
                if (inspectBundle.Entries()[i].Flags & 4)
                {
                    UnitySerializedFile file(inspectBundle.Entry(i), database.get());
                    auto doc = file.Document();
                    result["metadata"] = {{"format", doc.FormatVersion},
                                          {"platform", doc.Platform},
                                          {"type_tree", doc.HasTypeTree},
                                          {"script_references", doc.ScriptReferences},
                                          {"types", Json::array()}};
                    for (const auto &type : doc.Types)
                        result["metadata"]["types"].push_back({{"class", type.ClassId},
                                                               {"stripped", type.Stripped},
                                                               {"script_index", type.ScriptTypeIndex},
                                                               {"script_id", type.ScriptId},
                                                               {"hash", type.TypeHash},
                                                               {"nodes", type.Nodes.size()}});
                }
        }
        for (const auto &asset : reader.AssetIndex())
        {
            Json item = {
                {"id", asset.PathId}, {"class", asset.ClassId}, {"type_index", asset.TypeIndex}, {"size", asset.Size}};
            try
            {
                item["value"] = Value(reader.Metadata(asset.PathId));
            }
            catch (const std::exception &e)
            {
                item["error"] = e.what();
            }
            result["objects"].push_back(std::move(item));
        }
        WriteJson(Path(argv[5]), result);
        std::cout << reader.AssetIndex().size() << " objects\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
