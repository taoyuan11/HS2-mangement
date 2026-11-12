#include "CreationService.h"
#include <objbase.h>
#include <iostream>

using namespace hs2;
using namespace hs2::creation;

namespace
{
    void Check(bool ok, const std::string &message)
    {
        if (!ok)
            throw std::runtime_error(message);
    }
}

int wmain(int argc, wchar_t **argv)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int status = 0;
    try
    {
        if (argc != 7)
            throw std::invalid_argument(
                "usage: install_acceptance install|rollback sandbox data runtime native-report output");
        auto mode = Utf8(fs::path(argv[1]));
        fs::path game(argv[2]), output(argv[6]);
        auto marker = Json::parse(ReadFile(game / "agent-sandbox.json"));
        Check(marker.value("purpose", "") == "hs2-agent-game-validation", "independent owned game sandbox required");
        CreationService service(game, argv[3], argv[4]);
        fs::create_directories(output);
        auto journalPath = output / "installations.json";
        if (mode == "rollback")
        {
            auto operations = Json::parse(ReadFile(journalPath));
            for (auto it = operations.rbegin(); it != operations.rend(); ++it)
            {
                auto result = service.Rollback(it->at("id"));
                Check(result.Status == "rolled_back", result.Status + Json(result.Messages).dump());
                (*it)["rollback"] = result.Status;
                WriteJson(journalPath, operations);
            }
            WriteJson(output / "rollback-result.json", {{"passed", true}, {"operations", operations}});
        }
        else if (mode == "install")
        {
            Check(!fs::exists(journalPath), "use a new installation acceptance output");
            auto report = Json::parse(ReadFile(argv[5]));
            Json operations = Json::array(), items = Json::array();
            for (const auto &entry : report.at("cases"))
            {
                auto project = CreationProject::Open(Path(entry.at("project").get<std::string>()));
                std::string previousHash;
                for (auto version : {"first", "updated"})
                {
                    auto state = project->State();
                    state.LastBuild = entry.at(version);
                    state.Character = CharacterDocument::Open(Path(state.LastBuild.at("card").get<std::string>()));
                    state.Token = NewId();
                    state.LastBuild["state_token"] = state.Token;
                    auto plan = service.PlanInstall(state, project->Id());
                    Check(plan.Conflicts.empty(), plan.Summary());
                    auto result = service.Install(plan);
                    Check(result.Status == "installed", result.Status + Json(result.Messages).dump());
                    operations.push_back({{"id", result.Id},
                                          {"project", project->Id()},
                                          {"version", version},
                                          {"status", result.Status}});
                    WriteJson(journalPath, operations);
                    Check(result.Cards.size() == 1 &&
                              HashFile(result.Cards.front()) == state.LastBuild.at("card_hash").get<std::string>(),
                          "installed card hash mismatch");
                    Check(!service.PlanInstall(state, project->Id()).HasChanges(), "same content not skipped");
                    if (std::string(version) == "updated")
                    {
                        auto foreign = game / "mods" / Path("creation-conflict-" + project->Id() + ".zipmod");
                        Check(!fs::exists(foreign), "conflict fixture already exists");
                        WriteAtomic(foreign, ReadFile(Path(state.LastBuild.at("mod").get<std::string>()),
                                                      1024ull * 1024 * 1024));
                        Check(!service.PlanInstall(state, project->Id()).Conflicts.empty(),
                              "foreign same-GUID package accepted");
                        fs::remove(foreign);
                        for (auto kind : {"accessory", "hair", "clothes"})
                            items.push_back({{"path", Utf8(result.Cards.front())},
                                             {"kind", kind},
                                             {"slot", 0},
                                             {"target", "face"}});
                        for (auto target : {"face", "body", "clothes"})
                            items.push_back({{"path", Utf8(result.Cards.front())},
                                             {"kind", "texture"},
                                             {"slot", 0},
                                             {"target", target}});
                    }
                }
            }
            WriteJson(output / "install-result.json", {{"passed", true}, {"operations", operations}, {"items", items}});
            WriteJson(game / "agent-test-input.json", {{"items", items}});
            std::string tsv;
            for (const auto &item : items)
                tsv += item.at("path").get<std::string>() + "\t" + item.at("kind").get<std::string>() + "\t0\t" +
                       item.at("target").get<std::string>() + "\n";
            WriteAtomic(game / "agent-test-input.tsv", Bytes(tsv.begin(), tsv.end()));
        }
        else
            throw std::invalid_argument("unknown mode");
        std::cout << mode << " acceptance passed" << std::endl;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << std::endl;
        status = 1;
    }
    CoUninitialize();
    return status;
}
