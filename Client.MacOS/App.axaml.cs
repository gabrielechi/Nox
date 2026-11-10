using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Client.Core;
using Client.Core.Services;

namespace Client.MacOS;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Texts.LanguageChanged += PublishTexts;
        Texts.Apply(new ClientAppSettingsService().Load().Language);
        PublishTexts();
    }

    /// <summary>
    /// Exposes every localized string as an application resource ("NoxText.Key"),
    /// so XAML can use {DynamicResource NoxText.Key} and follow language changes.
    /// </summary>
    private void PublishTexts()
    {
        foreach (var (key, value) in Texts.Entries())
            Resources["NoxText." + key] = value;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
