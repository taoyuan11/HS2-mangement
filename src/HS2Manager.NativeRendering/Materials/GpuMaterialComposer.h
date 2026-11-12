#pragma once
#include "PostProcessing/NativeFullscreenPass.h"
#include "Character/CharacterDefinition.h"
#include "Assets/AssetStore.h"

namespace hs2
{
    class GpuMaterialInstance
    {
    public:
        GpuMaterialInstance(const GpuMaterialData &source, std::shared_ptr<NativeShaderAsset> shader);
        std::shared_ptr<NativeShaderAsset> Shader;
        std::string Name;
        int Queue() const;

        void Queue(int value)
        {
            queue_ = value;
        }

        ShaderValues Values;
        std::unordered_map<std::string, GpuTextureBinding> Textures;
        std::unordered_set<std::string> Keywords, ColorProperties, GammaProperties;
        std::unordered_map<std::string, std::string> OverrideTags, DefaultTextures;
        std::unordered_map<std::string, AssetReference> ImageSelections;
        std::unordered_map<std::string, std::shared_ptr<GpuTexture>> TextureOverrides;
        bool HasProperty(const std::string &name) const;
        std::optional<std::vector<float>> CopyPropertyValue(const std::string &name, int components) const;
        std::string Tag(const std::string &name) const;
        void SetShader(std::shared_ptr<NativeShaderAsset> shader);
        void Set(const std::string &property, std::vector<float> values);

        void Set(const std::string &property, float value)
        {
            Set(property, std::vector<float>{value});
        }

        void SetColor(const std::string &property, Vector4 color);
        void SetColor(const std::string &property, const Parameter &color, Vector4 fallback);
        Vector4 Vector(const std::string &property) const;
        void SetTextureTransform(const std::string &property, std::optional<Vector2> scale = {},
                                 std::optional<Vector2> offset = {});
        static int DefaultQueue(const NativeShaderAsset &shader);

    private:
        int queue_ = -1;
        std::unordered_set<std::string> properties_, assigned_;
        std::unordered_map<std::string, int> propertyTypes_;
        ShaderValues scalarValues_, vectorValues_;
    };

    class GpuMaterialComposer
    {
    public:
        GpuMaterialComposer(GraphicsDevice &graphics, NativeGpuAssetStore &store,
                            const CharacterDefinition &definition);
        bool HasSelection(const std::string &key) const;
        void UpdateSelections();
        std::shared_ptr<GpuMaterialInstance> CreateMaterial(const GpuMaterialData &source);
        void OptimizeShader(GpuMaterialInstance &material);
        std::shared_ptr<NativeShaderAsset> Shader(const std::string &name);
        std::shared_ptr<NativeShaderAsset> Shader(const GpuAssetPointer &pointer);
        NativeComputeAsset Compute(const GpuAssetPointer &pointer);
        std::shared_ptr<GpuMaterialInstance> Material(const std::string &selection);
        std::shared_ptr<GpuMaterialInstance> ShaderMaterial(const std::string &selection);
        void SelectTexture(GpuMaterialInstance &material, const std::string &property, const std::string &selection);
        void SelectTexture(GpuMaterialInstance &material, const std::string &property,
                           const std::optional<AssetReference> &reference);
        std::shared_ptr<GpuTexture> Texture(const std::string &selection);
        std::shared_ptr<GpuTexture> Texture(const GpuAssetPointer &pointer);
        std::shared_ptr<GpuTexture> TryTexture(const std::string &selection);
        std::shared_ptr<GpuTexture> Embedded(const EmbeddedTextureReference &reference, bool clamp = false);
        std::shared_ptr<GpuTexture> Overlay(const GpuTexture &source, const OverlayDefinition &overlay, int width,
                                            int height = 0);
        std::shared_ptr<GpuTexture> Copy(const GpuTexture &source, int width, int height);
        std::shared_ptr<GpuTexture> Compose(const GpuTexture &source, const GpuMaterialInstance &material, int width,
                                            int height = 0, const ShaderTextures &overrides = {},
                                            bool preserveDestination = false);
        std::shared_ptr<GpuTexture> ResolveTexture(const GpuMaterialInstance &material, const std::string &property);
        std::shared_ptr<GpuTexture> AssignedTexture(const GpuMaterialInstance &material, const std::string &property);
        std::shared_ptr<GpuTexture> TryGlobalTexture(const std::string &name);
        std::shared_ptr<GpuTexture> Default(const std::string &name);
        std::vector<std::uint8_t> ReadImage(const GpuTexture &texture);
        std::unordered_set<std::string> SourceReferenceDiagnostics;
        static float Linear(float value);

        GraphicsDevice &Graphics() const
        {
            return graphics_;
        }

    private:
        GraphicsDevice &graphics_;
        NativeGpuAssetStore &store_;
        const CharacterDefinition &definition_;
        int anisotropicFiltering_;
        std::unordered_map<std::string, std::shared_ptr<GpuTexture>> textures_, defaults_, embedded_;
        std::unordered_map<std::string, std::shared_ptr<NativeShaderAsset>> shaders_, namedShaders_;
        std::unordered_map<std::string, AssetReference> selections_;
        std::unique_ptr<GpuCopyPass> copy_;
        const AssetReference &Selection(const std::string &key) const;
        std::shared_ptr<GpuTexture> LooseImage(const AssetReference &reference);
        GpuCopyPass &Copier();
        std::shared_ptr<NativeShaderAsset> FindShader(const std::string &name);
    };

    Vector4 ParameterColor(const Parameter &value, Vector4 fallback);
    float ParameterComponent(const Parameter &value, int axis);
}
