using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ClankerExplorer.Tests.TestAppBuilder))]

namespace ClankerExplorer.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
