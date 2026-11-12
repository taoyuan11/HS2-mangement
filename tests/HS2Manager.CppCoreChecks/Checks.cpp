#include "Common/Core.h"
#include "Mods/ModIndex.h"
#include "Export/CardExport.h"
#include "Editing/CharacterEditComponents.h"
#include "Archive/ZipArchive.h"
#include <windows.h>
#include <algorithm>
#include <iostream>
#include <limits>

using namespace hs2;

namespace
{
    int checks = 0;

    void Check(bool ok, const char *name)
    {
        if (!ok)
            throw std::runtime_error(name);
        checks++;
    }

    template <class F> void Throws(F action, const char *name)
    {
        bool threw = false;
        try
        {
            action();
        }
        catch (const std::exception &)
        {
            threw = true;
        }
        Check(threw, name);
    }

    MessagePack S(std::string value)
    {
        return MessagePack::Scalar(std::move(value));
    }

    MessagePack I(std::int64_t value)
    {
        return MessagePack::Scalar(value);
    }

    void Number(Bytes &bytes, std::uint64_t value, int width, bool big = false)
    {
        for (int i = 0; i < width; i++)
            bytes.push_back(static_cast<std::uint8_t>(value >> ((big ? width - 1 - i : i) * 8)));
    }

    void Append(Bytes &a, const Bytes &b)
    {
        a.insert(a.end(), b.begin(), b.end());
    }

    void Text(Bytes &output, const std::string &text)
    {
        auto length = text.size();
        while (length >= 128)
        {
            output.push_back(static_cast<std::uint8_t>((length & 127) | 128));
            length >>= 7;
        }
        output.push_back(static_cast<std::uint8_t>(length));
        output.insert(output.end(), text.begin(), text.end());
    }

    Bytes Png()
    {
        Bytes output{137, 80, 78, 71, 13, 10, 26, 10};
        Number(output, 13, 4, true);
        output.insert(output.end(), {'I', 'H', 'D', 'R'});
        output.insert(output.end(), 13, 0);
        Number(output, 0, 4);
        Number(output, 0, 4, true);
        output.insert(output.end(), {'I', 'E', 'N', 'D'});
        Number(output, 0, 4);
        return output;
    }

    Bytes Parts(std::vector<MessagePack> parts)
    {
        Bytes output;
        for (auto &part : parts)
        {
            auto bytes = part.ToBytes();
            Number(output, bytes.size(), 4);
            Append(output, bytes);
        }
        output.push_back(0xee);
        return output;
    }

    Bytes Fixture()
    {
        auto face = MessagePack::Map({{std::string("shapeValueFace"),
                                       MessagePack::Array({MessagePack::Scalar(0.25f), MessagePack::Scalar(0.75f)})},
                                      {std::string("unknown"), MessagePack::Parse(Bytes{0xd4, 0x7f, 0x99})}});
        auto body = MessagePack::Map({{std::string("height"), MessagePack::Scalar(0.5f)}});
        auto empty = MessagePack::Map();
        auto parameter =
            MessagePack::Map({{std::string("sex"), I(1)}, {std::string("fullname"), S("测试人物")}}).ToBytes();
        auto references =
            MessagePack::Array({MessagePack::Binary(MessagePack::Map({{std::string("ModID"), S("example.mod")},
                                                                      {std::string("Slot"), I(7)},
                                                                      {std::string("CategoryNo"), I(9)},
                                                                      {std::string("Property"), S("hair.parts[0].id")}})
                                                        .ToBytes())});
        auto plugin = MessagePack::Array({I(0), MessagePack::Map({{std::string("info"), references}})});
        auto extension = MessagePack::Map({{std::string("com.bepis.sideloader.universalautoresolver"), plugin},
                                           {std::string("unknown.plugin"), MessagePack::Binary(Bytes{9, 8, 7})}})
                             .ToBytes();
        std::vector<std::pair<std::string, Bytes>> blocks{{"Parameter", parameter},
                                                          {"Custom", Parts({face, body, empty})},
                                                          {"Coordinate", Parts({empty, empty})},
                                                          {"KKEx", extension},
                                                          {"Future", Bytes{1, 2, 3, 4, 5}}};
        Bytes payload;
        std::vector<MessagePack> rows;
        for (auto &[name, bytes] : blocks)
        {
            payload.push_back(0xfd);
            rows.push_back(MessagePack::Map({{std::string("name"), S(name)},
                                             {std::string("pos"), I(payload.size())},
                                             {std::string("size"), I(bytes.size())},
                                             {std::string("version"), S("1")}}));
            Append(payload, bytes);
        }
        payload.push_back(0xfc);
        auto table = MessagePack::Map(
                         {{std::string("lstInfo"), MessagePack::Array(rows)}, {std::string("future"), S("preserve")}})
                         .ToBytes();
        auto output = Png();
        Number(output, 100, 4);
        Text(output, "【AIS_Chara】");
        Text(output, "1.0.0");
        Number(output, 0, 4);
        Text(output, "user");
        Text(output, "data");
        Number(output, table.size(), 4);
        Append(output, table);
        Number(output, payload.size(), 8);
        Append(output, payload);
        output.insert(output.end(), {0xaa, 0xbb, 0xcc});
        return output;
    }

