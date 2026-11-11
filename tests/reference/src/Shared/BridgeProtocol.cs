using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace HS2Manager.Protocol
{
    [DataContract]
    public sealed class BridgeMessage
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Kind = "";
        [DataMember] public string Id = "";
        [DataMember] public string Path = "";
        [DataMember] public string Text = "";
        [DataMember] public int Sex = 1;
        [DataMember] public float Yaw;
        [DataMember] public float Pitch = 8;
        [DataMember] public float Distance = 3.2f;
        [DataMember] public float PanX;
        [DataMember] public float PanY = 0.95f;
        [DataMember] public float PanZ;
        [DataMember] public float Light = 1;
        [DataMember] public float R = .16f;
        [DataMember] public float G = .17f;
        [DataMember] public float B = .19f;
    }
    public static class BridgeWire
    {
        public const int Width = 1280, Height = 720, FrameBytes = Width * Height * 4, Header = 64;
        public const long MapBytes = Header + (long)FrameBytes * 2;
        public static void Write(Stream stream, BridgeMessage value)
        {
            using (var data = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(BridgeMessage)).WriteObject(data, value);
                var bytes = data.ToArray(); var len = BitConverter.GetBytes(bytes.Length);
                stream.Write(len, 0, 4); stream.Write(bytes, 0, bytes.Length); stream.Flush();
            }
        }
        public static BridgeMessage Read(Stream stream)
        {
            var len = new byte[4]; ReadExact(stream, len); var n = BitConverter.ToInt32(len, 0);
            if (n < 1 || n > 1024 * 1024) throw new InvalidDataException("Invalid bridge message size");
            var bytes = new byte[n]; ReadExact(stream, bytes);
            using (var data = new MemoryStream(bytes))
            {
                var value = new DataContractJsonSerializer(typeof(BridgeMessage)).ReadObject(data) as BridgeMessage;
                if (value == null) throw new InvalidDataException("Empty bridge message");
                if (value.Version != 1) throw new InvalidDataException("Bridge version mismatch");
                return value;
            }
        }
        private static void ReadExact(Stream stream, byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length) { int n = stream.Read(bytes, offset, bytes.Length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
        }
    }
}
