# Pairing Nav & Relay/Coturn Settings (Windows Publisher) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the sign-out-then-back-to-Dashboard regression by making Pairing a normal,
always-reachable nav page instead of a full-shell gate; make the backend URL editable from
Settings; and add a `RelayMode`/coturn-URL settings UI synced with the
`/api/settings/relay` endpoint added in `dotnet_SonicRelay`
(`docs/superpowers/plans/2026-08-04-relay-coturn-settings-api.md`, which must land and deploy
before this plan's Tasks 3-5 are useful end-to-end, though they build and test fine against
that plan's already-fixed contracts either way).

**Architecture:** `MainWindow.axaml`'s sidebar/top-bar stop being hidden by an
`IsAuthenticated`-driven gate; `PairingView` becomes a normal page like `AudioView`/
`SettingsView`. `SettingsViewModel` grows an editable backend-URL field (wired to
`UserConfigurationLoader.SaveBackendAsync` + a runtime re-`Attach`) and a `RelayMode`/coturn
section backed by a new `IRelaySettingsApiClient`. `RelayPreferenceStore` becomes a
last-known-good cache for the server-synced `RelayMode` instead of the sole source of truth.

**Tech Stack:** Avalonia (XAML + C# view models), xUnit, the existing `ApiHttpClient`/
`RelayCommand`/test-double conventions already in this codebase.

## Global Constraints

- `RelayMode` is one of exactly three string values, matching the backend
  (`SonicRelay.Windows.Core.Configuration.RelayModes`): `automatic`, `forceRelay`,
  `disableFallback`.
- Changing `RelayMode` or the coturn URL from Settings writes through to
  `PUT /api/settings/relay` directly — never a local-only apply — so both apps converge on
  one server value.
- The local `RelayPreferenceStore` file is a last-known-good cache only; it is always
  overwritten by the latest server-confirmed value, never edited independently of a
  successful server round trip.
- Nothing here changes `PublisherWorkflow.LogoutAsync`, `DeviceIdentitySession.ResetAsync`,
  or `PublisherUiStateResolver` — all already correct from PR #48.
- Every existing test in `MainWindowViewModelStateTests.cs`,
  `RelayPreferenceStoreTests.cs`, and `WebRtcEndpointsTests`-equivalent Windows suites that
  isn't explicitly rewritten below must keep passing.

---

### Task 1: Always-visible shell + Pairing as a nav page

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/NavigationItem.cs`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/Views/MainWindow.axaml`
- Create: `src/SonicRelay.Windows.Desktop/Properties/AssemblyInfo.cs`
- Modify: `src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj` (only if it does
  not already exclude `Properties/AssemblyInfo.cs` from implicit globbing — .NET SDK-style
  projects pick up any `.cs` file under the project folder automatically, so no edit is
  normally needed; verify by building after Step 3 below)
- Test: `tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelStateTests.cs`

**Interfaces:**
- Produces: `PageKey.Pairing`; `MainWindowViewModel.IsPairing` (bool); `MainWindowViewModel`
  no longer has `ShowPairing`/`ShouldShowPairing`; `internal Task LogoutAsync()` (was
  `private`, now testable via `InternalsVisibleTo`).

- [ ] **Step 1: Write the failing tests**

Replace the *entire* contents of
`tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelStateTests.cs` with:

```csharp
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Pairing is a normal, always-reachable nav page (issue #26 follow-up) — it is no longer a
/// full-shell gate keyed off device-identity bootstrap, which is what let a sign-out's
/// automatic re-bootstrap silently flip the shell back to the dashboard before the user ever
/// saw the fresh pairing code.
/// </summary>
public sealed class MainWindowViewModelStateTests
{
    [Fact]
    public void Navigation_includes_a_pairing_destination()
    {
        var vm = new MainWindowViewModel();

        Assert.Contains(vm.Navigation, item => item.Key == PageKey.Pairing);
    }

    [Fact]
    public void Fresh_view_model_opens_on_the_dashboard()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
        Assert.False(vm.IsPairing);
    }

    [Fact]
    public void Preview_view_model_opens_on_the_dashboard()
    {
        var vm = MainWindowViewModel.CreatePreview();

        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
    }

    [Fact]
    public async Task Attaching_a_runtime_before_bootstrap_still_renders_with_no_pairing_view_model_yet()
    {
        // PublisherRuntime only creates its PairingViewModel once device-identity bootstrap
        // succeeds; attaching a freshly created runtime (bootstrap not yet run) must not
        // crash and must leave Pairing null until bootstrap completes (issue #26).
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio());
        var vm = new MainWindowViewModel();

        vm.Attach(runtime);

        Assert.Null(vm.Pairing);
    }

    [Fact]
    public async Task Signing_out_selects_the_pairing_page_even_if_rebootstrap_immediately_succeeds()
    {
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio());
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);
        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Session);

        await vm.LogoutAsync();

        Assert.Equal(PageKey.Pairing, vm.CurrentPage);
    }

    [Fact]
    public void Navigation_defaults_to_the_dashboard()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
        Assert.True(vm.IsDashboard);
        Assert.False(vm.IsSession);
        Assert.False(vm.IsDiagnostics);
        Assert.False(vm.IsPairing);
    }

    [Fact]
    public void Selecting_pairing_switches_the_current_page()
    {
        var vm = new MainWindowViewModel();

        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Pairing);

        Assert.Equal(PageKey.Pairing, vm.CurrentPage);
        Assert.True(vm.IsPairing);
        Assert.False(vm.IsDashboard);
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public AudioCaptureState State => AudioCaptureState.Stopped;
        public AudioCaptureDiagnostics Diagnostics { get; } = new(AudioCaptureState.Stopped, null, null, AudioLevelSnapshot.Silence, 0, 0);
        public string? PreferredDeviceId => null;
        public event Action<AudioCaptureState>? StateChanged;
        public event Action<AudioFrame>? FrameCaptured;
        public event Action<AudioLevelSnapshot>? LevelChanged;
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

(This keeps the rest of that test file's other test classes/methods, e.g. any beyond
`Selecting a destination switches...`, if the original file has more below what's quoted
here — check the current file's tail past the `Navigation_defaults_to_the_dashboard`/
`Selecting a destination...` tests before deleting anything past them, and keep those intact
unmodified.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj --filter FullyQualifiedName~MainWindowViewModelStateTests`
Expected: FAIL to build — `PageKey.Pairing`, `IsPairing`, and public `LogoutAsync` don't
exist yet.

- [ ] **Step 3: Add `PageKey.Pairing` and `InternalsVisibleTo`**

In `src/SonicRelay.Windows.Desktop/ViewModels/NavigationItem.cs`, change:

```csharp
public enum PageKey { Dashboard, Audio, Session, Diagnostics, Settings }
```

to:

```csharp
public enum PageKey { Dashboard, Pairing, Audio, Session, Diagnostics, Settings }
```

Create `src/SonicRelay.Windows.Desktop/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SonicRelay.Windows.Desktop.Tests")]
```

- [ ] **Step 4: Update `MainWindowViewModel`**

In `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`:

Add a `Pairing` entry to the `Navigation` list (right after `Dashboard`) inside the
constructor:

```csharp
Navigation =
[
    new NavigationItem(PageKey.Dashboard, "◧", "Dashboard"),
    new NavigationItem(PageKey.Pairing, "⇄", "Pairing"),
    new NavigationItem(PageKey.Audio, "♪", "Audio"),
    new NavigationItem(PageKey.Session, "⧉", "Session"),
    new NavigationItem(PageKey.Diagnostics, "⚙", "Diagnostics"),
    new NavigationItem(PageKey.Settings, "⚑", "Settings"),
];
```

Delete the `showPairing` field, the `ShowPairing` property, and the `ShouldShowPairing`
static method entirely.

In the `SelectedNavigation` setter, add `IsPairing` to the list of raised properties (next to
`IsDashboard`):

```csharp
RaisePropertyChanged(nameof(IsDashboard));
RaisePropertyChanged(nameof(IsPairing));
RaisePropertyChanged(nameof(IsSession));
```

Add the computed property next to `IsDashboard`:

```csharp
public bool IsPairing => CurrentPage == PageKey.Pairing;
```

In `Apply`, delete the line `ShowPairing = ShouldShowPairing(state);` — everything else in
that method (`Shell.Update`, `Pairing = runtime?.Pairing`, `RaiseCommandStates()`, etc.) stays.

Change `LogoutAsync` from `private` to `internal` and have it select the Pairing tab right
after signing out:

```csharp
internal async Task LogoutAsync()
{
    if (workflow is null) return;
    await workflow.LogoutAsync();
    SelectedNavigation = Navigation.Single(item => item.Key == PageKey.Pairing);
    if (runtime is not null)
    {
        try { await runtime.InitializeDeviceIdentityAsync(); }
        catch { }
    }
}
```

- [ ] **Step 5: Update `MainWindow.axaml`**

Replace the whole `<Panel>` body (from `<!-- Pairing surface...` through the closing
`</Panel>` right before `</Window>`) with:

```xml
  <Panel>
    <Grid ColumnDefinitions="Auto,*">
    <!-- Left navigation rail -->
    <controls:SidebarNavigation Grid.Column="0" Width="232" />

    <!-- Main area -->
    <DockPanel Grid.Column="1" LastChildFill="True">

      <!-- Top bar (shared across pages) -->
      <Border DockPanel.Dock="Top"
              Background="{DynamicResource Sonic.SidebarBackgroundBrush}"
              BorderBrush="{DynamicResource Sonic.CardBorderBrush}"
              BorderThickness="0,0,0,1"
              Padding="24,16">
        <Grid ColumnDefinitions="*,Auto,Auto">
          <StackPanel Spacing="2" VerticalAlignment="Center">
            <TextBlock Text="{Binding PageTitle}" FontSize="{StaticResource Sonic.FontSizeHeadline}"
                       FontWeight="SemiBold"
                       Foreground="{DynamicResource Sonic.TextPrimaryBrush}" />
            <TextBlock Text="{Binding PageSubtitle}" Classes="metric-label" />
          </StackPanel>
          <controls:AccountStatusHeader Grid.Column="1"
                                        DataContext="{Binding Shell}" />
          <!-- Forgets this device's identity and returns to the pairing page (issue #26
               follow-up) — the recovery path for a stale/rejected device credential that
               would otherwise never show a pairing code again without an app restart. -->
          <Button Grid.Column="2" Margin="16,0,0,0" Classes="ghost"
                  Content="Sign out" Command="{Binding LogoutCommand}" />
        </Grid>
      </Border>

      <!-- Page content, swapped by the sidebar selection -->
      <Panel>
        <!-- Pairing (issue #26 follow-up: a normal, always-reachable page instead of a
             full-shell gate — the old gate hid Settings too, so a bad backend URL left no
             way back in, and hid the pairing surface at exactly the moment bootstrap gave it
             real content to show). Renders its own "device identity unavailable" placeholder
             when Pairing is null. -->
        <controls:PairingView DataContext="{Binding Pairing}" IsVisible="{Binding IsPairing}" />

        <!-- Dashboard -->
        <DockPanel IsVisible="{Binding IsDashboard}" LastChildFill="True">
          <controls:TechnicalConsole DockPanel.Dock="Bottom"
                                     Margin="24,0,24,20"
                                     DataContext="{Binding Shell}" />
          <Border DockPanel.Dock="Top" Padding="24,16,24,0">
            <StackPanel Orientation="Horizontal" Spacing="10">
              <Button Content="Create session" Command="{Binding CreateSessionCommand}" />
              <Button Content="Start audio" Command="{Binding StartAudioCommand}" />
              <Button Content="Stop audio" Command="{Binding StopAudioCommand}" />
              <Button Content="End session" Command="{Binding EndSessionCommand}" />
              <Button Content="Retry" Command="{Binding RetryCommand}" />
            </StackPanel>
          </Border>
          <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
            <WrapPanel Orientation="Horizontal" Margin="16,16,16,0">
              <controls:SessionCodeCard Width="440" Margin="8" DataContext="{Binding Shell}" />
              <controls:AudioLevelMonitor Width="440" Margin="8"
                                          Level="{Binding Shell.AudioLevelFraction}"
                                          PeakText="{Binding Shell.AudioPeakDbText}"
                                          IsActive="{Binding Shell.IsCapturing}" />
              <controls:InfrastructureStatusCard Width="360" Margin="8" DataContext="{Binding Shell}" />
              <Border Classes="card" Width="360" Margin="8">
                <StackPanel Spacing="14">
                  <TextBlock Classes="card-title" Text="Stream Quality" />
                  <Grid ColumnDefinitions="*,*,*" ColumnSpacing="10">
                    <Border Grid.Column="0" Classes="card-elevated">
                      <StackPanel Spacing="4">
                        <TextBlock Classes="metric-label" Text="Latency" />
                        <TextBlock Classes="mono metric-value" Text="{Binding Shell.LatencyText}" />
                      </StackPanel>
                    </Border>
                    <Border Grid.Column="1" Classes="card-elevated">
                      <StackPanel Spacing="4">
                        <TextBlock Classes="metric-label" Text="Jitter" />
                        <TextBlock Classes="mono metric-value" Text="{Binding Shell.JitterText}" />
                      </StackPanel>
                    </Border>
                    <Border Grid.Column="2" Classes="card-elevated">
                      <StackPanel Spacing="4">
                        <TextBlock Classes="metric-label" Text="Loss" />
                        <TextBlock Classes="mono metric-value" Text="{Binding Shell.PacketLossText}" />
                      </StackPanel>
                    </Border>
                  </Grid>
                </StackPanel>
              </Border>
              <controls:BandwidthGauge Width="360" Margin="8" DataContext="{Binding Shell}" />
            </WrapPanel>
          </ScrollViewer>
        </DockPanel>

        <!-- Session (DataContext is the shell VM, so IsSession is read from the root VM) -->
        <controls:SessionView DataContext="{Binding Shell}"
                              IsVisible="{Binding ((vm:MainWindowViewModel)DataContext).IsSession, ElementName=RootWindow}" />

        <!-- Diagnostics -->
        <controls:DiagnosticsView
            IsVisible="{Binding ((vm:MainWindowViewModel)DataContext).IsDiagnostics, ElementName=RootWindow}" />

        <!-- Audio -->
        <controls:AudioView DataContext="{Binding Audio}"
                            IsVisible="{Binding ((vm:MainWindowViewModel)DataContext).IsAudio, ElementName=RootWindow}" />

        <!-- Settings -->
        <controls:SettingsView DataContext="{Binding Settings}"
                               IsVisible="{Binding ((vm:MainWindowViewModel)DataContext).IsSettings, ElementName=RootWindow}" />
      </Panel>
    </DockPanel>
    </Grid>
  </Panel>
```

(Note `PairingView`'s `IsVisible` binds directly to `IsPairing` with no `ElementName` needed —
unlike the other conditionally-shown pages, its `DataContext` is `Pairing`, not `Shell`/root,
so it must stay a sibling of the `Grid`'s data-context chain the same way it already was; the
existing `x:DataType="vm:MainWindowViewModel"` root context still resolves `IsPairing` on
`PairingView` correctly because `DataContext="{Binding Pairing}"` only overrides the context
seen by `PairingView`'s own children, not the `IsVisible` binding evaluated on `PairingView`
itself before that override applies.)

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj --filter FullyQualifiedName~MainWindowViewModelStateTests`
Expected: PASS.

- [ ] **Step 7: Build and run the full Desktop test project**

Run: `dotnet build src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj`
Expected: builds clean (confirms `InternalsVisibleTo` picked up the new `Properties/AssemblyInfo.cs` without a `.csproj` edit).

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj`
Expected: PASS, no regressions elsewhere in this test project.

- [ ] **Step 8: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/ViewModels/NavigationItem.cs src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs src/SonicRelay.Windows.Desktop/Views/MainWindow.axaml src/SonicRelay.Windows.Desktop/Properties/AssemblyInfo.cs tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelStateTests.cs
git commit -m "Make Pairing an always-reachable nav page instead of a full-shell gate"
```

---

### Task 2: Editable backend URL with live reconnect

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/SettingsViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/Controls/SettingsView.axaml`
- Test: `tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs` (new — check first
  whether this file already exists; if it does, add to it instead of creating it)

**Interfaces:**
- Consumes: `UserConfigurationLoader.SaveBackendAsync(Uri, CancellationToken)` (already
  exists, currently unused), `DesktopRuntimeFactory.Create(Uri)` (already exists),
  `MainWindowViewModel.Attach(PublisherRuntime?)` (already exists).
- Produces: `SettingsViewModel.BackendUrlInput` (string, two-way bindable),
  `SettingsViewModel.BackendUrlError` (string?), `SettingsViewModel.SaveBackendUrlCommand`
  (`RelayCommand`); `internal Task<string?> MainWindowViewModel.ChangeBackendUrlAsync(string
  rawUrl)` returning `null` on success or a user-facing error message.

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs` (or append to it if
it already exists):

```csharp
using SonicRelay.Windows.Desktop.ViewModels;

namespace SonicRelay.Windows.Desktop.Tests;

public sealed class SettingsViewModelBackendUrlTests
{
    [Fact]
    public async Task Save_rejects_a_non_absolute_url_without_calling_the_change_delegate()
    {
        var called = false;
        var vm = MakeConnectedViewModel(url =>
        {
            called = true;
            return Task.FromResult<string?>(null);
        });
        vm.BackendUrlInput = "not-a-url";

        await vm.SaveBackendUrlAsync();

        Assert.False(called);
        Assert.NotNull(vm.BackendUrlError);
    }

    [Fact]
    public async Task Save_surfaces_the_error_the_change_delegate_returns()
    {
        var vm = MakeConnectedViewModel(_ => Task.FromResult<string?>("Backend unreachable."));
        vm.BackendUrlInput = "https://new-backend.example.test/";

        await vm.SaveBackendUrlAsync();

        Assert.Equal("Backend unreachable.", vm.BackendUrlError);
    }

    [Fact]
    public async Task Successful_save_clears_any_previous_error()
    {
        var vm = MakeConnectedViewModel(_ => Task.FromResult<string?>(null));
        vm.BackendUrlInput = "https://good-backend.example.test/";

        await vm.SaveBackendUrlAsync();

        Assert.Null(vm.BackendUrlError);
    }

    private static SettingsViewModel MakeConnectedViewModel(Func<string, Task<string?>> changeBackendUrl) =>
        new(
            "https://old-backend.example.test/",
            new SonicRelay.Windows.Core.Configuration.RelayPreferenceStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-test-{Guid.NewGuid():N}.json")),
            new SonicRelay.Windows.Core.Audio.AudioQualityStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-test-quality-{Guid.NewGuid():N}.json")),
            changeBackendUrl);
}
```

(Check `AudioQualityStore`'s constructor signature before relying on the path override above
— if it does not take an optional path the same way `RelayPreferenceStore` does, use its
actual no-op-safe constructor instead; either way, avoid touching real
`%LOCALAPPDATA%\SonicRelay` files from a unit test.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj --filter FullyQualifiedName~SettingsViewModelBackendUrlTests`
Expected: FAIL to build — `SettingsViewModel` has no 4-argument constructor,
`BackendUrlInput`/`BackendUrlError`/`SaveBackendUrlAsync` don't exist yet.

- [ ] **Step 3: Add the editable backend-URL state to `SettingsViewModel`**

In `src/SonicRelay.Windows.Desktop/ViewModels/SettingsViewModel.cs`, add fields, a new
constructor overload, and the save method:

```csharp
private readonly Func<string, Task<string?>>? changeBackendUrl;
private string backendUrlInput = "";
private string? backendUrlError;

public SettingsViewModel(
    string backendUrl,
    RelayPreferenceStore relay,
    AudioQualityStore quality,
    Func<string, Task<string?>> changeBackendUrl)
    : this(backendUrl, relay, quality)
{
    this.changeBackendUrl = changeBackendUrl ?? throw new ArgumentNullException(nameof(changeBackendUrl));
    backendUrlInput = BackendUrl == "—" ? "" : BackendUrl;
    SaveBackendUrlCommand = new RelayCommand(SaveBackendUrlAsync);
}

public string BackendUrlInput
{
    get => backendUrlInput;
    set => SetProperty(ref backendUrlInput, value);
}

public string? BackendUrlError
{
    get => backendUrlError;
    private set => SetProperty(ref backendUrlError, value);
}

public RelayCommand SaveBackendUrlCommand { get; } = new(() => Task.CompletedTask);

public async Task SaveBackendUrlAsync()
{
    if (!Uri.TryCreate(backendUrlInput, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
    {
        BackendUrlError = "Enter a valid http:// or https:// URL.";
        return;
    }
    if (changeBackendUrl is null) return;

    BackendUrlError = await changeBackendUrl(uri.AbsoluteUri);
}
```

(`RelayCommand` requires a non-null delegate at construction, so the disconnected
(no-argument) constructor's `SaveBackendUrlCommand` field initializer above
— `new(() => Task.CompletedTask)` — is what backs it until the 4-argument constructor
overwrites it; this mirrors how `MainWindowViewModel` always has a live `RelayCommand`
instance, never null.)

- [ ] **Step 4: Wire `MainWindowViewModel.ChangeBackendUrlAsync` and pass it to `Attach`**

In `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`, change the `Settings =`
assignment inside `Attach`:

```csharp
Settings = next is null
    ? new SettingsViewModel()
    : new SettingsViewModel(next.BackendBaseUrl.ToString(), next.RelayPreference, next.AudioQuality, ChangeBackendUrlAsync);
```

Add the new method (near `LogoutAsync`):

```csharp
/// <summary>
/// Saves a new backend URL and reattaches to it live (issue #26 follow-up — a
/// <see cref="UserConfigurationLoader.SaveBackendAsync"/> already existed but nothing
/// called it, and the old full-shell pairing gate meant Settings itself was unreachable
/// whenever the configured backend was bad). Only rolls back to the previous runtime for
/// a save/parse/platform failure — an unreachable *new* backend is not rolled back, since
/// the always-visible shell (see Task 1) now lets the user just try again from this same
/// page, exactly like a bad URL at cold start.
/// </summary>
internal async Task<string?> ChangeBackendUrlAsync(string rawUrl)
{
    if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
    {
        return "Enter a valid http:// or https:// URL.";
    }

    PublisherRuntime? next;
    try
    {
        await new SonicRelay.Windows.Core.Configuration.UserConfigurationLoader().SaveBackendAsync(uri);
        next = SonicRelay.Windows.Desktop.DesktopRuntimeFactory.Create(uri);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or SonicRelay.Windows.Core.Configuration.ConfigurationValidationException)
    {
        return exception.Message;
    }

    if (next is null)
    {
        return "This platform has no supported publisher runtime.";
    }

    var previous = runtime;
    Attach(next);
    try { await next.InitializeDeviceIdentityAsync(); } catch { }
    if (previous is not null)
    {
        await previous.DisposeAsync();
    }
    return null;
}
```

- [ ] **Step 5: Add the UI**

In `src/SonicRelay.Windows.Desktop/Controls/SettingsView.axaml`, inside the existing
"Connection" card, replace the read-only `BackendUrl` `TextBlock` block with an editable field
plus a Save button and error text:

```xml
          <StackPanel Spacing="4">
            <TextBlock Classes="metric-label" Text="Backend" />
            <TextBox Text="{Binding BackendUrlInput}" Watermark="https://your-server.example.com/"
                     AutomationProperties.Name="Backend URL" />
            <StackPanel Orientation="Horizontal" Spacing="8">
              <Button Content="Save backend URL" Command="{Binding SaveBackendUrlCommand}" />
            </StackPanel>
            <TextBlock Classes="metric-label" Foreground="{DynamicResource Sonic.DangerBrush}"
                       IsVisible="{Binding BackendUrlError, Converter={x:Static ObjectConverters.IsNotNull}}"
                       Text="{Binding BackendUrlError}" TextWrapping="Wrap" />
          </StackPanel>
```

(`ObjectConverters` needs `xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"` — already
present — plus `Avalonia.Data.Converters`; add
`xmlns:conv="using:Avalonia.Data.Converters"` and use `{x:Static conv:ObjectConverters.IsNotNull}`
if the bare `ObjectConverters` reference doesn't resolve, matching whatever import style the
rest of this file already uses for static converters — check for an existing example in this
file or `PairingView.axaml`/`SessionView.axaml` first and copy it exactly rather than guessing.)

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj --filter FullyQualifiedName~SettingsViewModelBackendUrlTests`
Expected: PASS.

- [ ] **Step 7: Build the Desktop project (catches XAML binding typos) and run its full test suite**

Run: `dotnet build src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj`
Expected: builds clean.

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/ViewModels/SettingsViewModel.cs src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs src/SonicRelay.Windows.Desktop/Controls/SettingsView.axaml tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs
git commit -m "Make the backend URL editable from Settings, with live reconnect"
```

---

### Task 3: `RelaySettingsApiClient` and a 3-way `RelayMode` preference

**Files:**
- Create: `src/SonicRelay.Windows.ApiClient/Settings/RelaySettingsApiClient.cs`
- Create: `src/SonicRelay.Windows.Core/Configuration/RelayModes.cs`
- Modify: `src/SonicRelay.Windows.Core/Configuration/RelayPreferenceStore.cs`
- Test: `tests/SonicRelay.Windows.ApiClient.Tests/RelaySettingsApiClientTests.cs` (new)
- Test: `tests/SonicRelay.Windows.Core.Tests/RelayPreferenceStoreTests.cs` (rewrite)

**Interfaces:**
- Produces: `SonicRelay.Windows.Core.Configuration.RelayModes` (`Automatic`, `ForceRelay`,
  `DisableFallback` string constants + `IsValid`); `RelayPreferenceStore.RelayMode` (string,
  replaces the old sole-source-of-truth `ForceRelay` bool — `ForceRelay` becomes a read-only
  computed property: `RelayMode == RelayModes.ForceRelay`); `RelayPreferenceStore
  .SetRelayModeAsync(string, CancellationToken)`; `RelayPreferenceStore
  .ApplyFetchedRelayModeAsync(string, CancellationToken)` (same persistence, different name to
  make call sites self-documenting about *why* they're writing); `IRelaySettingsApiClient`
  with `GetAsync`/`UpdateAsync`; `RelaySettingsResponse(string RelayMode, IReadOnlyList<string>
  TurnUris, bool HasCustomTurnSecret)`; `UpdateRelaySettingsRequest(string? RelayMode,
  IReadOnlyList<string>? TurnUris, string? TurnStaticAuthSecret)` — field names and shapes
  match `dotnet_SonicRelay`'s `SettingsContracts.cs` exactly (Task 3 of that repo's plan).

- [ ] **Step 1: Write the failing `RelayPreferenceStore` tests**

Replace the entire contents of `tests/SonicRelay.Windows.Core.Tests/RelayPreferenceStoreTests.cs`:

```csharp
using System.Text.Json;
using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Core.Tests;

public sealed class RelayPreferenceStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"sonicrelay-prefs-{Guid.NewGuid():N}.json");

    [Fact]
    public void DefaultsToAutomaticWhenNoFileExists()
    {
        var store = new RelayPreferenceStore(path);

        Assert.Equal(RelayModes.Automatic, store.RelayMode);
        Assert.False(store.ForceRelay);
    }

    [Fact]
    public async Task PersistsRelayModeAcrossInstances()
    {
        await new RelayPreferenceStore(path).SetRelayModeAsync(RelayModes.ForceRelay);

        var reloaded = new RelayPreferenceStore(path);
        Assert.Equal(RelayModes.ForceRelay, reloaded.RelayMode);
        Assert.True(reloaded.ForceRelay);
    }

    [Fact]
    public async Task ApplyFetchedRelayModePersistsWithoutRequiringAWriteThroughCaller()
    {
        await new RelayPreferenceStore(path).ApplyFetchedRelayModeAsync(RelayModes.DisableFallback);

        Assert.Equal(RelayModes.DisableFallback, new RelayPreferenceStore(path).RelayMode);
    }

    [Fact]
    public void ReadingAnOldBooleanShapedFileMigratesForceRelayTrueToTheForceRelayMode()
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new { ForceRelay = true }));

        Assert.Equal(RelayModes.ForceRelay, new RelayPreferenceStore(path).RelayMode);
    }

    [Fact]
    public void ReadingAnOldBooleanShapedFileMigratesForceRelayFalseToAutomatic()
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new { ForceRelay = false }));

        Assert.Equal(RelayModes.Automatic, new RelayPreferenceStore(path).RelayMode);
    }

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Core.Tests/SonicRelay.Windows.Core.Tests.csproj --filter FullyQualifiedName~RelayPreferenceStoreTests`
Expected: FAIL to build — `RelayModes`, `RelayMode`, `SetRelayModeAsync`,
`ApplyFetchedRelayModeAsync` don't exist yet.

- [ ] **Step 3: Add `RelayModes`**

```csharp
namespace SonicRelay.Windows.Core.Configuration;

/// <summary>The three mutually-exclusive relay policies, matching the backend's RelayModes
/// (dotnet_SonicRelay's SonicRelay.Domain.RelaySettings.RelayModes) string-for-string so the
/// value round-trips through /api/settings/relay unchanged.</summary>
public static class RelayModes
{
    public const string Automatic = "automatic";
    public const string ForceRelay = "forceRelay";
    public const string DisableFallback = "disableFallback";

    public static bool IsValid(string? value) => value is Automatic or ForceRelay or DisableFallback;
}
```

- [ ] **Step 4: Rewrite `RelayPreferenceStore`**

Replace the entire contents of `src/SonicRelay.Windows.Core/Configuration/RelayPreferenceStore.cs`:

```csharp
using System.Text.Json;

namespace SonicRelay.Windows.Core.Configuration;

/// <summary>
/// Last-known-good cache of the server-synced <see cref="RelayMode"/> (issue #26 follow-up —
/// this used to be the sole source of truth for a local-only "force relay" boolean; the real
/// source of truth is now the backend's /api/settings/relay, and this store only exists so
/// the app has something sensible to render before the first fetch completes). The WebRTC
/// factory reads <see cref="ForceRelay"/> live via a delegate, unchanged.
/// </summary>
public sealed class RelayPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(UserConfigurationLoader.DefaultDirectory, "preferences.json");

    private readonly string _path;

    public RelayPreferenceStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        RelayMode = Load();
    }

    /// <summary>One of <see cref="RelayModes"/>; never any other value.</summary>
    public string RelayMode { get; private set; }

    /// <summary>Restrict ICE to relay (TURN) candidates; read live by the WebRTC factory.</summary>
    public bool ForceRelay => RelayMode == RelayModes.ForceRelay;

    /// <summary>A user changed the mode from Settings; the caller is expected to have already
    /// confirmed this with the server (PUT /api/settings/relay) before calling this — it does
    /// not itself talk to the network.</summary>
    public Task SetRelayModeAsync(string mode, CancellationToken cancellationToken = default) =>
        PersistAsync(mode, cancellationToken);

    /// <summary>A background/opened-Settings/pre-session fetch confirmed the server's current
    /// value; refresh the local cache to match. Same persistence as <see cref="SetRelayModeAsync"/>
    /// — the separate name only documents intent at call sites.</summary>
    public Task ApplyFetchedRelayModeAsync(string mode, CancellationToken cancellationToken = default) =>
        PersistAsync(mode, cancellationToken);

    private async Task PersistAsync(string mode, CancellationToken cancellationToken)
    {
        if (!RelayModes.IsValid(mode))
        {
            throw new ArgumentException($"Unknown relay mode '{mode}'.", nameof(mode));
        }

        RelayMode = mode;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(
            _path,
            JsonSerializer.Serialize(new PreferencesDocument(mode, null), JsonOptions),
            cancellationToken);
    }

    private string Load()
    {
        try
        {
            if (!File.Exists(_path)) return RelayModes.Automatic;
            var document = JsonSerializer.Deserialize<PreferencesDocument>(File.ReadAllText(_path), JsonOptions);
            if (document?.RelayMode is { } mode && RelayModes.IsValid(mode)) return mode;
            // Migrate the pre-existing boolean-only file shape (issue #26 predecessor).
            if (document?.ForceRelay is true) return RelayModes.ForceRelay;
            return RelayModes.Automatic;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing/corrupt preferences file must never block startup; default to automatic.
            return RelayModes.Automatic;
        }
    }

    private sealed record PreferencesDocument(string? RelayMode, bool? ForceRelay);
}
```

- [ ] **Step 5: Run the `RelayPreferenceStore` tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Core.Tests/SonicRelay.Windows.Core.Tests.csproj --filter FullyQualifiedName~RelayPreferenceStoreTests`
Expected: PASS.

- [ ] **Step 6: Write the failing `RelaySettingsApiClient` tests**

```csharp
using System.Net;
using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class RelaySettingsApiClientTests
{
    [Fact]
    public async Task GetAsync_sends_an_authenticated_GET_and_parses_the_response()
    {
        HttpRequestMessage? sentRequest = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            sentRequest = request;
            return Task.FromResult(FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """{"relayMode":"forceRelay","turnUris":["turn:relay.example.com:3478"],"hasCustomTurnSecret":true}"""));
        });
        var client = new RelaySettingsApiClient(TestClient.Create(handler), new SequenceAccessTokenProvider("token-1"));

        var response = await client.GetAsync();

        Assert.Equal(HttpMethod.Get, sentRequest!.Method);
        Assert.Equal("/api/settings/relay", sentRequest.RequestUri!.AbsolutePath);
        Assert.Equal("token-1", sentRequest.Headers.Authorization?.Parameter);
        Assert.Equal("forceRelay", response.RelayMode);
        Assert.Equal(["turn:relay.example.com:3478"], response.TurnUris);
        Assert.True(response.HasCustomTurnSecret);
    }

    [Fact]
    public async Task UpdateAsync_sends_a_PUT_with_the_request_body_and_parses_the_response()
    {
        HttpRequestMessage? sentRequest = null;
        string? sentBody = null;
        var handler = new FakeHttpMessageHandler(async (request, ct) =>
        {
            sentRequest = request;
            sentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """{"relayMode":"disableFallback","turnUris":[],"hasCustomTurnSecret":false}""");
        });
        var client = new RelaySettingsApiClient(TestClient.Create(handler), new SequenceAccessTokenProvider("token-1"));

        var response = await client.UpdateAsync(new UpdateRelaySettingsRequest("disableFallback", null, null));

        Assert.Equal(HttpMethod.Put, sentRequest!.Method);
        Assert.Contains("\"relayMode\":\"disableFallback\"", sentBody);
        Assert.Equal("disableFallback", response.RelayMode);
    }
}
```

(`FakeHttpMessageHandler`, `TestClient`, and `SequenceAccessTokenProvider` already exist in
this test project — reuse them exactly as `BackendIceServersProviderTests.cs` does; do not
redefine them.)

- [ ] **Step 7: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --filter FullyQualifiedName~RelaySettingsApiClientTests`
Expected: FAIL to build — `RelaySettingsApiClient` doesn't exist yet.