    void MessagePackChecks()
    {
        Bytes wire{0x83, 0xa1, 'x', 0xcd, 0, 7, 0x01, 0xc4, 3, 1, 2, 3, 0xa1, 'e', 0xd4, 42, 99};
        auto node = MessagePack::Parse(wire);
        Check(node.ToBytes() == wire, "messagepack exact wire roundtrip");
        auto modified = node.Set(std::string("x"), I(200));
        Check(modified.Require(std::string("x")).Integer() == 200, "messagepack update");
        Check(modified.Require(std::int64_t(1)).ToBytes() == Bytes({0xc4, 3, 1, 2, 3}),
              "integer map key and binary preserved");
        Check(modified.Require(std::string("e")).ToBytes() == Bytes({0xd4, 42, 99}), "extension tag preserved");
        Check(I(200).ToBytes() == Bytes({0xcc, 200}), "game compatible narrow integer");
        Throws(
            []
            {
                MessagePack::Parse(Bytes{0x81, 0xa1, 'x'});
            },
            "truncated messagepack rejected");
        Throws(
            []
            {
                MessagePack::Parse(Bytes{0xd9, 2, 0xc0, 0x80});
            },
            "overlong UTF8 rejected");
        Throws(
            []
            {
                MessagePack::Scalar(std::numeric_limits<float>::infinity());
            },
            "nonfinite editing rejected");
        auto duplicate = MessagePack::Parse(Bytes{0x82, 0xa1, 'x', 1, 0xa1, 'x', 2});
        Throws(
            [&]
            {
                duplicate.Require(std::string("x"));
            },
            "duplicate keys rejected");
        Bytes deep(50, 0x91);
        deep.push_back(0xc0);
        Throws(
            [&]
            {
                MessagePack::Parse(deep);
            },
            "nested bomb rejected");
    }

