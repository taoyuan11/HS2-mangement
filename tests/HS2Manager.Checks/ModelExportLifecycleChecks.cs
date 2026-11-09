using System.Diagnostics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class ModelExportLifecycleChecks
{
    internal static async Task Run(string player,string definition,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        for(int mode=0;mode<2;mode++)
        {
            string folder=Path.Combine(output,mode==0?"cancel":"kernel-exit");Directory.CreateDirectory(folder);
            string request=Path.Combine(folder,"request.json");
            File.WriteAllText(request,JsonSerializer.Serialize(new ModelExportRequest{DefinitionPath=Path.GetFullPath(definition),OutputDirectory=folder},UnityCharacterService.JsonOptions));
            int pid=0;bool reachedBuild=false;
            using(var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            using(var session=new UnityPreviewSession())
            {
                await session.StartAsync(player,Path.Combine(output,"Data"),cancellation.Token);pid=session.ProcessId;
                bool rejected=false;
                try{await session.ExportModelAsync(Path.Combine(folder,"missing-request.json"),cancellation.Token);}
                catch(IOException){rejected=true;}
                if(!rejected||!session.IsUsable)throw new Exception("Export request failure damaged the kernel session.");
                session.Progress+=message=>
                {
                    if(reachedBuild||!message.Text.StartsWith("构建角色部件",StringComparison.Ordinal))return;
                    reachedBuild=true;
                    if(mode==0)cancellation.Cancel();
                    else Process.GetProcessById(pid).Kill();
                };
                bool stopped=false;
                try{await session.ExportModelAsync(request,cancellation.Token);}
                catch(OperationCanceledException)when(mode==0){stopped=true;}
                catch(IOException)when(mode==1){stopped=true;}
                if(!stopped||!reachedBuild)throw new Exception("Export lifecycle scenario was not exercised.");
            }
            try{using var process=Process.GetProcessById(pid);if(!process.HasExited)throw new Exception("Export kernel leaked after cancellation/failure.");}
            catch(ArgumentException){}
            if(File.Exists(Path.Combine(folder,"model-result.json")))throw new Exception("Cancelled/failed export was published.");
            Console.WriteLine("PASS model kernel "+(mode==0?"cancellation":"exit")+", request failure and process cleanup");
        }
    }
}
