// 通过 C ABI 连接未经修改的 Autodesk FBX C++ SDK，不依赖 Unity 专用封装或引擎程序集。
#include <fbxsdk.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include <map>
#include <set>
#include <functional>
#include "../../src/HS2Manager.NativeCore/third_party/json.hpp"

#define API extern "C" __declspec(dllexport)

namespace
{
    thread_local std::string error;

    struct Scene
    {
        FbxManager *manager = nullptr;
        FbxScene *scene = nullptr;
        std::vector<FbxNode *> bones, meshes;
        std::vector<FbxSurfacePhong *> materials;

        Scene()
        {
            manager = FbxManager::Create();
            if (!manager)
                throw std::runtime_error("FBX manager creation failed");
            manager->SetIOSettings(FbxIOSettings::Create(manager, IOSROOT));
            scene = FbxScene::Create(manager, "Character");
            scene->GetGlobalSettings().SetAxisSystem(FbxAxisSystem::MayaYUp);
            scene->GetGlobalSettings().SetSystemUnit(FbxSystemUnit::cm);
        }

        ~Scene()
        {
            if (manager)
                manager->Destroy();
        }
    };

    template <class F> int call(F f)
    {
        try
        {
            error.clear();
            return f();
        }
        catch (const std::exception &e)
        {
            error = e.what();
            return -1;
        }
        catch (...)
        {
            error = "Unknown FBX SDK exception";
            return -1;
        }
    }

    Scene &context(void *p)
    {
        if (!p)
            throw std::invalid_argument("Null FBX context");
        return *static_cast<Scene *>(p);
    }

    template <class T> T *item(const std::vector<T *> &values, int index)
    {
        if (index < 0 || static_cast<size_t>(index) >= values.size())
            throw std::out_of_range("FBX object index");
        return values[index];
    }

    void finite(const float *values, int count)
    {
        if (!values || count < 0)
            throw std::invalid_argument("Invalid numeric span");
        for (int i = 0; i < count; i++)
            if (!std::isfinite(values[i]))
                throw std::invalid_argument("Non-finite FBX data");
    }

    // 沿用原有 SDK 调用的双精度厘米换算。
    // 缩放后转为单精度会抹去微小的形变位置差量，导致 FBX 写入器同时忽略对应的法线差量。
    FbxVector4 point(const float *p)
    {
        return FbxVector4(-static_cast<double>(p[0]) * 10.0, static_cast<double>(p[1]) * 10.0,
                          static_cast<double>(p[2]) * 10.0);
    }

    FbxVector4 direction(const float *p)
    {
        return FbxVector4(-p[0], p[1], p[2]);
    }

    template <class T> void direct(T *layer)
    {
        layer->SetMappingMode(FbxLayerElement::eByControlPoint);
        layer->SetReferenceMode(FbxLayerElement::eDirect);
    }
}

API const char *hs2_fbx_error()
{
    return error.c_str();
}