- [ ] **Step 8: Add `RelaySettingsApiClient`**

```csharp
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Settings;

public interface IRelaySettingsApiClient
{
    Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default);
    Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default);
}

public sealed record RelaySettingsResponse(string RelayMode, IReadOnlyList<string> TurnUris, bool HasCustomTurnSecret);

public sealed record UpdateRelaySettingsRequest(string? RelayMode, IReadOnlyList<string>? TurnUris, string? TurnStaticAuthSecret);

public sealed class RelaySettingsApiClient(
    HttpClient httpClient,
    IDeviceAccessTokenProvider accessTokenProvider) : IRelaySettingsApiClient
{
    private readonly ApiHttpClient _api = new(httpClient, accessTokenProvider);

    public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
        _api.SendAsync<RelaySettingsResponse>(HttpMethod.Get, "/api/settings/relay", null, true, cancellationToken, replaySafe: true);

    public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default) =>
        _api.SendAsync<RelaySettingsResponse>(HttpMethod.Put, "/api/settings/relay", request, true, cancellationToken);
}
```

- [ ] **Step 9: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --filter FullyQualifiedName~RelaySettingsApiClientTests`
Expected: PASS.

- [ ] **Step 10: Find and fix every other call site now broken by the `RelayPreferenceStore` API change**

Run: `grep -rn "SetForceRelayAsync" src/ tests/ --include=*.cs`
Expected: the only remaining references, if any, are in `SettingsViewModel.ForceRelay`'s
setter (Task 4 replaces that property) — do not fix it here, Task 4 removes it.

- [ ] **Step 11: Commit**

```bash
git add src/SonicRelay.Windows.ApiClient/Settings src/SonicRelay.Windows.Core/Configuration/RelayModes.cs src/SonicRelay.Windows.Core/Configuration/RelayPreferenceStore.cs tests/SonicRelay.Windows.ApiClient.Tests/RelaySettingsApiClientTests.cs tests/SonicRelay.Windows.Core.Tests/RelayPreferenceStoreTests.cs
git commit -m "Add RelaySettingsApiClient and a 3-way, server-synced RelayMode preference"
```

---

### Task 4: `RelayMode`/coturn Settings UI

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/SettingsViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/Controls/SettingsView.axaml`
- Modify: `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`
- Test: `tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `IRelaySettingsApiClient`/`RelaySettingsResponse`/`UpdateRelaySettingsRequest`
  (Task 3), `RelayPreferenceStore.ApplyFetchedRelayModeAsync` (Task 3),
  `PublisherSnapshot.HasDeviceIdentity` (already exists).
- Produces: `PublisherRuntime.RelaySettingsApi` (`IRelaySettingsApiClient`);
  `SettingsViewModel.RelayModeOptions` (`IReadOnlyList<string>`), `SettingsViewModel.RelayMode`
  (string, replaces the old `ForceRelay` bool property — the `ToggleSwitch` in XAML becomes a
  `ComboBox`), `SettingsViewModel.TurnUriInput` (string), `SettingsViewModel
  .HasDeviceIdentity` (bool), `SettingsViewModel.RelaySettingsError` (string?),
  `SettingsViewModel.RefreshRelaySettingsCommand`/`SaveRelayModeCommand`/`SaveTurnUriCommand`
  (`RelayCommand`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs`:

