using System.Globalization;
using System.Windows.Data;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>
/// Binds a nullable enum to a group of radio buttons, where the parameter names the option.
///
/// The nullable part carries meaning here rather than being incidental: null is "no reviewer has
/// decided", which is a different state from either choice and is what keeps an untouched clutch
/// an unsupported boundary instead of a silent default.
/// </summary>
public sealed class EnumOptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string option)
        {
            return false;
        }

        if (string.Equals(option, "None", StringComparison.OrdinalIgnoreCase))
        {
            return value is null;
        }

        return value is not null
            && string.Equals(value.ToString(), option, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the button being switched on carries information; the one switching off would
        // otherwise race it and clear the value that was just set.
        if (value is not true || parameter is not string option)
        {
            return Binding.DoNothing;
        }

        if (string.Equals(option, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return underlying.IsEnum && Enum.TryParse(underlying, option, ignoreCase: true, out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}
