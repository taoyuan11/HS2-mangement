using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using AIChara;
using HS2Manager.UnityPreview;
using UnityEngine;

namespace HS2Manager.GameReference
{
    internal static class EditingValidation
    {
        [Serializable] public sealed class Value {public string Path,Kind,Text;public double Number;}
        [Serializable] public sealed class Asset {public string Role,Bundle,Name;public int Slot;}
        [Serializable] public sealed class Expectation {public Value[] Values=new Value[0];public Asset[] Assets=new Asset[0];}
        [Serializable] public sealed class Result {public bool Passed;public int CheckedValues,CheckedAssets;public string[] Errors;}
        internal static void Write(ChaControl character,string card,string output)
        {
            var values=new List<Value>();
            Flatten("face",character.fileFace,values,0);Flatten("body",character.fileBody,values,0);Flatten("hair",character.fileHair,values,0);
            Flatten("clothes",character.nowCoordinate.clothes,values,0);Flatten("accessory",character.nowCoordinate.accessory,values,0);
            var assets=new List<Asset>();
            AddAssets("hair",character.infoHair,assets);AddAssets("clothes",character.infoClothes,assets);AddAssets("accessory",character.infoAccessory,assets);AddAssets("head",new[]{character.infoHead},assets);
            VisualFixture.Save(Path.Combine(output,"loaded-edit-values.json"),values.ToArray());
            VisualFixture.Save(Path.Combine(output,"loaded-edit-assets.json"),assets.ToArray());
            string path=card+".edit-expectations.json";
            if(!File.Exists(path))return;
            Expectation expected;
            using(var stream=File.OpenRead(path))expected=(Expectation)new DataContractJsonSerializer(typeof(Expectation)).ReadObject(stream);
            var actual=values.ToDictionary(v=>v.Path);var errors=new List<string>();
            foreach(var value in expected.Values??new Value[0])
            {
                Value found;
                if(!actual.TryGetValue(value.Path,out found)){errors.Add("Missing field: "+value.Path);continue;}
                if(found.Kind!=value.Kind||(value.Kind=="number"?Math.Abs(value.Number-found.Number)>1e-5:value.Text!=found.Text))
                    errors.Add(value.Path+": expected "+value.Number+"/"+value.Text+", loaded "+found.Number+"/"+found.Text);
            }
            foreach(var asset in expected.Assets??new Asset[0])
            {
                var found=assets.FirstOrDefault(a=>a.Role==asset.Role&&a.Slot==asset.Slot);
                if(found==null||!string.Equals(found.Bundle.Replace('\\','/'),asset.Bundle.Replace('\\','/'),StringComparison.OrdinalIgnoreCase)||found.Name!=asset.Name)
                    errors.Add(asset.Role+":"+asset.Slot+": wrong loaded resource; expected "+asset.Bundle+"/"+asset.Name+", actual "+(found==null?"missing":found.Bundle+"/"+found.Name));
            }
            VisualFixture.Save(Path.Combine(output,"editing-validation.json"),new Result{Passed=errors.Count==0,CheckedValues=expected.Values==null?0:expected.Values.Length,CheckedAssets=expected.Assets==null?0:expected.Assets.Length,Errors=errors.ToArray()});
            if(errors.Count>0)throw new InvalidDataException("Edited card values failed game validation: "+string.Join("; ",errors.Take(8).ToArray()));
        }
        private static void AddAssets(string role,ListInfoBase[] infos,List<Asset> result)
        {
            for(int i=0;i<infos.Length;i++)if(infos[i]!=null)result.Add(new Asset{Role=role,Slot=i,Bundle=infos[i].GetInfo(ChaListDefine.KeyType.MainAB),Name=infos[i].GetInfo(ChaListDefine.KeyType.MainData)});
        }
        private static void Flatten(string path,object value,List<Value> values,int depth)
        {
            if(value==null||depth>18)return;
            if(value is Color){var c=(Color)value;Flatten(path,new[]{c.r,c.g,c.b,c.a},values,depth+1);return;}
            if(value is Vector2){var v=(Vector2)value;Flatten(path,new[]{v.x,v.y},values,depth+1);return;}
            if(value is Vector3){var v=(Vector3)value;Flatten(path,new[]{v.x,v.y,v.z},values,depth+1);return;}
            if(value is Vector4){var v=(Vector4)value;Flatten(path,new[]{v.x,v.y,v.z,v.w},values,depth+1);return;}
            var type=value.GetType();
            if(value is string||value is bool||type.IsPrimitive||type.IsEnum||value is decimal)
            {
                bool text=value is string||value is bool;
                values.Add(new Value{Path=path,Kind=text?value is bool?"bool":"text":"number",Text=text?Convert.ToString(value,CultureInfo.InvariantCulture):null,Number=text?0:Convert.ToDouble(value,CultureInfo.InvariantCulture)});return;
            }
            var dictionary=value as IDictionary;
            if(dictionary!=null){foreach(DictionaryEntry item in dictionary)Flatten(path+"{"+item.Key+"}",item.Value,values,depth+1);return;}
            var array=value as Array;
            if(array!=null){int i=0;foreach(var item in array)Flatten(path+(array.Rank>1?"[2]":"")+"["+(i++)+"]",item,values,depth+1);return;}
            foreach(var property in type.GetProperties(BindingFlags.Public|BindingFlags.Instance).Where(p=>p.CanRead&&p.GetIndexParameters().Length==0&&p.Name!="version"))
                Flatten(path+"."+property.Name,property.GetValue(value,null),values,depth+1);
            foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.Instance))Flatten(path+"."+field.Name,field.GetValue(value),values,depth+1);
        }
    }
}