```csharp
using SonicRelay.Windows.ApiClient.Settings;

namespace SonicRelay.Windows.Desktop.Tests;

public sealed class SettingsViewModelRelaySettingsTests
{
    [Fact]
    public async Task Refresh_applies_the_servers_relay_mode_and_turn_uri()
    {
        var api = new StubRelaySettingsApiClient(
            get: new RelaySettingsResponse("forceRelay", ["turn:mine.example.com:3478"], true));
        var vm = MakeConnectedViewModel(api);

        await vm.RefreshRelaySettingsAsync();

        Assert.Equal("forceRelay", vm.RelayMode);
        Assert.Equal("turn:mine.example.com:3478", vm.TurnUriInput);
        Assert.Null(vm.RelaySettingsError);
    }

    [Fact]
    public async Task Saving_the_relay_mode_writes_through_to_the_server_and_applies_the_response()
    {
        var api = new StubRelaySettingsApiClient(
            update: new RelaySettingsResponse("disableFallback", [], false));
        var vm = MakeConnectedViewModel(api);
        vm.RelayMode = "disableFallback";

        await vm.SaveRelayModeAsync();

        Assert.Equal("disableFallback", api.LastUpdateRequest!.RelayMode);
        Assert.Equal("disableFallback", vm.RelayMode);
    }

    [Fact]
    public async Task Saving_the_turn_uri_sends_it_as_a_single_element_list()
    {
        var api = new StubRelaySettingsApiClient(
            update: new RelaySettingsResponse("automatic", ["turn:new.example.com:3478"], false));
        var vm = MakeConnectedViewModel(api);
        vm.TurnUriInput = "turn:new.example.com:3478";

        await vm.SaveTurnUriAsync();

        Assert.Equal(["turn:new.example.com:3478"], api.LastUpdateRequest!.TurnUris);
    }

    [Fact]
    public void Coturn_field_is_hidden_until_the_device_has_an_identity()
    {
        var vm = MakeConnectedViewModel(new StubRelaySettingsApiClient());

        Assert.False(vm.HasDeviceIdentity);

        vm.UpdateAuthentication(true);

        Assert.True(vm.HasDeviceIdentity);
    }

    private static SettingsViewModel MakeConnectedViewModel(IRelaySettingsApiClient api) =>
        new(
            "https://backend.example.test/",
            new SonicRelay.Windows.Core.Configuration.RelayPreferenceStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-relay-test-{Guid.NewGuid():N}.json")),
            new SonicRelay.Windows.Core.Audio.AudioQualityStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-relay-test-quality-{Guid.NewGuid():N}.json")),
            api,
            _ => Task.FromResult<string?>(null));

    private sealed class StubRelaySettingsApiClient(
        RelaySettingsResponse? get = null, RelaySettingsResponse? update = null) : IRelaySettingsApiClient
    {
        public UpdateRelaySettingsRequest? LastUpdateRequest { get; private set; }

        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(get ?? new RelaySettingsResponse("automatic", [], false));

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdateRequest = request;
            return Task.FromResult(update ?? new RelaySettingsResponse("automatic", [], false));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj --filter FullyQualifiedName~SettingsViewModelRelaySettingsTests`
