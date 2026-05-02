using System.Windows;
using Microsoft.Win32;

namespace NetClipboard;

public enum AppTheme { System, Dark, Light }

public static class ThemeManager
{
    private static readonly Uri DarkUri  = new("Themes/DarkTheme.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("Themes/LightTheme.xaml", UriKind.Relative);

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static event Action? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var actual = theme == AppTheme.System ? GetSystemTheme() : theme;
        var uri = actual == AppTheme.Light ? LightUri : DarkUri;
        var dict = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = dict;
        else
            merged.Add(dict);

        ThemeChanged?.Invoke();
    }

    public static AppTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int i)
                return i == 1 ? AppTheme.Light : AppTheme.Dark;
        }
        catch { }
        return AppTheme.Dark;
    }

    public static void StartWatcher()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void StopWatcher()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && Current == AppTheme.System)
        {
            Application.Current.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        }
    }
}