// The caller copies this thread-local UTF-8 result before the next import. All geometry is in HS2 units.
API const char *hs2_fbx_import_json(const char *path)
{
    thread_local std::string output;
    int status = call(
        [&]()
        {
            if (!path)
                throw std::invalid_argument("Missing model path");
            Scene context;
            auto *importer = FbxImporter::Create(context.manager, "NativeAssetImport");
            bool ok =
                importer->Initialize(path, -1, context.manager->GetIOSettings()) && importer->Import(context.scene);
            std::string message = importer->GetStatus().GetErrorString();
            importer->Destroy();
            if (!ok)
                throw std::runtime_error(message);
            std::string extension = path;
            std::transform(extension.begin(), extension.end(), extension.begin(),
                           [](unsigned char c)
                           {
                               return static_cast<char>(std::tolower(c));
                           });
            if (extension.size() >= 4 && extension.substr(extension.size() - 4) == ".obj")
            {
                context.scene->GetGlobalSettings().SetAxisSystem(FbxAxisSystem::MayaYUp);
                context.scene->GetGlobalSettings().SetSystemUnit(FbxSystemUnit::m);
            }
            FbxAxisSystem::MayaYUp.ConvertScene(context.scene);
            FbxSystemUnit::cm.ConvertScene(context.scene);
            FbxGeometryConverter converter(context.manager);
            if (!converter.Triangulate(context.scene, true))
                throw std::runtime_error("Model triangulation failed");
            using Json = nlohmann::json;
            Json result = {{"meshes", Json::array()}, {"bones", Json::array()}};
            auto point = [](FbxVector4 p)
            {
                return Json::array({-p[0] / 10., p[1] / 10., p[2] / 10.});
            };
            auto vector = [](FbxVector4 p)
            {
                return Json::array({-p[0], p[1], p[2]});
            };
            std::map<FbxNode *, std::string> boneNames;
            std::set<std::string> uniqueBones;
            std::function<void(FbxNode *)> bone = [&](FbxNode *node)
            {
                if (!node || node == context.scene->GetRootNode() || boneNames.count(node))
                    return;
                bone(node->GetParent());
                std::string name = node->GetName();
                if (name.empty() || !uniqueBones.insert(name).second)
                    throw std::runtime_error("Imported skeleton has duplicate bone names");
                boneNames[node] = name;
                auto matrix = node->EvaluateGlobalTransform();
                auto q = matrix.GetQ();
                auto origin = matrix.MultT(FbxVector4(0, 0, 0));
                Json bindWorld = Json::array();
                for (int axis = 0; axis < 3; ++axis)
                {
                    FbxVector4 basis(0, 0, 0);
                    basis[axis] = axis == 0 ? -10. : 10.;
                    auto delta = matrix.MultT(basis) - origin;
                    bindWorld.push_back(-delta[0] / 10.);
                    bindWorld.push_back(delta[1] / 10.);
                    bindWorld.push_back(delta[2] / 10.);
                    bindWorld.push_back(0.);
                }
                for (auto value : point(origin))
                    bindWorld.push_back(value);
                bindWorld.push_back(1.);
                result["bones"].push_back(
                    {{"name", name},
                     {"parent", boneNames.count(node->GetParent()) ? boneNames.at(node->GetParent()) : ""},
                     {"position", point(matrix.GetT())},
                     {"rotation", Json::array({q[0], -q[1], -q[2], q[3]})},
                     {"matrix", bindWorld},
                     {"added", true}});
            };
            std::size_t totalVertices = 0, totalTriangles = 0;
            std::function<void(FbxNode *)> visit = [&](FbxNode *node)
            {
                if (auto *mesh = node->GetMesh())
                {
                    auto count = mesh->GetControlPointsCount();
                    if (count < 0 || count > 1000000)
                        throw std::runtime_error("Imported vertex limit exceeded");
                    totalTriangles += mesh->GetPolygonCount();
                    if (totalTriangles > 250000)
                        throw std::runtime_error("Imported triangle limit exceeded");
                    std::vector<std::vector<std::pair<std::string, double>>> weights(count);
                    for (int skinIndex = 0; skinIndex < mesh->GetDeformerCount(FbxDeformer::eSkin); ++skinIndex)
                    {
                        auto *skin = static_cast<FbxSkin *>(mesh->GetDeformer(skinIndex, FbxDeformer::eSkin));
                        for (int clusterIndex = 0; clusterIndex < skin->GetClusterCount(); ++clusterIndex)
                        {
                            auto *cluster = skin->GetCluster(clusterIndex);
                            auto *link = cluster->GetLink();
                            if (!link)
                                throw std::runtime_error("Imported skin has no bone");
                            bone(link);
                            for (int i = 0; i < cluster->GetControlPointIndicesCount(); ++i)
                            {
                                int cp = cluster->GetControlPointIndices()[i];
                                double w = cluster->GetControlPointWeights()[i];
                                if (cp < 0 || cp >= count || !std::isfinite(w) || w < 0)
                                    throw std::runtime_error("Invalid imported skin weight");
                                weights[cp].emplace_back(boneNames.at(link), w);
                            }
                        }
                    }
                    FbxAMatrix geometry;
                    geometry.SetTRS(node->GetGeometricTranslation(FbxNode::eSourcePivot),
                                    node->GetGeometricRotation(FbxNode::eSourcePivot),
                                    node->GetGeometricScaling(FbxNode::eSourcePivot));
                    auto world = node->EvaluateGlobalTransform() * geometry, normal = world.Inverse().Transpose();
                    FbxStringList uvNames;
                    mesh->GetUVSetNames(uvNames);
                    if (!uvNames.GetCount())
                        throw std::runtime_error("Imported mesh has no UVs");
                    Json m = {{"name", std::string(node->GetName()) + "_" + std::to_string(result["meshes"].size())},
                              {"positions", Json::array()},
                              {"normals", Json::array()},
                              {"uv", Json::array()},
                              {"uv1", Json::array()},
                              {"indices", Json::array()},
                              {"weights", Json::array()},
                              {"materials", Json::array()},
                              {"shapes", Json::array()}};
                    std::vector<int> controlPoints;
                    bool normals = true;
                    auto *materials = mesh->GetElementMaterial();
                    for (int polygon = 0; polygon < mesh->GetPolygonCount(); ++polygon)
                    {
                        if (mesh->GetPolygonSize(polygon) != 3)
                            throw std::runtime_error("Non-triangle after model triangulation");
                        int first = static_cast<int>(m["positions"].size());
                        for (int corner = 0; corner < 3; ++corner)
                        {
                            int cp = mesh->GetPolygonVertex(polygon, corner);
                            controlPoints.push_back(cp);
                            if (cp < 0 || cp >= count)
                                throw std::runtime_error("Invalid imported mesh index");
                            auto p = world.MultT(mesh->GetControlPointAt(cp));
                            m["positions"].push_back(point(p));
                            FbxVector4 n;
                            if (!mesh->GetPolygonVertexNormal(polygon, corner, n))
                                normals = false;
                            n[3] = 0;
                            n = normal.MultT(n);
                            n.Normalize();
                            m["normals"].push_back(vector(n));
                            for (int channel = 0; channel < std::min(2, uvNames.GetCount()); ++channel)
                            {
                                FbxVector2 uv;
                                bool unmapped = false;
                                if (!mesh->GetPolygonVertexUV(polygon, corner, uvNames.GetStringAt(channel), uv,
                                                              unmapped) ||
                                    unmapped)
                                    throw std::runtime_error("Imported UV is unmapped");
                                m[channel ? "uv1" : "uv"].push_back(Json::array({uv[0], uv[1]}));
                            }
                            Json vw = Json::array();
                            for (auto &w : weights[cp])
                                vw.push_back({{"bone", w.first}, {"weight", w.second}});
                            m["weights"].push_back(std::move(vw));
                        }
                        // The X reflection changes handedness; account for mirrored object transforms as well.
                        m["indices"].push_back(first);
                        m["indices"].push_back(first + (world.Determinant() < 0 ? 1 : 2));
                        m["indices"].push_back(first + (world.Determinant() < 0 ? 2 : 1));
                        int material = 0;
                        if (materials && materials->GetIndexArray().GetCount())
                            material = materials->GetIndexArray().GetAt(
                                materials->GetMappingMode() == FbxLayerElement::eByPolygon ? polygon : 0);
                        m["materials"].push_back(std::max(0, material));
                    }
                    if (!normals)
                        m["normals"] = Json::array();
                    for (int deformer = 0; deformer < mesh->GetDeformerCount(FbxDeformer::eBlendShape); ++deformer)
                    {
                        auto *blend =
                            static_cast<FbxBlendShape *>(mesh->GetDeformer(deformer, FbxDeformer::eBlendShape));
                        for (int channel = 0; channel < blend->GetBlendShapeChannelCount(); ++channel)
                        {
                            auto *shapes = blend->GetBlendShapeChannel(channel);
                            for (int frame = 0; frame < shapes->GetTargetShapeCount(); ++frame)
                            {
                                auto *shape = shapes->GetTargetShape(frame);
                                if (shape->GetControlPointsCount() != count)
                                    throw std::runtime_error("Imported morph topology differs from mesh");
                                Json s = {{"name", shapes->GetName()},
                                          {"weight", shapes->GetTargetShapeFullWeights()[frame]},
                                          {"positions", Json::array()}};
                                for (int cp : controlPoints)
                                {
                                    auto delta = world.MultT(shape->GetControlPointAt(cp)) -
                                                 world.MultT(mesh->GetControlPointAt(cp));
                                    s["positions"].push_back(point(delta));
                                }
                                m["shapes"].push_back(std::move(s));
                            }
                        }
                    }
                    totalVertices += m["positions"].size();
                    if (totalVertices > 1000000)
                        throw std::runtime_error("Imported split vertex limit exceeded");
                    result["meshes"].push_back(std::move(m));
                }
                for (int child = 0; child < node->GetChildCount(); ++child)
                    visit(node->GetChild(child));
            };
            visit(context.scene->GetRootNode());
            if (result["meshes"].empty())
                throw std::runtime_error("Imported model has no meshes");
            output = result.dump();
            return 0;
        });
    return status < 0 ? nullptr : output.c_str();
}

