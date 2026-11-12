#include "Agent.h"
#include <windows.h>
#include <objbase.h>
#include <iostream>
using namespace hs2;
#include "Fixtures.h"

namespace
{
    void Check(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }

    template <class F> void Throws(F function)
    {
        bool failed = false;
        try
        {
            function();
        }
        catch (const std::exception &)
        {
            failed = true;
        }
        Check(failed, "expected rejection");
    }
}

int wmain(int argc, wchar_t **argv)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    auto root = fs::temp_directory_path() / Path("hs2-agent-checks-" + NewId());
    int code = 0;
    try
    {
        if (argc != 2)
            throw std::invalid_argument("fixture PNG required");
        referencePng = ReadFile(argv[1]);
        fs::create_directories(root);
        auto fixture = root / "base.png";
        WriteAtomic(fixture, Fixture());
        auto original = CharacterDocument::Open(fixture);
        auto changed = original.WithName("新建姓名").WithCover(referencePng);
        auto result = CharacterDocument::FromBytes(changed.ToBytes(), root / "created.png");
        Check(!changed.IsOriginal() && !original.ContentEquals(changed), "identity dirty tracking");
        Check(result.Node("face.unknown").ContentEquals(original.Node("face.unknown")), "unknown face bytes");
        Check(result.Extensions()->ContentEquals(*original.Extensions()), "extensions retained");
        Check(result.BlockBytes("Future").size() == 5, "future block retained");
        Check(result.Sex() == 1, "sex retained");
        auto overlay = agent::ApplyOverlay(original, {{"target", "face"}}, referencePng);
        auto skin = overlay.Extensions()->Require(std::string("KSOX"));
        Check(skin.Require(std::int64_t(0)).Integer() == 2, "skin overlay version");
        auto data = skin.Require(std::int64_t(1));
        auto lookup = MessagePack::Parse(data.Require(std::string("Lookup")).Payload());
        auto textureId = lookup.Require(std::int64_t(0)).Require(std::int64_t(2)).Integer();
        Check(data.Require(std::string("_TextureID_") + std::to_string(textureId)).Payload().size() ==
                  referencePng.size(),
              "skin texture embedded");
        auto clothes =
            agent::ApplyOverlay(overlay, {{"target", "clothes"}, {"clothing_name", "ct_clothesTop"}}, referencePng);
        Check(clothes.Extensions()->Require(std::string("KSOX")).ContentEquals(skin), "unrelated overlay retained");
        Check(clothes.Extensions()
                  ->Require(std::string("unknown.plugin"))
                  .ContentEquals(original.Extensions()->Require(std::string("unknown.plugin"))),
              "unknown plugin retained after overlay");
        auto roundtrip = CharacterDocument::FromBytes(clothes.ToBytes(), root / "overlay.png");
        Check(roundtrip.Extensions()->ContentEquals(*clothes.Extensions()), "overlay binary roundtrip");
        WriteAtomic(root / "created.png", result.ToBytes());
        Check(CardParser().Parse(root / "created.png").Name == "新建姓名", "name roundtrip");
        Check(ReadFile(fixture) == Fixture(), "source unchanged");
        Throws(
            [&]
            {
                original.WithName("");
            });
        Throws(
            [&]
            {
                original.WithCover(Bytes{1, 2});
            });
        Json config{{"data_directory", Utf8(root / "Agent")},
                    {"game_path", Utf8(root / "Game")},
                    {"base_cards", {{"female", Utf8(fixture)}}}};
        std::string sessionId;
        {
            agent::Service service(config, root);
            Throws(
                [&]
                {
                    agent::Service duplicate(config, root);
                });
            auto capabilities = service.Call("hs2_capabilities", Json::object());
            Check(capabilities["base_cards"]["female"]["valid"] == true, "base validation");
            Check(agent::Service::Tools().size() == 15, "tool discovery");
            auto session = service.Call("hs2_begin_card",
                                        {{"sex", "female"}, {"reference_image", {{"path", Utf8(fs::path(argv[1]))}}}});
            sessionId = session["session_id"];
            Json args{{"session_id", sessionId},
                      {"expected_revision", 0},
                      {"changes", {{{"path", "face.shapeValueFace[0]"}, {"value", .6}}}}};
            auto edited = service.Call("hs2_apply_changes", args);
            Check(edited["revision"] == 1 && edited["iterations"] == 1, "revision commit");
            Throws(
                [&]
                {
                    service.Call("hs2_apply_changes", args);
                });
            args["expected_revision"] = 1;
            args["changes"][0]["value"] = 10000;
            Throws(
                [&]
                {
                    service.Call("hs2_apply_changes", args);
                });
            args["changes"][0]["path"] = "forbidden.path";
            Throws(
                [&]
                {
                    service.Call("hs2_apply_changes", args);
                });
            Check(service.Call("hs2_get_session", {{"session_id", sessionId}})["revision"] == 1,
                  "invalid batch atomic");
            args["changes"] = {{{"path", "face.shapeValueFace[0]"}, {"value", .8}},
                               {{"path", "forbidden.path"}, {"value", 0}}};
            Throws(
                [&]
                {
                    service.Call("hs2_apply_changes", args);
                });
            auto draftPath = root / "Agent/Sessions" / Path(sessionId) / "revision-1.png";
            auto draft = CharacterDocument::Open(draftPath);
            Check(draft.Node("face.shapeValueFace[0]").ScalarValue == MessagePack::Value(.6f),
                  "partial batch not committed");
            auto restored = service.Call("hs2_restore_revision",
                                         {{"session_id", sessionId}, {"expected_revision", 1}, {"revision", 0}});
            Check(restored["revision"] == 2, "restore monotonic revision");
            auto blockedDraft = root / "Agent/Sessions" / Path(sessionId) / "revision-3.png";
            fs::create_directory(blockedDraft);
            args["expected_revision"] = 2;
            args["changes"] = {{{"path", "face.shapeValueFace[0]"}, {"value", .7}}};
            Throws(
                [&]
                {
                    service.Call("hs2_apply_changes", args);
                });
            Check(service.Call("hs2_get_session", {{"session_id", sessionId}})["revision"] == 2,
                  "draft write failure retained committed revision");
            fs::remove(blockedDraft);
            auto budget = service.Call("hs2_begin_card",
                                       {{"sex", "female"}, {"reference_image", {{"path", Utf8(fs::path(argv[1]))}}}});
            for (int iteration = 0; iteration < 12; ++iteration)
                budget = service.Call(
                    "hs2_apply_changes",
                    {{"session_id", budget["session_id"]},
                     {"expected_revision", iteration},
                     {"changes", {{{"path", "face.shapeValueFace[0]"}, {"value", iteration % 2 ? .4 : .6}}}}});
            Throws(
                [&]
                {
                    service.Call("hs2_apply_changes",
                                 {{"session_id", budget["session_id"]},
                                  {"expected_revision", 12},
                                  {"changes", {{{"path", "face.shapeValueFace[0]"}, {"value", .7}}}}});
                });
            Check(service.Call("hs2_get_session", {{"session_id", budget["session_id"]}})["revision"] == 12,
                  "twelve iteration budget retained best draft");
            Json buildArgs{{"session_id", sessionId},
                           {"expected_revision", 2},
                           {"spec", {{"kind", "hair"}, {"asset_key", "missing-tool-fixture"}}}};
            for (int attempt = 0; attempt < 4; ++attempt)
            {
                auto job = service.Call("hs2_build_asset", buildArgs);
                for (int wait = 0; wait < 100 && (job["status"] == "queued" || job["status"] == "running"); ++wait)
                {
                    std::this_thread::sleep_for(std::chrono::milliseconds(5));
                    job = service.Call("hs2_get_job", {{"job_id", job["job_id"]}});
                }
                Check(job["status"] == "failed", "missing-tool job failed without changing draft");
                if (attempt == 3)
                    Check(job.at("error").get<std::string>().starts_with("ASSET_BUILD_LIMIT"),
                          "three asset attempt limit");
            }
            Check(service.Call("hs2_get_session", {{"session_id", sessionId}})["revision"] == 2,
                  "failed build retained revision");
            Throws(
                [&]
                {
                    service.Call("hs2_get_session", {{"session_id", "../escape"}});
                });
            auto corrupt = root / "bad.png";
            WriteAtomic(corrupt, Bytes{1, 2, 3});
            Throws(
                [&]
                {
                    service.Call("hs2_begin_card", {{"sex", "female"}, {"reference_image", {{"path", Utf8(corrupt)}}}});
                });
            service.Stop();
        }
        auto interruptedId = NewId();
        WriteJson(root / "Agent/Jobs" / Path(interruptedId + ".json"),
                  {{"job_id", interruptedId}, {"status", "running"}});
        {
            agent::Service service(config, root);
            Check(service.Call("hs2_get_session", {{"session_id", sessionId}})["revision"] == 2,
                  "restart session recovery");
            Check(service.Call("hs2_get_job", {{"job_id", interruptedId}})["status"] == "interrupted",
                  "interrupted job recovery");
            auto encoded = service.Call(
                "hs2_begin_card",
                {{"sex", "female"},
                 {"reference_image", {{"mime_type", "image/png"}, {"base64", agent::Base64(referencePng)}}}});
            Check(encoded["revision"] == 0, "base64 image session");
        }
        Check(agent::Unbase64(agent::Base64(referencePng)) == referencePng, "image payload roundtrip");
        Throws(
            []
            {
                agent::Unbase64("%%%invalid");
            });
        std::cout << "PASS: identity, cover, unknown data, sessions, revisions, atomic validation, image inputs, "
                     "persistence\n";
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        code = 1;
    }
    // 本检查只删除自己分配的临时目录。
    if (Within(root, fs::temp_directory_path()) && root.filename().wstring().starts_with(L"hs2-agent-checks-"))
    {
        std::error_code error;
        fs::remove_all(root, error);
    }
    CoUninitialize();
    return code;
}
