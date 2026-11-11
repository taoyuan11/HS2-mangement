#include "PostProcessing/NativePostSettings.h"
#include <algorithm>
#include <unordered_set>
#include <stdexcept>

namespace hs2
{
    float NativePostSettings::Scalar(const std::string &effect, const std::string &key, float fallback) const
    {
        auto values = Value(effect, key);
        return values.empty() ? fallback : values.front();
    }

    std::vector<float> NativePostSettings::Value(const std::string &effect, const std::string &key,
                                                 std::vector<float> fallback) const
    {
        auto e = effects_.find(effect);
        if (e == effects_.end())
            return fallback;
        auto f = e->second.find(key);
        return f == e->second.end() ? fallback : f->second;
    }

    std::string NativePostSettings::TexturePath(const std::string &effect, const std::string &key) const
    {
        auto it = textures_.find({effect, key});
        return it == textures_.end() ? std::string{} : it->second;
    }

    std::vector<std::string> NativePostSettings::EnabledEffects() const
    {
        std::vector<std::string> result;
        for (const auto &[name, _] : effects_)
            if (Enabled(name))
                result.push_back(name);
        std::sort(result.begin(), result.end());
        return result;
    }

    void NativePostSettings::ValidateCoverage() const
    {
        const std::unordered_set<std::string> supported{
            "Bloom",    "AmbientOcclusion", "ColorGrading",          "ChromaticAberration",
            "Vignette", "MotionBlur",       "ScreenSpaceReflections"};
        for (const auto &effect : EnabledEffects())
            if (!supported.contains(effect))
                throw std::runtime_error("Native postprocessing effect is not implemented: " + effect);
        if (Enabled("Vignette") && Scalar("Vignette", "mode") != 0)
            throw std::runtime_error("Masked vignette needs its original texture composition path.");
        if (Enabled("Bloom") && Scalar("Bloom", "dirtIntensity") > 0 && !TexturePath("Bloom", "dirtTexture").empty())
            throw std::runtime_error("Bloom dirt texture composition is not implemented.");
    }

    void NativePostSettings::Blend(const std::string &effect, const std::string &key, const std::vector<float> &values,
                                   float weight)
    {
        if (weight <= 0)
            return;
        const std::unordered_set<std::string> discrete{"enabled",    "mode",    "gradingMode", "tonemapper", "quality",
                                                       "kernelSize", "rounded", "fastMode",    "ambientOnly"};
        auto previous = Value(effect, key, std::vector<float>(values.size()));
        if (previous.size() != values.size())
            throw std::runtime_error("Postprocessing parameter dimension changed: " + key);
        if (discrete.contains(key))
        {
            Set(effect, key, values);
            return;
        }
        for (size_t i = 0; i < values.size(); ++i)
            previous[i] += (values[i] - previous[i]) * weight;
        Set(effect, key, std::move(previous));
    }

    std::optional<std::vector<float>> NativePostSettings::Numbers(const Parameter &value)
    {
        if (value.Children.empty())
        {
            if (value.Text.empty())
                return std::vector<float>{static_cast<float>(value.Number)};
            return {};
        };
        for (const auto &names : std::vector<std::vector<std::string>>{{"r", "g", "b", "a"}, {"x", "y", "z", "w"}})
            if (value.Has(names.front()))
            {
                std::vector<float> result;
                for (const auto &n : names)
                    if (value.Has(n))
                        result.push_back(value.Float(n));
                return result;
            }
        return {};
    }