    void ExportChecks(const fs::path &root)
    {
        auto game = root / L"ExportGame", output = root / L"Exports";
        fs::create_directories(game / L"UserData" / L"chara" / L"female");
        fs::create_directories(game / L"mods");
        fs::create_directories(game / L"BepInEx" / L"plugins");
        auto source = game / L"UserData" / L"chara" / L"female" / L"original.png";
        WriteAtomic(source, Fixture());
        CardRepository repository(root / L"ExportData");
        auto card = CardParser().Parse(source);
        repository.Save(card);
        CardExportService service(repository);
        auto plan = service.BuildPlan({card, card}, game, output, {CardExportMode::PerCard, CardExportLevel::CardOnly});
        Check(plan.RequestedCards == 1 && plan.Packages.size() == 1 && plan.Packages[0].Issues.empty() &&
                  plan.Packages[0].Files.size() == 1,
              "card only plan does not require dependencies");
        auto result = service.Export(plan);
        Check(result.Status == CardExportStatus::Complete && result.ExportedCards() == 1, "card export completes");
        Check(HashFile(ExportPaths::Destination(Path(result.OutputFolders[0]),
                                                plan.Packages[0].Files[0].RelativePath)) == HashFile(source),
              "card export includes full payload");
        Check(fs::exists(Path(result.ReportPath)) && fs::exists(Path(result.OutputFolders[0]) / L"导出清单.txt"),
              "export reports written");
        auto repeated = service.Export(plan);
        Check(repeated.Status == CardExportStatus::Complete && repeated.OutputFolders != result.OutputFolders,
              "export reserves new directory without overwrite");
        Throws(
            [&]
            {
                service.BuildPlan({card}, game, game / L"mods");
            },
            "output inside game rejected");
        for (auto path : {"../escape", "C:/escape", "one//two", "one/../two", "one/space ", "one/stream:data"})
            Throws(
                [&]
                {
                    ExportPaths::Relative(path);
                },
                "unsafe export path rejected");
        Check(ExportPaths::SafeName("CON") == "_CON" && ExportPaths::SafeName("a?b. ") == "a_b", "safe exported names");
        auto changed = service.BuildPlan({card}, game, output, {CardExportMode::PerCard, CardExportLevel::CardOnly});
        auto bytes = Fixture();
        bytes.push_back(0x71);
        WriteAtomic(source, bytes);
        auto rejected = service.Export(changed);
        Check(rejected.Status == CardExportStatus::Incomplete && rejected.ExportedCards() == 0,
              "source change after plan blocks export");
        WriteAtomic(source, Fixture());
        for (auto level : {CardExportLevel::WithMods, CardExportLevel::WithPlugins, CardExportLevel::WithResources})
        {
            auto dependencyPlan = service.BuildPlan({card}, game, output, {CardExportMode::Merged, level});
            Check(dependencyPlan.Packages.size() == 1 && !dependencyPlan.Packages[0].Issues.empty(),
                  "missing dependencies reported at export level");
            auto incomplete = service.Export(dependencyPlan);
            Check(incomplete.Status == CardExportStatus::Incomplete && incomplete.ExportedCards() == 1,
                  "incomplete package preserves valid card");
        }
        // Optional integrations must not pull unrelated installed plugins (and their missing
        // dependencies) into an otherwise self-contained character package.
        ModIndex pluginIndex;
        pluginIndex.Complete = true;
        auto makePlugin = [&](const std::string &name, const std::string &guid)
        {
            auto path = game / L"BepInEx/plugins" / Path(name + ".dll");
            WriteAtomic(path, Bytes{1, 2, 3});
            PluginFileInfo info;
            info.Path = Utf8(path);
            info.Size = 3;
            info.ModifiedUtcTicks = ModifiedTicks(path);
            info.AssemblyName = name;
            info.Version = "1.0";
            info.Plugins.push_back({guid, name, "1.0", {}, {}});
            return info;
        };
        auto primary = makePlugin("Primary", "unknown.plugin");
        primary.Plugins.front().Dependencies.push_back({"optional.integration", "", true});
        auto optional = makePlugin("Optional", "optional.integration");
        optional.References.push_back({"MissingIntegrationLibrary", "1.0"});
        pluginIndex.PluginFiles = {primary, optional, makePlugin("ExtendedSave", "com.bepis.bepinex.extendedsave")};
        auto dependencyCard = card;
        dependencyCard.Dependencies.clear();
        dependencyCard.PluginDataIds = {"unknown.plugin"};
        auto soft = CardExportDependencies(game, pluginIndex).Collect(dependencyCard, CardExportLevel::WithPlugins);
        Check(std::none_of(soft.Files.begin(), soft.Files.end(),
                           [&](const auto &file)
                           {
                               return file.Source == optional.Path;
                           }) &&
                  std::none_of(soft.Issues.begin(), soft.Issues.end(),
                               [](const auto &issue)
                               {
                                   return issue.find("MissingIntegrationLibrary") != std::string::npos;
                               }),
              "soft plugin dependency does not drag an unrelated integration into export");
        pluginIndex.PluginFiles.front().Plugins.front().Dependencies.front().Optional = false;
        auto hard = CardExportDependencies(game, pluginIndex).Collect(dependencyCard, CardExportLevel::WithPlugins);
        Check(std::any_of(hard.Files.begin(), hard.Files.end(),
                          [&](const auto &file)
                          {
                              return file.Source == optional.Path;
                          }) &&
                  std::any_of(hard.Issues.begin(), hard.Issues.end(),
                              [](const auto &issue)
                              {
                                  return issue.find("MissingIntegrationLibrary") != std::string::npos;
                              }),
              "required plugin dependency still validates its transitive assemblies");
        auto configuration = makePlugin("ConfigurationManager", "com.bepis.bepinex.configurationmanager");
        configuration.Version = "16.4.0.0";
        configuration.References.push_back({"BepInEx.BepIn4Patcher", "1.0.0.0"});
        pluginIndex.PluginFiles = {configuration, makePlugin("ExtendedSave", "com.bepis.bepinex.extendedsave")};
        dependencyCard.PluginDataIds = {"com.bepis.bepinex.configurationmanager"};
        auto legacy = CardExportDependencies(game, pluginIndex).Collect(dependencyCard, CardExportLevel::WithPlugins);
        Check(std::none_of(legacy.Issues.begin(), legacy.Issues.end(),
                           [](auto &issue)
                           {
                               return issue.find("BepIn4Patcher") != std::string::npos;
                           }) &&
                  std::any_of(legacy.Warnings.begin(), legacy.Warnings.end(),
                              [](auto &warning)
                              {
                                  return warning.find("BepInEx 4") != std::string::npos;
                              }),
              "verified configuration manager legacy compatibility is optional and reported");
        pluginIndex.PluginFiles.front().Version = "17.0.0.0";
        auto unverified =
            CardExportDependencies(game, pluginIndex).Collect(dependencyCard, CardExportLevel::WithPlugins);
        Check(std::any_of(unverified.Issues.begin(), unverified.Issues.end(),
                          [](auto &issue)
                          {
                              return issue.find("BepIn4Patcher") != std::string::npos;
                          }),
              "unverified plugin versions retain dependency validation");
        std::stop_source stop;
        stop.request_stop();
        auto cancelled = service.Export(plan, {}, stop.get_token());
        Check(cancelled.Status == CardExportStatus::Cancelled && cancelled.OutputFolders.empty(),
              "cancelled export no output");
        Throws(
            [&]
            {
                service.BuildPlan({card}, game, output, {}, {}, stop.get_token());
            },
            "planning respects cancellation");
    }

