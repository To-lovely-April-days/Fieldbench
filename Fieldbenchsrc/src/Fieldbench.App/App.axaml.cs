using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Fieldbench.App.ViewModels;
using Fieldbench.App.Views;
using Fieldbench.Core.Sessions;

namespace Fieldbench.App;

public class App : Application
{
    private ResourceInclude? _currentLang;

    public static Workbench Workbench { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Workbench ??= new Workbench();

        ApplyTheme(Workbench.Settings.Theme);
        ApplyLanguage(Workbench.Settings.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(Workbench);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => Workbench.SettingsStore.Save();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(string theme)
    {
        RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Workbench.Settings.Theme = theme;
    }

    public void ApplyLanguage(string lang)
    {
        var uri = new Uri(lang == "zh"
            ? "avares://Fieldbench.App/Assets/Strings.zh.axaml"
            : "avares://Fieldbench.App/Assets/Strings.en.axaml");

        var include = new ResourceInclude(uri) { Source = uri };
        if (_currentLang is not null)
        {
            Resources.MergedDictionaries.Remove(_currentLang);
        }

        Resources.MergedDictionaries.Add(include);
        _currentLang = include;
        Workbench.Settings.Language = lang;
    }

    public static new App? Current => Application.Current as App;
}