API void *hs2_fbx_create()
{
    try
    {
        error.clear();
        return new Scene();
    }
    catch (const std::exception &e)
    {
        error = e.what();
        return nullptr;
    }
}

API void hs2_fbx_destroy(void *p)
{
    delete static_cast<Scene *>(p);
}

API int hs2_fbx_bone(void *p, const char *name, int parent, const float *translation, const float *rotation)
{
    return call(
        [&]
        {
            auto &c = context(p);
            finite(translation, 3);
            finite(rotation, 4);
            auto *node = FbxNode::Create(c.scene, name);
            (parent < 0 ? c.scene->GetRootNode() : item(c.bones, parent))->AddChild(node);
            node->SetTransformationInheritType(FbxTransform::eInheritRSrs);
            auto *skeleton = FbxSkeleton::Create(c.scene, name);
            skeleton->SetSkeletonType(parent < 0 ? FbxSkeleton::eRoot : FbxSkeleton::eLimbNode);
            skeleton->Size.Set(.1);
            node->SetNodeAttribute(skeleton);
            auto t = point(translation);
            node->LclTranslation.Set(FbxDouble3(t[0], t[1], t[2]));
            FbxAMatrix matrix;
            matrix.SetQ(FbxQuaternion(rotation[0], -rotation[1], -rotation[2], rotation[3]));
            auto r = matrix.GetR();
            node->LclRotation.Set(FbxDouble3(r[0], r[1], r[2]));
            c.bones.push_back(node);
            return static_cast<int>(c.bones.size() - 1);
        });
}