Expected: FAIL to build.

- [ ] **Step 3: Extend `SettingsViewModel`**

Replace the `ForceRelay` property and add the new state. Remove:

```csharp
public bool ForceRelay
{
    get => forceRelay;
    set
    {
        if (SetProperty(ref forceRelay, value) && relay is not null)
            Persist(relay.SetForceRelayAsync(value));
    }
}
```

and the `private bool forceRelay;` field and its `forceRelay = relay.ForceRelay;` assignment
in the 3-argument constructor. Add instead (fields alongside the existing ones, and a new
constructor overload alongside the 4-argument one from Task 2):

```csharp
private readonly IRelaySettingsApiClient? relaySettingsApi;
private string relayMode = SonicRelay.Windows.Core.Configuration.RelayModes.Automatic;
private string turnUriInput = "";
private string? relaySettingsError;
private bool hasDeviceIdentity;

public SettingsViewModel(
    string backendUrl,
    RelayPreferenceStore relay,
    AudioQualityStore quality,
    IRelaySettingsApiClient relaySettingsApi,
    Func<string, Task<string?>> changeBackendUrl)
    : this(backendUrl, relay, quality, changeBackendUrl)
{
    this.relaySettingsApi = relaySettingsApi ?? throw new ArgumentNullException(nameof(relaySettingsApi));
    relayMode = relay.RelayMode;
    RefreshRelaySettingsCommand = new RelayCommand(RefreshRelaySettingsAsync);
    SaveRelayModeCommand = new RelayCommand(SaveRelayModeAsync);
    SaveTurnUriCommand = new RelayCommand(SaveTurnUriAsync);
}

public IReadOnlyList<string> RelayModeOptions { get; } =
[
    SonicRelay.Windows.Core.Configuration.RelayModes.Automatic,
    SonicRelay.Windows.Core.Configuration.RelayModes.ForceRelay,
    SonicRelay.Windows.Core.Configuration.RelayModes.DisableFallback,
];

public string RelayMode
{
    get => relayMode;
    set => SetProperty(ref relayMode, value);
}

public string TurnUriInput
{
    get => turnUriInput;
    set => SetProperty(ref turnUriInput, value);
}

public string? RelaySettingsError
{
    get => relaySettingsError;
    private set => SetProperty(ref relaySettingsError, value);
}

public bool HasDeviceIdentity
{
    get => hasDeviceIdentity;
    private set => SetProperty(ref hasDeviceIdentity, value);
}

public void UpdateAuthentication(bool value) => HasDeviceIdentity = value;

public RelayCommand RefreshRelaySettingsCommand { get; } = new(() => Task.CompletedTask);
public RelayCommand SaveRelayModeCommand { get; } = new(() => Task.CompletedTask);
public RelayCommand SaveTurnUriCommand { get; } = new(() => Task.CompletedTask);

public async Task RefreshRelaySettingsAsync()
{
    if (relaySettingsApi is null) return;
    try
    {
        ApplyRelaySettings(await relaySettingsApi.GetAsync());
        RelaySettingsError = null;
    }
    catch (SonicRelay.Windows.ApiClient.Errors.ApiClientException exception)
    {
        RelaySettingsError = exception.Message;
    }
}

public async Task SaveRelayModeAsync()
{
    if (relaySettingsApi is null) return;
    try
    {
        ApplyRelaySettings(await relaySettingsApi.UpdateAsync(new UpdateRelaySettingsRequest(relayMode, null, null)));
        RelaySettingsError = null;
    }
    catch (SonicRelay.Windows.ApiClient.Errors.ApiClientException exception)
    {
        RelaySettingsError = exception.Message;
    }
}

public async Task SaveTurnUriAsync()
{
    if (relaySettingsApi is null) return;
    try
    {
        var uris = string.IsNullOrWhiteSpace(turnUriInput) ? Array.Empty<string>() : new[] { turnUriInput };
        ApplyRelaySettings(await relaySettingsApi.UpdateAsync(new UpdateRelaySettingsRequest(null, uris, null)));
        RelaySettingsError = null;
    }
    catch (SonicRelay.Windows.ApiClient.Errors.ApiClientException exception)
    {
        RelaySettingsError = exception.Message;
    }
}

private void ApplyRelaySettings(RelaySettingsResponse response)
{
    RelayMode = response.RelayMode;
    TurnUriInput = response.TurnUris.Count > 0 ? response.TurnUris[0] : "";
    if (relay is not null)
    {
        Persist(relay.ApplyFetchedRelayModeAsync(response.RelayMode));
    }
}
```

