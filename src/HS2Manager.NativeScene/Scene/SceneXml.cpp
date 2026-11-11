#include "Scene/SceneXml.h"
#include <Windows.h>
#include <shlwapi.h>
#include <xmllite.h>
#include <wrl/client.h>
#include <stdexcept>

namespace hs2
{
    const SceneXmlNode *SceneXmlNode::Child(std::string_view name) const
    {
        for (auto &c : Children)
            if (c.Name == name)
                return &c;
        return nullptr;
    }

    const SceneXmlNode *SceneXmlNode::Descendant(std::string_view name) const
    {
        if (Name == name)
            return this;
        for (auto &c : Children)
            if (auto *found = c.Descendant(name))
                return found;
        return nullptr;
    }

    std::string SceneXmlNode::Value(std::string_view name, std::string fallback) const
    {
        auto *node = Child(name);
        return node && !node->Text.empty() ? node->Text : std::move(fallback);
    }

    std::string SceneXmlNode::Attribute(std::string_view name, std::string fallback) const
    {
        auto found = Attributes.find(std::string(name));
        return found == Attributes.end() ? fallback : found->second;
    }

    SceneXmlNode ReadSceneXml(std::span<const uint8_t> bytes)
    {
        using Microsoft::WRL::ComPtr;
        auto check = [](HRESULT hr)
        {
            if (FAILED(hr))
                throw std::runtime_error("Invalid XML document.");
        };
        auto text = [](const wchar_t *p, UINT n)
        {
            if (!p || !n)
                return std::string{};
            int size = WideCharToMultiByte(CP_UTF8, 0, p, n, nullptr, 0, nullptr, nullptr);
            std::string result(size, '\0');
            WideCharToMultiByte(CP_UTF8, 0, p, n, result.data(), size, nullptr, nullptr);
            return result;
        };
        if (bytes.size() > 4 * 1024 * 1024)
            throw std::runtime_error("XML size exceeds limit.");
        ComPtr<IStream> input;
        input.Attach(SHCreateMemStream(bytes.data(), static_cast<UINT>(bytes.size())));
        if (!input)
            throw std::runtime_error("Cannot allocate XML stream.");
        ComPtr<IXmlReader> reader;
        check(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void **>(reader.GetAddressOf()), nullptr));
        check(reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit));
        check(reader->SetInput(input.Get()));
        SceneXmlNode root;
        std::vector<SceneXmlNode *> stack{&root};
        XmlNodeType type;
        HRESULT status;
        size_t count = 0;
        while ((status = reader->Read(&type)) == S_OK)
        {
            if (++count > 100000 || stack.size() > 64)
                throw std::runtime_error("XML nesting or node count exceeds limit.");
            if (type == XmlNodeType_Element)
            {
                const wchar_t *name;
                UINT length;
                check(reader->GetLocalName(&name, &length));
                SceneXmlNode node;
                node.Name = text(name, length);
                if (reader->MoveToFirstAttribute() == S_OK)
                {
                    do
                    {
                        const wchar_t *key, *value;
                        UINT k, v;
                        check(reader->GetLocalName(&key, &k));
                        check(reader->GetValue(&value, &v));
                        node.Attributes[text(key, k)] = text(value, v);
                    } while (reader->MoveToNextAttribute() == S_OK);
                    check(reader->MoveToElement());
                }
                stack.back()->Children.push_back(std::move(node));
                if (!reader->IsEmptyElement())
                    stack.push_back(&stack.back()->Children.back());
            }
            else if (type == XmlNodeType_EndElement)
            {
                if (stack.size() < 2)
                    throw std::runtime_error("Invalid XML end element.");
                stack.pop_back();
            }
            else if (type == XmlNodeType_Text || type == XmlNodeType_CDATA || type == XmlNodeType_Whitespace)
            {
                const wchar_t *value;
                UINT length;
                check(reader->GetValue(&value, &length));
                stack.back()->Text += text(value, length);
            }
            else if (type == XmlNodeType_DocumentType)
                throw std::runtime_error("XML DTD is prohibited.");
        }
        check(status);
        if (root.Children.size() != 1 || stack.size() != 1)
            throw std::runtime_error("Invalid XML root.");
        return std::move(root.Children.front());
    }
}
