using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace NetClipboard;

/// <summary>
/// Picks file vs text template per entry type.
/// </summary>
public class EntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is ClipboardFileEntry ? FileTemplate : TextTemplate;
}

/// <summary>
/// Returns Visible when IsEncrypted=true AND IsDecrypted=false (i.e. still locked).
/// Bound to IsEncrypted; IsDecrypted checked via DataTrigger override in XAML.
/// Simplified: Visible when true, Collapsed when false.
/// </summary>
public class EncryptedLockedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverse: Visible when IsEncrypted=false, Collapsed when true.
/// </summary>
public class EncryptedLockedInverseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
