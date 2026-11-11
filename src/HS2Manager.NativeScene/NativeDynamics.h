#pragma once
#include "CharacterDefinition.h"
#include "Animation.h"
#include <set>

namespace hs2 {
struct NativeDynamicCollider {
    NativeSceneNode* Node=nullptr;
    Vector3 Center{};
    float Radius=0,Height=0;
    int Axis=0,Bound=0;
    void Constrain(Vector3& position,float particleRadius,bool jobSphere=false)const;
};
class NativeBoneChain {
public:
    NativeBoneChain(NativeSceneNode& host,NativeSceneNode& root,Parameter settings,
        std::vector<NativeDynamicCollider> colliders,
        std::optional<std::vector<NativeSceneRenderer*>> visibility={},
        const std::unordered_map<NativeSceneNode*,Matrix4x4>* setupTransforms=nullptr,
        const std::unordered_map<NativeSceneNode*,Quaternion>* setupRotations=nullptr);
    void Restore();void Reset();void RefreshBoundOrientation();void RefreshGravityOrientation();void UpdateVisibility();void Step();
private:
    struct Particle {NativeSceneNode* Bone=nullptr;int Parent=-1;Vector3 Position{},Previous{},Local{},End{},RestPosition{},SetupPosition{};Quaternion RestRotation{0,0,0,1};Matrix4x4 Matrix{};float Distance=0,Length=0,Damping=0,Elasticity=0,Stiffness=0,Inert=0,Radius=0;};
    NativeSceneNode *host_,*root_;Parameter settings_;std::vector<Particle> particles_;std::vector<NativeDynamicCollider> colliders_;std::optional<std::vector<NativeSceneRenderer*>> visibility_;std::set<std::string> exclusions_,notRolls_;Vector3 gravity_{},force_{},localGravity_{},previousHost_{};float totalLength_=0;bool visible_=true,justEnabled_=false;
    const std::unordered_map<NativeSceneNode*,Matrix4x4>* setupTransforms_;
    const std::unordered_map<NativeSceneNode*,Quaternion>* setupRotations_;
    void Add(NativeSceneNode* bone,int parent,float distance);float Value(const std::string& name,float t,bool clamp=true)const;
    Matrix4x4 SetupMatrix(NativeSceneNode* node)const;Quaternion SetupRotation(NativeSceneNode* node)const;Vector3 SetupPosition(NativeSceneNode* node)const;
};
class NativeBodyDynamics {
public:
    NativeBodyDynamics(const Parameter& config,const Parameter& body,const std::unordered_map<std::string,NativeSceneNode*>& bones,const std::vector<NativeDynamicCollider>& available,const std::optional<ColliderSupportDefinition>& support);
    void Restore();void Reset();void Step(float delta);
private:
    struct Node {NativeSceneNode *Bone=nullptr,*Reference=nullptr;Parameter Data;Vector3 Position{},Previous{},Local{},InitialScale{};float Damping=0,Elasticity=0,Stiffness=0,Inert=0,NextLength=0,Radius=0;};
    std::vector<Node> nodes_;NativeSceneNode* root_;Vector3 gravity_{},force_{},previousRoot_{};std::vector<NativeDynamicCollider> colliders_;float rate_=60,reflect_=1,accumulator_=0;int maxSteps_=3;
};
}
