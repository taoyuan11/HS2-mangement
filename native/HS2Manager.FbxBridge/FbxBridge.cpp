// HS2Manager's C ABI bridge to the unmodified Autodesk FBX C++ SDK.
// This module does not use the Unity-specific SDK wrapper or engine assemblies.
#include <fbxsdk.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

#define API extern "C" __declspec(dllexport)
namespace {
thread_local std::string error;
struct Scene {
    FbxManager* manager=nullptr;
    FbxScene* scene=nullptr;
    std::vector<FbxNode*> bones, meshes;
    std::vector<FbxSurfacePhong*> materials;
    Scene() {
        manager=FbxManager::Create();if(!manager)throw std::runtime_error("FBX manager creation failed");
        manager->SetIOSettings(FbxIOSettings::Create(manager,IOSROOT));
        scene=FbxScene::Create(manager,"Character");
        scene->GetGlobalSettings().SetAxisSystem(FbxAxisSystem::MayaYUp);
        scene->GetGlobalSettings().SetSystemUnit(FbxSystemUnit::cm);
    }
    ~Scene(){if(manager)manager->Destroy();}
};
template<class F> int call(F f){try{error.clear();return f();}catch(const std::exception& e){error=e.what();return -1;}catch(...){error="Unknown FBX SDK exception";return -1;}}
Scene& context(void* p){if(!p)throw std::invalid_argument("Null FBX context");return *static_cast<Scene*>(p);}
template<class T>T* item(const std::vector<T*>& values,int index){if(index<0||static_cast<size_t>(index)>=values.size())throw std::out_of_range("FBX object index");return values[index];}
void finite(const float* values,int count){if(!values||count<0)throw std::invalid_argument("Invalid numeric span");for(int i=0;i<count;i++)if(!std::isfinite(values[i]))throw std::invalid_argument("Non-finite FBX data");}
// Keep the centimetre conversion in double precision, as in the established
// managed SDK call. Rounding the scaled value to float can erase tiny shape
// position deltas, which also makes the FBX writer omit their normal deltas.
FbxVector4 point(const float* p){return FbxVector4(-static_cast<double>(p[0])*10.0,static_cast<double>(p[1])*10.0,static_cast<double>(p[2])*10.0);}
FbxVector4 direction(const float* p){return FbxVector4(-p[0],p[1],p[2]);}
template<class T> void direct(T* layer){layer->SetMappingMode(FbxLayerElement::eByControlPoint);layer->SetReferenceMode(FbxLayerElement::eDirect);}
}
API const char* hs2_fbx_error(){return error.c_str();}
API void* hs2_fbx_create(){try{error.clear();return new Scene();}catch(const std::exception& e){error=e.what();return nullptr;}}
API void hs2_fbx_destroy(void* p){delete static_cast<Scene*>(p);}
API int hs2_fbx_bone(void* p,const char* name,int parent,const float* translation,const float* rotation){return call([&]{
    auto& c=context(p);finite(translation,3);finite(rotation,4);
    auto* node=FbxNode::Create(c.scene,name);(parent<0?c.scene->GetRootNode():item(c.bones,parent))->AddChild(node);
    node->SetTransformationInheritType(FbxTransform::eInheritRSrs);
    auto* skeleton=FbxSkeleton::Create(c.scene,name);skeleton->SetSkeletonType(parent<0?FbxSkeleton::eRoot:FbxSkeleton::eLimbNode);skeleton->Size.Set(.1);node->SetNodeAttribute(skeleton);
    auto t=point(translation);node->LclTranslation.Set(FbxDouble3(t[0],t[1],t[2]));
    FbxAMatrix matrix;matrix.SetQ(FbxQuaternion(rotation[0],-rotation[1],-rotation[2],rotation[3]));auto r=matrix.GetR();node->LclRotation.Set(FbxDouble3(r[0],r[1],r[2]));
    c.bones.push_back(node);return static_cast<int>(c.bones.size()-1);
});}
API int hs2_fbx_material(void* p,const char* name,const char* baseColor,const char* normal,const char* emission){return call([&]{
    auto& c=context(p);auto* m=FbxSurfacePhong::Create(c.scene,name);m->Diffuse.Set(FbxDouble3(1,1,1));m->ShadingModel.Set(FbxString("Phong"));
    auto texture=[&](const char* path,FbxProperty property){if(!path||!*path)return;auto* t=FbxFileTexture::Create(c.scene,path);t->SetFileName(path);t->SetRelativeFileName(path);t->SetTextureUse(FbxTexture::eStandard);t->SetMappingType(FbxTexture::eUV);t->UVSet.Set(FbxString("UV0"));property.ConnectSrcObject(t);};
    texture(baseColor,m->Diffuse);texture(normal,m->NormalMap);texture(emission,m->Emissive);
    c.materials.push_back(m);return static_cast<int>(c.materials.size()-1);
});}
API int hs2_fbx_mesh(void* p,const char* name,int count,const float* positions,const float* normals,const float* tangents,const float* binormals){return call([&]{
    auto& c=context(p);if(count<1||count>10000000)throw std::invalid_argument("FBX vertex count");finite(positions,count*3);finite(normals,count*3);
    auto* mesh=FbxMesh::Create(c.scene,name);mesh->InitControlPoints(count);auto* n=mesh->CreateElementNormal();direct(n);
    for(int i=0;i<count;i++){mesh->SetControlPointAt(point(positions+i*3),i);n->GetDirectArray().Add(direction(normals+i*3));}
    if(tangents&&binormals){finite(tangents,count*3);finite(binormals,count*3);auto* t=mesh->CreateElementTangent();auto* b=mesh->CreateElementBinormal();direct(t);direct(b);for(int i=0;i<count;i++){t->GetDirectArray().Add(direction(tangents+i*3));b->GetDirectArray().Add(direction(binormals+i*3));}}
    auto* node=FbxNode::Create(c.scene,name);node->SetNodeAttribute(mesh);c.scene->GetRootNode()->AddChild(node);c.meshes.push_back(node);return static_cast<int>(c.meshes.size()-1);
});}
API int hs2_fbx_uv(void* p,int meshIndex,int channel,const float* uv){return call([&]{
    auto* mesh=item(context(p).meshes,meshIndex)->GetMesh();if(channel<0||channel>7)throw std::invalid_argument("FBX UV channel");finite(uv,mesh->GetControlPointsCount()*2);
    while(mesh->GetLayerCount()<=channel)mesh->CreateLayer();auto* layer=FbxLayerElementUV::Create(mesh,("UV"+std::to_string(channel)).c_str());direct(layer);
    for(int i=0;i<mesh->GetControlPointsCount();i++)layer->GetDirectArray().Add(FbxVector2(uv[i*2],uv[i*2+1]));mesh->GetLayer(channel)->SetUVs(layer);return 0;
});}
API int hs2_fbx_colors(void* p,int meshIndex,const float* rgba){return call([&]{
    auto* mesh=item(context(p).meshes,meshIndex)->GetMesh();finite(rgba,mesh->GetControlPointsCount()*4);auto* layer=mesh->CreateElementVertexColor();direct(layer);
    for(int i=0;i<mesh->GetControlPointsCount();i++){auto* v=rgba+i*4;layer->GetDirectArray().Add(FbxColor(v[0],v[1],v[2],v[3]));}return 0;
});}
API int hs2_fbx_triangles(void* p,int meshIndex,int material,int count,const int* indices,int mirrored){return call([&]{
    auto& c=context(p);auto* node=item(c.meshes,meshIndex);auto* mesh=node->GetMesh();if(!indices||count<0||count%3)throw std::invalid_argument("FBX triangle span");
    int slot=node->AddMaterial(item(c.materials,material));auto* layer=mesh->GetElementMaterial();if(!layer){layer=mesh->CreateElementMaterial();layer->SetMappingMode(FbxLayerElement::eByPolygon);layer->SetReferenceMode(FbxLayerElement::eIndexToDirect);mesh->GetLayer(0)->SetMaterials(layer);}
    for(int i=0;i<count;i+=3){for(int k=0;k<3;k++)if(indices[i+k]<0||indices[i+k]>=mesh->GetControlPointsCount())throw std::out_of_range("FBX triangle vertex");mesh->BeginPolygon(slot,-1,-1,false);mesh->AddPolygon(indices[i]);mesh->AddPolygon(indices[i+(mirrored?1:2)]);mesh->AddPolygon(indices[i+(mirrored?2:1)]);mesh->EndPolygon();}
    // Set every polygon slot explicitly; SDK defaults differ with multiple UV layers.
    auto& slots=layer->GetIndexArray();slots.SetCount(mesh->GetPolygonCount());for(int i=mesh->GetPolygonCount()-count/3;i<mesh->GetPolygonCount();i++)slots.SetAt(i,slot);return 0;
});}
API int hs2_fbx_skin(void* p,int meshIndex,int boneIndex,int count,const int* indices,const float* weights){return call([&]{
    auto& c=context(p);auto* mesh=item(c.meshes,meshIndex)->GetMesh();auto* bone=item(c.bones,boneIndex);finite(weights,count);
    auto* skin=static_cast<FbxSkin*>(mesh->GetDeformer(0,FbxDeformer::eSkin));if(!skin){skin=FbxSkin::Create(c.scene,"Skin");mesh->AddDeformer(skin);}
    auto* cluster=FbxCluster::Create(c.scene,"Cluster");cluster->SetLink(bone);cluster->SetLinkMode(FbxCluster::eNormalize);
    for(int i=0;i<count;i++){if(indices[i]<0||indices[i]>=mesh->GetControlPointsCount()||weights[i]<0)throw std::invalid_argument("FBX skin influence");cluster->AddControlPointIndex(indices[i],weights[i]);}
    FbxAMatrix identity;identity.SetIdentity();cluster->SetTransformMatrix(identity);cluster->SetTransformLinkMatrix(bone->EvaluateGlobalTransform());skin->AddCluster(cluster);return 0;
});}
API int hs2_fbx_shape(void* p,int meshIndex,const char* name,float weight,const float* positions,const float* normals,const float* tangents){return call([&]{
    auto& c=context(p);auto* mesh=item(c.meshes,meshIndex)->GetMesh();int count=mesh->GetControlPointsCount();finite(positions,count*3);finite(normals,count*3);if(!std::isfinite(weight)||weight<=0)throw std::invalid_argument("FBX shape weight");
    auto* blend=static_cast<FbxBlendShape*>(mesh->GetDeformer(0,FbxDeformer::eBlendShape));if(!blend){blend=FbxBlendShape::Create(c.scene,"Expressions");mesh->AddDeformer(blend);}
    FbxBlendShapeChannel* channel=nullptr;for(int i=0;i<blend->GetBlendShapeChannelCount();i++)if(std::string(blend->GetBlendShapeChannel(i)->GetName())==name)channel=blend->GetBlendShapeChannel(i);
    if(!channel){channel=FbxBlendShapeChannel::Create(c.scene,name);channel->DeformPercent.Set(0);blend->AddBlendShapeChannel(channel);}
    auto* shape=FbxShape::Create(c.scene,name);shape->InitControlPoints(count);auto* n=shape->CreateElementNormal();direct(n);FbxGeometryElementTangent* t=nullptr;if(tangents){finite(tangents,count*3);t=shape->CreateElementTangent();direct(t);}
    for(int i=0;i<count;i++){shape->SetControlPointAt(point(positions+i*3),i);n->GetDirectArray().Add(direction(normals+i*3));if(t)t->GetDirectArray().Add(direction(tangents+i*3));}
    channel->AddTargetShape(shape,weight);return 0;
});}
API int hs2_fbx_write(void* p,const char* path){return call([&]{
    auto& c=context(p);auto* pose=FbxPose::Create(c.scene,"ReferencePose");pose->SetIsBindPose(true);for(auto* node:c.bones)pose->Add(node,FbxMatrix(node->EvaluateGlobalTransform()));for(auto* node:c.meshes)pose->Add(node,FbxMatrix(node->EvaluateGlobalTransform()));c.scene->AddPose(pose);
    auto* exporter=FbxExporter::Create(c.manager,"HS2Manager");int binary=c.manager->GetIOPluginRegistry()->FindWriterIDByDescription("FBX binary (*.fbx)");
    bool ok=exporter->Initialize(path,binary,c.manager->GetIOSettings())&&exporter->Export(c.scene);std::string message=exporter->GetStatus().GetErrorString();exporter->Destroy();if(!ok)throw std::runtime_error(message);return 0;
});}
struct Summary{int nodes,meshes,vertices,polygons,bones,clusters,channels,shapeFrames,materials;};
API int hs2_fbx_reopen(const char* path,Summary* output){return call([&]{
    if(!output)throw std::invalid_argument("Null FBX summary");Scene c;auto* importer=FbxImporter::Create(c.manager,"Validation");bool ok=importer->Initialize(path,-1,c.manager->GetIOSettings())&&importer->Import(c.scene);std::string message=importer->GetStatus().GetErrorString();importer->Destroy();if(!ok)throw std::runtime_error(message);*output={};
    output->materials=c.scene->GetMaterialCount();for(int i=0;i<c.scene->GetNodeCount();i++){auto* node=c.scene->GetNode(i);output->nodes++;if(node->GetSkeleton())output->bones++;auto* mesh=node->GetMesh();if(!mesh)continue;output->meshes++;output->vertices+=mesh->GetControlPointsCount();output->polygons+=mesh->GetPolygonCount();
        for(int k=0;k<mesh->GetDeformerCount(FbxDeformer::eSkin);k++)output->clusters+=static_cast<FbxSkin*>(mesh->GetDeformer(k,FbxDeformer::eSkin))->GetClusterCount();
        for(int k=0;k<mesh->GetDeformerCount(FbxDeformer::eBlendShape);k++){auto* b=static_cast<FbxBlendShape*>(mesh->GetDeformer(k,FbxDeformer::eBlendShape));output->channels+=b->GetBlendShapeChannelCount();for(int j=0;j<b->GetBlendShapeChannelCount();j++)output->shapeFrames+=b->GetBlendShapeChannel(j)->GetTargetShapeCount();}
    }return 0;
});}
