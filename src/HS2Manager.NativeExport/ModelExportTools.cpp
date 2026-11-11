#include "ModelExportInternal.h"
#include "OwnedToolProcess.h"
#include <windows.h>
#include <ShlObj.h>
#include <algorithm>
#include <cwctype>
#include <set>

namespace hs2 {
namespace {
std::wstring Environment(const wchar_t* name){auto size=GetEnvironmentVariableW(name,nullptr,0);if(!size)return {};std::wstring value(size,L'\0');auto actual=GetEnvironmentVariableW(name,value.data(),size);if(!actual||actual>=size)return {};value.resize(actual);return value;}
std::wstring Lower(std::wstring value){std::transform(value.begin(),value.end(),value.begin(),[](wchar_t c){return static_cast<wchar_t>(std::towlower(c));});return value;}
struct RegistryKey{HKEY Value{};~RegistryKey(){if(Value)RegCloseKey(Value);}};
std::wstring RegistryString(HKEY key,const wchar_t* name){DWORD bytes=0;if(RegGetValueW(key,nullptr,name,RRF_RT_REG_SZ|RRF_RT_REG_EXPAND_SZ,nullptr,nullptr,&bytes)!=ERROR_SUCCESS)return {};std::wstring value(bytes/sizeof(wchar_t),L'\0');if(RegGetValueW(key,nullptr,name,RRF_RT_REG_SZ|RRF_RT_REG_EXPAND_SZ,nullptr,value.data(),&bytes)!=ERROR_SUCCESS)return {};while(!value.empty()&&value.back()==L'\0')value.pop_back();return value;}
fs::path KnownFolder(REFKNOWNFOLDERID id){PWSTR value{};if(FAILED(SHGetKnownFolderPath(id,0,nullptr,&value)))return {};fs::path path(value);CoTaskMemFree(value);return path;}
void CandidateDirectories(std::vector<fs::path>& paths,const fs::path& root,ModelExportTarget target,bool prefix=false){
    std::error_code error;if(root.empty()||!fs::is_directory(root,error))return;std::vector<fs::path> directories;
    for(fs::directory_iterator it(root,fs::directory_options::skip_permission_denied,error),end;!error&&it!=end;it.increment(error))if(it->is_directory(error)&&(!prefix||Lower(it->path().filename().wstring()).starts_with(L"blender")))directories.push_back(it->path());
    std::sort(directories.begin(),directories.end(),[](auto const& a,auto const& b){return Lower(a.wstring())>Lower(b.wstring());});
    for(auto const& path:directories)paths.push_back(target==ModelExportTarget::Blender?path/L"blender.exe":path/L"Engine"/L"Binaries"/L"Win64"/L"UnrealEditor-Cmd.exe");
}
void WriteText(const fs::path& path,std::string const& value){WriteAtomic(path,{reinterpret_cast<const std::uint8_t*>(value.data()),value.size()});}
Json ReadJson(const fs::path& path,std::stop_token token){auto bytes=ReadFile(path,16ull*1024*1024,token);return Json::parse(bytes);}
void RemoveOwned(const fs::path& owner,const fs::path& path){
    auto root=fs::absolute(owner).lexically_normal(),target=fs::absolute(path).lexically_normal();
    if(SamePath(root,target)||!Within(target,root))throw std::runtime_error("拒绝清理工作目录之外的文件。");
    std::error_code error;if(!fs::exists(target,error))return;
    for(auto current=target;!SamePath(current,root);current=current.parent_path()){
        auto attributes=GetFileAttributesW(current.c_str());if(attributes!=INVALID_FILE_ATTRIBUTES&&(attributes&FILE_ATTRIBUTE_REPARSE_POINT))throw std::runtime_error("转换临时目录不能包含链接。");
        if(current==current.parent_path())throw std::runtime_error("转换临时目录越界。");
    }
    fs::remove_all(target);
}
constexpr std::string_view CachePlaceholder="%ENGINEVERSIONAGNOSTICUSERDIR%DerivedDataCache/HS2Manager";
void CreateProject(const fs::path& directory,const fs::path& cache){
    fs::create_directories(directory/L"Config");
    WriteText(directory/L"HS2Character.uproject",R"({"FileVersion":3,"Plugins":[{"Name":"PythonScriptPlugin","Enabled":true},{"Name":"EditorScriptingUtilities","Enabled":true}]})");
    std::string config=R"([/Script/EngineSettings.GameMapsSettings]
EditorStartupMap=/Game/HS2Character/Preview
GameDefaultMap=/Game/HS2Character/Preview
[/Script/Engine.RendererSettings]
r.Substrate=False
r.AntiAliasingMethod=4
r.RayTracing=False
r.AllowStaticLighting=False
r.DefaultFeature.AutoExposure=False
r.UsePreExposure=False
r.DynamicGlobalIlluminationMethod=0
r.ReflectionMethod=0
[Zen.AutoLaunch]
AutoLaunch=False
[DerivedDataBackendGraph]
Root=(Type=KeyLength, Length=120, Inner=Local)
Local=(Type=FileSystem, ReadOnly=false, Clean=false, Flush=false, Path="%ENGINEVERSIONAGNOSTICUSERDIR%DerivedDataCache/HS2Manager")
)";
    auto path=Utf8(fs::absolute(cache));std::replace(path.begin(),path.end(),'\\','/');if(path.find_first_of("\"\r\n")!=path.npos)throw std::runtime_error("无效的模型导出缓存路径。");fs::create_directories(cache);config.replace(config.find(CachePlaceholder),CachePlaceholder.size(),path);WriteText(directory/L"Config"/L"DefaultEngine.ini",config);
}
fs::path ScriptName(ModelExportOptions const& options){return options.Target==ModelExportTarget::Blender?L"BlenderNative.py":L"UnrealNative.py";}
void PrepareScripts(ModelExportOptions const& options,const fs::path& runtime,const fs::path& workspace){
    auto scripts=workspace/L"converter";fs::create_directories(scripts);
    for(auto const& name:{ScriptName(options),fs::path(options.Target==ModelExportTarget::Blender?L"BlenderImport.py":L"UnrealImport.py")}){
        auto source=runtime/L"Resources"/L"ModelExport"/name;if(!fs::is_regular_file(source))throw std::runtime_error("模型导出组件缺失："+Utf8(source));
        auto destination=scripts/name;if(!fs::exists(destination))fs::copy_file(source,destination);else if(HashFile(source)!=HashFile(destination))throw std::runtime_error("转换组件在运行期间发生变化。");
    }
}
Json RunConverter(const ModelExportOptions& options,const fs::path& executable,const fs::path& workspace,const fs::path& data,
    std::string const& mode,const fs::path& source,const fs::path& output,Json const& materialIds,std::stop_token token){
    CheckCancelled(token);auto scripts=workspace/L"converter",resultPath=scripts/Path(mode+"-result.json");
    if(fs::exists(resultPath))fs::remove(resultPath);
    WriteJson(scripts/L"request.json",Json{{"Mode",mode},{"Source",source.empty()?"":Utf8(fs::absolute(source))},{"Output",Utf8(fs::absolute(output))},{"Result",Utf8(fs::absolute(resultPath))},{"MaterialIds",materialIds}});
    auto name=options.Target==ModelExportTarget::Blender?"Blender":"Unreal";auto log=data/L"Logs"/L"ModelExport"/Path(std::string(name)+"-"+mode+"-"+NewId()+".log");fs::create_directories(log.parent_path());auto script=fs::absolute(scripts/ScriptName(options));std::vector<std::wstring> args;
    if(options.Target==ModelExportTarget::Blender)args={L"--background",L"--factory-startup",L"--python-exit-code",L"1",L"--log-file",log.wstring(),L"--python",script.wstring()};
    else{
        args={fs::absolute(output/L"HS2Character.uproject").wstring()};
        if(mode=="convert"){args.push_back(L"-ExecutePythonScript="+script.wstring());args.insert(args.end(),{L"-windowed",L"-ResX=1280",L"-ResY=960"});}
        else{args.push_back(L"-run=pythonscript");args.push_back(L"-script="+script.wstring());}
        args.insert(args.end(),{L"-unattended",L"-nop4",L"-nosplash",L"-nosound",L"-NoLiveCoding",L"-NoZenAutoLaunch",L"-notrace",L"-notraceserver",L"-ddc=DerivedDataBackendGraph",L"-abslog="+log.wstring()});
    }
    auto code=RunOwnedTool(executable,args,fs::absolute(output),std::chrono::seconds(options.ToolTimeoutSeconds),fs::path(log.wstring()+L".process.log"),token);
    if(!fs::is_regular_file(resultPath))throw std::runtime_error(ModelExportTools::Name(options.Target)+" "+mode+" 失败（退出码 "+std::to_string(code)+"）。日志："+Utf8(log));
    auto result=ReadJson(resultPath,token);if(code!=0||!result.is_object()||!result.value("Passed",false))throw std::runtime_error(ModelExportTools::Name(options.Target)+" "+mode+" 失败："+result.value("Error","进程退出码 "+std::to_string(code))+"\n日志："+Utf8(log));return result;
}
}

