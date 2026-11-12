#pragma once
#include "MeshDocument.h"

namespace hs2::creation
{
    struct PaintLayer
    {
        std::string Id = NewId(), Name = "图层";
        float Opacity = 1;
        bool Visible = true, Locked = false;
        std::shared_ptr<const Bytes> Pixels = std::make_shared<Bytes>();
    };

    class PaintDocument
    {
    public:
        int Width = 1024, Height = 1024;
        bool Srgb = true;
        std::vector<PaintLayer> Layers;
        static PaintDocument Blank(int width, int height);
        static PaintDocument Import(const fs::path &path);
        static PaintDocument FromJson(const Json &json);
        Json ToJson() const;
        void Validate() const;
        bool SamePixels(const PaintDocument &other) const;
        Bytes Composite(bool checker = false) const;
        void AddLayer(std::string name);
        void ImportLayer(const fs::path &path);
        void Stroke(std::size_t layer, std::span<const Vector2> points, Vector4 rgba, float radius, float strength,
                    float hardness, bool erase = false);
        void Fill(std::size_t layer, Vector2 point, Vector4 rgba, float tolerance = .05f);
        Vector4 Sample(Vector2 point) const;
        void Save(const fs::path &path) const;
    };
}
