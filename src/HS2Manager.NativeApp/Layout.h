#pragma once
#include <algorithm>
#include <winrt/Microsoft.UI.Xaml.Controls.h>

namespace hs2::ui
{
struct WrapPanel : winrt::Microsoft::UI::Xaml::Controls::PanelT<WrapPanel>
{
    winrt::Windows::Foundation::Size MeasureOverride(winrt::Windows::Foundation::Size available)
    {
        float x = 0, y = 0, rowHeight = 0, width = 0;
        for (auto const &child : Children())
        {
            child.Measure(available);
            auto size = child.DesiredSize();
            if (x > 0 && x + size.Width > available.Width)
            {
                width = std::max(width, x);
                y += rowHeight;
                x = rowHeight = 0;
            }
            x += size.Width;
            rowHeight = std::max(rowHeight, size.Height);
        }
        return {std::max(width, x), y + rowHeight};
    }
    winrt::Windows::Foundation::Size ArrangeOverride(winrt::Windows::Foundation::Size available)
    {
        float width = 0, y = 0, rowHeight = 0;
        uint32_t start = 0;
        auto row = [&](uint32_t end) {
            float x = 0;
            for (uint32_t i = start; i < end; ++i)
            {
                auto child = Children().GetAt(i);
                auto size = child.DesiredSize();
                child.Arrange({x, y, size.Width, rowHeight});
                x += size.Width;
            }
        };
        for (uint32_t i = 0; i < Children().Size(); ++i)
        {
            auto size = Children().GetAt(i).DesiredSize();
            if (width > 0 && width + size.Width > available.Width)
            {
                row(i);
                y += rowHeight;
                width = rowHeight = 0;
                start = i;
            }
            width += size.Width;
            rowHeight = std::max(rowHeight, size.Height);
        }
        row(Children().Size());
        return available;
    }
};
} // namespace hs2::ui
