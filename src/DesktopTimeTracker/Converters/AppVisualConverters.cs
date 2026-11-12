using System.Globalization;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopTimeTracker.Converters;

/// <summary>exe 路径 → 应用图标（带静态缓存，供排行列表使用）。</summary>
public sealed class ExeIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryGetIcon(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(exePath, out ImageSource? cached)) return cached;
            ImageSource? result = null;
            try
            {
                using Icon? icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                {
                    result = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    result.Freeze();
                }
            }
            catch (Exception) { }
            Cache[exePath] = result;
            return result;
        }
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => TryGetIcon(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>exe 路径 → 有图标时隐藏（用来在无图标时显示排名数字）。</summary>
public sealed class ExeIconFallbackVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ExeIconConverter.TryGetIcon(value as string) is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>分类名 → 主题色画刷。</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string cat = (value as string ?? "").Trim();
        string key = cat switch
        {
            "工作" => "GreenBrush",
            "学习" => "CyanBrush",
            "沟通" => "PurpleBrush",
            "上网" => "AccentBrush",
            "娱乐" => "AmberBrush",
            _ => "MutedBrush"
        };
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>比例 × 参数 = 像素宽度（用于分类堆叠条）。</summary>
public sealed class RatioWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double ratio) return 0d;
        double total = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double p) ? p : 1d;
        return Math.Max(0, ratio * total);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>相等比较：value == parameter（用于胶囊单选按钮双向绑定）。</summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string s ? s : Binding.DoNothing;
}
