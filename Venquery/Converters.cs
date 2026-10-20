using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Venquery;

/// <summary>Maps a device's PnP class to a small Fluent-style stroke icon (24×24 space).</summary>
public sealed class CategoryIconConverter : IValueConverter
{
    // Simple line geometries, drawn stroked (no fill) at any size via Stretch=Uniform.
    private const string Monitor  = "M2,4 H22 V17 H2 Z M9,21 H15 M12,17 V21";
    private const string Ethernet = "M3,15 H21 V21 H3 Z M12,15 V9 M8,9 H16 V6 H8 Z";
    private const string Chip     = "M7,7 H17 V17 H7 Z M10,10 H14 V14 H10 Z M10,4 V7 M14,4 V7 M10,17 V20 M14,17 V20 M4,10 H7 M4,14 H7 M17,10 H20 M17,14 H20";
    private const string Usb       = "M12,21 V4 M9.5,7.5 H14.5 L12,4 Z M12,14 L16,11.5 V9 M12,10 L8,8 V6";
    private const string Storage  = "M3,5 H21 V19 H3 Z M6,9 H18 M6,13 H18 M6,17 H11";
    private const string Bluetooth = "M8,7 L16,17 L12,21 V3 L16,7 L8,17";
    private const string Camera   = "M3,7 H7 L9,5 H15 L17,7 H21 V19 H3 Z M12,16 A3,3 0 1 0 12,10 A3,3 0 0 0 12,16 Z";
    private const string Speaker  = "M4,9 H8 L13,5 V19 L8,15 H4 Z M16.5,9 A4,4 0 0 1 16.5,15 M19,7 A7.5,7.5 0 0 1 19,17";
    private const string Board    = "M4,4 H20 V20 H4 Z M8,8 H12 V12 H8 Z M15,8 V16 M8,15 H12";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var cat = (value as string ?? "").ToLowerInvariant();
        var data = cat switch
        {
            "display" => Monitor,
            "net" => Ethernet,
            "bluetooth" => Bluetooth,
            "usb" or "hidclass" or "mouse" or "keyboard" or "ports" => Usb,
            "scsiadapter" or "hdc" => Storage,
            "media" or "audioendpoint" => Speaker,
            "image" or "camera" => Camera,
            "system" => Chip,
            "motherboard" => Board,
            _ => Board,
        };
        return Geometry.Parse(data);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>true → Collapsed, false → Visible (opposite of BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
