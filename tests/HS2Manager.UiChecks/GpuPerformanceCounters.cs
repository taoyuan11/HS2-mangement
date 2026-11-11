using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// PDH records only the explicitly supplied owned PIDs. No system-wide process
// lifecycle control is performed by this read-only performance collector.
internal sealed class GpuPerformanceCounters:IDisposable
{
    private nint query;
    private readonly Dictionary<string,nint> counters=[];
    public Dictionary<string,string> Errors {get;}=[];
    public GpuPerformanceCounters()
    {
        Check(PdhOpenQuery(null,0,out query));
        Add("engines",@"\GPU Engine(*)\Utilization Percentage");
        Add("dedicated",@"\GPU Process Memory(*)\Dedicated Usage");
        Add("shared",@"\GPU Process Memory(*)\Shared Usage");
        Add("adapterDedicated",@"\GPU Adapter Memory(*)\Dedicated Usage");
        Check(PdhCollectQueryData(query));
    }
    private void Add(string key,string path)
    {
        uint status=PdhAddEnglishCounter(query,path,0,out var counter);
        if(status==0)counters.Add(key,counter);else Errors[key]="PDH 0x"+status.ToString("X8");
    }
    public object Sample(IReadOnlySet<int> owned)
    {
        Check(PdhCollectQueryData(query));
        var values=counters.ToDictionary(pair=>pair.Key,pair=>Read(pair.Value));
        bool IsOwned(string name)=>Regex.Match(name,@"(?:^|_)pid_(\d+)(?:_|$)")is{Success:true} match&&owned.Contains(int.Parse(match.Groups[1].Value));
        var engineRows=values.GetValueOrDefault("engines")?.Where(p=>IsOwned(p.Key)).ToDictionary(p=>p.Key,p=>p.Value)??[];
        var engines=engineRows.GroupBy(p=>Regex.Replace(p.Key,@"pid_\d+_","")).ToDictionary(p=>p.Key,p=>p.Sum(v=>v.Value));
        double? Total(string key)=>values.TryGetValue(key,out var rows)?rows.Where(p=>IsOwned(p.Key)).Sum(p=>p.Value):null;
        return new{GpuPercent=engines.Count==0?(double?)null:engines.Values.Max(),Engines=engines,DedicatedBytes=Total("dedicated"),SharedBytes=Total("shared"),AdapterDedicated=values.GetValueOrDefault("adapterDedicated"),Errors};
    }
    private static Dictionary<string,double> Read(nint counter)
    {
        uint bytes=0,count=0;uint status=PdhGetFormattedCounterArray(counter,0x200|0x8000,ref bytes,out count,0);
        if(status==0x800007D5||status==0x800007D1)return [];
        if(status!=0x800007D2)Check(status);
        if(bytes==0)return [];
        nint buffer=Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            status=PdhGetFormattedCounterArray(counter,0x200|0x8000,ref bytes,out count,buffer);
            if(status==0x800007D2)return Read(counter);
            Check(status);var result=new Dictionary<string,double>();int stride=Marshal.SizeOf<Item>();
            for(int i=0;i<count;i++)
            {
                var item=Marshal.PtrToStructure<Item>(buffer+i*stride);
                if(item.Value.Status is 0 or 1&&double.IsFinite(item.Value.Number))result[Marshal.PtrToStringUni(item.Name)!]=item.Value.Number;
            }
            return result;
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    private static void Check(uint status){if(status!=0)throw new InvalidOperationException("PDH status 0x"+status.ToString("X8"));}
    public void Dispose(){if(query!=0){PdhCloseQuery(query);query=0;}}
    [StructLayout(LayoutKind.Explicit,Size=16)]private struct Value{[FieldOffset(0)]public uint Status;[FieldOffset(8)]public double Number;}
    [StructLayout(LayoutKind.Sequential)]private struct Item{public nint Name;public Value Value;}
    [DllImport("pdh.dll",EntryPoint="PdhOpenQueryW",CharSet=CharSet.Unicode)]private static extern uint PdhOpenQuery(string? source,nuint data,out nint query);
    [DllImport("pdh.dll",EntryPoint="PdhAddEnglishCounterW",CharSet=CharSet.Unicode)]private static extern uint PdhAddEnglishCounter(nint query,string path,nuint data,out nint counter);
    [DllImport("pdh.dll")]private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll",EntryPoint="PdhGetFormattedCounterArrayW",CharSet=CharSet.Unicode)]private static extern uint PdhGetFormattedCounterArray(nint counter,uint format,ref uint bytes,out uint count,nint buffer);
    [DllImport("pdh.dll")]private static extern uint PdhCloseQuery(nint query);
}
