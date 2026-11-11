#include "OwnedToolProcess.h"
#include <windows.h>
#include <system_error>
#include <thread>

namespace hs2 {
namespace {
struct Handle {
    HANDLE Value=nullptr;
    explicit Handle(HANDLE value=nullptr):Value(value){}
    ~Handle(){if(Value&&Value!=INVALID_HANDLE_VALUE)CloseHandle(Value);}
    Handle(const Handle&)=delete;Handle& operator=(const Handle&)=delete;
};
void Require(bool value,const char* context){if(!value)throw std::system_error(static_cast<int>(GetLastError()),std::system_category(),context);}
std::wstring Quote(std::wstring_view value){
    if(value.find(L'\0')!=value.npos)throw std::invalid_argument("Invalid process argument");
    std::wstring text=L"\"";size_t slashes=0;
    for(auto c:value){if(c==L'\\'){++slashes;continue;}text.append(slashes*(c==L'"'?2:1)+(c==L'"'?1:0),L'\\');text+=c;slashes=0;}
    text.append(slashes*2,L'\\');text+=L'"';return text;
}
struct Attributes {
    std::vector<std::byte> Buffer;LPPROC_THREAD_ATTRIBUTE_LIST Value=nullptr;
    Attributes(){SIZE_T bytes=0;InitializeProcThreadAttributeList(nullptr,2,0,&bytes);Buffer.resize(bytes);Value=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(Buffer.data());Require(InitializeProcThreadAttributeList(Value,2,0,&bytes)!=FALSE,"Create process attributes");}
    ~Attributes(){if(Value)DeleteProcThreadAttributeList(Value);}
};
struct OwnedJob {
    Handle Job{CreateJobObjectW(nullptr,nullptr)};Handle Process,Thread;
    OwnedJob(){Require(Job.Value!=nullptr,"Create export process job");JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;Require(SetInformationJobObject(Job.Value,JobObjectExtendedLimitInformation,&limits,sizeof(limits))!=FALSE,"Set export process lifetime");}
    void Reap(){
        // ActiveProcesses can reach zero before all process handles are signalled.
        // Keep handles to the job's children so callers observe completed teardown.
        std::vector<std::byte> processIds(sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST)+128*sizeof(ULONG_PTR));
        for(;;){auto list=reinterpret_cast<JOBOBJECT_BASIC_PROCESS_ID_LIST*>(processIds.data());if(QueryInformationJobObject(Job.Value,JobObjectBasicProcessIdList,list,static_cast<DWORD>(processIds.size()),nullptr))break;auto error=GetLastError();if(error!=ERROR_MORE_DATA)Require(false,"Enumerate export process descendants");processIds.resize(processIds.size()*2);}
        auto list=reinterpret_cast<JOBOBJECT_BASIC_PROCESS_ID_LIST*>(processIds.data());
        std::vector<std::unique_ptr<Handle>> descendants;
        for(DWORD i=0;i<list->NumberOfProcessIdsInList;++i){auto handle=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,static_cast<DWORD>(list->ProcessIdList[i]));if(!handle)continue;BOOL belongs=FALSE;if(IsProcessInJob(handle,Job.Value,&belongs)&&belongs)descendants.push_back(std::make_unique<Handle>(handle));else CloseHandle(handle);}
        Require(TerminateJobObject(Job.Value,ERROR_CANCELLED)!=FALSE,"Stop export process descendants");
        if(Process.Value)Require(WaitForSingleObject(Process.Value,10000)==WAIT_OBJECT_0,"Wait for export process exit");
        for(auto const& descendant:descendants)Require(WaitForSingleObject(descendant->Value,10000)==WAIT_OBJECT_0,"Wait for export descendant exit");
        auto end=std::chrono::steady_clock::now()+std::chrono::seconds(10);
        for(;;){JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info{};Require(QueryInformationJobObject(Job.Value,JobObjectBasicAccountingInformation,&info,sizeof(info),nullptr)!=FALSE,"Verify export process cleanup");if(!info.ActiveProcesses)return;if(std::chrono::steady_clock::now()>=end)throw std::runtime_error("模型转换子进程尚未退出。");std::this_thread::sleep_for(std::chrono::milliseconds(20));}
    }
    ~OwnedJob(){if(Job.Value)TerminateJobObject(Job.Value,ERROR_CANCELLED);if(Process.Value)WaitForSingleObject(Process.Value,10000);}
};
}
int RunOwnedTool(const fs::path& executable,const std::vector<std::wstring>& arguments,const fs::path& directory,
    std::chrono::milliseconds timeout,const fs::path& log,std::stop_token cancellation){
    CheckCancelled(cancellation);if(timeout.count()<=0)throw std::invalid_argument("Invalid process timeout");
    auto full=fs::absolute(executable);if(!fs::is_regular_file(full)||!fs::is_directory(directory))throw std::runtime_error("模型转换程序或工作目录不存在。");
    OwnedJob owned;Attributes attributes;
    fs::create_directories(log.parent_path());SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};
    Handle output(CreateFileW(log.c_str(),GENERIC_WRITE,FILE_SHARE_READ,&security,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));Require(output.Value!=INVALID_HANDLE_VALUE,"Create export process log");
    Handle input(CreateFileW(L"NUL",GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));Require(input.Value!=INVALID_HANDLE_VALUE,"Create export process input");
    HANDLE inherited[]={output.Value,input.Value};
    Require(UpdateProcThreadAttribute(attributes.Value,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr)!=FALSE,"Limit inherited handles");
    Require(UpdateProcThreadAttribute(attributes.Value,0,PROC_THREAD_ATTRIBUTE_JOB_LIST,&owned.Job.Value,sizeof(HANDLE),nullptr,nullptr)!=FALSE,"Attach export process job");
    STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.StartupInfo.dwFlags=STARTF_USESHOWWINDOW|STARTF_USESTDHANDLES;startup.StartupInfo.wShowWindow=SW_HIDE;startup.StartupInfo.hStdInput=input.Value;startup.StartupInfo.hStdOutput=output.Value;startup.StartupInfo.hStdError=output.Value;startup.lpAttributeList=attributes.Value;
    auto command=Quote(full.wstring());for(auto const& argument:arguments)command+=L" "+Quote(argument);
    if(command.size()>=32767)throw std::invalid_argument("模型转换命令过长。");
    PROCESS_INFORMATION process{};Require(CreateProcessW(full.c_str(),command.data(),nullptr,nullptr,TRUE,CREATE_SUSPENDED|CREATE_NO_WINDOW|EXTENDED_STARTUPINFO_PRESENT,nullptr,directory.c_str(),&startup.StartupInfo,&process)!=FALSE,"Start model conversion");
    owned.Process.Value=process.hProcess;owned.Thread.Value=process.hThread;
    auto ledger="Owned process PID: "+std::to_string(process.dwProcessId)+"\r\n";DWORD written{};WriteFile(output.Value,ledger.data(),static_cast<DWORD>(ledger.size()),&written,nullptr);
    Require(ResumeThread(process.hThread)!=DWORD(-1),"Resume model conversion");
    auto begin=std::chrono::steady_clock::now();int result=0;
    try{
        for(;;){CheckCancelled(cancellation);auto wait=WaitForSingleObject(process.hProcess,100);if(wait==WAIT_OBJECT_0)break;Require(wait!=WAIT_FAILED,"Wait for model conversion");if(std::chrono::steady_clock::now()-begin>=timeout)throw std::runtime_error("模型转换超时，已回收转换进程。日志："+Utf8(log));}
        DWORD code{};Require(GetExitCodeProcess(process.hProcess,&code)!=FALSE,"Read conversion exit status");result=static_cast<int>(code);
        owned.Reap();
    }catch(...){owned.Reap();throw;}
    return result;
}
}
