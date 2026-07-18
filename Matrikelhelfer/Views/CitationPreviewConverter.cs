using System;
using System.Globalization;
using System.Windows.Data;
using Matrikelhelfer.Models;
using Matrikelhelfer.Services;

namespace Matrikelhelfer.Views;

// Renders a citation style's template against the currently displayed
// citation - used by the style ComboBox's ItemTemplate so every entry
// previews what it would actually look like, not just its name. A
// MultiBinding (not a plain converter) since the item template's own
// DataContext is the CitationStyle being rendered, and reaching the
// ViewModel's DisplayedInfo needs a second binding via RelativeSource.
class CitationPreviewConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is CitationStyle style && values[1] is MatriculaInfo info)
        {
            return CitationTemplateEngine.Render(style, info);
        }
        return "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
