#include "Preview/PreviewResourceContext.h"
#include <algorithm>

namespace hs2
{
    namespace
    {
        std::string Lower(std::string value)
        {
            for (auto &c : value)
                if (c >= 'A' && c <= 'Z')
                    c = char(c - 'A' + 'a');
            return value;
        }

        std::string Normalize(std::string value)
        {
            std::replace(value.begin(), value.end(), '\\', '/');
            auto path = Path(value);
            if (path.has_root_path())
                throw std::runtime_error("预览资源引用不能是绝对路径。");
            for (auto &part : path)
                if (part == ".." || Utf8(part).find(':') != std::string::npos)
                    throw std::runtime_error("预览资源引用不能越过资源目录。");
            value = Utf8(path.lexically_normal());
            std::replace(value.begin(), value.end(), '\\', '/');
            return Lower(value);
        }
    }

    std::optional<fs::path> PreviewResourceContext::Loose(std::string relative) const
    {
        auto found = Files.find(Normalize(std::move(relative)));
        if (found != Files.end())
            return found->second;
        return {};
    }

    size_t PreviewResourceContext::Priority(const fs::path &path) const
    {
        auto found = Priorities.find(path);
        return found == Priorities.end() ? 0 : found->second;
    }

    fs::path PreviewResourceContext::File(std::string relative) const
    {
        auto key = Normalize(relative);
        if (key.starts_with("bepinex/plugins/") && key.ends_with(".dll"))
            if (auto plugin = Plugin(Path(relative).filename().wstring()))
                return *plugin;
        if (auto loose = Loose(relative))
            return *loose;
        return Game / Path(relative);
    }

    std::optional<fs::path> PreviewResourceContext::Plugin(std::wstring_view name) const
    {
        auto key = Lower(Utf8(fs::path(name)));
        for (auto &plugin : Index.PluginFiles)
            if (Lower(Utf8(Path(plugin.Path).filename())) == key)
                return Path(plugin.Path);
        return {};
    }

    std::vector<fs::path> PreviewResourceContext::Enumerate(std::string relative) const
    {
        auto prefix = Normalize(relative);
        if (!prefix.ends_with('/'))
            prefix += '/';
        std::map<std::string, fs::path> result;
        auto directory = Game / Path(relative);
        if (fs::is_directory(directory))
            for (auto &entry : fs::recursive_directory_iterator(directory))
                if (entry.is_regular_file())
                    result[Normalize(Utf8(entry.path().lexically_relative(Game)))] = entry.path();
        for (auto &[name, path] : Files)
            if (name.starts_with(prefix))
                result[name] = path;
        std::vector<fs::path> output;
        for (auto &[name, path] : result)
            output.push_back(path);
        return output;
    }

    fs::path PreviewResourceFile(const fs::path &game, const PreviewResources &resources, std::string relative)
    {
        return resources ? resources->File(std::move(relative)) : game / Path(relative);
    }
}