    void ComponentChecks()
    {
        auto value = [](std::string name, double number)
        {
            Parameter p;
            p.Name = std::move(name);
            p.Number = number;
            p.Encoding = "float";
            return p;
        };
        CharacterDefinition definition;
        auto part = std::make_shared<CharacterPart>();
        part->Role = "hair";
        part->Slot = 0;
        Parameter data;
        data.Children = {value("useTopColor", 0), value("useUnderColor", 1), value("useMesh", 0),
                         value("useAcsColor01", 1)};
        part->Components.push_back({{}, "CmpHair", data});
        definition.Parts.push_back(part);
        auto visible = [&](const std::string &path)
        {
            CharacterField field;
            field.Path = path;
            return CharacterFieldCapabilities::Visible(field, &definition);
        };
        Check(visible("hair.parts[0].id") && !visible("hair.parts[0].topColor") &&
                  visible("hair.parts[0].underColor") && !visible("hair.parts[0].meshType") &&
                  visible("hair.parts[0].acsColorInfo[0].color") && !visible("hair.parts[0].acsColorInfo[1].color"),
              "hair capability fields follow component metadata");
        Check(!visible("hair.parts[1].topColor") && visible("hair.parts[1].id") && visible("face.headId"),
              "missing part capability visibility");
        part->Components.clear();
        Check(visible("hair.parts[0].topColor"), "unknown component metadata remains editable");
        auto document = CharacterDocument::FromBytes(Fixture(), L"component.png");
        auto scalar = [](float n)
        {
            return MessagePack::Scalar(n);
        };
        auto color = MessagePack::Array({scalar(0), scalar(0), scalar(0), scalar(1)});
        document = document.WithNode(
            "hair",
            MessagePack::Map({{std::string("parts"),
                               MessagePack::Array({MessagePack::Map(
                                   {{std::string("id"), I(1)},
                                    {std::string("dictBundle"), MessagePack::Map()},
                                    {std::string("acsColorInfo"),
                                     MessagePack::Array({MessagePack::Map({{std::string("color"), color}})})}})})}}));
        Parameter bone;
        auto vector = [&](std::string name, float x, float y, float z)
        {
            Parameter p;
            p.Name = std::move(name);
            p.Children = {value("x", x), value("y", y), value("z", z)};
            return p;
        };
        bone.Children = {vector("posMin", -1, 0, 0), vector("posMax", 1, 0, 2),    vector("basePos", 0, 9, 1),
                         vector("rotMin", 0, 0, 0),  vector("rotMax", 10, 20, 30), vector("baseRot", 5, 30, -5)};
        Parameter bones;
        bones.Name = "boneInfo";
        bones.Children = {bone};
        Parameter colors;
        colors.Name = "acsDefColor";
        Parameter tint;
        tint.Children = {value("r", .25), value("g", .5), value("b", .75), value("a", 1)};
        colors.Children = {tint};
        Parameter hair;
        hair.Children = {bones, colors};
        auto initialized = CharacterEditRules::InitializeAsset(document, "hair.parts[0].id", {{{}, "CmpHair", hair}});
        Check(initialized.Node("hair.parts[0].dictBundle{0}.moveRate[0]").ScalarValue == MessagePack::Value(.5f) &&
                  initialized.Node("hair.parts[0].dictBundle{0}.moveRate[1]").ScalarValue == MessagePack::Value(0.f),
              "asset bone rate initialization");
        Check(initialized.Node("hair.parts[0].dictBundle{0}.rotRate[1]").ScalarValue == MessagePack::Value(1.f) &&
                  initialized.Node("hair.parts[0].dictBundle{0}.rotRate[2]").ScalarValue == MessagePack::Value(0.f),
              "asset bone rate clamping");
        Check(initialized.Node("hair.parts[0].acsColorInfo[0].color[2]").ScalarValue == MessagePack::Value(.75f),
              "asset default accessory colors initialized");
    }

