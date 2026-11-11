#include "DataLocation.h"
#include <algorithm>
#include <fstream>
#include <system_error>
#include <windows.h>
#include <winnetwk.h>
#include <winsqlite/winsqlite3.h>

namespace hs2::ui {
namespace {
std::wstring Upper(std::wstring value) {
  CharUpperBuffW(value.data(), static_cast<DWORD>(value.size()));
  return value;
}
bool Equal(std::wstring_view a, std::wstring_view b) {
  return Upper(std::wstring(a)) == Upper(std::wstring(b));
}
std::string Key(fs::path const &path) {
  auto value = Utf8(fs::path(Upper(path.wstring())));
  return Sha256({reinterpret_cast<uint8_t const *>(value.data()), value.size()})
      .substr(0, 24);
}
std::wstring Share(fs::path const &root) {
  auto value = root.wstring();
  if (value.size() != 3 || value[1] != L':')
    return {};
  DWORD length = 512;
  std::wstring buffer(length, L'\0');
  auto status =
      WNetGetConnectionW(value.substr(0, 2).c_str(), buffer.data(), &length);
  if (status == ERROR_MORE_DATA) {
    buffer.resize(length);
    status =
        WNetGetConnectionW(value.substr(0, 2).c_str(), buffer.data(), &length);
  }
  if (status)
    return {};
  buffer.resize(wcslen(buffer.c_str()));
  return buffer;
}
DWORD Attributes(fs::path const &path) {
  auto attrs = GetFileAttributesW(path.c_str());
  if (attrs != INVALID_FILE_ATTRIBUTES)
    return attrs;
  auto error = GetLastError();
  if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
    return INVALID_FILE_ATTRIBUTES;
  throw std::system_error(error, std::system_category(), "无法读取数据目录");
}
bool Ordinary(fs::path const &path, bool directory) {
  auto attrs = Attributes(path);
  if (attrs == INVALID_FILE_ATTRIBUTES)
    return false;
  if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) ||
      static_cast<bool>(attrs & FILE_ATTRIBUTE_DIRECTORY) != directory)
    throw std::runtime_error("迁移源包含链接或文件类型不符：" + Utf8(path));
  return true;
}
fs::path SourceConfiguration(fs::path const &base) {
  auto framework =
      Equal(base.filename().wstring(), L"win-x64") ? base.parent_path() : base;
  auto configuration = framework.parent_path();
  auto bin = configuration.parent_path();
  if (Equal(bin.filename().wstring(), L"bin")) {
    auto project = bin.parent_path();
    if (fs::exists(project / L"HS2Manager.App.csproj") ||
        fs::exists(project / L"HS2Manager.NativeApp.vcxproj") ||
        (Equal(project.filename().wstring(), L"HS2Manager.App") &&
         fs::exists(project.parent_path() / L"HS2Manager.NativeApp" /
                    L"HS2Manager.NativeApp.vcxproj")))
      return configuration;
  }
  // Native source output may be rooted in a separate local build directory.
  return {};
}
fs::path Adjacent(fs::path const &base, fs::path const &configuration) {
  auto current = base / L"Data";
  if (fs::exists(current))
    return current;
  if (!configuration.empty())
    for (auto suffix :
         {L"net10.0-windows/Data", L"net10.0-windows/win-x64/Data"}) {
      auto candidate = configuration / suffix;
      if (fs::exists(candidate / L"library.db") ||
          fs::exists(candidate / L"settings.json"))
        return candidate;
    }
  return current;
}
fs::path LocalCache(fs::path const &path, std::wstring const &category,
                    fs::path const &local) {
  auto identity = StorageIdentity(path),
       parent = local / L"HS2Manager" / category,
       canonical = parent / Key(identity);
  if (fs::exists(canonical / L"Data"))
    return canonical;
  wchar_t drives[512]{};
  auto count = GetLogicalDriveStringsW(512, drives);
  if (count && count < 512)
    for (auto drive = drives; *drive; drive += wcslen(drive) + 1) {
      auto share = Share(fs::path(drive));
      if (share.empty())
        continue;
      share += L'\\';
      auto full = identity.wstring();
      if (Upper(full).starts_with(Upper(share))) {
        auto legacy = parent / Key(fs::path(drive) / full.substr(share.size()));
        if (fs::exists(legacy / L"Data"))
          return legacy;
      }
    }
  auto previous = parent / Key(fs::absolute(path).lexically_normal());
  return fs::exists(previous / L"Data") ? previous : canonical;
}
void CopyDirectory(fs::path const &source, fs::path const &target) {
  if (!Ordinary(source, true))
    return;
  fs::create_directories(target);
  for (auto const &item : fs::directory_iterator(source)) {
    auto attrs = Attributes(item.path());
    if (attrs & FILE_ATTRIBUTE_DIRECTORY)
      CopyDirectory(item.path(), target / item.path().filename());
    else if (Ordinary(item.path(), false))
      fs::copy_file(item.path(), target / item.path().filename());
  }
}
void BackupDatabase(fs::path const &source, fs::path const &target) {
  struct Database {
    sqlite3 *Value{};
    ~Database() {
      if (Value)
        sqlite3_close(Value);
    }
  } input, output;
  auto check = [](int code, sqlite3 *db) {
    if (code != SQLITE_OK && code != SQLITE_DONE)
      throw std::runtime_error(db ? sqlite3_errmsg(db) : "SQLite 数据迁移失败");
  };
  check(sqlite3_open_v2(Utf8(source).c_str(), &input.Value,
                        SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX, nullptr),
        input.Value);
  check(sqlite3_open_v2(Utf8(target).c_str(), &output.Value,
                        SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, nullptr),
        output.Value);
  sqlite3_busy_timeout(input.Value, 10000);
  sqlite3_busy_timeout(output.Value, 10000);
  auto backup = sqlite3_backup_init(output.Value, "main", input.Value, "main");
  if (!backup)
    throw std::runtime_error(sqlite3_errmsg(output.Value));
  auto result = sqlite3_backup_step(backup, -1),
       finished = sqlite3_backup_finish(backup);
  check(result, output.Value);
  check(finished, output.Value);
}
bool Guid(std::string const &value) {
  return value.size() == 32 &&
         std::all_of(value.begin(), value.end(),
                     [](unsigned char c) { return std::isxdigit(c) != 0; });
}
void Rebase(fs::path const &staging, fs::path const &destination) {
  auto operations = staging / L"Operations";
  if (!fs::exists(operations))
    return;
  for (auto const &item : fs::directory_iterator(operations))
    if (Equal(item.path().extension().wstring(), L".json")) {
      auto bytes = ReadFile(item.path());
      auto journal = Json::parse(bytes);
      auto id = journal.value("Id", std::string{}),
           card =
               journal.value("Card", Json::object()).value("Id", std::string{}),
           backup = journal.value("BackupPath", std::string{});
      if (backup.empty() || !Guid(id) || !Guid(card))
        continue;
      auto relative = fs::path(L"CardBackups") / Path(card) / Path(id + ".png"),
           copied = staging / relative;
      auto normalized = Path(backup).wstring();
      std::replace(normalized.begin(), normalized.end(), L'/', L'\\');
      if (!Upper(normalized).ends_with(Upper(L"\\" + relative.wstring())) ||
          !fs::exists(copied))
        continue;
      if (!Equal(Path(HashFile(copied)).wstring(),
                 Path(journal.value("ExpectedSourceHash", std::string{}))
                     .wstring()))
        throw std::runtime_error("编辑备份校验失败，未迁移数据：" + backup);
      journal["BackupPath"] = Utf8(destination / relative);
      WriteJson(item.path(), journal);
    }
}
} // namespace
bool IsNetworkPath(fs::path const &path) {
  auto full = fs::absolute(path).wstring();
  if (Upper(full).starts_with(L"\\\\?\\UNC\\"))
    return true;
  if (full.starts_with(L"\\\\?\\"))
    full = full.substr(4);
  return full.starts_with(L"\\\\") ||
         GetDriveTypeW(fs::path(full).root_path().c_str()) == DRIVE_REMOTE;
}
fs::path StorageIdentity(fs::path const &path) {
  auto full = fs::absolute(path).lexically_normal().wstring();
  if (Upper(full).starts_with(L"\\\\?\\UNC\\"))
    full = L"\\\\" + full.substr(8);
  else if (full.starts_with(L"\\\\?\\"))
    full = full.substr(4);
  auto root = fs::path(full).root_path();
  auto share = Share(root);
  if (!share.empty())
    full = (fs::path(share) / full.substr(root.wstring().size())).wstring();
  while (full.size() > 3 && (full.back() == L'\\' || full.back() == L'/'))
    full.pop_back();
  return full;
}
std::wstring LibraryMutexName(fs::path const &path) {
  auto key = Key(fs::absolute(path).lexically_normal());
  return L"Local\\HS2Manager.App." + Path(key).wstring();
}
LibraryLocation
ResolveLibraryLocation(fs::path base,
                       std::vector<std::wstring> const &arguments,
                       fs::path localAppData) {
  std::optional<fs::path> explicitData;
  for (size_t i = 0; i < arguments.size(); ++i) {
    if (arguments[i] != L"--data")
      throw std::runtime_error("无法识别的启动参数。");
    if (++i >= arguments.size() || arguments[i].empty())
      throw std::runtime_error("--data 后需要指定本地数据目录。");
    explicitData = fs::absolute(arguments[i]).lexically_normal();
  }
  if (explicitData) {
    if (IsNetworkPath(*explicitData))
      throw std::runtime_error(
          "资料库必须位于本地磁盘；人物卡和游戏目录可位于 NAS。");
    return {*explicitData, {}};
  }
  auto sourceLength =
      GetEnvironmentVariableW(L"HS2MANAGER_SOURCE_OUTPUT", nullptr, 0);
  std::wstring source(sourceLength, L'\0');
  if (sourceLength) {
    auto length = GetEnvironmentVariableW(L"HS2MANAGER_SOURCE_OUTPUT",
                                          source.data(), sourceLength);
    if (length && length < sourceLength) {
      source.resize(length);
      if (!SourceConfiguration(source).empty())
        base = source;
    }
  }
  base = fs::absolute(base).lexically_normal();
  auto configuration = SourceConfiguration(base),
       adjacent = Adjacent(base, configuration);
  if (!IsNetworkPath(base))
    return {adjacent, {}};
  auto destination = LocalCache(configuration.empty() ? base : configuration,
                                configuration.empty() ? L"Portable" : L"Source",
                                localAppData) /
                     L"Data";
  // Enumerating the install propagates access errors rather than publishing an
  // empty library. The historical project path remains the library identity
  // after the managed source is archived; the native sibling is then the
  // installed source tree.
  auto installation =
      configuration.empty() ? base : configuration.parent_path().parent_path();
  if (!configuration.empty() &&
      Equal(installation.filename().wstring(), L"HS2Manager.App") &&
      !fs::exists(installation)) {
    auto native = installation.parent_path() / L"HS2Manager.NativeApp";
    if (fs::exists(native / L"HS2Manager.NativeApp.vcxproj"))
      installation = native;
  }
  (void)fs::directory_iterator(installation);
  return {destination, adjacent};
}
void CopyLibrary(fs::path const &source, fs::path const &destination) {
  if (fs::exists(destination))
    return;
  if (IsNetworkPath(destination))
    throw std::runtime_error("迁移目标必须位于本地磁盘。");
  auto staging = fs::path(destination.wstring() + L".migrating-" +
                          Path(NewId()).wstring());
  if (!SamePath(staging.parent_path(), destination.parent_path()))
    throw std::runtime_error("数据迁移临时目录越界。");
  fs::create_directories(staging);
  try {
    if (Ordinary(source, true)) {
      if (Ordinary(source / L"library.db", false))
        BackupDatabase(source / L"library.db", staging / L"library.db");
      if (Ordinary(source / L"settings.json", false))
        fs::copy_file(source / L"settings.json", staging / L"settings.json");
      for (auto name : {L"Covers", L"Operations", L"Logs", L"CardBackups"})
        CopyDirectory(source / name, staging / name);
      Rebase(staging, destination);
    }
    fs::rename(staging, destination);
  } catch (...) {
    std::error_code ignored;
    fs::remove_all(staging, ignored);
    throw;
  }
}
void PrepareLibrary(LibraryLocation const &location) {
  if (location.MigrationSource.empty() || fs::exists(location.Directory))
    return;
  auto mutex = CreateMutexW(nullptr, FALSE,
                            LibraryMutexName(location.MigrationSource).c_str());
  if (!mutex)
    throw std::system_error(GetLastError(), std::system_category());
  auto exists = GetLastError() == ERROR_ALREADY_EXISTS;
  try {
    if (exists)
      throw std::runtime_error(
          "请先关闭使用旧数据目录的 HS2 Manager，再迁移到本机。");
    CopyLibrary(location.MigrationSource, location.Directory);
  } catch (...) {
    CloseHandle(mutex);
    throw;
  }
  CloseHandle(mutex);
}
} // namespace hs2::ui