API int hs2_fbx_material(void *p, const char *name, const char *baseColor, const char *normal, const char *emission)
{
    return call(
        [&]
        {
            auto &c = context(p);
            auto *m = FbxSurfacePhong::Create(c.scene, name);
            m->Diffuse.Set(FbxDouble3(1, 1, 1));
            m->ShadingModel.Set(FbxString("Phong"));
            auto texture = [&](const char *path, FbxProperty property)
            {
                if (!path || !*path)
                    return;
                auto *t = FbxFileTexture::Create(c.scene, path);
                t->SetFileName(path);
                t->SetRelativeFileName(path);
                t->SetTextureUse(FbxTexture::eStandard);
                t->SetMappingType(FbxTexture::eUV);
                t->UVSet.Set(FbxString("UV0"));
                property.ConnectSrcObject(t);
            };
            texture(baseColor, m->Diffuse);
            texture(normal, m->NormalMap);
            texture(emission, m->Emissive);
            c.materials.push_back(m);
            return static_cast<int>(c.materials.size() - 1);
        });
}

API int hs2_fbx_mesh(void *p, const char *name, int count, const float *positions, const float *normals,
                     const float *tangents, const float *binormals)
{
    return call(
        [&]
        {
            auto &c = context(p);
            if (count < 1 || count > 10000000)
                throw std::invalid_argument("FBX vertex count");
            finite(positions, count * 3);
            finite(normals, count * 3);
            auto *mesh = FbxMesh::Create(c.scene, name);
            mesh->InitControlPoints(count);
            auto *n = mesh->CreateElementNormal();
            direct(n);
            for (int i = 0; i < count; i++)
            {
                mesh->SetControlPointAt(point(positions + i * 3), i);
                n->GetDirectArray().Add(direction(normals + i * 3));
            }
            if (tangents && binormals)
            {
                finite(tangents, count * 3);
                finite(binormals, count * 3);
                auto *t = mesh->CreateElementTangent();
                auto *b = mesh->CreateElementBinormal();
                direct(t);
                direct(b);
                for (int i = 0; i < count; i++)
                {
                    t->GetDirectArray().Add(direction(tangents + i * 3));
                    b->GetDirectArray().Add(direction(binormals + i * 3));
                }
            }
            auto *node = FbxNode::Create(c.scene, name);
            node->SetNodeAttribute(mesh);
            c.scene->GetRootNode()->AddChild(node);
            c.meshes.push_back(node);
            return static_cast<int>(c.meshes.size() - 1);
        });
}

API int hs2_fbx_uv(void *p, int meshIndex, int channel, const float *uv)
{
    return call(
        [&]
        {
            auto *mesh = item(context(p).meshes, meshIndex)->GetMesh();
            if (channel < 0 || channel > 7)
                throw std::invalid_argument("FBX UV channel");
            finite(uv, mesh->GetControlPointsCount() * 2);
            while (mesh->GetLayerCount() <= channel)
                mesh->CreateLayer();
            auto *layer = FbxLayerElementUV::Create(mesh, ("UV" + std::to_string(channel)).c_str());
            direct(layer);
            for (int i = 0; i < mesh->GetControlPointsCount(); i++)
                layer->GetDirectArray().Add(FbxVector2(uv[i * 2], uv[i * 2 + 1]));
            mesh->GetLayer(channel)->SetUVs(layer);
            return 0;
        });
}

