using Avalonia;
using Avalonia.Headless;
using Fieldbench.App;

[assembly: AvaloniaTestApplication(typeof(Fieldbench.App.UITests.TestAppBuilder))]

namespace Fieldbench.App.UITests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        });
}