    std::vector<float> NativePostSettings::SampleCurve(const Parameter &spline)
    {
        auto keys = spline.Get("curve").Get("m_Curve").Children;
        std::sort(keys.begin(), keys.end(),
                  [](const auto &a, const auto &b)
                  {
                      return a.Float("time") < b.Float("time");
                  });
        std::vector<float> samples(128);
        auto shift = [](Parameter key, float offset)
        {
            for (auto &p : key.Children)
                if (p.Name == "time")
                    p.Number += offset;
            return key;
        };
        for (size_t i = 0; i < samples.size(); ++i)
        {
            float t = static_cast<float>(i) / 128;
            if (keys.empty())
            {
                samples[i] = spline.Float("m_ZeroValue", .5f);
                continue;
            }
            if (keys.size() == 1)
            {
                samples[i] = keys.front().Float("value");
                continue;
            }
            auto array = keys;
            if (spline.Float("m_Loop") != 0)
            {
                float range = spline.Float("m_Range", 1);
                if (t < keys.front().Float("time"))
                    array.insert(array.begin(), shift(keys.back(), -range));
                if (t > keys.back().Float("time"))
                    array.push_back(shift(keys.front(), range));
            }
            auto end = std::find_if(array.begin(), array.end(),
                                    [&](const auto &key)
                                    {
                                        return key.Float("time") >= t;
                                    });
            if (end == array.begin() || end == array.end())
            {
                samples[i] = (end == array.begin() ? array.front() : array.back()).Float("value");
                continue;
            }
            const auto &a = *(end - 1);
            const auto &b = *end;
            if (a.Float("weightedMode") != 0 || b.Float("weightedMode") != 0)
                throw std::runtime_error("Weighted postprocessing spline keys require Bezier inversion.");
            float dt = b.Float("time") - a.Float("time"), u = (t - a.Float("time")) / dt, u2 = u * u, u3 = u2 * u;
            samples[i] = (2 * u3 - 3 * u2 + 1) * a.Float("value") + (u3 - 2 * u2 + u) * dt * a.Float("outSlope") +
                         (-2 * u3 + 3 * u2) * b.Float("value") + (u3 - u2) * dt * b.Float("inSlope");
        }
        return samples;
    }