    void ZipFile(const fs::path &path, const std::vector<std::pair<std::string, std::string>> &entries)
    {
        Bytes data, directory;
        for (auto &[name, text] : entries)
        {
            auto offset = data.size();
            Bytes payload(text.begin(), text.end());
            auto crc = Crc32(payload);
            Number(data, 0x04034b50, 4);
            Number(data, 20, 2);
            Number(data, 0x800, 2);
            Number(data, 0, 2);
            Number(data, 0, 4);
            Number(data, crc, 4);
            Number(data, payload.size(), 4);
            Number(data, payload.size(), 4);
            Number(data, name.size(), 2);
            Number(data, 0, 2);
            data.insert(data.end(), name.begin(), name.end());
            Append(data, payload);
            Number(directory, 0x02014b50, 4);
            Number(directory, 20, 2);
            Number(directory, 20, 2);
            Number(directory, 0x800, 2);
            Number(directory, 0, 2);
            Number(directory, 0, 4);
            Number(directory, crc, 4);
            Number(directory, payload.size(), 4);
            Number(directory, payload.size(), 4);
            Number(directory, name.size(), 2);
            Number(directory, 0, 2);
            Number(directory, 0, 2);
            Number(directory, 0, 2);
            Number(directory, 0, 2);
            Number(directory, 0, 4);
            Number(directory, offset, 4);
            directory.insert(directory.end(), name.begin(), name.end());
        }
        auto offset = data.size();
        Append(data, directory);
        Number(data, 0x06054b50, 4);
        Number(data, 0, 2);
        Number(data, 0, 2);
        Number(data, entries.size(), 2);
        Number(data, entries.size(), 2);
        Number(data, directory.size(), 4);
        Number(data, offset, 4);
        Number(data, 0, 2);
        WriteAtomic(path, data);
    }