Add the needed `using SonicRelay.Windows.ApiClient.Settings;` at the top of the file.

- [ ] **Step 4: Wire it up from `MainWindowViewModel`**

Add a `RelaySettingsApi` property on `PublisherRuntime` (see Step 6 below), then update the
`Attach` call in `MainWindowViewModel.cs`:

```csharp
Settings = next is null
    ? new SettingsViewModel()
    : new SettingsViewModel(next.BackendBaseUrl.ToString(), next.RelayPreference, next.AudioQuality, next.RelaySettingsApi, ChangeBackendUrlAsync);
```

In `Apply`, right after `Shell.Update(state, diagnostics, forceRelay);`, add:

```csharp
Settings.UpdateAuthentication(state?.HasDeviceIdentity ?? false);
```

- [ ] **Step 5: Update the XAML**

In `src/SonicRelay.Windows.Desktop/Controls/SettingsView.axaml`, replace the "Force relay
(TURN only)" `Grid` (the one with the `ToggleSwitch`) with:

```xml
          <StackPanel Spacing="4">
            <TextBlock Text="Relay mode" Foreground="{DynamicResource Sonic.TextPrimaryBrush}" />
            <TextBlock Classes="metric-label" Text="Automatic falls back to relay only if a direct path fails." />
            <ComboBox ItemsSource="{Binding RelayModeOptions}" SelectedItem="{Binding RelayMode}"
                      AutomationProperties.Name="Relay mode" />
            <Button Content="Save relay mode" Command="{Binding SaveRelayModeCommand}" Margin="0,4,0,0" />
          </StackPanel>
          <StackPanel Spacing="4" IsVisible="{Binding HasDeviceIdentity}">
            <TextBlock Text="Coturn URL" Foreground="{DynamicResource Sonic.TextPrimaryBrush}" />
            <TextBlock Classes="metric-label" Text="Override the TURN server this backend hands out to every device." />
            <TextBox Text="{Binding TurnUriInput}" Watermark="turn:your-coturn-server.example.com:3478"
                     AutomationProperties.Name="Coturn URL" />
            <Button Content="Save coturn URL" Command="{Binding SaveTurnUriCommand}" />
          </StackPanel>
          <TextBlock Classes="metric-label" Foreground="{DynamicResource Sonic.DangerBrush}"
                     IsVisible="{Binding RelaySettingsError, Converter={x:Static ObjectConverters.IsNotNull}}"
                     Text="{Binding RelaySettingsError}" TextWrapping="Wrap" />
```