std::string ModelExportTools::Name(ModelExportTarget target){switch(target){case ModelExportTarget::GenericFbx:return "通用 3D 模型（FBX）";case ModelExportTarget::Blender:return "Blender 3D 模型";case ModelExportTarget::Unreal:return "Unreal 3D 模型";default:throw std::invalid_argument("无效模型导出类型");}}
std::string ModelExportTools::Detect(ModelExportTarget target){
    if(target==ModelExportTarget::GenericFbx)return {};static_cast<void>(Name(target));std::vector<fs::path> candidates;
    auto programFiles=Environment(L"ProgramFiles");CandidateDirectories(candidates,fs::path(programFiles)/(target==ModelExportTarget::Blender?L"Blender Foundation":L"Epic Games"),target);
    if(target==ModelExportTarget::Unreal){RegistryKey key;if(RegOpenKeyExW(HKEY_CURRENT_USER,L"Software\\Epic Games\\Unreal Engine\\Builds",0,KEY_READ,&key.Value)==ERROR_SUCCESS)for(DWORD i=0;;++i){wchar_t name[16384];DWORD length=16384;auto code=RegEnumValueW(key.Value,i,name,&length,nullptr,nullptr,nullptr,nullptr);if(code==ERROR_NO_MORE_ITEMS)break;if(code!=ERROR_SUCCESS)continue;auto location=RegistryString(key.Value,name);if(!location.empty())candidates.push_back(fs::path(location)/L"Engine"/L"Binaries"/L"Win64"/L"UnrealEditor-Cmd.exe");}}
    else{
        RegistryKey key;if(RegOpenKeyExW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",0,KEY_READ|KEY_WOW64_64KEY,&key.Value)==ERROR_SUCCESS)for(DWORD i=0;;++i){wchar_t name[512];DWORD length=512;auto code=RegEnumKeyExW(key.Value,i,name,&length,nullptr,nullptr,nullptr,nullptr);if(code==ERROR_NO_MORE_ITEMS)break;if(code!=ERROR_SUCCESS)continue;RegistryKey item;if(RegOpenKeyExW(key.Value,name,0,KEY_READ,&item.Value)!=ERROR_SUCCESS)continue;if(Lower(RegistryString(item.Value,L"DisplayName")).starts_with(L"blender")){auto path=RegistryString(item.Value,L"InstallLocation");if(!path.empty())candidates.push_back(fs::path(path)/L"blender.exe");}}
        CandidateDirectories(candidates,KnownFolder(FOLDERID_Desktop),target,true);CandidateDirectories(candidates,KnownFolder(FOLDERID_Downloads),target,true);
    }
    auto path=Environment(L"PATH");size_t offset=0;while(offset<path.size()){auto end=path.find(L';',offset);auto part=path.substr(offset,end==path.npos?path.npos:end-offset);if(part.size()>1&&part.front()==L'"'&&part.back()==L'"')part=part.substr(1,part.size()-2);if(!part.empty())candidates.push_back(fs::path(part)/(target==ModelExportTarget::Blender?L"blender.exe":L"UnrealEditor-Cmd.exe"));if(end==path.npos)break;offset=end+1;}
    for(auto const& candidate:candidates){std::error_code error;if(fs::is_regular_file(candidate,error))return Utf8(fs::absolute(candidate));}return {};
}
std::string ModelExportTools::Resolve(ModelExportOptions const& options){
    if(options.Target==ModelExportTarget::GenericFbx)return {};static_cast<void>(Name(options.Target));auto configured=options.Target==ModelExportTarget::Blender?options.BlenderPath:options.UnrealEditorPath;
    auto begin=configured.find_first_not_of(" \t\r\n\""),end=configured.find_last_not_of(" \t\r\n\"");configured=begin==configured.npos?Detect(options.Target):configured.substr(begin,end-begin+1);
    auto path=Path(configured);if(options.Target==ModelExportTarget::Unreal&&Lower(path.filename().wstring())==L"unrealeditor.exe")path=path.parent_path()/L"UnrealEditor-Cmd.exe";
    std::error_code error;if(path.empty()||!fs::is_regular_file(path,error))throw std::runtime_error("未找到 "+Name(options.Target)+" 所需的软件。请在设置 → 模型导出中选择"+(options.Target==ModelExportTarget::Blender?" blender.exe。":" UnrealEditor-Cmd.exe。"));return Utf8(fs::absolute(path));
}
void ProbeModelExport(const ModelExportOptions& options,const fs::path& executable,const fs::path& runtime,const fs::path& workspace,const fs::path& data,std::stop_token token){
    if(options.Target==ModelExportTarget::GenericFbx)return;CheckCancelled(token);PrepareScripts(options,runtime,workspace);auto probe=workspace/L"probe";fs::create_directories(probe);if(options.Target==ModelExportTarget::Unreal)CreateProject(probe,data/L"ModelExport"/L"cache"/L"Unreal");RunConverter(options,executable,workspace,data,"probe",{},probe,Json::array(),token);
}
void ConvertModelExport(const ModelExportOptions& options,const fs::path& executable,const fs::path& runtime,const fs::path& workspace,const fs::path& data,const fs::path& source,const fs::path& output,Json& result,std::stop_token token){
    if(options.Target==ModelExportTarget::GenericFbx)return;PrepareScripts(options,runtime,workspace);fs::create_directories(output);if(options.Target==ModelExportTarget::Unreal)CreateProject(output,data/L"ModelExport"/L"cache"/L"Unreal");auto materials=ReadJson(source/L"materials.json",token);Json ids=Json::array();for(auto const& material:materials.at("Materials"))ids.push_back(material.at("Id"));
    RunConverter(options,executable,workspace,data,"convert",source,output,ids,token);auto verified=RunConverter(options,executable,workspace,data,"verify",{},output,ids,token);
    result["Target"]=options.Target==ModelExportTarget::Blender?"Blender":"Unreal";result["ToolVersion"]=verified.value("ToolVersion","");result["EntryPoint"]=options.Target==ModelExportTarget::Blender?"character.blend":"HS2Character.uproject";
    if(!result.contains("Warnings")||!result["Warnings"].is_array())result["Warnings"]=Json::array();if(verified.contains("Warnings"))for(auto const& warning:verified.at("Warnings"))if(std::find(result["Warnings"].begin(),result["Warnings"].end(),warning)==result["Warnings"].end())result["Warnings"].push_back(warning);
    WriteJson(output/L"native-result.json",verified);for(auto name:{L"骨骼映射.json",L"预览参考.png"})if(fs::is_regular_file(source/name))fs::copy_file(source/name,output/name);
    if(options.Target==ModelExportTarget::Unreal){for(auto name:{L"Saved",L"Intermediate",L"DerivedDataCache"})RemoveOwned(output,output/name);auto config=ReadFile(output/L"Config"/L"DefaultEngine.ini");std::string text(config.begin(),config.end());auto cache=Utf8(fs::absolute(data/L"ModelExport"/L"cache"/L"Unreal"));std::replace(cache.begin(),cache.end(),'\\','/');auto pos=text.find(cache);if(pos!=text.npos)text.replace(pos,cache.size(),CachePlaceholder);WriteText(output/L"Config"/L"DefaultEngine.ini",text);}
    std::string instructions=options.Target==ModelExportTarget::Blender?"打开 character.blend。贴图和环境资源已打包；默认使用 Eevee 和场景灯光。角色、骨架与预览场景分组，可单独隐藏灯光和相机。材质节点、骨架和表情均可编辑。\n":"使用生成版本的 Unreal Engine 打开 HS2Character.uproject，默认关卡为 Preview。角色资产位于 Content/HS2Character，可通过 Asset Actions → Migrate 迁移到自己的项目。预览设置只属于本项目。\n";
    WriteText(output/L"导入说明.txt",instructions+"生成版本："+result.value("ToolVersion","")+"\n保留参考姿态、骨架、蒙皮和默认表情；不包含待机动画或物理组件。专用材质及跨引擎光照存在近似，见导出报告。无需运行 Python 脚本。\n");CheckCancelled(token);
}
}
