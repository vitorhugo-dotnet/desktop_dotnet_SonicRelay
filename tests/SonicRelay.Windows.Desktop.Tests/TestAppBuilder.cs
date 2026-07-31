using Avalonia;
using Avalonia.Headless;
using SonicRelay.Windows.Desktop;
using SonicRelay.Windows.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Builds the real shell <see cref="App"/> on the headless platform with Skia rendering, so
/// smoke tests can lay out and rasterize the actual design system (tokens, fonts, controls)
/// rather than a stub.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
