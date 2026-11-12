using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopTimeTracker.Converters;

public sealed class DurationTextConverter : IValueConverter
{
    public bool Compact { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double seconds = value is double d ? d : 0;
        if (seconds < 0) seconds = 0;
        return Format(seconds, Compact);
    }

    public static string Format(double seconds, bool compact = false)
    {
        if (seconds < 1) return "0秒";
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1)
        {
            int hours = (int)span.TotalHours;
            return compact ? $"{hours}时{span.Minutes}分" : $"{hours} 小时 {span.Minutes} 分";
        }
        if (span.TotalMinutes >= 1)
        {
            return compact ? $"{span.Minutes}分{span.Seconds}秒" : $"{span.Minutes} 分 {span.Seconds} 秒";
        }
        return $"{span.Seconds} 秒";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ClockConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime dt && dt != default ? dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{d * 100:0.#}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RatioToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double ratio = value is double d ? d : 0;
        double max = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ? m : 100;
        return Math.Max(2.0, Math.Clamp(ratio, 0, 1) * max);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FallbackTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? "";
        return string.IsNullOrWhiteSpace(text) ? parameter as string ?? "—" : text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class RankConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(CultureInfo.InvariantCulture) : "1";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}