#pragma once
#include <functional>
#include <map>
#include <winrt/Microsoft.UI.Xaml.Data.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Xaml.Interop.h>

namespace hs2::ui
{
    using Object = winrt::Windows::Foundation::IInspectable;
    using TypeName = winrt::Windows::UI::Xaml::Interop::TypeName;
    namespace data = winrt::Microsoft::UI::Xaml::Data;
    struct Observable;

    struct Property : winrt::implements<Property, data::ICustomProperty>
    {
        winrt::hstring name;

        explicit Property(winrt::hstring value) : name(std::move(value))
        {
        }

        TypeName Type() const
        {
            return winrt::xaml_typename<Object>();
        }

        winrt::hstring Name() const
        {
            return name;
        }

        bool CanRead() const
        {
            return true;
        }

        bool CanWrite() const
        {
            return true;
        }

        Object GetValue(Object const &target) const;
        void SetValue(Object const &target, Object const &value) const;

        Object GetIndexedValue(Object const &, Object const &) const
        {
            throw winrt::hresult_not_implemented();
        }

        void SetIndexedValue(Object const &, Object const &, Object const &) const
        {
            throw winrt::hresult_not_implemented();
        }
    };

    // 运行时 XAML 绑定完全使用原生机制，无需反射、CLR 或托管宿主。
    struct Observable : winrt::implements<Observable, data::ICustomPropertyProvider, data::INotifyPropertyChanged,
                                          winrt::Windows::Foundation::IStringable>
    {
        std::map<winrt::hstring, Object, std::less<>> values;
        std::function<void(winrt::hstring const &)> Changed;
        winrt::event<data::PropertyChangedEventHandler> changed;

        Object Get(winrt::hstring const &name) const
        {
            auto it = values.find(name);
            return it == values.end() ? nullptr : it->second;
        }

        void Set(winrt::hstring const &name, Object const &value, bool notify = true)
        {
            values.insert_or_assign(name, value);
            changed(*this, data::PropertyChangedEventArgs(name));
            if (notify && Changed)
                Changed(name);
        }

        template <typename T> void Put(winrt::hstring const &name, T const &value)
        {
            Set(name, winrt::box_value(value), false);
        }

        void Text(winrt::hstring const &name, std::string_view value)
        {
            Put(name, winrt::to_hstring(value));
        }

        winrt::hstring String(winrt::hstring const &name) const
        {
            return winrt::unbox_value_or<winrt::hstring>(Get(name), L"");
        }

        bool Bool(winrt::hstring const &name) const
        {
            return winrt::unbox_value_or<bool>(Get(name), false);
        }

        data::ICustomProperty GetCustomProperty(winrt::hstring const &name) const
        {
            return winrt::make<Property>(name);
        }

        data::ICustomProperty GetIndexedProperty(winrt::hstring const &, TypeName const &) const
        {
            return nullptr;
        }

        winrt::hstring GetStringRepresentation() const
        {
            return String(L"Name");
        }

        winrt::hstring ToString() const
        {
            return GetStringRepresentation();
        }

        TypeName Type() const
        {
            return {L"HS2Manager.NativeApp.Observable", winrt::Windows::UI::Xaml::Interop::TypeKind::Custom};
        }

        winrt::event_token PropertyChanged(data::PropertyChangedEventHandler const &handler)
        {
            return changed.add(handler);
        }

        void PropertyChanged(winrt::event_token const &token) noexcept
        {
            changed.remove(token);
        }
    };

    inline Object Property::GetValue(Object const &target) const
    {
        return winrt::get_self<Observable>(target.as<data::ICustomPropertyProvider>())->Get(name);
    }

    inline void Property::SetValue(Object const &target, Object const &value) const
    {
        winrt::get_self<Observable>(target.as<data::ICustomPropertyProvider>())->Set(name, value);
    }

    struct VisibilityConverter : winrt::implements<VisibilityConverter, data::IValueConverter>
    {
        bool nullTest;

        explicit VisibilityConverter(bool test = false) : nullTest(test)
        {
        }

        Object Convert(Object const &value, TypeName const &, Object const &parameter, winrt::hstring const &) const
        {
            bool visible = nullTest ? static_cast<bool>(value) : winrt::unbox_value_or<bool>(value, false);
            if (winrt::unbox_value_or<winrt::hstring>(parameter, L"") == L"Invert")
                visible = !visible;
            return winrt::box_value(visible ? winrt::Microsoft::UI::Xaml::Visibility::Visible
                                            : winrt::Microsoft::UI::Xaml::Visibility::Collapsed);
        }

        Object ConvertBack(Object const &, TypeName const &, Object const &, winrt::hstring const &) const
        {
            return winrt::Microsoft::UI::Xaml::DependencyProperty::UnsetValue();
        }
    };

    struct NavigationConverter : winrt::implements<NavigationConverter, data::IValueConverter>
    {
        Object Convert(Object const &value, TypeName const &, Object const &parameter, winrt::hstring const &) const
        {
            if (winrt::unbox_value_or<winrt::hstring>(value, L"") ==
                winrt::unbox_value_or<winrt::hstring>(parameter, L""))
                return winrt::Microsoft::UI::Xaml::Application::Current().Resources().Lookup(
                    winrt::box_value(L"SelectedBrush"));
            return winrt::Microsoft::UI::Xaml::Media::SolidColorBrush(winrt::Windows::UI::Color{0, 0, 0, 0});
        }

        Object ConvertBack(Object const &, TypeName const &, Object const &, winrt::hstring const &) const
        {
            return winrt::Microsoft::UI::Xaml::DependencyProperty::UnsetValue();
        }
    };
} // 命名空间 hs2::ui
