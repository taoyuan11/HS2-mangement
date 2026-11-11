#include "Animation.h"
#include <algorithm>
#include <bit>
#include <cmath>
#include <functional>
#include <set>
#include <stdexcept>

namespace hs2 {
namespace {
void Require(bool value,const char* error){if(!value)throw std::runtime_error(error);}
int Count(float value,int maximum=100000){Require(std::isfinite(value)&&value>=0&&value<=maximum&&std::floor(value)==value,"Invalid animation count.");return static_cast<int>(value);}
std::uint32_t U32(const Parameter& p){Require(std::isfinite(p.Number)&&p.Number>=0&&p.Number<=UINT32_MAX&&std::floor(p.Number)==p.Number,"Invalid animation unsigned integer.");return static_cast<uint32_t>(p.Number);}
Quaternion Normalize(Quaternion q){float x=q.x*q.x,y=q.y*q.y,z=q.z*q.z,w=q.w*q.w;Require(std::isfinite(x+y+z+w)&&x+y+z+w>=1e-20f,"Invalid animated rotation.");float xz=std::sqrt((x+y)+(z+w)),yw=std::sqrt((y+z)+(w+x));return {q.x/xz,q.y/yw,q.z/xz,q.w/yw};}
float Bezier(float a,float b,float c,float d,float t){float s=1-t;return s*s*s*a+3*s*s*t*b+3*s*t*t*c+t*t*t*d;}
}
NativeAnimationClip::NativeAnimationClip(const Parameter& data){
    Name=data.String("m_Name");for(auto key:{"m_RotationCurves","m_CompressedRotationCurves","m_EulerCurves","m_PositionCurves","m_ScaleCurves","m_FloatCurves","m_PPtrCurves"})Require(data.Get(key).Array().empty(),"Legacy animation curves require their own evaluator.");
    auto& muscle=data.Get("m_MuscleClip");auto& clip=muscle.Get("m_Clip").Get("data");Start=muscle.Float("m_StartTime");Duration=muscle.Float("m_StopTime")-Start;Require(std::isfinite(Start)&&std::isfinite(Duration)&&Duration>0,"Invalid animation duration.");
    auto& stream=clip.Get("m_StreamedClip");auto count=Count(stream.Float("curveCount"));streamed_.resize(count);std::vector<uint32_t> words;for(auto& p:stream.Get("data").Array())words.push_back(U32(p));size_t cursor=0;float previous=-INFINITY;
    while(cursor<words.size()){
        Require(words.size()-cursor>=2,"Animation stream frame is truncated.");float time=std::bit_cast<float>(words[cursor++]);auto keys=words[cursor++];if(time==INFINITY&&keys==0&&cursor==words.size())break;Require(std::isfinite(time)&&time>=previous&&keys<=static_cast<unsigned>(count)&&words.size()-cursor>=uint64_t(keys)*5,"Animation stream frame is invalid.");previous=time;std::set<uint32_t> seen;
        auto read=[&](){float v=std::bit_cast<float>(words[cursor++]);Require(std::isfinite(v),"Non-finite animation coefficient.");return v;};
        for(uint32_t i=0;i<keys;++i){auto index=words[cursor++];Require(index<static_cast<unsigned>(count)&&seen.insert(index).second,"Animation curve index is invalid.");auto a=read(),b=read(),c=read(),d=read();streamed_[index].push_back({time,a,b,c,d});}
    }
    for(auto& s:streamed_)Require(!s.empty(),"Animation stream has an empty curve.");auto& dense=clip.Get("m_DenseClip");denseCount_=Count(dense.Float("m_CurveCount"));denseFrames_=Count(dense.Float("m_FrameCount"),10000000);denseRate_=dense.Float("m_SampleRate");denseStart_=dense.Float("m_BeginTime");
    for(auto& p:dense.Get("m_SampleArray").Array()){float v=static_cast<float>(p.Number);Require(std::isfinite(v),"Non-finite animation sample.");dense_.push_back(v);}Require(uint64_t(denseCount_)*denseFrames_==dense_.size()&&(!denseCount_||(denseFrames_>0&&std::isfinite(denseRate_)&&denseRate_>0&&std::isfinite(denseStart_))),"Invalid dense animation samples.");
    for(auto& p:clip.Get("m_ConstantClip").Get("data").Array()){float v=static_cast<float>(p.Number);Require(std::isfinite(v),"Non-finite animation constant.");constants_.push_back(v);}
    int offset=0;for(auto& p:data.Get("m_ClipBindingConstant").Get("genericBindings").Array()){
        NativeAnimationBinding b;b.PathHash=U32(p.Get("path"));b.Attribute=U32(p.Get("attribute"));b.ClassId=static_cast<int>(p.Int("typeID"));b.CustomType=static_cast<int>(p.Int("customType"));b.Offset=offset;b.Components=1;
        if(b.ClassId==4){Require(b.Attribute>=1&&b.Attribute<=4,"Unknown transform animation attribute.");b.Components=b.Attribute==2?4:3;}Require(p.Float("isPPtrCurve")==0,"Object-reference animation requires explicit material/node binding.");offset+=b.Components;Bindings.push_back(b);
    }
    Require(constants_.size()<=INT_MAX-count-denseCount_,"Animation curve count exceeds limit.");CurveCount=count+denseCount_+static_cast<int>(constants_.size());Require(offset==CurveCount,"Animation bindings do not account for all curves.");
}
std::vector<float> NativeAnimationClip::Evaluate(float time,bool loop) const {
    Require(std::isfinite(time),"Non-finite animation time.");time=Start+(loop?std::clamp(time-std::floor(time/Duration)*Duration,0.f,Duration):std::clamp(time,0.f,Duration));std::vector<float> values(CurveCount);
    for(size_t curve=0;curve<streamed_.size();++curve){auto& keys=streamed_[curve];size_t low=0,high=keys.size();while(low+1<high){size_t mid=(low+high)/2;if(keys[mid].Time<=time)low=mid;else high=mid;}auto& key=keys[low];float dt=std::max(0.f,time-key.Time);values[curve]=((key.A*dt+key.B)*dt+key.C)*dt+key.D;}
    if(denseCount_){float frame=std::clamp((time-denseStart_)*denseRate_,0.f,float(denseFrames_-1));int first=static_cast<int>(frame),second=std::min(first+1,denseFrames_-1);float t=frame-first;for(int curve=0;curve<denseCount_;++curve){float a=dense_[first*denseCount_+curve],b=dense_[second*denseCount_+curve];values[streamed_.size()+curve]=a+(b-a)*t;}}
    std::copy(constants_.begin(),constants_.end(),values.begin()+streamed_.size()+denseCount_);return values;
}
std::uint32_t NativeAnimationClip::PathHash(std::string_view path){uint32_t crc=UINT32_MAX;for(unsigned char c:path){crc^=c;for(int bit=0;bit<8;++bit)crc=(crc>>1)^((crc&1)?0xedb88320u:0);}return ~crc;}
Quaternion EulerRotation(Vector3 degrees){Quaternion q;DirectX::XMStoreFloat4(&q,DirectX::XMQuaternionRotationRollPitchYaw(degrees.x*DirectX::XM_PI/180,degrees.y*DirectX::XM_PI/180,degrees.z*DirectX::XM_PI/180));return q;}
NativeAnimationPlayer::NativeAnimationPlayer(std::shared_ptr<const NativeAnimationClip> clip,NativeSceneNode& root,std::span<NativeSceneNode* const> nodes,std::span<NativeSceneRenderer* const> renderers):clip_(std::move(clip)){
    Require(clip_!=nullptr,"Animation clip is null.");std::unordered_map<uint32_t,NativeSceneNode*> paths;std::set<uint32_t> required,unbound;std::set<NativeSceneNode*> allowed(nodes.begin(),nodes.end());for(auto& b:clip_->Bindings)if(b.ClassId==4||b.ClassId==137)required.insert(b.PathHash);
    std::function<void(NativeSceneNode*,std::string)> walk=[&](auto* n,auto path){if(!allowed.empty()&&!allowed.contains(n))return;auto hash=NativeAnimationClip::PathHash(path);Require(paths.emplace(hash,n).second||!required.contains(hash),"Animation path collision in an active binding.");for(auto* child:n->Children())walk(child,path.empty()?child->Name:path+"/"+child->Name);};walk(&root,{});
    for(auto& binding:clip_->Bindings){if(binding.ClassId==95&&binding.CustomType==8&&binding.Attribute>=7&&binding.Attribute<=13)continue;auto found=paths.find(binding.PathHash);
        if(binding.ClassId==137&&!renderers.empty()){if(found==paths.end()){unbound.insert(binding.PathHash);continue;}NativeSceneRenderer* renderer=nullptr;for(auto* r:renderers)if(r->Node==found->second){Require(renderer==nullptr,"Animated renderer binding is ambiguous.");renderer=r;}Require(renderer&&renderer->Mesh,"Animated skinned renderer is missing.");auto& shapes=renderer->Mesh->BlendShapes;auto shape=std::find_if(shapes.begin(),shapes.end(),[&](auto& s){return s.Hash==binding.Attribute;});Require(shape!=shapes.end(),"Blend-shape animation binding is unresolved.");morphs_.push_back({binding.Offset,renderer,static_cast<size_t>(shape-shapes.begin())});continue;}
        Require(binding.ClassId==4,"Animation component binding requires an evaluator.");if(found!=paths.end())tracks_.push_back({binding,found->second});else unbound.insert(binding.PathHash);
    }UnboundPaths.assign(unbound.begin(),unbound.end());
}
void NativeAnimationPlayer::Sample(float time,bool loop){auto values=clip_->Evaluate(time,loop);for(auto& m:morphs_){Require(m.Shape<m.Renderer->BlendWeights.size(),"Animated blend weight is missing.");m.Renderer->BlendWeights[m.Shape]=values[m.Offset];}for(auto& t:tracks_){auto& b=t.Binding;auto i=b.Offset;switch(b.Attribute){case 1:t.Node->LocalPosition({values[i],values[i+1],values[i+2]});break;case 2:t.Node->LocalRotation(Normalize({values[i],values[i+1],values[i+2],values[i+3]}));break;case 3:t.Node->LocalScale({values[i],values[i+1],values[i+2]});break;case 4:t.Node->LocalRotation(EulerRotation({values[i],values[i+1],values[i+2]}));break;}}for(auto& b:clip_->Bindings)if(b.ClassId==95)RootMotion[b.Attribute]=values[b.Offset];}
AnimationCurve::AnimationCurve(std::vector<CurveKey> keys):keys_(std::move(keys)){for(size_t i=0;i<keys_.size();++i){auto& k=keys_[i];Require(std::isfinite(k.Time)&&std::isfinite(k.Value)&&!std::isnan(k.InSlope)&&!std::isnan(k.OutSlope)&&std::isfinite(k.InWeight)&&std::isfinite(k.OutWeight)&&k.InWeight>=0&&k.OutWeight>=0&&(i==0||k.Time>=keys_[i-1].Time),"Invalid animation curve key.");}}
float AnimationCurve::Evaluate(float time) const {
    Require(std::isfinite(time),"Invalid curve sample time.");if(keys_.empty())return 0;if(keys_.size()==1)return keys_[0].Value;float start=keys_[0].Time,end=keys_.back().Time,duration=end-start;if(duration<=0)return keys_.back().Value;
    if(time<start||time>=end){auto wrap=time<start?Pre:Post;if(wrap==CurveWrap::Clamp)return time<start?keys_[0].Value:keys_.back().Value;float period=wrap==CurveWrap::PingPong?duration*2:duration;float offset=std::fmod(std::fmod(time-start,period)+period,period);time=start+(offset>duration?period-offset:offset);}
    size_t hi=1;while(hi<keys_.size()-1&&keys_[hi].Time<=time)++hi;auto& a=keys_[hi-1];auto& b=keys_[hi];float dt=b.Time-a.Time;if(dt<=0)return b.Value;if(std::isinf(a.OutSlope)||std::isinf(b.InSlope))return time<b.Time?a.Value:b.Value;float t=std::clamp((time-a.Time)/dt,0.f,1.f);
    if(!(a.Weighted&2)&&!(b.Weighted&1)){dt=std::max(.0001f,dt);float difference=b.Value-a.Value;float incoming=dt*b.InSlope,outgoing=dt*a.OutSlope,inverseSquared=1/(dt*dt);float cubic=(((incoming+outgoing)-difference)-difference)*inverseSquared/dt;float quadratic=((((difference+difference)+difference)-outgoing)-outgoing-incoming)*inverseSquared;float elapsed=time-a.Time;return ((elapsed*cubic+quadratic)*elapsed+a.OutSlope)*elapsed+a.Value;}
    float aw=(a.Weighted&2)?a.OutWeight:1.f/3,bw=(b.Weighted&1)?b.InWeight:1.f/3,low=0,high=1,u=t;for(int i=0;i<24;++i){float x=Bezier(0,aw,1-bw,1,u);if(x<t)low=u;else high=u;u=(low+high)*.5f;}return Bezier(a.Value,a.Value+a.OutSlope*dt*aw,b.Value-b.InSlope*dt*bw,b.Value,u);
}
IndependentBlink::IndependentBlink(int seed){state_[0]=static_cast<uint32_t>(seed);for(size_t i=1;i<4;++i)state_[i]=1812433253u*state_[i-1]+1;}
uint32_t IndependentBlink::Next(){auto mixed=state_[0]^(state_[0]<<11),last=state_[3];state_[0]=state_[1];state_[1]=state_[2];state_[2]=state_[3];return state_[3]=last^(last>>19)^mixed^(mixed>>8);}
float IndependentBlink::Duration(){return .15f+.05f*(1-(Next()&0x7fffff)/8388607.f);}
float IndependentBlink::Evaluate(float time){Require(std::isfinite(time)&&time>=0,"Invalid blink sample time.");float remaining=std::max(0.f,deadline_-time);open_=phase_==0?1:std::clamp(phase_==1?remaining/duration_:1-remaining/duration_,0.f,1.f);if(time<=deadline_)return open_;if(phase_==0){duration_=Duration();deadline_=time+duration_;closedFrames_=Next()%3+1;phase_=1;}else if(phase_==1){if(--closedFrames_<=0){duration_=Duration();deadline_=time+duration_;phase_=-1;}}else{float frequency=std::clamp(float(Next()%30)/30,0.f,1.f);deadline_=static_cast<float>(double(time)+double(.2f)*(30*frequency));phase_=0;}return open_;}
__declspec(noinline) float FixedTime(int frame){volatile float divisor=30.f;return static_cast<float>(frame)/divisor;}
}