    NativePostSettings::NativePostSettings(const EnvironmentDefinition &environment)
    {
        Set("ColorGrading", "colorFilter", {1, 1, 1, 1});
        for (const auto *n : {"lift", "gamma", "gain"})
            Set("ColorGrading", n, {1, 1, 1, 0});
        for (const auto *n : {"mixerRedOutRedIn", "mixerGreenOutGreenIn", "mixerBlueOutBlueIn"})
            Set("ColorGrading", n, {100});
        for (const auto *n : {"hueVsHueCurve", "hueVsSatCurve", "satVsSatCurve", "lumVsSatCurve"})
            curves_[n] = std::vector<float>(128, .5f);
        for (const auto *n : {"masterCurve", "redCurve", "greenCurve", "blueCurve"})
        {
            auto &curve = curves_[n];
            for (int i = 0; i < 128; ++i)
                curve.push_back(i / 128.f);
        }
        Set("Bloom", "threshold", {1});
        Set("Bloom", "softKnee", {.5f});
        Set("Bloom", "diffusion", {7});
        Set("Bloom", "clamp", {65472});
        Set("Bloom", "color", {1, 1, 1, 1});
        Set("Vignette", "color", {0, 0, 0, 1});
        Set("Vignette", "center", {.5f, .5f});
        Set("Vignette", "smoothness", {.2f});
        Set("Vignette", "roundness", {1});
        Set("Vignette", "rounded", {0});
        Set("AmbientOcclusion", "thicknessModifier", {1});
        Set("AmbientOcclusion", "noiseFilterTolerance", {0});
        Set("AmbientOcclusion", "blurTolerance", {-4.6f});
        Set("AmbientOcclusion", "upsampleTolerance", {-12});
        Set("AmbientOcclusion", "color", {0, 0, 0, 1});
        Set("AmbientOcclusion", "ambientOnly", {1});
        Set("AmbientOcclusion", "directLightingStrength", {0});
        Set("MotionBlur", "shutterAngle", {270});
        Set("MotionBlur", "sampleCount", {10});
        Set("ScreenSpaceReflections", "preset", {1});
        Set("ScreenSpaceReflections", "thickness", {8});
        Set("ScreenSpaceReflections", "maximumIterationCount", {16});
        Set("ScreenSpaceReflections", "distanceFade", {.5f});
        Set("ScreenSpaceReflections", "maximumMarchDistance", {100});
        Set("ScreenSpaceReflections", "vignette", {.5f});
        auto profiles = environment.Profiles;
        std::stable_sort(profiles.begin(), profiles.end(),
                         [](const auto &a, const auto &b)
                         {
                             return a.Priority < b.Priority;
                         });
        for (const auto &profile : profiles)
        {
            float weight = std::clamp(profile.Weight, 0.f, 1.f);
            if (weight == 0)
                continue;
            for (const auto &effect : profile.Effects)
                if (effect.Float("active", 1) != 0)
                    for (const auto &field : effect.Children)
                        if (field.Float("overrideState") != 0)
                        {
                            const auto &value = field.Get("value");
                            if (value.Has("curve"))
                            {
                                if (effect.Name != "ColorGrading")
                                    throw std::runtime_error("Curve override in unsupported effect: " + effect.Name);
                                auto samples = SampleCurve(value);
                                auto it = curves_.find(field.Name);
                                if (it == curves_.end())
                                    throw std::runtime_error("Unknown grading curve: " + field.Name);
                                for (size_t i = 0; i < samples.size(); ++i)
                                    it->second[i] += (samples[i] - it->second[i]) * weight;
                            }
                            else if (auto numeric = Numbers(value))
                                Blend(effect.Name, field.Name, *numeric, weight);
                            else if (!value.Text.empty())
                                textures_[{effect.Name, field.Name}] = value.Text;
                        }
        }
        if (!environment.DhhEnabled)
            return;
        const auto &f = environment.DhhFloats;
        const auto &b = environment.DhhBools;
        const auto &c = environment.DhhColors;
        float mix = std::clamp(f[45], 0.f, 1.f);
        auto d = [&](const std::string &effect, const std::string &key, std::vector<float> values)
        {
            Blend(effect, key, values, mix);
        };
        const char *names[]{"DepthOfField",        "Bloom",    "AmbientOcclusion", "ColorGrading",
                            "ChromaticAberration", "Vignette", "MotionBlur",       "ScreenSpaceReflections"};
        for (size_t i = 0; i < 8; ++i)
            d(names[i], "enabled", {b[i] ? 1.f : 0.f});
        d("Bloom", "intensity", {f[3]});
        d("Bloom", "threshold", {f[4]});
        d("Bloom", "softKnee", {f[5]});
        d("Bloom", "diffusion", {f[6]});
        d("Bloom", "anamorphicRatio", {f[7]});
        d("Bloom", "color", {c[0] * .005f, c[1] * .005f, c[2] * .005f, 1});
        d("AmbientOcclusion", "intensity", {f[8]});
        d("AmbientOcclusion", "radius", {f[9]});
        d("AmbientOcclusion", "mode", {b[11] ? 1.f : 0.f});
        d("AmbientOcclusion", "quality", {4});
        d("AmbientOcclusion", "thicknessModifier", {1});
        d("AmbientOcclusion", "directLightingStrength", {1});
        d("AmbientOcclusion", "color", {c[3] * .005f, c[4] * .005f, c[5] * .005f, 1});
        d("ColorGrading", "gradingMode", {f[42] == 0 ? 1.f : 0.f});
        d("ColorGrading", "tonemapper", {f[10] == 0 ? 1.f : 2.f});
        const char *grading[]{"temperature", "tint", "postExposure", "hueShift", "saturation", "contrast"};
        for (size_t i = 0; i < 6; ++i)
            d("ColorGrading", grading[i], {f[11 + i]});
        const char *controls[]{"lift", "gamma", "gain"};
        for (size_t i = 0; i < 3; ++i)
            d("ColorGrading", controls[i], {1, 1, 1, f[17 + i] + environment.Brightness});
        for (const auto *n : {"hueVsHueCurve", "satVsSatCurve", "lumVsSatCurve"})
            for (auto &v : curves_[n])
                v += (.5f - v) * mix;
        if (f[43] > 0)
            textures_[{"ColorGrading", "ldrLut"}] = environment.LutPath;
        d("ColorGrading", "ldrLutContribution", {f[44]});
        d("ChromaticAberration", "intensity", {f[20]});
        d("Vignette", "intensity", {f[21]});
        d("Vignette", "smoothness", {f[22]});
        d("MotionBlur", "shutterAngle", {f[23]});
        d("ScreenSpaceReflections", "vignette", {f[24]});
        d("ScreenSpaceReflections", "maximumMarchDistance", {f[25]});
    }
}