    void ModChecks(const fs::path &root)
    {
        auto game = root / L"ModGame";
        fs::create_directories(game / L"mods");
        fs::create_directories(game / L"BepInEx" / L"plugins");
        auto source = game / L"mods" / L"test.zipmod";
        ZipFile(
            source,
            {{"manifest.xml",
              R"(<manifest schema-ver="1"><guid>new.mod</guid><name>Fixture &amp; Mod</name><version>2.1</version><game>HS2</game><migrationInfo><info migrationType="Migrate" guidOld="old.mod" guidNew="new.mod" category="so_hair_b" idOld="1" idNew="2"/></migrationInfo><MaterialEditor><Shader Name="Custom/Test" AssetBundle="shader/custom.unity3d"/></MaterialEditor><KK_UncensorSelector><body><guid>body.fixture</guid></body></KK_UncensorSelector></manifest>)"},
             {"abdata/test.bytes", "unchanged"}});
        auto mod = ModDependencyService::ReadManifest(source);
        Check(mod.Guid == "new.mod" && mod.Name == "Fixture & Mod" && mod.Compatible, "native XML manifest parse");
        Check(mod.Migrations.size() == 1 && mod.Migrations[0].NewSlot == 2 &&
                  NativeModCategories::Parse(mod.Migrations[0].Category) == 300,
              "named migration category");
        Check(mod.ExportMetadata && mod.ExportMetadata->Shaders.size() == 1 &&
                  mod.ExportMetadata->Shaders[0].Bundles[0] == "shader/custom.unity3d",
              "shader provider metadata");
        Check(mod.ExportMetadata->BodyGuids == std::vector<std::string>{"body.fixture"} &&
                  mod.ExportMetadata->Version == 2,
              "body provider dependency metadata");
        CardRepository repository(root / L"ModData");
        ModDependencyService service(repository);
        auto index = service.Scan(game);
        Check(index.Complete && index.Files.size() == 1, "native MOD scan");
        CardRecord card;
        card.Dependencies = {{"old.mod", 1, 300, "test"}};
        Check(service.Diagnose(card, game)[0].State == DependencyState::Found, "migration dependency resolution");
        card.Dependencies[0].Slot = 99;
        Check(service.Diagnose(card, game)[0].State == DependencyState::Missing, "migration slot mismatch");
        Check(service.Diagnose(card, game, false)[0].State == DependencyState::Unknown,
              "offline dependency is unknown");
        auto old = mod;
        old.Version = "1.9";
        old.Path = "short.zipmod";
        auto newer = mod;
        newer.Version = "1.10";
        newer.Path = "new.zipmod";
        Check(NativeModSelection::Select({old, newer})[0].Version == "1.10", "numeric manifest version ordering");
        old.Version.clear();
        old.ModifiedUtcTicks = 50;
        newer.ModifiedUtcTicks = 25;
        Check(NativeModSelection::Select({old, newer})[0].Path == old.Path, "versionless group picks newest file");
        auto cached = ModDependencyService(repository);
        Check(cached.Index() && cached.Index()->Files[0].Guid == mod.Guid, "MOD index JSON compatibility");
        ZipFile(
            game / L"mods" / L"dtd.zipmod",
            {{"manifest.xml",
              R"(<!DOCTYPE manifest [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><manifest><guid>&x;</guid></manifest>)"}});
        Throws(
            [&]
            {
                ModDependencyService::ReadManifest(game / L"mods" / L"dtd.zipmod");
            },
            "XML external entity prohibited");
        Check(!service.Scan(game).Complete, "broken manifest marks incomplete");
        Check(PluginFileIndex::CompareVersions("1.2.0", "1.1.99") > 0 &&
                  PluginFileIndex::PluginGuid("KKABMPlugin.ABMData") == "KKABMX.Core",
              "plugin version and aliases");
    }

