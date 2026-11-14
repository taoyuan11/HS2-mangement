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
            bool hasItem = false;
            for (auto const &child : Children())
            {
                if (child.Visibility() == winrt::Microsoft::UI::Xaml::Visibility::Collapsed)
                    continue;
                child.Measure(available);
                auto size = child.DesiredSize();
                if (hasItem && x + ColumnSpacing + size.Width > available.Width)
                {
                    width = std::max(width, x);
                    y += rowHeight + RowSpacing;
                    x = rowHeight = 0;
                    hasItem = false;
                }
                x += (hasItem ? ColumnSpacing : 0) + size.Width;
                rowHeight = std::max(rowHeight, size.Height);
                hasItem = true;
            }
            return {std::max(width, x), y + rowHeight};
        }

        winrt::Windows::Foundation::Size ArrangeOverride(winrt::Windows::Foundation::Size available)
        {
            float width = 0, y = 0, rowHeight = 0;
            bool hasItem = false;
            uint32_t start = 0;
            auto row = [&](uint32_t end)
            {
                float x = 0;
                bool first = true;
                for (uint32_t i = start; i < end; ++i)
                {
                    auto child = Children().GetAt(i);
                    if (child.Visibility() == winrt::Microsoft::UI::Xaml::Visibility::Collapsed)
                        continue;
                    auto size = child.DesiredSize();
                    if (!first)
                        x += ColumnSpacing;
                    child.Arrange({x, y, size.Width, rowHeight});
                    x += size.Width;
                    first = false;
                }
            };
            for (uint32_t i = 0; i < Children().Size(); ++i)
            {
                auto child = Children().GetAt(i);
                if (child.Visibility() == winrt::Microsoft::UI::Xaml::Visibility::Collapsed)
                    continue;
                auto size = child.DesiredSize();
                if (hasItem && width + ColumnSpacing + size.Width > available.Width)
                {
                    row(i);
                    y += rowHeight + RowSpacing;
                    width = rowHeight = 0;
                    start = i;
                    hasItem = false;
                }
                width += (hasItem ? ColumnSpacing : 0) + size.Width;
                rowHeight = std::max(rowHeight, size.Height);
                hasItem = true;
            }
            row(Children().Size());
            return available;
        }

        // 工具栏在窄窗口下换行，折叠控件不占间距；每行采用同一高度对齐。
        static constexpr float ColumnSpacing = 8;
        static constexpr float RowSpacing = 8;
    };
} // 命名空间 hs2::ui
