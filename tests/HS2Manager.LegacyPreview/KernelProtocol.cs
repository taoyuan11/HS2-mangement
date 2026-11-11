using System;
using System.IO;
using System.Text;

namespace HS2Manager.Preview.Contracts
{
    [Serializable]
    public sealed class KernelMessage
    {
        public int Version = KernelWire.Version;
        public string Session = "";
        public string Kind = "";
        public string RequestId = "";
        public long Load;
        public long Revision;
        public string Path = "";
        public string Text = "";
        public int Width = 1280, Height = 720;
        public float[] Values = new float[0];
        public bool Playing;
    }
    public static class KernelWire
    {
        public const int Version = 4, MaxWidth = 1920, MaxHeight = 1080, Header = 64;
        public const long MapBytes = Header + MaxWidth * MaxHeight * 4;
        public static void Write(Stream stream, string json)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(json);
            if(bytes.Length>1048576)throw new IOException("Command exceeds protocol limit.");
            byte[] length=BitConverter.GetBytes(bytes.Length);
            stream.Write(length,0,4);stream.Write(bytes,0,bytes.Length);stream.Flush();
        }
        public static string Read(Stream stream)
        {
            byte[] length=new byte[4];ReadAll(stream,length);
            int count=BitConverter.ToInt32(length,0);
            if(count<2 || count>1048576)throw new IOException("Invalid command size.");
            byte[] bytes=new byte[count];ReadAll(stream,bytes);return Encoding.UTF8.GetString(bytes);
        }
        private static void ReadAll(Stream stream,byte[] buffer)
        {
            int offset=0;while(offset<buffer.Length){int count=stream.Read(buffer,offset,buffer.Length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}
        }
    }
}
