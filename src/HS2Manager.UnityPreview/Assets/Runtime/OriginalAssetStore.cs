using System;
using System.Collections;
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
        private sealed class BundleEntry
        {
            internal AssetBundle Bundle;
            internal string Stamp;
            internal string[] Names;
            internal readonly Dictionary<string,Object> Assets=new Dictionary<string,Object>(StringComparer.OrdinalIgnoreCase);
        }
        private readonly Dictionary<string, BundleEntry> bundles = new Dictionary<string, BundleEntry>(StringComparer.OrdinalIgnoreCase);
        private string[] shaderSearchOrder=new string[0];
        private readonly Dictionary<string,Texture2D> loose=new Dictionary<string,Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,string> looseStamps=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<AssetBundleCreateRequest> pending=new HashSet<AssetBundleCreateRequest>();
        private sealed class PendingBundle
        {internal string Path,Stamp;internal AssetBundleCreateRequest Request;}
        private bool disposed;
        public int RetainedBundles {get;private set;}
        public int LoadedBundles {get;private set;}
        public int BundleCount {get{return bundles.Count;}}
        public OriginalAssetStore() { }
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
                    bundles.Add(full, new BundleEntry{Bundle=bundle,Stamp=Stamp(full)});
                }
                shaderSearchOrder=bundles.Keys.ToArray();
            }
            catch { Dispose(); throw; }
        }
        private static string Stamp(string path){var file=new FileInfo(path);return file.Length+"|"+file.LastWriteTimeUtc.Ticks;}
        public IEnumerator Prepare(CharacterDefinition definition,Func<bool> canceled,Action<string> progress)
        {
            if(disposed)throw new ObjectDisposedException("OriginalAssetStore");
            while(pending.Count>0){if(canceled())throw new OperationCanceledException();yield return null;}
            shaderSearchOrder=definition.BundlePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var required=new HashSet<string>(shaderSearchOrder,StringComparer.OrdinalIgnoreCase);
            var images=new HashSet<string>(definition.Selections.Select(s=>s.Reference).Concat(definition.Shaders.SelectMany(s=>s.Defaults.Select(d=>d.Texture)))
                .Where(r=>r!=null&&r.Bundle.EndsWith(".png",StringComparison.OrdinalIgnoreCase)).Select(r=>Path.GetFullPath(r.Bundle)),StringComparer.OrdinalIgnoreCase);
            RetainedBundles=0;LoadedBundles=0;
            // A changed dependency may already be referenced by a cached prefab/material.
            // Rebuild the set on in-place changes instead of retaining objects with dead PPtrs.
            bool changed=bundles.Any(p=>required.Contains(p.Key)&&(!File.Exists(p.Key)||p.Value.Stamp!=Stamp(p.Key)));
            // The old character has already been destroyed. Remove every old exclusive
            // bundle before loading new ones, including bundles with a reused CAB name.
            foreach(var path in bundles.Keys.ToArray())
            {
                if(canceled())throw new OperationCanceledException();
                if(!changed&&required.Contains(path)&&File.Exists(path)&&bundles[path].Stamp==Stamp(path)){RetainedBundles++;continue;}
                bundles[path].Bundle.Unload(true);bundles.Remove(path);
            }
            foreach(var path in loose.Keys.ToArray())
                if(!images.Contains(path)||!File.Exists(path)||looseStamps[path]!=Stamp(path))
                {Object.Destroy(loose[path]);loose.Remove(path);looseStamps.Remove(path);}
            var missing=shaderSearchOrder.Where(p=>!bundles.ContainsKey(p))
                .Select(p=>new PendingBundle{Path=p,Stamp=Stamp(p)}).ToArray();
            // Two native reads overlap decompression and NAS I/O without queuing the
            // entire resource set or growing memory with obsolete card requests.
            var active=new List<PendingBundle>();int next=0;
            try
            {
                while(next<missing.Length || active.Count>0)
                {
                    if(disposed)throw new OperationCanceledException();
                    // A superseded request stops scheduling more I/O. Drain the two native
                    // requests before the next character can load a conflicting CAB name.
                    bool stop=canceled();
                    if(!stop)while(active.Count<2 && next<missing.Length)
                    {
                        var item=missing[next++];item.Request=AssetBundle.LoadFromFileAsync(item.Path);
                        pending.Add(item.Request);active.Add(item);
                    }
                    foreach(var item in active.Where(p=>p.Request.isDone).ToArray())
                    {
                        var bundle=item.Request.assetBundle;
                        if(bundle==null)throw new IOException("原始资源包无法加载："+item.Path);
                        bundles.Add(item.Path,new BundleEntry{Bundle=bundle,Stamp=item.Stamp});LoadedBundles++;
                        pending.Remove(item.Request);active.Remove(item);
                        progress("读取角色资源 "+(RetainedBundles+LoadedBundles)+"/"+required.Count);
                    }
                    if(stop && active.Count==0)throw new OperationCanceledException();
                    if(active.Count>0)yield return null;
                }
            }
            finally
            {
                foreach(var item in active)
                {
                    var request=item.Request;
                    if(!request.isDone)request.completed+=_=>{if(request.assetBundle!=null)request.assetBundle.Unload(true);pending.Remove(request);};
                    else
                    {
                        pending.Remove(request);
                        if(!bundles.ContainsKey(item.Path)&&request.assetBundle!=null)request.assetBundle.Unload(true);
                    }
                }
            }
            if(canceled())throw new OperationCanceledException();
        }
        public T Load<T>(AssetReference reference) where T : Object
        {
            if(reference!=null&&reference.Bundle.EndsWith(".png",StringComparison.OrdinalIgnoreCase)&&typeof(Texture).IsAssignableFrom(typeof(T)))
            {
                Texture2D image;
                string full=Path.GetFullPath(reference.Bundle);
                if(!loose.TryGetValue(full,out image))
                {
                    image=new Texture2D(2,2,TextureFormat.RGBA32,true);
                    if(!image.LoadImage(File.ReadAllBytes(reference.Bundle))){Object.Destroy(image);throw new IOException("MOD PNG 纹理无法解码："+reference.Asset);}
                    if(reference.Asset.Contains("clamp"))image.wrapMode=TextureWrapMode.Clamp;
                    else if(reference.Asset.Contains("repeat"))image.wrapMode=TextureWrapMode.Repeat;
                    image.name=reference.Asset;loose.Add(full,image);looseStamps.Add(full,Stamp(full));
                }
                return image as T;
            }
            BundleEntry entry;
            if (reference == null || !bundles.TryGetValue(Path.GetFullPath(reference.Bundle), out entry))
                throw new IOException("资源包未登记。");
            string key=typeof(T).FullName+"|"+reference.Asset;
            Object cached;if(entry.Assets.TryGetValue(key,out cached)&&cached!=null)return cached as T;
            if(entry.Names==null)entry.Names=entry.Bundle.GetAllAssetNames();
            var candidates = entry.Names.Where(name => name.Equals(reference.Asset, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(name).Equals(reference.Asset, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(name).Equals(reference.Asset, StringComparison.OrdinalIgnoreCase));
            foreach (var candidate in candidates)
            {
                var result = entry.Bundle.LoadAsset<T>(candidate);
                if (result != null) {entry.Assets[key]=result;return result;}
            }
            throw new IOException("原始资源不存在：" + reference.Asset + " (" + typeof(T).Name + ") @ " + reference.Bundle);
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            foreach (var bundle in bundles.Values) bundle.Bundle.Unload(true);
            bundles.Clear();
            foreach(var texture in loose.Values)Object.Destroy(texture);loose.Clear();looseStamps.Clear();
            foreach(var request in pending)
                if(request.isDone){if(request.assetBundle!=null)request.assetBundle.Unload(true);}
                else request.completed+=_=>{if(request.assetBundle!=null)request.assetBundle.Unload(true);};
            pending.Clear();
        }
        public Shader FindShader(string name)
        {
            var found=Shader.Find(name);
            if(found!=null)return found;
            foreach(var path in shaderSearchOrder)
            {
                BundleEntry bundle;if(!bundles.TryGetValue(path,out bundle))continue;
                foreach(var shader in bundle.Bundle.LoadAllAssets<Shader>())if(shader.name==name)return shader;
                foreach(var material in bundle.Bundle.LoadAllAssets<Material>())if(material.shader!=null && material.shader.name==name)return material.shader;
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
