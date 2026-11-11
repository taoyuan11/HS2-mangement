#include "AssetReader.h"
#include "../HS2Manager.CppRenderingChecks/FrozenDefinition.h"
#include "Animation.h"
#include "ShapeRig.h"
#include "ZipArchive.h"
#include "../../src/HS2Manager.NativeCore/third_party/json.hpp"
#include <cmath>
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>

using namespace hs2;
namespace {
void Check(bool pass,const char* message){if(!pass)throw std::runtime_error(message);}
template<class F>void Reject(F f){bool rejected=false;try{f();}catch(const std::exception&){rejected=true;}Check(rejected,"Malformed input was accepted.");}
void Near(float a,float b,float eps=1e-5f){Check(std::isfinite(a)&&std::abs(a-b)<=eps,"Numeric mismatch.");}
void Near(Vector3 a,Vector3 b,float eps=1e-5f){Near(a.x,b.x,eps);Near(a.y,b.y,eps);Near(a.z,b.z,eps);}
void FrozenSpecialCurve(){
    auto data=fixture::Parameter(nlohmann::json::parse(R"({"Children":[{"Name":"outSlope","Encoding":"float-special","Text":"Infinity","Number":0},{"Name":"negative","Encoding":"float-special","Text":"-Infinity","Number":0},{"Name":"nan","Encoding":"float-special","Text":"NaN","Number":0}]})"));
    Check(std::isinf(data.Float("outSlope"))&&data.Float("outSlope")>0,"Frozen fixture lost positive infinity tangent.");
    Check(std::isinf(data.Float("negative"))&&data.Float("negative")<0&&std::isnan(data.Float("nan")),"Frozen fixture lost special numeric values.");
    AnimationCurve stepped({{0,3,0,data.Float("outSlope")},{.2f,.1f,0,0}});
    Near(stepped.Evaluate(.12f),3);Near(stepped.Evaluate(.2f),.1f);
}
void Compression(){
    const std::vector<uint8_t> literal{0x50,'h','e','l','l','o'};auto decoded=DecompressUnityBlock(literal,5,2);Check(std::string(decoded.begin(),decoded.end())=="hello","LZ4 literal decode mismatch.");
    const std::vector<uint8_t> repeated{0x14,'a',1,0,0x10,'b'};decoded=DecompressUnityBlock(repeated,10,3);Check(std::string(decoded.begin(),decoded.end())=="aaaaaaaaab","LZ4 overlapping match mismatch.");
    Reject([&]{DecompressUnityBlock(literal,4,2);});Reject([&]{DecompressUnityBlock(std::vector<uint8_t>{0,0,0},4,2);});Reject([&]{DecompressUnityBlock(std::vector<uint8_t>{0xf0},15,2);});
    Reject([]{UnityBundle bundle(std::vector<uint8_t>{'U','n','i','t','y','F','S',0});});
}
void Nodes(){
    NativeSceneNode root("root"),child("child"),leaf("leaf");child.SetParent(&root);leaf.SetParent(&child);root.LocalPosition({1,2,3});root.LocalScale({2,-3,4});child.LocalPosition({3,5,7});leaf.LocalPosition({.3f,.2f,.1f});
    const Vector3 point{2,3,5};Near(leaf.InverseTransformPoint(leaf.TransformPoint(point)),point,1e-4f);auto cached=leaf.WorldMatrix();root.LocalPosition({2,3,4});auto moved=leaf.WorldMatrix();Near(moved._41,cached._41+1);Near(moved._42,cached._42+1);Near(moved._43,cached._43+1);
    root.Active=false;Check(!leaf.ActiveInHierarchy(),"Inactive ancestor did not hide child.");root.Active=true;Reject([&]{root.SetParent(&leaf);});Reject([&]{root.LocalRotation({0,0,0,0});});Reject([&]{root.LocalPosition({NAN,0,0});});Check(root.Traverse().size()==3,"Hierarchy traversal mismatch.");
    root.LocalRotation({0,.38268343f,0,.92387953f});Near(leaf.InverseTransformPoint(leaf.TransformPoint(point)),point,2e-4f);
    auto matrix=leaf.WorldMatrix();Near(hs2::TransformPoint(point,matrix),leaf.TransformPoint(point),2e-4f);
}
GpuMeshData Mesh(){GpuMeshData m;m.Name="weighted triangle";m.VertexCount=3;m.Channels={{0,0,0,0,3},{1,0,12,0,3},{2,0,24,0,4},{12,0,40,0,4},{13,0,56,0,4}};m.StreamOffsets={0};m.StreamStrides={72};m.VertexBytes.resize(216);for(int i=0;i<3;++i){float v[18]{float(i),0,0,0,1,0,1,0,0,-1,1,0,0,0,0,0,0,0};std::memcpy(m.VertexBytes.data()+i*72,v,72);}m.IndexBytes={0,0,1,0,2,0};m.Submeshes={{0,3,0,0}};m.BindPoses={IdentityMatrix()};m.BlendShapes={{"smile",1,{{100,{1},{{0,1,0}},{{0,0,1}},{{0,1,0}}}}}};return m;}
void Geometry(){auto mesh=Mesh();mesh.Validate();auto transform=IdentityMatrix();transform._41=5;auto result=DeformMesh(mesh,std::vector<float>{50},std::vector<Matrix4x4>{transform});Near(result.Positions[1],{6,.5f,0});Near(result.Normals[1],{0,1,.5f});Near(result.Tangents[1].w,-1);Near(result.Positions[0],{5,0,0});
    Reject([&]{DeformMesh(mesh,std::vector<float>{NAN});});auto bad=mesh;bad.IndexBytes[4]=3;Reject([&]{bad.Validate();});bad=mesh;bad.VertexBytes.pop_back();Reject([&]{bad.Validate();});
    NativeSceneNode root("root"),child("child");child.SetParent(&root);child.LocalPosition({1,0,0});root.LocalPosition({2,0,0});std::vector<NativeSceneNode*> bones{&child};auto skin=BuildSkinMatrices(bones,mesh.BindPoses,IdentityMatrix());Near(skin[0]._41,3);
    NativeSceneRenderer cpu;cpu.Node=&root;cpu.RootBone=&child;cpu.Mesh=std::make_shared<GpuMeshData>(mesh);cpu.Source.Skinned=true;cpu.Bones={&child};cpu.BlendWeights={0};root.LocalScale({2,3,4});auto local=cpu.DeformAnimation();Near(local.Positions[1],{2,0,0});Near(root.TransformPoint(local.Positions[1]),child.TransformPoint({1,0,0}));Near(local.Normals[1],{0,9,0});root.LocalScale({1,1,1});
    auto box=NativeRendererBounds{{0,0,.5f},{.1f,.1f,.1f}};Check(box.Intersects(IdentityMatrix()),"Visible bounds were culled.");box.Center={4,0,.5f};Check(!box.Intersects(IdentityMatrix()),"Outside bounds were accepted.");
    NativeSceneRenderer renderer;renderer.Node=&root;renderer.RootBone=&child;renderer.Mesh=std::make_shared<GpuMeshData>(mesh);renderer.Source.Skinned=true;Parameter update;update.Name="m_UpdateWhenOffscreen";update.Number=1;renderer.Source.Settings.Children.push_back(update);renderer.Bones={&child};renderer.BlendWeights={100};auto posed=renderer.Bounds();Near(posed.Center,{4,0,0});Near(posed.Extents,{1,0,0});renderer.BlendWeights={0};auto unblended=renderer.Bounds();Near(unblended.Center,posed.Center);child.LocalPosition({4,1,2});posed=renderer.Bounds();Near(posed.Center,{7,1,2});Near(posed.Extents,{1,0,0});renderer.Source.Skinned=false;posed=renderer.Bounds();Near(posed.Center,{3,0,0});Near(posed.Extents,{1,0,0});
}
void CurveReference(const std::filesystem::path& fixture){std::ifstream input(fixture);auto json=nlohmann::json::parse(input);auto& curves=json["Input"]["Curves"];auto& expected=json["Expected"]["Curves"];size_t samples=0;for(size_t i=0;i<curves.size();++i){auto& row=curves[i];std::vector<CurveKey> keys;for(auto& k:row["Keys"])keys.push_back({k["Time"],k["Value"],k["In"],k["Out"],k["InWeight"],k["OutWeight"],k["Weighted"]});AnimationCurve curve(std::move(keys));auto wrap=[](int v){return v==2?CurveWrap::Loop:v==4?CurveWrap::PingPong:CurveWrap::Clamp;};curve.Pre=wrap(row["Pre"]);curve.Post=wrap(row["Post"]);for(size_t j=0;j<row["Times"].size();++j){float actual=curve.Evaluate(row["Times"][j]);float reference=expected[i]["Values"][j];if(actual!=reference)throw std::runtime_error("Independent curve reference differs: "+std::to_string(i)+"/"+std::to_string(j));++samples;}}std::cout<<"curve reference samples="<<samples<<'\n';}
void RealResources(const std::filesystem::path& game,const std::filesystem::path& tpk){
    UnityClassDatabase database(tpk);auto types=database.Type(43,"2018.2.21f1");Check(!types.empty()&&types[0].Type=="Mesh","TPK mesh schema mismatch.");
    auto path=game/"abdata/chara/oo_base.unity3d";NativeGpuAssetReader reader(path,&database);Check(!reader.AssetIndex().empty(),"No real resource objects.");size_t read=0,meshes=0,textures=0,materials=0;
    for(const auto& asset:reader.AssetIndex()){
        if(asset.ClassId==43){auto mesh=reader.Mesh(asset.PathId);mesh.Validate();++meshes;}
        else if(asset.ClassId==28||asset.ClassId==89){auto texture=reader.Texture(asset.PathId);Check(!texture.Bytes.empty(),"Texture payload empty.");++textures;}
        else if(asset.ClassId==21){reader.Material(asset.PathId);++materials;}
        else reader.Metadata(asset.PathId);++read;
    }
    std::cout<<"real assets="<<read<<" meshes="<<meshes<<" textures="<<textures<<" materials="<<materials<<'\n';
    NativeGpuAssetReader shape(game/"abdata/list/customshape.unity3d",&database);auto keys=shape.ReadText("cf_anmShapeBody"),categories=shape.ReadText("cf_custombody");NativeShapeRig rig(game);auto prefab=reader.Prefab("p_cf_anim");std::unordered_map<std::string,BonePose> bones;for(auto& node:prefab.Nodes)bones.emplace(node.Name,BonePose{node.Position,node.Scale,node.Rotation});std::vector<float> values(64,.5f);rig.Apply(bones,"ShapeBodyInfoFemale",keys,std::string_view(reinterpret_cast<const char*>(categories.data()),categories.size()),values);Check(!bones.empty(),"Shape rig has no bones.");std::cout<<"body shape formula applied to "<<bones.size()<<" bones\n";
    auto first=std::filesystem::recursive_directory_iterator(game/"mods");for(auto& file:first)if(file.is_regular_file()&&file.path().extension()==".zipmod"){ZipArchive zip(file.path());auto* manifest=zip.Find("manifest.xml");if(!manifest)continue;auto bytes=zip.Read(*manifest,2*1024*1024);Check(!bytes.empty(),"Empty mod manifest.");std::cout<<"zipmod CRC verified, entries="<<zip.Entries().size()<<'\n';break;}
}
}
int wmain(int argc,wchar_t** argv){try{FrozenSpecialCurve();Compression();Nodes();Geometry();CurveReference(std::filesystem::path(__FILE__).parent_path()/"../HS2Manager.NativeChecks/Fixtures/native-curve-reference.json");if(argc==3)RealResources(argv[1],argv[2]);std::cout<<"Native scene checks passed\n";return 0;}catch(const std::exception& ex){std::cerr<<ex.what()<<'\n';return 1;}}
