using System.Text;

namespace HS2Manager.Export;

/// <summary>Portable linear HDR images; input rows are ordered from the top of the image.</summary>
public static class NativeHdrImages
{
    private static void Validate(int width,int height,float[] pixels)
    {
        if(width<1||height<1||pixels.Length!=checked(width*height*4)||pixels.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid HDR image.");
    }
    public static void WriteRadiance(string path,int width,int height,float[] pixels)
    {
        Validate(width,height,pixels);if(width is <8 or >32767)throw new ArgumentOutOfRangeException(nameof(width));
        using var stream=File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n"));
        var row=new byte[width*4];
        for(int y=0;y<height;y++)
        {
            for(int x=0;x<width;x++)
            {
                int i=(y*width+x)*4;float maximum=Math.Max(pixels[i],Math.Max(pixels[i+1],pixels[i+2]));
                int exponent=maximum<1e-32f?0:(int)Math.Floor(Math.Log2(maximum))+1;
                double scale=maximum<1e-32f?0:256/Math.Pow(2,exponent);
                for(int c=0;c<3;c++)row[c*width+x]=(byte)Math.Clamp(pixels[i+c]*scale,0,255);
                row[3*width+x]=maximum<1e-32f?(byte)0:checked((byte)(exponent+128));
            }
            stream.Write([2,2,(byte)(width>>8),(byte)width]);
            for(int c=0;c<4;c++)for(int x=0;x<width;x+=128)
            {int length=Math.Min(128,width-x);stream.WriteByte((byte)length);stream.Write(row,c*width+x,length);}
        }
    }
    public static void WriteOpenExr(string path,int width,int height,float[] pixels)
    {
        Validate(width,height,pixels);
        // Single-part, uncompressed HALF scanlines: https://openexr.com/en/latest/OpenEXRFileLayout.html
        using var stream=File.Create(path);using var writer=new BinaryWriter(stream,Encoding.ASCII,true);
        writer.Write(20000630);writer.Write(2);
        void Text(string value){writer.Write(Encoding.ASCII.GetBytes(value));writer.Write((byte)0);}
        void Attribute(string name,string type,Action<BinaryWriter> value)
        {
            using var memory=new MemoryStream();using(var buffer=new BinaryWriter(memory,Encoding.ASCII,true))value(buffer);
            Text(name);Text(type);writer.Write(checked((int)memory.Length));writer.Write(memory.ToArray());
        }
        string[] channels=["A","B","G","R"];
        Attribute("channels","chlist",b=>{foreach(string channel in channels){b.Write(Encoding.ASCII.GetBytes(channel));b.Write((byte)0);b.Write(1);b.Write(0);b.Write(1);b.Write(1);}b.Write((byte)0);});
        Attribute("compression","compression",b=>b.Write((byte)0));
        void Window(BinaryWriter b){b.Write(0);b.Write(0);b.Write(width-1);b.Write(height-1);}
        Attribute("dataWindow","box2i",Window);Attribute("displayWindow","box2i",Window);Attribute("lineOrder","lineOrder",b=>b.Write((byte)0));
        Attribute("pixelAspectRatio","float",b=>b.Write(1f));Attribute("screenWindowCenter","v2f",b=>{b.Write(0f);b.Write(0f);});Attribute("screenWindowWidth","float",b=>b.Write(1f));writer.Write((byte)0);
        long first=stream.Position+height*8L;int rowBytes=checked(width*8);
        for(int y=0;y<height;y++)writer.Write(first+y*(rowBytes+8L));
        int[] components=[3,2,1,0];
        for(int y=0;y<height;y++)
        {
            writer.Write(y);writer.Write(rowBytes);
            foreach(int c in components)for(int x=0;x<width;x++)
            {
                float value=pixels[(y*width+x)*4+c];if(Math.Abs(value)>65504)throw new InvalidDataException("HDR sample exceeds HALF range.");
                writer.Write(BitConverter.HalfToUInt16Bits((Half)value));
            }
        }
    }
}
