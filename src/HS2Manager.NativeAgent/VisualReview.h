#pragma once
#include "Common/Core.h"

namespace hs2::agent
{
    bool ReviewView(std::string_view view);
    Json ReferenceRegions(const fs::path &reference, const Json &regions, const fs::path &output);
    Json ComparisonImages(const Json &references, const Json &images, const fs::path &output);
    Json ProbeImages(const Json &probes, const fs::path &output);
    Json ReviewRequirements(const Json &session);
    Json AssessRender(const Json &session, const Json &args);
    bool CurrentReviewPassed(const Json &session);
}
