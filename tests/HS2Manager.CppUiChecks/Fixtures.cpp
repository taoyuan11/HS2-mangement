#include "Common/Core.h"
#include <iostream>
using namespace hs2;

namespace
{
    void Append(Bytes &a, std::span<const uint8_t> b)
    {
        a.insert(a.end(), b.begin(), b.end());
    }

    void Number(Bytes &a, uint64_t n, int count)
    {
        for (int i = 0; i < count; ++i)
            a.push_back(static_cast<uint8_t>(n >> (8 * i)));
    }

    void Text(Bytes &a, std::string const &text)
    {
        auto n = text.size();
        while (n >= 128)
        {
            a.push_back(static_cast<uint8_t>(n) | 128);
            n >>= 7;
        }
        a.push_back(static_cast<uint8_t>(n));
        Append(a, {reinterpret_cast<uint8_t const *>(text.data()), text.size()});
    }

    MessagePack S(std::string value)
    {
        return MessagePack::Scalar(std::move(value));
    }

    MessagePack I(int64_t value)
    {
        return MessagePack::Scalar(value);
    }

    Bytes Parts(std::vector<MessagePack> parts)
    {
        Bytes result;
        for (auto const &part : parts)
        {
            auto bytes = part.ToBytes();
            Number(result, bytes.size(), 4);
            Append(result, bytes);
        }
        return result;
    }

    Bytes Card(std::span<const uint8_t> png, int index)
    {
        auto parameter = MessagePack::Map({{std::string("fullname"), S("界面测试人物 " + std::to_string(index))},
                                           {std::string("sex"), I(index % 2)}})
                             .ToBytes();
        auto empty = MessagePack::Map();
        auto face = MessagePack::Map({{std::string("shapeValueFace"),
                                       MessagePack::Array({MessagePack::Scalar(.25f), MessagePack::Scalar(.75f)})}});
        auto body = MessagePack::Map({{std::string("shapeValueBody"),
                                       MessagePack::Array({MessagePack::Scalar(.5f), MessagePack::Scalar(.6f)})}});
        std::vector<std::pair<std::string, Bytes>> blocks{
            {"Parameter", parameter}, {"Custom", Parts({face, body, empty})}, {"Coordinate", Parts({empty, empty})}};
        Bytes data;
        std::vector<MessagePack> rows;
        for (auto const &[name, bytes] : blocks)
        {
            rows.push_back(MessagePack::Map({{std::string("name"), S(name)},
                                             {std::string("version"), S("1.0.0")},
                                             {std::string("pos"), I(data.size())},
                                             {std::string("size"), I(bytes.size())}}));
            Append(data, bytes);
        }
        auto table = MessagePack::Map({{std::string("lstInfo"), MessagePack::Array(rows)}}).ToBytes();
        Bytes output(png.begin(), png.end());
        Number(output, 100, 4);
        Text(output, "【AIS_Chara】");
        Text(output, "1.0.0");
        Number(output, 0, 4);
        Text(output, "native-ui-checks");
        Text(output, "fixture");
        Number(output, table.size(), 4);
        Append(output, table);
        Number(output, data.size(), 8);
        Append(output, data);
        return output;
    }
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc == 3 && std::wstring_view(argv[1]) == L"--verify")
        {
            CardRepository repository(argv[2]);
            auto all = repository.All();
            auto valid = std::count_if(all.begin(), all.end(),
                                       [](auto const &card)
                                       {
                                           return card.Status == CardStatus::Valid;
                                       });
            if (all.size() != 81 || valid != 80)
                throw std::runtime_error("UI startup did not preserve or index the populated fixture");
            std::cout << "PASS UI library: 80 characters, 1 image-only\n";
            return 0;
        }
        if (argc != 3)
            throw std::runtime_error("Usage: hs2_ui_fixtures <new local test directory> <cover PNG>");
        fs::path root = fs::absolute(argv[1]);
        if (fs::exists(root))
            throw std::runtime_error("Fixture destination must not exist");
        auto png = ReadFile(argv[2]);
        png.resize(CardParser::FindPngEnd(png));
        auto game = root / L"Game", cards = game / L"UserData" / L"chara" / L"female";
        fs::create_directories(cards);
        for (int i = 0; i < 80; ++i)
            WriteAtomic(cards / Path("sample-" + std::to_string(i) + ".png"), Card(png, i));
        WriteAtomic(cards / L"image-only.png", png);
        auto directory = root / L"Data";
        AppSettings settings;
        settings.GamePath = Utf8(game);
        settings.Theme = "Light";
        settings.Save(directory);
        CardRepository repository(directory);
        CardLibraryService library(repository);
        auto report = library.Scan({cards});
        if (report.Valid != 80 || report.Invalid != 1)
            throw std::runtime_error("UI fixture scan mismatch");
        auto all = repository.All();
        for (auto &card : all)
            if (card.Name == "界面测试人物 0")
            {
                card.Favorite = true;
                card.Tags = {"测试标签"};
                card.Notes = "独立 UI 验证副本";
                repository.Save(card);
            }
        std::cout << "PASS fixture: 80 characters, 1 image-only, offline game root; " << Utf8(directory) << '\n';
        return 0;
    }
    catch (std::exception const &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
