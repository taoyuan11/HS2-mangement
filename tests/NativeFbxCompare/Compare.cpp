#include <fbxsdk.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <map>
#include <memory>
#include <sstream>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

// 独立读取两个完整的 FBX 文件；
// 预期数组不由导出器提供，且此程序不依赖渲染器或引擎。
namespace
{
    struct Loaded
    {
        FbxManager *manager = FbxManager::Create();
        FbxScene *scene = nullptr;

        explicit Loaded(const char *path)
        {
            if (!manager)
                throw std::runtime_error("FBX manager creation failed");
            manager->SetIOSettings(FbxIOSettings::Create(manager, IOSROOT));
            scene = FbxScene::Create(manager, "Comparison");
            auto *importer = FbxImporter::Create(manager, "Comparison");
            bool ok = importer->Initialize(path, -1, manager->GetIOSettings()) && importer->Import(scene);
            std::string error = importer->GetStatus().GetErrorString();
            importer->Destroy();
            if (!ok)
                throw std::runtime_error(error);
        }

        ~Loaded()
        {
            if (manager)
                manager->Destroy();
        }
    };

    struct Metric
    {
        double maximum = 0;
        unsigned long long values = 0, failures = 0;
        std::string worstAt;
    };

    struct Report
    {
        std::string context;
        std::map<std::string, Metric> metrics;
        std::vector<std::string> errors;

        void require(bool condition, const std::string &message)
        {
            if (!condition && errors.size() < 200)
                errors.push_back(message);
        }

        void number(const std::string &field, double a, double b, double tolerance)
        {
            auto &m = metrics[field];
            m.values++;
            double difference = std::abs(a - b);
            if (!std::isfinite(a) || !std::isfinite(b) || difference > tolerance)
                m.failures++;
            if (!std::isfinite(difference) || difference > m.maximum)
            {
                m.maximum = std::isfinite(difference) ? difference : 1e300;
                m.worstAt = context;
            }
        }

        void matrix(const std::string &field, const FbxAMatrix &a, const FbxAMatrix &b)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    number(field, a[row][col], b[row][col], .001);
        }

        bool passed() const
        {
            return errors.empty() && std::all_of(metrics.begin(), metrics.end(),
                                                 [](const auto &p)
                                                 {
                                                     return p.second.failures == 0;
                                                 });
        }