API int hs2_fbx_colors(void *p, int meshIndex, const float *rgba)
{
    return call(
        [&]
        {
            auto *mesh = item(context(p).meshes, meshIndex)->GetMesh();
            finite(rgba, mesh->GetControlPointsCount() * 4);
            auto *layer = mesh->CreateElementVertexColor();
            direct(layer);
            for (int i = 0; i < mesh->GetControlPointsCount(); i++)
            {
                auto *v = rgba + i * 4;
                layer->GetDirectArray().Add(FbxColor(v[0], v[1], v[2], v[3]));
            }
            return 0;
        });
}

API int hs2_fbx_triangles(void *p, int meshIndex, int material, int count, const int *indices, int mirrored)
{
    return call(
        [&]
        {
            auto &c = context(p);
            auto *node = item(c.meshes, meshIndex);
            auto *mesh = node->GetMesh();
            if (!indices || count < 0 || count % 3)
                throw std::invalid_argument("FBX triangle span");
            int slot = node->AddMaterial(item(c.materials, material));
            auto *layer = mesh->GetElementMaterial();
            if (!layer)
            {
                layer = mesh->CreateElementMaterial();
                layer->SetMappingMode(FbxLayerElement::eByPolygon);
                layer->SetReferenceMode(FbxLayerElement::eIndexToDirect);
                mesh->GetLayer(0)->SetMaterials(layer);
            }
            for (int i = 0; i < count; i += 3)
            {
                for (int k = 0; k < 3; k++)
                    if (indices[i + k] < 0 || indices[i + k] >= mesh->GetControlPointsCount())
                        throw std::out_of_range("FBX triangle vertex");
                mesh->BeginPolygon(slot, -1, -1, false);
                mesh->AddPolygon(indices[i]);
                mesh->AddPolygon(indices[i + (mirrored ? 1 : 2)]);
                mesh->AddPolygon(indices[i + (mirrored ? 2 : 1)]);
                mesh->EndPolygon();
            }
            // 显式设置每个多边形槽位，避免多个 UV 层时 SDK 默认值不一致。
            auto &slots = layer->GetIndexArray();
            slots.SetCount(mesh->GetPolygonCount());
            for (int i = mesh->GetPolygonCount() - count / 3; i < mesh->GetPolygonCount(); i++)
                slots.SetAt(i, slot);
            return 0;
        });
}

API int hs2_fbx_skin(void *p, int meshIndex, int boneIndex, int count, const int *indices, const float *weights)
{
    return call(
        [&]
        {
            auto &c = context(p);
            auto *mesh = item(c.meshes, meshIndex)->GetMesh();
            auto *bone = item(c.bones, boneIndex);
            finite(weights, count);
            auto *skin = static_cast<FbxSkin *>(mesh->GetDeformer(0, FbxDeformer::eSkin));
            if (!skin)
            {
                skin = FbxSkin::Create(c.scene, "Skin");
                mesh->AddDeformer(skin);
            }
            auto *cluster = FbxCluster::Create(c.scene, "Cluster");
            cluster->SetLink(bone);
            cluster->SetLinkMode(FbxCluster::eNormalize);
            for (int i = 0; i < count; i++)
            {
                if (indices[i] < 0 || indices[i] >= mesh->GetControlPointsCount() || weights[i] < 0)
                    throw std::invalid_argument("FBX skin influence");
                cluster->AddControlPointIndex(indices[i], weights[i]);
            }
            FbxAMatrix identity;
            identity.SetIdentity();
            cluster->SetTransformMatrix(identity);
            cluster->SetTransformLinkMatrix(bone->EvaluateGlobalTransform());
            skin->AddCluster(cluster);
            return 0;
        });
}

