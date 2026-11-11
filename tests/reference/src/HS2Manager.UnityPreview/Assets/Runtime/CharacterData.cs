using System.IO;
using System.Runtime.Serialization.Json;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.UnityPreview
{
    internal static class CharacterData
    {
        internal static CharacterDefinition Read(string path)
        {
            // Unity's serializer truncates recursively nested classes. Parameter trees must
            // preserve deep expression tables, physics patterns and post-processing curves.
            using(var stream=File.OpenRead(path))
            {
                var result=(CharacterDefinition)new DataContractJsonSerializer(typeof(CharacterDefinition)).ReadObject(stream);
                if(result.ParameterNodes>0&&result.CountParameterNodes()!=result.ParameterNodes)throw new InvalidDataException("人物参数树不完整。");
                return result;
            }
        }
    }
}
