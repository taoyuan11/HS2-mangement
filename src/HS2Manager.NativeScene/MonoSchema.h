#pragma once
#include "AssetReader.h"
namespace hs2 {
// Derives the Unity 2018 serialized-field layout from ECMA-335 metadata only.
// Managed assemblies are never loaded or executed.
class NativeMonoSchema {
public:
    NativeMonoSchema(std::filesystem::path managedDirectory,std::shared_ptr<const UnityClassDatabase> database);
    ~NativeMonoSchema();
    std::vector<UnityTypeNode> Type(std::string assembly,std::string namespaceName,std::string className,std::string_view unityVersion="2018.2.21f1");
private:struct Impl;std::unique_ptr<Impl> impl_;
};
}