    void CoreChecks(const fs::path &root)
    {
        auto folder = root / L"cards";
        fs::create_directories(folder);
        auto source = folder / L"人物.png";
        auto fixture = Fixture();
        WriteAtomic(source, fixture);
        CardParser parser;
        auto card = parser.Parse(source);
        Check(card.Status == CardStatus::Valid && card.Name == "测试人物" && card.Sex == 1, "card metadata parse");
        Check(card.Dependencies.size() == 1 && card.Dependencies[0].Guid == "example.mod" &&
                  card.Dependencies[0].Slot == 7,
              "sideloader references parse");
        Check(card.PluginDataIds.size() == 2 && card.DependenciesComplete, "plugin metadata parse");
        auto image = folder / L"image.png";
        WriteAtomic(image, Png());
        Check(parser.Parse(image).Status == CardStatus::ImageOnly, "plain PNG classification");
        auto corrupt = folder / L"broken.png";
        WriteAtomic(corrupt, Bytes{0, 1, 2});
        Check(parser.Parse(corrupt).Status == CardStatus::Corrupt, "corrupt PNG classification");
        Check(parser.Parse(folder / L"missing.png").Status == CardStatus::Unreadable, "unreadable classification");
        Check(HashFile(source) == Sha256(fixture), "streaming SHA256");
        Check(Sha256(Bytes{}) == "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
              "SHA256 reference vector");
        auto document = CharacterDocument::Open(source);
        Check(document.ToBytes() == fixture && document.IsOriginal(), "character exact byte roundtrip");
        auto edited = document.WithValue("face.shapeValueFace[0]", 0.9f);
        Check(!edited.IsOriginal() && document.IsOriginal(), "immutable edit draft");
        auto saved = CharacterDocument::FromBytes(edited.ToBytes(), source);
        Check(saved.Node("face.shapeValueFace[0]").ScalarValue == MessagePack::Value(0.9f),
              "edited value survives serialize");
        Check(std::equal(saved.Cover().begin(), saved.Cover().end(), document.Cover().begin(), document.Cover().end()),
              "PNG cover unchanged");
        Check(std::equal(saved.BlockBytes("Future").begin(), saved.BlockBytes("Future").end(),
                         document.BlockBytes("Future").begin(), document.BlockBytes("Future").end()),
              "unknown block preserved");
        Check(saved.Node("face.unknown").ToBytes() == document.Node("face.unknown").ToBytes(),
              "unknown extension preserved");
        Check(saved.ToBytes()[saved.ToBytes().size() - 1] == 0xcc, "trailing data preserved");
        CharacterEditSession session(card, document);
        session.BeginGesture();
        session.SetValue("body.height", 0.7f);
        session.SetValue("body.height", 0.8f);
        session.EndGesture();
        Check(session.IsDirty() && session.CanUndo(), "edit gesture dirty");
        session.Undo();
        Check(!session.IsDirty() && session.CanRedo(), "gesture undo unit");
        session.Redo();
        Check(session.IsDirty(), "edit redo");
        session.RestoreAll();
        Check(!session.IsDirty(), "restore baseline");
        auto data = root / L"Data";
        CardRepository repository(data);
        card.Root = Utf8(folder);
        card.Favorite = true;
        card.Tags = {"标签"};
        card.Notes = "测试备注";
        repository.Save(card);
        Check(repository.FindByPath(source)->Notes == card.Notes, "SQLite persistence");
        auto scanned = repository.SaveScan(parser.Parse(source));
        Check(scanned.Id == card.Id && scanned.Favorite && scanned.Tags == card.Tags, "scan preserves metadata");
        repository.StoreMetadata("test", Json{{"value", 42}});
        Check(repository.LoadMetadata("test")->at("value") == 42, "SQLite metadata persistence");
        repository.Backup(root / L"backup.db");
        Check(fs::file_size(root / L"backup.db") > 0, "SQLite WAL backup");
        AppSettings settings;
        settings.GamePath = Utf8(root);
        settings.ExtraRoots = {Utf8(folder)};
        settings.Theme = "Dark";
        settings.LastCardExportLevel = CardExportLevel::WithResources;
        settings.Save(data);
        auto loaded = AppSettings::Load(data);
        Check(loaded.ExtraRoots == settings.ExtraRoots && loaded.LastCardExportLevel == settings.LastCardExportLevel &&
                  loaded.Theme == "Dark",
              "settings compatible roundtrip");
        CardLibraryService library(repository);
        auto scan = library.Scan({folder});
        Check(scan.Candidates == 3 && scan.Valid == 1 && scan.Invalid == 2, "scan classifications");
        Check(library.Scan({folder}).Unchanged == 2, "scan incremental covers");
        fs::create_directories(folder / L"sub");
        WriteAtomic(folder / L"sub" / L"nested.png", fixture);
        WriteAtomic(folder / L".HS2ManagerTrash" / L"hidden.png", fixture);
        Check(CardLibraryService::EnumerateCards(folder, false).size() == 3, "nonrecursive scan");
        Check(CardLibraryService::EnumerateCards(folder, true).size() == 4, "recursive scan excludes trash");
        library.Scan({folder}, {}, {}, true);
        auto nested = repository.FindByPath(folder / L"sub" / L"nested.png");
        fs::remove(folder / L"sub" / L"nested.png");
        library.Scan({folder});
        Check(repository.FindById(nested->Id)->Availability == FileAvailability::Available,
              "shallow scan retains deep entries");
        library.Scan({folder}, {}, {}, true);
        Check(repository.FindById(nested->Id)->Availability == FileAvailability::Missing,
              "successful scan marks missing");
        auto offlineRoot = root / L"offline";
        auto offlineCard = card;
        offlineCard.Id = NewId();
        offlineCard.Path = Utf8(offlineRoot / L"a.png");
        offlineCard.Root = Utf8(offlineRoot);
        repository.Save(offlineCard);
        auto offline = library.Scan({offlineRoot});
        Check(!offline.Warnings.empty() &&
                  repository.FindById(offlineCard.Id)->Availability == FileAvailability::Offline,
              "offline retains index");
        std::stop_source stop;
        stop.request_stop();
        Throws(
            [&]
            {
                library.Scan({folder}, {}, stop.get_token());
            },
            "scan cancellation");
        CardFileService files(repository);
        auto exports = files.Export({card}, root / L"exports");
        Check(exports.size() == 1 && exports[0].Success && HashFile(Path(exports[0].Destination)) == HashFile(source),
              "verified complete PNG export");
        auto imports = files.Import({source}, root / L"game");
        Check(imports.size() == 1 && imports[0].Success &&
                  fs::exists(root / L"game" / L"UserData" / L"chara" / L"female" / source.filename()),
              "import by sex");
        auto imported = repository.FindByPath(Path(imports[0].Destination)).value();
        files.Quarantine(imported, root / L"game");
        auto quarantined = repository.FindById(imported.Id).value();
        Check(quarantined.Availability == FileAvailability::Quarantined && !fs::exists(Path(imported.Path)),
              "reversible quarantine");
        files.Restore(quarantined);
        auto restored = repository.FindById(imported.Id).value();
        Check(restored.Availability == FileAvailability::Available && fs::exists(Path(imported.Path)),
              "quarantine restore");
        auto request = CardDeleteRequest::FromCard(restored);
        WriteAtomic(Path(restored.Path), Bytes{1, 2, 3});
        auto failed = files.Delete({request});
        Check(!failed[0].Success && fs::exists(Path(restored.Path)) && repository.FindById(restored.Id).has_value(),
              "delete rejects changed version");
        auto updated = parser.Parse(Path(restored.Path));
        updated.Id = restored.Id;
        repository.Save(updated);
        auto deletion = files.Delete({CardDeleteRequest::FromCard(updated)});
        Check(deletion[0].Success && !fs::exists(Path(updated.Path)) && !repository.FindById(updated.Id),
              "exclusive verified deletion");
        CardEditService editor(repository);
        auto edit = editor.Save(edited, card, false);
        Check(edit.Card.Favorite && edit.Card.Tags == card.Tags && edit.Card.Id != card.Id &&
                  HashFile(source) == document.SourceHash(),
              "edit save as preserves source and metadata");
        auto overwriteDocument = CharacterDocument::Open(source).WithValue("body.height", 0.65f);
        auto overwrite = editor.Save(overwriteDocument, repository.FindById(card.Id).value(), true);
        Check(fs::exists(Path(overwrite.BackupPath)) && HashFile(Path(overwrite.BackupPath)) == document.SourceHash(),
              "overwrite durable verified backup");
        auto op = files.Operations();
        auto found = std::find_if(op.begin(), op.end(),
                                  [&](auto &item)
                                  {
                                      return item.Id == overwrite.OperationId;
                                  });
        auto restoredEdit = editor.RestoreBackup(*found);
        Check(HashFile(source) == document.SourceHash(), "restore edit backup");
        files.Recover();
        Check(files.Recover().empty(), "recovery is idempotent");
    }
}

