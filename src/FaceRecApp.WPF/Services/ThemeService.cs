using System.Windows;

namespace FaceRecApp.WPF.Services;

public class ThemeService
{
    public bool IsDark { get; private set; }

    public void ToggleTheme()
    {
        IsDark = !IsDark;
        var colorsUri = IsDark
            ? new Uri("Themes/DarkColors.xaml", UriKind.Relative)
            : new Uri("Themes/Colors.xaml", UriKind.Relative);

        var mergedDicts = Application.Current.Resources.MergedDictionaries;
        if (mergedDicts.Count > 0)
            mergedDicts[0] = new ResourceDictionary { Source = colorsUri };
    }
}
