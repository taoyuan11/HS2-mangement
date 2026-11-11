#pragma once
#include "../HS2Manager.NativeScene/EnvironmentDefinition.h"
#include <map>

namespace hs2 {
class NativePostSettings {
public:
    explicit NativePostSettings(const EnvironmentDefinition& environment);
    void UseStaticReference(){Set("MotionBlur","enabled",{0});}
    float Scalar(const std::string& effect,const std::string& key,float fallback=0)const;
    std::vector<float> Value(const std::string& effect,const std::string& key,std::vector<float> fallback={})const;
    bool Enabled(const std::string& effect)const{return Scalar(effect,"enabled")!=0;}
    std::string TexturePath(const std::string& effect,const std::string& key)const;
    const std::unordered_map<std::string,std::vector<float>>& GradingCurves()const{return curves_;}
    const std::unordered_map<std::string,std::unordered_map<std::string,std::vector<float>>>& Snapshot()const{return effects_;}
    std::vector<std::string> EnabledEffects()const;
    void ValidateCoverage()const;
private:
    std::unordered_map<std::string,std::unordered_map<std::string,std::vector<float>>> effects_;
    std::unordered_map<std::string,std::vector<float>> curves_;
    std::map<std::pair<std::string,std::string>,std::string> textures_;
    void Set(const std::string& effect,const std::string& key,std::vector<float> values){effects_[effect][key]=std::move(values);}
    void Blend(const std::string& effect,const std::string& key,const std::vector<float>& values,float weight);
    static std::optional<std::vector<float>> Numbers(const Parameter& value);
    static std::vector<float> SampleCurve(const Parameter& spline);
};
}