int wmain(int argc, wchar_t **argv)
{
    fs::path root;
    try
    {
        MessagePackChecks();
        ComponentChecks();
        wchar_t temp[MAX_PATH];
        GetTempPathW(MAX_PATH, temp);
        root = fs::path(temp) / Path("HS2Manager-CppCoreChecks-" + NewId());
        fs::create_directories(root);
        ExportChecks(root);
        CoreChecks(root);
        ModChecks(root);
        if (argc > 1)
        {
            auto path = fs::path(argv[1]);
            auto parsed = CardParser().Parse(path);
            Check(parsed.Status == CardStatus::Valid, "real card parse");
            auto original = ReadFile(path);
            auto document = CharacterDocument::Open(path);
            Check(document.ToBytes() == original, "real card byte roundtrip");
            auto fields = CharacterEditSchema::Build(document);
            Check(fields.size() > 100, "real card editor field schema");
            for (auto &field : fields)
                Check(document.Contains(field.Path), "schema path exists");
            auto references = CharacterAssetReferences::All(document);
            Check(references.size() <= parsed.Dependencies.size(), "editable resolver reference contract");
        }
        if (argc > 2)
        {
            auto mod = ModDependencyService::ReadManifest(fs::path(argv[2]));
            Check(mod.Compatible && !mod.Guid.empty(), "real zipmod manifest");
        }
        if (argc > 3)
        {
            auto plugin = PluginFileIndex::Read(fs::path(argv[3]));
            Check(!plugin.AssemblyName.empty() && !plugin.Plugins.empty() && plugin.Error.empty(),
                  "real BepInEx metadata");
            std::cout << "Plugin " << plugin.Plugins[0].Guid << " " << plugin.Plugins[0].Version
                      << " references=" << plugin.References.size() << "\n";
        }
        fs::remove_all(root);
        std::cout << "PASS " << checks << " native core checks\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << "FAIL after " << checks << ": " << error.what() << "\nFixture retained: " << Utf8(root) << "\n";
        return 1;
    }
}
