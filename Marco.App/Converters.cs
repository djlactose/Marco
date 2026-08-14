using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Marco.Core.Model;

namespace Marco.App;

/// <summary>Maps a machine status to a status-dot brush.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MachineStatus.Alive or MachineStatus.Done => Brushes.SeaGreen,
        MachineStatus.Partial => Brushes.Goldenrod,
        MachineStatus.Scanning => Brushes.SteelBlue,
        MachineStatus.AuthFailed => Brushes.OrangeRed,
        MachineStatus.Unreachable => Brushes.Gray,
        MachineStatus.Error => Brushes.Firebrick,
        _ => Brushes.Silver,
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Bytes → a compact GB string, e.g. "16.0 GB". Empty for zero/unknown.</summary>
public sealed class BytesToGbConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        long bytes = value switch { long l => l, int i => i, _ => 0 };
        if (bytes <= 0) return string.Empty;
        return $"{bytes / 1024d / 1024d / 1024d:0.#} GB";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Visible, false → Collapsed. (Avoids referencing the framework converter by key.)</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Non-empty/non-null → Visible, else Collapsed.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true,
        };
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
