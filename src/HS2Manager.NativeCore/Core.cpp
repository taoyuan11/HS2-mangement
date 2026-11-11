#include "Core.h"
#include <windows.h>
#include <bcrypt.h>
#include <objbase.h>
#include <algorithm>
#include <array>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <system_error>

namespace hs2 {
namespace {
struct Handle { HANDLE value; ~Handle(){ if(value != INVALID_HANDLE_VALUE) CloseHandle(value); } };
void WinCheck(bool condition, const char* operation) { if(!condition) throw std::system_error(GetLastError(), std::system_category(), operation); }
struct Hasher {
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    Hasher(){ if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0) throw std::runtime_error("SHA256 初始化失败"); if(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)<0){BCryptCloseAlgorithmProvider(algorithm,0);throw std::runtime_error("SHA256 初始化失败");} }
    ~Hasher(){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
    void Add(std::span<const std::uint8_t> data){while(!data.empty()){auto n=static_cast<ULONG>((std::min)(data.size(),std::size_t(131072)));if(BCryptHashData(hash,const_cast<PUCHAR>(data.data()),n,0)<0)throw std::runtime_error("SHA256 计算失败");data=data.subspan(n);}}
    std::string Finish(){std::array<std::uint8_t,32> value{};if(BCryptFinishHash(hash,value.data(),32,0)<0)throw std::runtime_error("SHA256 计算失败");std::string text;constexpr char hex[]="0123456789ABCDEF";for(auto b:value){text+=hex[b>>4];text+=hex[b&15];}return text;}
};
void WriteHandle(HANDLE handle,std::span<const std::uint8_t> bytes){while(!bytes.empty()){DWORD written=0;auto n=static_cast<DWORD>((std::min)(bytes.size(),std::size_t(131072)));WinCheck(WriteFile(handle,bytes.data(),n,&written,nullptr)!=0 && written!=0,"WriteFile");bytes=bytes.subspan(written);}WinCheck(FlushFileBuffers(handle)!=0,"FlushFileBuffers");}
}
void CheckCancelled(std::stop_token token){if(token.stop_requested())throw Cancelled();}
std::string Utf8(const fs::path& path){auto bytes=path.u8string();return {reinterpret_cast<const char*>(bytes.data()),bytes.size()};}
fs::path Path(std::string_view utf8){return fs::path(std::u8string(reinterpret_cast<const char8_t*>(utf8.data()),utf8.size()));}
std::string NewId(){GUID id{};if(FAILED(CoCreateGuid(&id)))throw std::runtime_error("生成标识失败");wchar_t buffer[40];StringFromGUID2(id,buffer,40);std::string result;for(auto c:std::wstring_view(buffer))if(c!='{'&&c!='}'&&c!='-')result+=static_cast<char>(c);return result;}
std::string UtcNow(){SYSTEMTIME t{};GetSystemTime(&t);char buffer[32];sprintf_s(buffer,"%04d-%02d-%02dT%02d:%02d:%02d.%03dZ",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds);return buffer;}
std::int64_t ModifiedTicks(const fs::path& path){WIN32_FILE_ATTRIBUTE_DATA data{};WinCheck(GetFileAttributesExW(path.c_str(),GetFileExInfoStandard,&data)!=0,"GetFileAttributesExW");ULARGE_INTEGER n{};n.LowPart=data.ftLastWriteTime.dwLowDateTime;n.HighPart=data.ftLastWriteTime.dwHighDateTime;return static_cast<std::int64_t>(n.QuadPart)+504911232000000000ll;}
Bytes ReadFile(const fs::path& path,std::size_t limit,std::stop_token token){Handle handle{CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_SEQUENTIAL_SCAN,nullptr)};WinCheck(handle.value!=INVALID_HANDLE_VALUE,"读取文件");LARGE_INTEGER length{};WinCheck(GetFileSizeEx(handle.value,&length)!=0,"读取文件长度");if(length.QuadPart<0||static_cast<std::uint64_t>(length.QuadPart)>limit)throw std::runtime_error("文件超过大小限制");Bytes data(static_cast<std::size_t>(length.QuadPart));std::size_t offset=0;while(offset<data.size()){CheckCancelled(token);DWORD count=0;WinCheck(::ReadFile(handle.value,data.data()+offset,static_cast<DWORD>((std::min)(data.size()-offset,std::size_t(131072))),&count,nullptr)!=0,"ReadFile");if(!count)throw std::runtime_error("文件读取中截断");offset+=count;}return data;}
std::string Sha256(std::span<const std::uint8_t> data){Hasher hash;hash.Add(data);return hash.Finish();}
std::string HashFile(const fs::path& path,std::stop_token token){Handle handle{CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_SEQUENTIAL_SCAN,nullptr)};WinCheck(handle.value!=INVALID_HANDLE_VALUE,"读取校验文件");Hasher hash;std::array<std::uint8_t,131072> data{};for(;;){CheckCancelled(token);DWORD count{};WinCheck(::ReadFile(handle.value,data.data(),static_cast<DWORD>(data.size()),&count,nullptr)!=0,"校验读取");if(!count)break;hash.Add(std::span(data).first(count));}return hash.Finish();}
void WriteAtomic(const fs::path& path,std::span<const std::uint8_t> bytes){fs::create_directories(path.parent_path());auto temporary=path;temporary+=Path("."+NewId()+".tmp");try{{Handle handle{CreateFileW(temporary.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr)};WinCheck(handle.value!=INVALID_HANDLE_VALUE,"创建临时文件");WriteHandle(handle.value,bytes);}WinCheck(MoveFileExW(temporary.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=0,"提交文件");}catch(...){std::error_code error;fs::remove(temporary,error);throw;}}
void WriteJson(const fs::path& path,const Json& value){auto text=value.dump(2);WriteAtomic(path,{reinterpret_cast<const std::uint8_t*>(text.data()),text.size()});}
bool SamePath(const fs::path& a,const fs::path& b){auto x=fs::absolute(a).lexically_normal().wstring(),y=fs::absolute(b).lexically_normal().wstring();while(x.size()>3&&x.back()==L'\\')x.pop_back();while(y.size()>3&&y.back()==L'\\')y.pop_back();return CompareStringOrdinal(x.c_str(),static_cast<int>(x.size()),y.c_str(),static_cast<int>(y.size()),TRUE)==CSTR_EQUAL;}
bool Within(const fs::path& path,const fs::path& directory){if(directory.empty())return false;auto p=fs::absolute(path).lexically_normal();auto d=fs::absolute(directory).lexically_normal();for(auto at=p.parent_path();!at.empty();at=at.parent_path()){if(SamePath(at,d))return true;if(at==at.parent_path())break;}return false;}
std::string CardRecord::FileName()const{return Utf8(hs2::Path(Path).filename());}
std::string CardRecord::Folder()const{return Utf8(hs2::Path(Path).parent_path());}
std::string CardRecord::SexLabel()const{return Sex==1?"女性":Sex==0?"男性":"未知";}
std::string CardRecord::StatusLabel()const{switch(Status){case CardStatus::Valid:return "人物卡";case CardStatus::ImageOnly:return "普通图片";case CardStatus::Unsupported:return "不支持的卡片";case CardStatus::Unreadable:return "无法读取";default:return "解析异常";}}
std::string CardRecord::AvailabilityLabel()const{switch(Availability){case FileAvailability::Available:return "可用";case FileAvailability::Offline:return "目录离线";case FileAvailability::Missing:return "文件缺失";default:return "已隔离";}}
#define HS2_JSON_WRITE(member) j[#member] = v.member
#define HS2_JSON_READ(member) if(j.contains(#member)&&!j[#member].is_null()) j.at(#member).get_to(v.member)
void to_json(Json& j,const ModReference& v){j=Json::object();HS2_JSON_WRITE(Guid);HS2_JSON_WRITE(Slot);HS2_JSON_WRITE(Category);HS2_JSON_WRITE(Property);}
void from_json(const Json& j,ModReference& v){HS2_JSON_READ(Guid);HS2_JSON_READ(Slot);HS2_JSON_READ(Category);HS2_JSON_READ(Property);}
void to_json(Json& j,const CardRecord& v){j=Json::object();HS2_JSON_WRITE(Id);HS2_JSON_WRITE(Path);HS2_JSON_WRITE(Root);HS2_JSON_WRITE(Name);HS2_JSON_WRITE(Sex);HS2_JSON_WRITE(Version);HS2_JSON_WRITE(Status);HS2_JSON_WRITE(Error);HS2_JSON_WRITE(Size);HS2_JSON_WRITE(ModifiedUtcTicks);HS2_JSON_WRITE(PngLength);HS2_JSON_WRITE(CoverFile);HS2_JSON_WRITE(Hash);HS2_JSON_WRITE(Favorite);HS2_JSON_WRITE(Tags);HS2_JSON_WRITE(Collection);HS2_JSON_WRITE(Notes);HS2_JSON_WRITE(Dependencies);HS2_JSON_WRITE(PluginDataIds);HS2_JSON_WRITE(DependenciesComplete);HS2_JSON_WRITE(Availability);HS2_JSON_WRITE(OriginalPath);HS2_JSON_WRITE(QuarantinedUtcTicks);}
void from_json(const Json& j,CardRecord& v){HS2_JSON_READ(Id);HS2_JSON_READ(Path);HS2_JSON_READ(Root);HS2_JSON_READ(Name);HS2_JSON_READ(Sex);HS2_JSON_READ(Version);HS2_JSON_READ(Status);HS2_JSON_READ(Error);HS2_JSON_READ(Size);HS2_JSON_READ(ModifiedUtcTicks);HS2_JSON_READ(PngLength);HS2_JSON_READ(CoverFile);HS2_JSON_READ(Hash);HS2_JSON_READ(Favorite);HS2_JSON_READ(Tags);HS2_JSON_READ(Collection);HS2_JSON_READ(Notes);HS2_JSON_READ(Dependencies);HS2_JSON_READ(PluginDataIds);HS2_JSON_READ(DependenciesComplete);HS2_JSON_READ(Availability);HS2_JSON_READ(OriginalPath);HS2_JSON_READ(QuarantinedUtcTicks);}
void to_json(Json& j,const AppSettings& v){j=Json::object();HS2_JSON_WRITE(LastCardExportLevel);HS2_JSON_WRITE(GamePath);HS2_JSON_WRITE(ExtraRoots);HS2_JSON_WRITE(Collections);HS2_JSON_WRITE(RecursiveCardSearch);HS2_JSON_WRITE(Theme);HS2_JSON_WRITE(BlenderPath);HS2_JSON_WRITE(UnrealEditorPath);HS2_JSON_WRITE(Sort);HS2_JSON_WRITE(WindowWidth);HS2_JSON_WRITE(WindowHeight);}
void from_json(const Json& j,AppSettings& v){HS2_JSON_READ(LastCardExportLevel);HS2_JSON_READ(GamePath);HS2_JSON_READ(ExtraRoots);HS2_JSON_READ(Collections);HS2_JSON_READ(RecursiveCardSearch);HS2_JSON_READ(Theme);HS2_JSON_READ(BlenderPath);HS2_JSON_READ(UnrealEditorPath);HS2_JSON_READ(Sort);HS2_JSON_READ(WindowWidth);HS2_JSON_READ(WindowHeight);if(int(v.LastCardExportLevel)<1||int(v.LastCardExportLevel)>4)v.LastCardExportLevel=CardExportLevel::WithMods;}
void to_json(Json& j,const FileOperationRecord& v){j=Json::object();HS2_JSON_WRITE(Id);HS2_JSON_WRITE(Kind);HS2_JSON_WRITE(Source);HS2_JSON_WRITE(Destination);HS2_JSON_WRITE(Staging);HS2_JSON_WRITE(Hash);HS2_JSON_WRITE(Phase);HS2_JSON_WRITE(Error);HS2_JSON_WRITE(ExpectedSourceHash);HS2_JSON_WRITE(BackupPath);HS2_JSON_WRITE(SwapBackup);HS2_JSON_WRITE(CreatedUtc);j["ExpectedSize"]=v.ExpectedSize?Json(*v.ExpectedSize):Json();j["ExpectedModifiedUtcTicks"]=v.ExpectedModifiedUtcTicks?Json(*v.ExpectedModifiedUtcTicks):Json();j["Card"]=v.Card?Json(*v.Card):Json();}
void from_json(const Json& j,FileOperationRecord& v){HS2_JSON_READ(Id);HS2_JSON_READ(Kind);HS2_JSON_READ(Source);HS2_JSON_READ(Destination);HS2_JSON_READ(Staging);HS2_JSON_READ(Hash);HS2_JSON_READ(Phase);HS2_JSON_READ(Error);HS2_JSON_READ(ExpectedSourceHash);HS2_JSON_READ(BackupPath);HS2_JSON_READ(SwapBackup);HS2_JSON_READ(CreatedUtc);if(j.contains("ExpectedSize")&&!j["ExpectedSize"].is_null())v.ExpectedSize=j["ExpectedSize"].get<std::int64_t>();if(j.contains("ExpectedModifiedUtcTicks")&&!j["ExpectedModifiedUtcTicks"].is_null())v.ExpectedModifiedUtcTicks=j["ExpectedModifiedUtcTicks"].get<std::int64_t>();if(j.contains("Card")&&!j["Card"].is_null())v.Card=j["Card"].get<CardRecord>();}
#undef HS2_JSON_WRITE
#undef HS2_JSON_READ
std::vector<fs::path> AppSettings::ScanRoots()const{std::vector<fs::path> result{Path(GamePath)/L"UserData"/L"chara"/L"female"};for(auto& root:ExtraRoots){auto p=Path(root);if(std::none_of(result.begin(),result.end(),[&](auto& existing){return SamePath(existing,p);}))result.push_back(p);}return result;}
AppSettings AppSettings::Load(const fs::path& directory){auto path=directory/L"settings.json";if(!fs::exists(path))return {};auto bytes=ReadFile(path,4*1024*1024);try{return Json::parse(bytes).get<AppSettings>();}catch(const Json::exception&){auto backup=path;backup+=Path(".invalid-"+NewId());fs::copy_file(path,backup);return {};}}
void AppSettings::Save(const fs::path& directory)const{WriteJson(directory/L"settings.json",*this);}
CardDeleteRequest CardDeleteRequest::FromCard(const CardRecord& c){return {c.Id,Utf8(fs::absolute(hs2::Path(c.Path))),c.Size,c.ModifiedUtcTicks};}
}
