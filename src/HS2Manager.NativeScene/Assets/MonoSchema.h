#pragma once
#include "Assets/AssetReader.h"

namespace hs2
{
    // 仅通过 ECMA-335 元数据推导 Unity 2018 序列化字段布局，不加载或执行托管程序集。
    class NativeMonoSchema
    {
    public:
        NativeMonoSchema(std::filesystem::path managedDirectory, std::shared_ptr<const UnityClassDatabase> database);
        ~NativeMonoSchema();
        std::vector<UnityTypeNode> Type(std::string assembly, std::string namespaceName, std::string className,
                                        std::string_view unityVersion = "2018.2.21f1");

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };
}