        void write(const char *path) const
        {
            std::ofstream f(path);
            if (!f)
                throw std::runtime_error("Cannot write comparison report");
            f << std::setprecision(17) << "{\"SchemaVersion\":1,\"Passed\":" << (passed() ? "true" : "false")
              << ",\"PositionToleranceCentimeters\":0.001,\"DirectionTolerance\":0.0001,\"WeightTolerance\":0.00001,"
                 "\"RawShapeNormalValidationRequired\":true,\"VisualParityVerified\":false,\"Errors\":[";
            for (size_t i = 0; i < errors.size(); i++)
            {
                if (i)
                    f << ',';
                f << std::quoted(errors[i]);
            }
            f << "],\"Metrics\":{";
            bool first = true;
            for (const auto &p : metrics)
            {
                if (!first)
                    f << ',';
                first = false;
                f << std::quoted(p.first) << ":{\"MaximumError\":" << p.second.maximum
                  << ",\"Values\":" << p.second.values << ",\"Failures\":" << p.second.failures
                  << ",\"WorstAt\":" << std::quoted(p.second.worstAt) << '}';
            }
            f << "}}\n";
        }
    };

    template <class T>
    void vectorValues(Report &r, const std::string &field, const T &a, const T &b, int count, double tolerance)
    {
        for (int i = 0; i < count; i++)
            r.number(field, a[i], b[i], tolerance);
    }

    template <class T> void layer(Report &r, const std::string &name, T *a, T *b, int dimensions, double tolerance)
    {
        r.require((a == nullptr) == (b == nullptr), name + " layer presence differs");
        if (!a || !b)
            return;
        r.require(a->GetMappingMode() == b->GetMappingMode(), name + " mapping mode differs");
        r.require(a->GetReferenceMode() == b->GetReferenceMode(), name + " reference mode differs");
        auto &av = a->GetDirectArray();
        auto &bv = b->GetDirectArray();
        r.require(av.GetCount() == bv.GetCount(), name + " direct count differs");
        for (int i = 0; i < std::min(av.GetCount(), bv.GetCount()); i++)
            vectorValues(r, name, av.GetAt(i), bv.GetAt(i), dimensions, tolerance);
        auto &ai = a->GetIndexArray();
        auto &bi = b->GetIndexArray();
        r.require(ai.GetCount() == bi.GetCount(), name + " index count differs");
        for (int i = 0; i < std::min(ai.GetCount(), bi.GetCount()); i++)
            r.require(ai.GetAt(i) == bi.GetAt(i), name + " index differs");
    }

    std::map<std::string, FbxNode *> nodes(FbxScene *scene)
    {
        std::map<std::string, FbxNode *> result;
        for (int i = 0; i < scene->GetNodeCount(); i++)
        {
            auto *node = scene->GetNode(i);
            if (node == scene->GetRootNode())
                continue;
            if (!result.emplace(node->GetName(), node).second)
                throw std::runtime_error("FBX contains duplicate node names");
        }
        return result;
    }

    std::string parent(FbxNode *n)
    {
        return n->GetParent() && n->GetParent()->GetParent() ? n->GetParent()->GetName() : "";
    }

    std::map<std::string, FbxCluster *> clusters(FbxMesh *mesh)
    {
        std::map<std::string, FbxCluster *> result;
        for (int i = 0; i < mesh->GetDeformerCount(FbxDeformer::eSkin); i++)
        {
            auto *skin = static_cast<FbxSkin *>(mesh->GetDeformer(i, FbxDeformer::eSkin));
            for (int j = 0; j < skin->GetClusterCount(); j++)
            {
                auto *c = skin->GetCluster(j);
                if (!c->GetLink())
                    throw std::runtime_error("Cluster has no linked bone");
                if (!result.emplace(c->GetLink()->GetName(), c).second)
                    throw std::runtime_error("Duplicate skin cluster link");
            }
        }
        return result;
    }

    std::map<int, double> influences(FbxCluster *c)
    {
        std::map<int, double> result;
        for (int i = 0; i < c->GetControlPointIndicesCount(); i++)
            result[c->GetControlPointIndices()[i]] += c->GetControlPointWeights()[i];
        return result;
    }

    void skin(Report &r, FbxMesh *a, FbxMesh *b)
    {
        auto aa = clusters(a), bb = clusters(b);
        r.require(aa.size() == bb.size(), std::string(a->GetName()) + " skin cluster count differs");
        for (const auto &item : aa)
        {
            auto found = bb.find(item.first);
            r.require(found != bb.end(), "Missing cluster bone " + item.first);
            if (found == bb.end())
                continue;
            auto *x = item.second;
            auto *y = found->second;
            r.require(x->GetLinkMode() == y->GetLinkMode(), "Cluster link mode differs");
            FbxAMatrix am, bm;
            x->GetTransformMatrix(am);
            y->GetTransformMatrix(bm);
            r.matrix("SkinTransform", am, bm);
            x->GetTransformLinkMatrix(am);
            y->GetTransformLinkMatrix(bm);
            r.matrix("SkinLinkTransform", am, bm);
            auto av = influences(x), bv = influences(y);
            r.require(av.size() == bv.size(), "Skin influence count differs");
            for (const auto &weight : av)
            {
                auto actual = bv.find(weight.first);
                r.require(actual != bv.end(), "Skin influence vertex differs");
                if (actual != bv.end())
                    r.number("SkinWeight", weight.second, actual->second, .00001);
            }
        }
    }

    std::map<std::string, FbxBlendShapeChannel *> channels(FbxMesh *mesh)
    {
        std::map<std::string, FbxBlendShapeChannel *> result;
        for (int i = 0; i < mesh->GetDeformerCount(FbxDeformer::eBlendShape); i++)
        {
            auto *blend = static_cast<FbxBlendShape *>(mesh->GetDeformer(i, FbxDeformer::eBlendShape));
            for (int j = 0; j < blend->GetBlendShapeChannelCount(); j++)
            {
                auto *c = blend->GetBlendShapeChannel(j);
                if (!result.emplace(c->GetName(), c).second)
                    throw std::runtime_error("Duplicate blend channel");
            }
        }
        return result;
    }

    void shapes(Report &r, FbxMesh *a, FbxMesh *b)
    {
        auto aa = channels(a), bb = channels(b);
        r.require(aa.size() == bb.size(), "Blend channel count differs");
        for (const auto &item : aa)
        {
            auto found = bb.find(item.first);
            r.require(found != bb.end(), "Missing blend channel " + item.first);
            if (found == bb.end())
                continue;
            auto *x = item.second;
            auto *y = found->second;
            r.number("BlendDefaultWeight", x->DeformPercent.Get(), y->DeformPercent.Get(), .00001);
            r.require(x->GetTargetShapeCount() == y->GetTargetShapeCount(), "Blend frame count differs");
            for (int frame = 0; frame < std::min(x->GetTargetShapeCount(), y->GetTargetShapeCount()); frame++)
            {
                r.context = std::string(a->GetName()) + "/" + item.first + "/" + std::to_string(frame);
                r.number("BlendFrameWeight", x->GetTargetShapeFullWeights()[frame],
                         y->GetTargetShapeFullWeights()[frame], .00001);
                auto *av = x->GetTargetShape(frame);
                auto *bv = y->GetTargetShape(frame);
                r.require(av->GetControlPointsCount() == bv->GetControlPointsCount(), "Blend vertex count differs");
                for (int vertex = 0; vertex < std::min(av->GetControlPointsCount(), bv->GetControlPointsCount());
                     vertex++)
                    vectorValues(r, "BlendPosition", av->GetControlPointAt(vertex), bv->GetControlPointAt(vertex), 3,
                                 .001);
                // 旧版 FBX 法线采用稀疏相对差量；
                // SDK 展开的形变数组在部分已有控制点上也不可靠。
                // Compare-FbxShapeNormals.py 会独立读取完整的二进制基础值与差量，其结果必须一并检查。
            }
        }
    }

    void mesh(Report &r, FbxNode *a, FbxNode *b)
    {
        auto *x = a->GetMesh();
        auto *y = b->GetMesh();
        r.require(x->GetControlPointsCount() == y->GetControlPointsCount(), "Mesh vertex count differs");
        for (int i = 0; i < std::min(x->GetControlPointsCount(), y->GetControlPointsCount()); i++)
            vectorValues(r, "Position", x->GetControlPointAt(i), y->GetControlPointAt(i), 3, .001);
        r.require(x->GetPolygonCount() == y->GetPolygonCount(), "Polygon count differs");
        for (int i = 0; i < std::min(x->GetPolygonCount(), y->GetPolygonCount()); i++)
        {
            r.require(x->GetPolygonSize(i) == y->GetPolygonSize(i), "Polygon size differs");
            for (int j = 0; j < std::min(x->GetPolygonSize(i), y->GetPolygonSize(i)); j++)
                r.require(x->GetPolygonVertex(i, j) == y->GetPolygonVertex(i, j), "Triangle winding/index differs");
        }
        layer(r, "Normal", x->GetElementNormal(), y->GetElementNormal(), 3, .0001);
        layer(r, "Tangent", x->GetElementTangent(), y->GetElementTangent(), 3, .0001);
        layer(r, "Binormal", x->GetElementBinormal(), y->GetElementBinormal(), 3, .0001);
        r.require(x->GetElementUVCount() == y->GetElementUVCount(), "UV layer count differs");
        for (int i = 0; i < std::min(x->GetElementUVCount(), y->GetElementUVCount()); i++)
            layer(r, "UV", x->GetElementUV(i), y->GetElementUV(i), 2, .00001);
        layer(r, "VertexColor", x->GetElementVertexColor(), y->GetElementVertexColor(), 4, .00001);
        auto *am = x->GetElementMaterial();
        auto *bm = y->GetElementMaterial();
        r.require(am && bm, "Material slot layer missing");
        if (am && bm)
        {
            r.require(am->GetIndexArray().GetCount() == bm->GetIndexArray().GetCount(),
                      "Material polygon count differs");
            for (int i = 0; i < std::min(am->GetIndexArray().GetCount(), bm->GetIndexArray().GetCount()); i++)
                r.require(am->GetIndexArray().GetAt(i) == bm->GetIndexArray().GetAt(i),
                          "Polygon material slot differs");
        }
        r.require(a->GetMaterialCount() == b->GetMaterialCount(), "Node material count differs");
        for (int i = 0; i < std::min(a->GetMaterialCount(), b->GetMaterialCount()); i++)
            r.require(std::string(a->GetMaterial(i)->GetName()) == b->GetMaterial(i)->GetName(),
                      "Node material identity differs");
        skin(r, x, y);
        shapes(r, x, y);
    }

    void traceNormals(FbxScene *scene, const char *nodeName, const char *channelName, const std::string &output)
    {
        auto all = nodes(scene);
        auto found = all.find(nodeName);
        if (found == all.end() || !found->second->GetMesh())
            throw std::runtime_error("Normal trace mesh is missing");
        auto *mesh = found->second->GetMesh();
        auto blend = channels(mesh);
        auto target = blend.find(channelName);
        if (target == blend.end())
            throw std::runtime_error("Normal trace channel is missing");
        auto *shape = target->second->GetTargetShape(0);
        std::ofstream file(output, std::ios::binary);
        int32_t count = mesh->GetControlPointsCount();
        file.write(reinterpret_cast<const char *>(&count), 4);
        std::cout << nodeName << '/' << channelName
                  << " sparse control points: " << shape->GetControlPointIndicesCount() << '\n';
        auto &base = mesh->GetElementNormal()->GetDirectArray();
        auto &delta = shape->GetElementNormal()->GetDirectArray();
        if (base.GetCount() != count || delta.GetCount() != count)
            throw std::runtime_error("Normal trace requires direct control-point layers");
        for (int i = 0; i < count; i++)
        {
            auto a = base.GetAt(i), b = delta.GetAt(i);
            double data[] = {a[0], a[1], a[2], b[0], b[1], b[2]};
            file.write(reinterpret_cast<const char *>(data), sizeof(data));
        }
    }
}