- [ ] **Step 6: Give `PublisherRuntime` a `RelaySettingsApi` and refresh it once at attach time**

In `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`, add a field/property and
construct it in `Create` next to the other API clients:

```csharp
public IRelaySettingsApiClient RelaySettingsApi { get; }
```

In the constructor parameter list, add `IRelaySettingsApiClient relaySettingsApi` and assign
`RelaySettingsApi = relaySettingsApi;`. In `Create`, right after the existing
`iceServersProvider`/`relayPreference` construction:

```csharp
var relaySettingsApi = new RelaySettingsApiClient(http, deviceIdentitySession);
```

and pass `relaySettingsApi` into the `new PublisherRuntime(...)` call (matching argument
order to whatever the constructor ends up needing — keep it adjacent to `relayPreference` in
both the parameter list and the call site for readability). Add
`using SonicRelay.Windows.ApiClient.Settings;` to this file's usings.

- [ ] **Step 7: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj --filter FullyQualifiedName~SettingsViewModelRelaySettingsTests`
Expected: PASS.

- [ ] **Step 8: Build everything and run every Windows test project**

Run: `dotnet build src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj`
Expected: clean build (this also compiles `PublisherRuntime.cs`'s new dependency).

Run: `dotnet test` from the repository root (or each `*.Tests.csproj` individually if the
repo has no top-level solution test aggregation — check for a `.sln` file first)
Expected: PASS across every test project, no regressions.

- [ ] **Step 9: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/ViewModels/SettingsViewModel.cs src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs src/SonicRelay.Windows.Desktop/Controls/SettingsView.axaml src/SonicRelay.Windows.Presentation/PublisherRuntime.cs tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs
git commit -m "Add RelayMode and coturn URL settings UI, synced with the backend"
```

