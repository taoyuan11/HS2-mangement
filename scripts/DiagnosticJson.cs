using System;
using System.Text;

namespace HS2Manager.BuildTools
{
    public static class DiagnosticJson
    {
        // Unity's historical DataContractJsonSerializer writes bare INF/NaN.
        // Preserve those diagnostic values as named strings for strict readers;
        // the original evidence file is never rewritten.
        public static string QuoteNonFinite(string text)
        {
            var result=new StringBuilder(text.Length);bool quoted=false,escaped=false;
            string[] tokens={"-Infinity","Infinity","-INF","INF","NaN"};
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];
                if(quoted)
                {
                    result.Append(c);
                    if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='"')quoted=false;
                    continue;
                }
                if(c=='"'){quoted=true;result.Append(c);continue;}
                string found=null;
                foreach(string token in tokens)
                {
                    int end=i+token.Length;
                    if(end>text.Length||string.CompareOrdinal(text,i,token,0,token.Length)!=0)continue;
                    if(end==text.Length||",]} \t\r\n".IndexOf(text[end])>=0){found=token;break;}
                }
                if(found==null)result.Append(c);
                else{result.Append('"').Append(found).Append('"');i+=found.Length-1;}
            }
            return result.ToString();
        }
    }
}
