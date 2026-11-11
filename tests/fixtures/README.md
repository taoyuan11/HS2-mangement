# Frozen numerical references

These JSON files retain the original reference samples from before the C++ migration. Moving them out of the retired C# test project does not change their contents.

- `native-curve-reference.json`: animation-curve samples consumed by `HS2Manager.CppSceneChecks/SceneChecks.cpp` in the `hs2_scene_checks` CTest target.
- `native-math-reference.json`: preserved numerical samples for historical comparison; the default CTest suite does not consume this file.

The retired C# / Unity generators and projects can be retrieved from Git history. Running the current C++ checks does not require them or a .NET SDK.