API int hs2_fbx_shape(void *p, int meshIndex, const char *name, float weight, const float *positions,
                      const float *normals, const float *tangents)
{
    return call(
        [&]
        {
            auto &c = context(p);
            auto *mesh = item(c.meshes, meshIndex)->GetMesh();
            int count = mesh->GetControlPointsCount();
            finite(positions, count * 3);
            finite(normals, count * 3);
            if (!std::isfinite(weight) || weight <= 0)
                throw std::invalid_argument("FBX shape weight");
            auto *blend = static_cast<FbxBlendShape *>(mesh->GetDeformer(0, FbxDeformer::eBlendShape));
            if (!blend)
            {
                blend = FbxBlendShape::Create(c.scene, "Expressions");
                mesh->AddDeformer(blend);
            }
            FbxBlendShapeChannel *channel = nullptr;
            for (int i = 0; i < blend->GetBlendShapeChannelCount(); i++)
                if (std::string(blend->GetBlendShapeChannel(i)->GetName()) == name)
                    channel = blend->GetBlendShapeChannel(i);
            if (!channel)
            {
                channel = FbxBlendShapeChannel::Create(c.scene, name);
                channel->DeformPercent.Set(0);
                blend->AddBlendShapeChannel(channel);
            }
            auto *shape = FbxShape::Create(c.scene, name);
            shape->InitControlPoints(count);
            auto *n = shape->CreateElementNormal();
            direct(n);
            FbxGeometryElementTangent *t = nullptr;
            if (tangents)
            {
                finite(tangents, count * 3);
                t = shape->CreateElementTangent();
                direct(t);
            }
            for (int i = 0; i < count; i++)
            {
                shape->SetControlPointAt(point(positions + i * 3), i);
                n->GetDirectArray().Add(direction(normals + i * 3));
                if (t)
                    t->GetDirectArray().Add(direction(tangents + i * 3));
            }
            channel->AddTargetShape(shape, weight);
            return 0;
        });
}

API int hs2_fbx_write(void *p, const char *path)
{
    return call(
        [&]
        {
            auto &c = context(p);
            auto *pose = FbxPose::Create(c.scene, "ReferencePose");
            pose->SetIsBindPose(true);
            for (auto *node : c.bones)
                pose->Add(node, FbxMatrix(node->EvaluateGlobalTransform()));
            for (auto *node : c.meshes)
                pose->Add(node, FbxMatrix(node->EvaluateGlobalTransform()));
            c.scene->AddPose(pose);
            auto *exporter = FbxExporter::Create(c.manager, "HS2Manager");
            int binary = c.manager->GetIOPluginRegistry()->FindWriterIDByDescription("FBX binary (*.fbx)");
            bool ok = exporter->Initialize(path, binary, c.manager->GetIOSettings()) && exporter->Export(c.scene);
            std::string message = exporter->GetStatus().GetErrorString();
            exporter->Destroy();
            if (!ok)
                throw std::runtime_error(message);
            return 0;
        });
}

struct Summary
{
    int nodes, meshes, vertices, polygons, bones, clusters, channels, shapeFrames, materials;
};

API int hs2_fbx_reopen(const char *path, Summary *output)
{
    return call(
        [&]
        {
            if (!output)
                throw std::invalid_argument("Null FBX summary");
            Scene c;
            auto *importer = FbxImporter::Create(c.manager, "Validation");
            bool ok = importer->Initialize(path, -1, c.manager->GetIOSettings()) && importer->Import(c.scene);
            std::string message = importer->GetStatus().GetErrorString();
            importer->Destroy();
            if (!ok)
                throw std::runtime_error(message);
            *output = {};
            output->materials = c.scene->GetMaterialCount();
            for (int i = 0; i < c.scene->GetNodeCount(); i++)
            {
                auto *node = c.scene->GetNode(i);
                output->nodes++;
                if (node->GetSkeleton())
                    output->bones++;
                auto *mesh = node->GetMesh();
                if (!mesh)
                    continue;
                output->meshes++;
                output->vertices += mesh->GetControlPointsCount();
                output->polygons += mesh->GetPolygonCount();
                for (int k = 0; k < mesh->GetDeformerCount(FbxDeformer::eSkin); k++)
                    output->clusters +=
                        static_cast<FbxSkin *>(mesh->GetDeformer(k, FbxDeformer::eSkin))->GetClusterCount();
                for (int k = 0; k < mesh->GetDeformerCount(FbxDeformer::eBlendShape); k++)
                {
                    auto *b = static_cast<FbxBlendShape *>(mesh->GetDeformer(k, FbxDeformer::eBlendShape));
                    output->channels += b->GetBlendShapeChannelCount();
                    for (int j = 0; j < b->GetBlendShapeChannelCount(); j++)
                        output->shapeFrames += b->GetBlendShapeChannel(j)->GetTargetShapeCount();
                }
            }
            return 0;
        });
}
