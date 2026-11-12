#pragma once
#include "Common/Core.h"
#include "Character/CharacterPreparation.h"
#include <map>
#include <thread>

namespace hs2::agent
{
    Json ReadJson(const fs::path &file);
    std::string Base64(std::span<const std::uint8_t> bytes);
    Bytes Unbase64(std::string_view value);
    void ValidateImage(const fs::path &file);
    MessagePack::Value Scalar(const Json &value);
    Json ScalarJson(const MessagePack::Value &value);
    CharacterDocument ApplyOverlay(CharacterDocument document, const Json &spec, Bytes png);

    class Service
    {
    public:
        Service(Json config, fs::path runtime);
        ~Service();
        Json Call(const std::string &name, const Json &arguments);
        static Json Tools();
        void Stop();

    private:
        struct Job
        {
            Json State;
            std::jthread Thread;
        };

        Json config_;
        fs::path data_, game_, runtime_;
        std::unique_ptr<CardRepository> repository_;
        std::shared_ptr<void> instance_;
        std::optional<ModIndex> baseIndex_;
        std::mutex workGate_, jobsGate_;
        std::map<std::string, std::shared_ptr<Job>> jobs_;
        fs::path SessionPath(const std::string &id) const;
        Json Session(const Json &args) const;
        CharacterDocument Document(const Json &session) const;
        void SaveRevision(Json &session, const CharacterDocument &document, const Json &args);
        void CheckRevision(const Json &session, const Json &args) const;
        PreviewResources Resources(const Json &session, std::stop_token token);
        Json Execute(const std::string &name, const Json &args, std::stop_token token);
        Json Start(const std::string &name, const Json &args);
        Json Render(Json &session, const CharacterDocument &document, const Json &args, std::stop_token token);
        Json PrepareAsset(Json &session, const Json &args, std::stop_token token);
        Json BuildAsset(Json &session, const Json &args, std::stop_token token);
        Json Validate(Json &session, std::stop_token token);
        Json Finalize(Json &session, const Json &args, std::stop_token token);
    };
}
