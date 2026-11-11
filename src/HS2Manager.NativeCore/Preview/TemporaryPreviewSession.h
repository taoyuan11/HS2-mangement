#pragma once
#include "Preview/PreviewResourceContext.h"

namespace hs2
{
    class TemporaryPreviewStorage
    {
    public:
        static std::shared_ptr<TemporaryPreviewStorage> Create(const fs::path &data);
        static void CleanAbandoned(const fs::path &data);
        ~TemporaryPreviewStorage();

        const fs::path &Root() const
        {
            return root_;
        }

        TemporaryPreviewStorage(const TemporaryPreviewStorage &) = delete;
        TemporaryPreviewStorage &operator=(const TemporaryPreviewStorage &) = delete;

    private:
        TemporaryPreviewStorage() = default;
        fs::path root_;
        void *lock_ = nullptr;
    };

    struct TemporaryPreviewCard
    {
        CardRecord Card;
        fs::path Relative;
        PreviewResources Resources;
    };

    class TemporaryPreviewSession
    {
    public:
        static std::shared_ptr<TemporaryPreviewSession> Open(const fs::path &source, const fs::path &game,
                                                             const fs::path &data, const fs::path &decoder,
                                                             const std::optional<ModIndex> &previous = {},
                                                             const std::optional<std::wstring> &password = {},
                                                             Progress progress = {}, std::stop_token cancellation = {});

        fs::path Source;
        std::vector<TemporaryPreviewCard> Cards;
        std::vector<std::string> Warnings;
        std::shared_ptr<TemporaryPreviewStorage> Storage;
    };
}
