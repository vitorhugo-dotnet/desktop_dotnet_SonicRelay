using System.Reflection;
using Avalonia.Headless.XUnit;
using SonicRelay.Windows.Desktop.Views;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Pins the public product name to the canonical spelling from
/// dotnet_SonicRelay#38: <c>SonicRelay</c>, one word, capital S and R, no space
/// and no underscore. The assembly name itself stays
/// <c>SonicRelay.Windows.Desktop</c> — that is a technical identifier the
/// installer, the shortcut target and the CI pipeline all key off of — so the
/// user-visible strings have to be set explicitly instead of defaulting to it.
/// </summary>
public sealed class BrandingTests
{
    private const string PublicName = "SonicRelay";

    private static readonly Assembly DesktopAssembly = typeof(MainWindow).Assembly;

    [Fact]
    public void ProductNameIsTheCanonicalPublicName()
    {
        // Surfaces in Windows Explorer's "Product name" and in Task Manager.
        var product = DesktopAssembly.GetCustomAttribute<AssemblyProductAttribute>();

        Assert.NotNull(product);
        Assert.Equal(PublicName, product.Product);
    }

    [Fact]
    public void FileDescriptionCarriesTheCanonicalPublicName()
    {
        // AssemblyTitle becomes the executable's FileDescription, which is the
        // column Task Manager shows for a running process.
        var title = DesktopAssembly.GetCustomAttribute<AssemblyTitleAttribute>();

        Assert.NotNull(title);
        Assert.Equal($"{PublicName} Publisher", title.Title);
    }

    [Fact]
    public void AssemblyNameIsUnchanged()
    {
        // Guards against "fixing" the branding by renaming the assembly, which
        // would break the installer, the shortcut target and the CI publish step.
        Assert.Equal("SonicRelay.Windows.Desktop", DesktopAssembly.GetName().Name);
    }

    [Theory]
    [InlineData("Sonic Relay")]
    [InlineData("Sonic_Relay")]
    public void MetadataNeverUsesASpacedOrUnderscoredVariant(string forbidden)
    {
        var strings = new[]
        {
            DesktopAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product,
            DesktopAssembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title,
            DesktopAssembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description,
            DesktopAssembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company,
        };

        foreach (var value in strings)
        {
            Assert.DoesNotContain(forbidden, value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [AvaloniaFact]
    public void MainWindowTitleUsesTheCanonicalPublicName()
    {
        var window = new MainWindow();

        Assert.Equal($"{PublicName} Publisher", window.Title);
    }
}