int main(int argc, char **argv)
{
    try
    {
        if (argc != 4 && argc != 6)
            throw std::runtime_error(
                "Usage: HS2Manager.FbxCompare REFERENCE ACTUAL REPORT.json [TRACE_NODE TRACE_CHANNEL]");
        Loaded reference(argv[1]), actual(argv[2]);
        Report report;
        if (argc == 6)
        {
            traceNormals(reference.scene, argv[4], argv[5], std::string(argv[3]) + ".reference-normals.bin");
            traceNormals(actual.scene, argv[4], argv[5], std::string(argv[3]) + ".actual-normals.bin");
        }
        report.number("SystemUnitCentimeters", reference.scene->GetGlobalSettings().GetSystemUnit().GetScaleFactor(),
                      actual.scene->GetGlobalSettings().GetSystemUnit().GetScaleFactor(), 0);
        auto a = nodes(reference.scene), b = nodes(actual.scene);
        report.require(a.size() == b.size(), "Node count differs");
        for (const auto &item : a)
        {
            auto found = b.find(item.first);
            report.require(found != b.end(), "Missing node " + item.first);
            if (found == b.end())
                continue;
            report.context = item.first;
            auto *x = item.second;
            auto *y = found->second;
            report.require(parent(x) == parent(y), "Parent differs: " + item.first);
            report.require((x->GetSkeleton() != nullptr) == (y->GetSkeleton() != nullptr),
                           "Skeleton kind differs: " + item.first);
            report.require((x->GetMesh() != nullptr) == (y->GetMesh() != nullptr), "Mesh kind differs: " + item.first);
            report.matrix("NodeGlobalTransform", x->EvaluateGlobalTransform(), y->EvaluateGlobalTransform());
            if (x->GetMesh() && y->GetMesh())
                mesh(report, x, y);
        }
        report.write(argv[3]);
        std::cout << (report.passed() ? "PASS" : "FAIL")
                  << " complete FBX arrays, weights, frames, slots and transforms\n";
        return report.passed() ? 0 : 1;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 2;
    }
}