---

### Task 5: Refresh relay settings when a new session starts

**Files:**
- Modify: `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`
- Test: `tests/SonicRelay.Windows.Presentation.Tests/PublisherRuntimeTests.cs` (check whether
  this file already exists; if not, create it — most `PublisherRuntime` behavior today is only
  exercised indirectly through Desktop-level tests, so this may be the first test file
  dedicated to it)

**Interfaces:**
- Consumes: `IRelaySettingsApiClient.GetAsync` (Task 3), `RelayPreferenceStore
  .ApplyFetchedRelayModeAsync` (Task 3).

Scope note: the design doc describes polling every 30s while a session is active. This task
implements the narrower, still-correct slice of that: a refresh exactly when a new session
starts. `DisableFallback` itself never goes stale client-side — `/api/webrtc/ice-servers`
already re-evaluates the backend's current `RelayMode` on every call, per viewer, per
connection (Task 2 of the `dotnet_SonicRelay` plan), so a client never has to poll to find out
TURN was removed. The only thing that can go stale locally is `ForceRelay`'s effect on
`iceTransportPolicy`, which is decided once per `CreateAsync` call
(`SipSorceryPeerConnectionFactory.CreateAsync`) — refreshing right before a session (and thus
its first viewer connection) starts covers the case that actually matters; a continuous
background timer while already streaming is real but lower-value polish, left for a later
increment rather than adding new timer-lifecycle machinery to this already-large change.

