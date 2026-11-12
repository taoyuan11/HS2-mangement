#pragma once

namespace
{
    Bytes referencePng;

    MessagePack S(std::string value)
    {
        return MessagePack::Scalar(std::move(value));
    }

    MessagePack I(std::int64_t value)
    {
        return MessagePack::Scalar(value);
    }

    void Number(Bytes &bytes, std::uint64_t value, int width, bool big = false)
    {
        for (int i = 0; i < width; i++)
            bytes.push_back(static_cast<std::uint8_t>(value >> ((big ? width - 1 - i : i) * 8)));
    }

    void Append(Bytes &a, const Bytes &b)
    {
        a.insert(a.end(), b.begin(), b.end());
    }

    void Text(Bytes &output, const std::string &text)
    {
        auto length = text.size();
        while (length >= 128)
        {
            output.push_back(static_cast<std::uint8_t>((length & 127) | 128));
            length >>= 7;
        }
        output.push_back(static_cast<std::uint8_t>(length));
        output.insert(output.end(), text.begin(), text.end());
    }

    Bytes Png()
    {
        Bytes output{137, 80, 78, 71, 13, 10, 26, 10};
        Number(output, 13, 4, true);
        output.insert(output.end(), {'I', 'H', 'D', 'R'});
        output.insert(output.end(), 13, 0);
        Number(output, 0, 4);
        Number(output, 0, 4, true);
        output.insert(output.end(), {'I', 'E', 'N', 'D'});
        Number(output, 0, 4);
        return output;
    }

    Bytes Parts(std::vector<MessagePack> parts)
    {
        Bytes output;
        for (auto &part : parts)
        {
            auto bytes = part.ToBytes();
            Number(output, bytes.size(), 4);
            Append(output, bytes);
        }
        output.push_back(0xee);
        return output;
    }

    Bytes Fixture()
    {
        auto face = MessagePack::Map({{std::string("shapeValueFace"),
                                       MessagePack::Array({MessagePack::Scalar(0.25f), MessagePack::Scalar(0.75f)})},
                                      {std::string("unknown"), MessagePack::Parse(Bytes{0xd4, 0x7f, 0x99})}});
        auto body = MessagePack::Map({{std::string("height"), MessagePack::Scalar(0.5f)}});
        auto empty = MessagePack::Map();
        auto parameter =
            MessagePack::Map({{std::string("sex"), I(1)}, {std::string("fullname"), S("测试人物")}}).ToBytes();
        auto references =
            MessagePack::Array({MessagePack::Binary(MessagePack::Map({{std::string("ModID"), S("example.mod")},
                                                                      {std::string("Slot"), I(7)},
                                                                      {std::string("CategoryNo"), I(9)},
                                                                      {std::string("Property"), S("hair.parts[0].id")}})
                                                        .ToBytes())});
        auto plugin = MessagePack::Array({I(0), MessagePack::Map({{std::string("info"), references}})});
        auto extension = MessagePack::Map({{std::string("com.bepis.sideloader.universalautoresolver"), plugin},
                                           {std::string("unknown.plugin"), MessagePack::Binary(Bytes{9, 8, 7})}})
                             .ToBytes();
        std::vector<std::pair<std::string, Bytes>> blocks{{"Parameter", parameter},
                                                          {"Custom", Parts({face, body, empty})},
                                                          {"Coordinate", Parts({empty, empty})},
                                                          {"KKEx", extension},
                                                          {"Future", Bytes{1, 2, 3, 4, 5}}};
        Bytes payload;
        std::vector<MessagePack> rows;
        for (auto &[name, bytes] : blocks)
        {
            payload.push_back(0xfd);
            rows.push_back(MessagePack::Map({{std::string("name"), S(name)},
                                             {std::string("pos"), I(payload.size())},
                                             {std::string("size"), I(bytes.size())},
                                             {std::string("version"), S("1")}}));
            Append(payload, bytes);
        }
        payload.push_back(0xfc);
        auto table = MessagePack::Map(
                         {{std::string("lstInfo"), MessagePack::Array(rows)}, {std::string("future"), S("preserve")}})
                         .ToBytes();
        auto output = referencePng;
        Number(output, 100, 4);
        Text(output, "【AIS_Chara】");
        Text(output, "1.0.0");
        Number(output, 0, 4);
        Text(output, "user");
        Text(output, "data");
        Number(output, table.size(), 4);
        Append(output, table);
        Number(output, payload.size(), 8);
        Append(output, payload);
        output.insert(output.end(), {0xaa, 0xbb, 0xcc});
        return output;
    }
}
