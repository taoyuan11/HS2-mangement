using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HS2Manager.UnityPreview
{
    public sealed class OriginalAssetStore : IDisposable
    {
        private readonly Dictionary<string, AssetBundle> bundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,Texture2D> loose=new Dictionary<string,Texture2D>(StringComparer.OrdinalIgnoreCase);
        public OriginalAssetStore(IEnumerable<string> paths)
        {
            try
            {
                foreach (var path in paths)
                {
                    var full = Path.GetFullPath(path);
                    if (bundles.ContainsKey(full)) continue;
                    var bundle = AssetBundle.LoadFromFile(full);
                    if (bundle == null) throw new IOException("原始资源包无法加载：" + full);
                    bundles.Add(full, bundle);
                }
            }
            catch { Dispose(); throw; }
        }
        public T Load<T>(AssetReference reference) where T : Object
        {
            if(reference!=null&&reference.Bundle.EndsWith(".png",StringComparison.OrdinalIgnoreCase)&&typeof(Texture).IsAssignableFrom(typeof(T)))
            {
                Texture2D image;
                if(!loose.TryGetValue(reference.Bundle,out image))
                {
                    image=new Texture2D(2,2,TextureFormat.RGBA32,true);
                    if(!image.LoadImage(File.ReadAllBytes(reference.Bundle))){Object.Destroy(image);throw new IOException("MOD PNG 纹理无法解码："+reference.Asset);}
                    if(reference.Asset.Contains("clamp"))image.wrapMode=TextureWrapMode.Clamp;
                    else if(reference.Asset.Contains("repeat"))image.wrapMode=TextureWrapMode.Repeat;
                    image.name=reference.Asset;loose.Add(reference.Bundle,image);
                }
                return image as T;
            }
            AssetBundle bundle;
            if (reference == null || !bundles.TryGetValue(Path.GetFullPath(reference.Bundle), out bundle))
                throw new IOException("资源包未登记。");
            var candidates = bundle.GetAllAssetNames().Where(name => name.Equals(reference.Asset, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(name).Equals(reference.Asset, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(name).Equals(reference.Asset, StringComparison.OrdinalIgnoreCase));
            foreach (var candidate in candidates)
            {
                var result = bundle.LoadAsset<T>(candidate);
                if (result != null) return result;
            }
            throw new IOException("原始资源不存在：" + reference.Asset + " (" + typeof(T).Name + ") @ " + reference.Bundle);
        }
        public void Dispose()
        {
            foreach (var bundle in bundles.Values) bundle.Unload(true);
            bundles.Clear();
            foreach(var texture in loose.Values)Object.Destroy(texture);loose.Clear();
        }
        public Shader FindShader(string name)
        {
            var found=Shader.Find(name);
            if(found!=null)return found;
            foreach(var bundle in bundles.Values)
            {
                foreach(var shader in bundle.LoadAllAssets<Shader>())if(shader.name==name)return shader;
                foreach(var material in bundle.LoadAllAssets<Material>())if(material.shader!=null && material.shader.name==name)return material.shader;
            }
            return null;
        }
        public Shader LoadShader(ShaderDefinition definition)
        {
            if(definition.Source==null)return FindShader(definition.Name);
            var source=Load<Object>(definition.Source);
            if(source is Shader)return (Shader)source;
            if(source is Material)return ((Material)source).shader;
            var prefab=source as GameObject;
            if(prefab!=null)
            {
                // Inspect native renderers only; never instantiate the prefab's scripts.
                foreach(var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach(var material in renderer.sharedMaterials)
                        if(material!=null&&material.shader!=null&&material.shader.name==definition.Name)return material.shader;
            }
            return FindShader(definition.Name);
        }
    }
}