- [ ] **Step 1: Write the failing test**

Create `tests/SonicRelay.Windows.Presentation.Tests/PublisherRuntimeTests.cs` (first check it
doesn't already exist under a different name covering `PublisherRuntime` — if one exists,
add this test to it instead):

```csharp
using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PublisherRuntimeRelaySettingsTests
{
    [Fact]
    public async Task Starting_a_session_refreshes_relay_settings_from_the_backend()
    {
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudioCaptureService());
        var calls = 0;
        var stub = new RecordingRelaySettingsApiClient(() => calls++);
        // Swap in a stub after Create — PublisherRuntime has no seam for injecting this at
        // construction today; check whether Task 4 added one (a constructor parameter or
        // internal setter) before resorting to reflection, and prefer that seam if present.
        TestRelaySettingsInjection.Replace(runtime, stub);

        await runtime.Workflow.CreateSessionAsync();

        Assert.Equal(1, calls);
    }
}
```

This test is deliberately written to fail loudly if `PublisherRuntime` has no injection seam
for `IRelaySettingsApiClient` yet — resolve that in Step 3 by adding one (an optional
constructor/factory parameter, mirroring `credentialStoreOverride` on
`PublisherRuntime.Create`), then rewrite this test's setup to use the real seam instead of a
placeholder `TestRelaySettingsInjection` helper (which does not exist — do not attempt to
compile against it; it's a placeholder for "however Step 3 ends up exposing the seam").

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SonicRelay.Windows.Presentation.Tests/SonicRelay.Windows.Presentation.Tests.csproj --filter FullyQualifiedName~PublisherRuntimeRelaySettingsTests`
Expected: FAIL to build (the placeholder helper doesn't exist) — this is expected and
resolved by Step 3's real implementation before this test is finalized.

- [ ] **Step 3: Add a relay-settings-override seam to `PublisherRuntime.Create` and the refresh call**

In `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`, add an optional parameter to
`Create`:

```csharp
public static PublisherRuntime Create(
    Uri backendBaseUrl,
    IAudioCaptureService audioCapture,
    IDeviceCredentialStore? credentialStoreOverride = null,
    AudioOutputPreferenceStore? audioOutputPreferenceOverride = null,
    IRelaySettingsApiClient? relaySettingsApiOverride = null)
```

and use it where `relaySettingsApi` is built:

```csharp
var relaySettingsApi = relaySettingsApiOverride ?? new RelaySettingsApiClient(http, deviceIdentitySession);
```

Extend `OnWorkflowStateChanged` (which already detects the session-start/end transition via
`hadActiveSession`) to also trigger a refresh on session *start*:

```csharp
private void OnWorkflowStateChanged(PublisherSnapshot state)
{
    var hasSession = state.SessionId is not null;
    if (!hadActiveSession && hasSession)
    {
        _ = RefreshRelaySettingsAsync();
    }
    if (hadActiveSession && !hasSession)
    {
        _ = peers.RemoveAllAsync();
    }
    hadActiveSession = hasSession;
    // ...(the rest of the method — the signature/logging block below — is unchanged)
}

private async Task RefreshRelaySettingsAsync()
{
    try
    {
        var response = await RelaySettingsApi.GetAsync();
        await RelayPreference.ApplyFetchedRelayModeAsync(response.RelayMode);
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        // Best-effort — a stale local RelayMode only affects the client-side ICE transport
        // policy for the session about to start, never security or the TURN entries
        // themselves (those are decided server-side, live, per connection).
        _ = WriteDiagnosticAsync("runtime", "Could not refresh relay settings before session start.",
            new Dictionary<string, string> { ["error"] = exception.Message });
    }
}
```

Update the test from Step 1 to use `relaySettingsApiOverride:` on `PublisherRuntime.Create`
directly instead of the placeholder helper:

```csharp
using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PublisherRuntimeRelaySettingsTests
{
    [Fact]
    public async Task Starting_a_session_refreshes_relay_settings_from_the_backend()
    {
        var calls = 0;
        var stub = new RecordingRelaySettingsApiClient(() => calls++);
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"),
            new FakeAudioCaptureService(),
            relaySettingsApiOverride: stub);

        try { await runtime.Workflow.CreateSessionAsync(); } catch { /* backend unreachable in this test; the call still counts */ }

        Assert.Equal(1, calls);
    }

    private sealed class RecordingRelaySettingsApiClient(Action onGet) : IRelaySettingsApiClient
    {
        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        {
            onGet();
            return Task.FromResult(new RelaySettingsResponse("automatic", [], false));
        }

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
```

(Reuse whatever `FakeAudioCaptureService`/equivalent test double this test project already has
for `IAudioCaptureService` — check `PublisherWorkflowTests.cs`'s fakes first rather than
redefining one; `CreateSessionAsync` will likely fail against the unreachable
`backend.example.test` host, which is fine and expected here — the refresh happens
synchronously-scheduled inside `OnWorkflowStateChanged` before that failure path even matters,
since `CreateSessionAsync`'s own `SetState` call for `SessionId` happens before the signaling
connect that would fail; if in practice the assertion is flaky because `SessionId` never gets
set before the method throws, assert on `calls` with a short retry/`Task.Delay(50)` poll
instead of assuming synchronous ordering.)

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test tests/SonicRelay.Windows.Presentation.Tests/SonicRelay.Windows.Presentation.Tests.csproj --filter FullyQualifiedName~PublisherRuntimeRelaySettingsTests`
Expected: PASS.

- [ ] **Step 5: Run the full Presentation test project**

Run: `dotnet test tests/SonicRelay.Windows.Presentation.Tests/SonicRelay.Windows.Presentation.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/SonicRelay.Windows.Presentation/PublisherRuntime.cs tests/SonicRelay.Windows.Presentation.Tests/PublisherRuntimeTests.cs
git commit -m "Refresh relay settings from the backend when a new session starts"
```

---

## Self-review notes (already applied above)

- Spec coverage: Task 1 covers the sign-out regression fix and the "Settings always
  reachable" requirement together (the design doc treats them as one architectural fix).
  Task 2 covers the editable backend URL. Tasks 3-4 cover `RelayMode`/coturn settings UI and
  server sync. Task 5 covers cross-device propagation for the one case that's client-visible
  (`ForceRelay`'s local ICE policy) — the spec's "every 30s while active" continuous timer is
  explicitly scoped down with a stated reason (see Task 5's note) rather than silently
  dropped.
- No placeholders: every step has literal code; Task 5's Step 1 intentionally shows an
  interim, admittedly-non-compiling test to make the TDD "red" step visible, and Step 3
  immediately replaces it with the real, compiling version — that is a step *sequence*, not a
  placeholder left unresolved.
- Type consistency: `RelaySettingsResponse`/`UpdateRelaySettingsRequest` field names are
  identical between Task 3's client and every later task/test that constructs them;
  `SettingsViewModel`'s constructors chain (`this(...)`) so each added overload's fields stay
  consistent with the ones before it; `PublisherRuntime.RelaySettingsApi`/`RelayPreference`
  property names introduced in Task 4 are the exact names Task 5 extends.
