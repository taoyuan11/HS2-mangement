#include "../../src/HS2Manager.NativeExport/ModelExportInternal.h"
#include "../../src/HS2Manager.NativeExport/OwnedToolProcess.h"
#include <windows.h>
#include <iostream>
#include <thread>
using namespace hs2;
namespace {
int assertions=0;
void Check(bool condition,const char* message){++assertions;if(!condition)throw std::runtime_error(message);}
Json Read(const fs::path& path){return Json::parse(ReadFile(path));}
void Text(const fs::path& path,std::string const& value){WriteAtomic(path,{reinterpret_cast<const uint8_t*>(value.data()),value.size()});}
void Stopped(const fs::path& pidFile){
    Check(fs::is_regular_file(pidFile),"Owned child did not report PID");DWORD pid=Read(pidFile).at("Pid");auto process=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!process){Check(GetLastError()==ERROR_INVALID_PARAMETER,"Cannot verify child state");return;}auto state=WaitForSingleObject(process,0);CloseHandle(process);Check(state==WAIT_OBJECT_0,"Owned child survived parent exit");
}
}
int wmain(int argc,wchar_t** argv){fs::path root;try{
    if(argc!=2)throw std::runtime_error("Usage: hs2_tool_checks <fixture exe>");CoInitializeEx(nullptr,COINIT_MULTITHREADED);auto fixture=fs::absolute(argv[1]);root=fs::temp_directory_path()/Path("HS2 native tool 检查 "+NewId());fs::create_directories(root);
    auto echo=root/L"echo.json";std::vector<std::wstring> args{L"--echo",echo.wstring(),L"",L"two words",L"quote\"inside",L"trailing slash\\",L"three\\\\\\\"quote",L"中文参数",L"$(no shell)",L"`literal`",L"a&b|c>file"};
    Check(RunOwnedTool(fixture,args,root,std::chrono::seconds(10),root/L"echo.log")==0,"Argument fixture failed");auto actual=Read(echo);Check(actual.size()==args.size()-2,"Argument count mismatch");for(size_t i=2;i<args.size();++i)Check(actual.at(i-2)==Utf8(fs::path(args[i])),"Windows argument quoting mismatch");
    auto child=root/L"exited-child.json";Check(RunOwnedTool(fixture,{L"--spawn-exit",child.wstring()},root,std::chrono::seconds(10),root/L"exit.log")==0,"Parent process failed");Stopped(child);
    child=root/L"timeout-child.json";bool timedOut=false;try{RunOwnedTool(fixture,{L"--spawn",child.wstring()},root,std::chrono::milliseconds(900),root/L"timeout.log");}catch(std::runtime_error const& e){timedOut=std::string(e.what()).find("超时")!=std::string::npos;}Check(timedOut,"Timeout was not reported");Stopped(child);
    child=root/L"cancelled-child.json";std::stop_source cancellation;std::jthread cancel([&](std::stop_token stop){auto limit=std::chrono::steady_clock::now()+std::chrono::seconds(5);while(!stop.stop_requested()&&!fs::exists(child)&&std::chrono::steady_clock::now()<limit)std::this_thread::sleep_for(std::chrono::milliseconds(20));cancellation.request_stop();});bool cancelled=false;try{RunOwnedTool(fixture,{L"--spawn",child.wstring()},root,std::chrono::seconds(10),root/L"cancel.log",cancellation.get_token());}catch(Cancelled const&){cancelled=true;}cancel.request_stop();cancel.join();Check(cancelled,"Cancellation was not reported");Stopped(child);
    auto runtime=root/L"runtime",workspace=root/L"workspace",data=root/L"data",source=root/L"source";fs::create_directories(runtime/L"Resources"/L"ModelExport");fs::create_directories(source);
    for(auto name:{L"BlenderNative.py",L"BlenderImport.py",L"UnrealNative.py",L"UnrealImport.py"})Text(runtime/L"Resources"/L"ModelExport"/name,"# Mock process test resource; not executed.\n");
    WriteJson(source/L"materials.json",Json{{"Materials",Json::array({Json{{"Id","material-1"}}})}});Text(source/L"骨骼映射.json","{}");Text(source/L"预览参考.png","test-only PNG sentinel");
    ModelExportOptions options;Check(ModelExportTools::Resolve(options).empty(),"Generic FBX unexpectedly needs a tool");options.Target=ModelExportTarget::Blender;options.BlenderPath=Utf8(fixture);Check(SamePath(Path(ModelExportTools::Resolve(options)),fixture),"Configured tool not resolved");
    for(auto target:{ModelExportTarget::Blender,ModelExportTarget::Unreal}){
        options.Target=target;auto ownedWorkspace=workspace/(target==ModelExportTarget::Blender?L"blender":L"unreal"),output=root/(target==ModelExportTarget::Blender?L"blend-output":L"unreal-output");
        ProbeModelExport(options,fixture,runtime,ownedWorkspace,data);Json result{{"Warnings",Json::array({"fixture warning"})}};ConvertModelExport(options,fixture,runtime,ownedWorkspace,data,source,output,result);
        Check(result["Warnings"].size()==1,"Warnings not deduplicated");Check(result["ToolVersion"]=="test-only native fixture","Verification result not captured");Check(fs::is_regular_file(output/Path(result["EntryPoint"].get<std::string>())),"Entry point missing");Check(fs::is_regular_file(output/L"骨骼映射.json"),"Bone map not carried over");Check(fs::is_regular_file(output/L"native-result.json"),"Reopen verification not saved");
        if(target==ModelExportTarget::Unreal){Check(!fs::exists(output/L"Saved"),"Unreal temp data shipped");auto config=ReadFile(output/L"Config"/L"DefaultEngine.ini");std::string text(config.begin(),config.end());Check(text.find("%ENGINEVERSIONAGNOSTICUSERDIR%DerivedDataCache/HS2Manager")!=text.npos,"Local cache leaked into project");}
    }
    Check(Within(root,fs::temp_directory_path())&&!SamePath(root,fs::temp_directory_path()),"Temporary cleanup escaped root");fs::remove_all(root);CoUninitialize();std::cout<<"PASS native export tools: "<<assertions<<" assertions, argument fidelity, child cleanup, timeout, cancellation, isolated conversion/reopen protocol\n";return 0;
}catch(std::exception const& e){std::cerr<<"FAIL "<<e.what()<<"; fixture directory: "<<Utf8(root)<<'\n';return 1;}}
