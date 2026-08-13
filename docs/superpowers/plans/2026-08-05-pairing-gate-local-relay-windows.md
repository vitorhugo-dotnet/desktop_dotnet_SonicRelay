# Shell Gate & Local Relay Settings — Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the technical console from covering the dashboard, gate the shell on device identity, replace the permanently-dead "Not signed in" label, make sign-out stop orphaning pairings, and move relay/coturn settings back to local per-device preferences.

**Architecture:** Five independent slices. Tasks 1-2 are presentation-only. Task 3 adds the shell gate driven by `PublisherSnapshot.HasDeviceIdentity`. Task 4 reworks the destructive sign-out into a confirmed unpair. Task 5 deletes the relay-settings API client and turns the coturn field into a local override applied in `BackendIceServersProvider`.

**Tech Stack:** .NET 10, Avalonia (compiled bindings, `x:DataType`), xUnit, `TestAppBuilder` for headless UI tests.

## Global Constraints

- "Signed in" means the device has a bootstrapped identity — `PublisherSnapshot.HasDeviceIdentity`, which is `IsAuthenticated && DeviceId.HasValue`. Active pairings are deliberately **not** part of the gate.
- Settings must stay reachable in every state, including before bootstrap. A wrong backend URL must always be correctable from inside the app; this is the constraint that made the previous full-shell pairing gate wrong.
- Avalonia compiled bindings: on an element whose `DataContext` is overridden, read root-view-model properties through `{Binding ((vm:MainWindowViewModel)DataContext).X, ElementName=RootWindow}`. An implicit `{Binding X}` there resolves against the overridden type and fails to build with AVLN2000.
- `RelayModes` values are the literals `automatic`, `forceRelay`, `disableFallback` and must stay string-identical across all three repos.
- The relay/coturn preference is per-device and local. Nothing in this repo may call `/api/settings/relay` after Task 5.
- The backend's TURN URL is never written into a UI field. An empty coturn field means "use whatever the backend sends".
- Run all tests with: `dotnet test SonicRelay.Windows.slnx`
- Commit after every task.

---

### Task 1: Stop the technical console from covering the dashboard

`TechnicalConsole` is `DockPanel.Dock="Bottom"` with no height limit. Its
`ItemsControl` grows with the activity log (`PublisherWorkflow` retains 100
entries), so its desired height outgrows the space the `DockPanel` has and it
renders over the cards above. The inner `ScrollViewer` never scrolls because
nothing bounds it.

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/Controls/TechnicalConsole.axaml:8-10`
- Test: `tests/SonicRelay.Windows.Desktop.Tests/ShellRenderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Write the failing test**

Append to `tests/SonicRelay.Windows.Desktop.Tests/ShellRenderTests.cs`, using
the same headless-render setup the other tests in that file already use:

`ShellRenderTests` uses `[AvaloniaFact]` from `Avalonia.Headless.XUnit`, not
plain `[Fact]` — the control has to be constructed on an Avalonia UI thread.

```csharp
[AvaloniaFact]
public void Technical_console_is_height_bounded_so_it_cannot_cover_the_dashboard_cards()
{
    var console = new SonicRelay.Windows.Desktop.Controls.TechnicalConsole
    {
        DataContext = new DashboardShellViewModel()
    };

    var card = console.GetVisualDescendants().OfType<Border>()
        .First(border => border.Classes.Contains("card"));

    Assert.True(double.IsFinite(card.MaxHeight),
        "The console must cap its height or the DockPanel lets it grow over the cards above.");
}
```

The assertion reads a property set in XAML, so no layout pass is needed — the
control only has to be constructed, which `AvaloniaFact` makes safe. Add
`using Avalonia.VisualTree;` for `GetVisualDescendants`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests --filter FullyQualifiedName~Technical_console_is_height_bounded`
Expected: FAIL — `MaxHeight` is `PositiveInfinity` by default.

- [ ] **Step 3: Bound the console**

In `src/SonicRelay.Windows.Desktop/Controls/TechnicalConsole.axaml`, change the
outer card `Border` to cap its height. The inner `ScrollViewer` already has
`VerticalScrollBarVisibility="Auto"` and the code-behind already auto-scrolls
to the newest line, so bounding the card is the whole fix:

```xml
  <Border Classes="card" MaxHeight="260">
```

260 leaves roughly eight log lines visible at the default window height while
keeping the dashboard cards fully on screen; the log scrolls inside that box
rather than growing past it.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests --filter FullyQualifiedName~Technical_console_is_height_bounded`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/Controls/TechnicalConsole.axaml tests/SonicRelay.Windows.Desktop.Tests/ShellRenderTests.cs
git commit -m "Cap the technical console height so it stops covering the cards

Docked to the bottom with no height limit, the console's desired height grew
with the 100-entry activity log until it exceeded what the DockPanel could
give it and rendered over the dashboard cards. Capping it lets the inner
ScrollViewer do the job it was already configured for."
```

---

### Task 2: Replace the dead account label with device identity

`AccountLabel => accountEmail ?? "Not signed in"` is fed by
`snapshot.UserEmail`, which has been permanently `null` since Identity was
removed from the backend. The label can never render anything else.

**Files:**
- Modify: `src/SonicRelay.Windows.Presentation/PublisherSnapshot.cs:9-10`
- Modify: `src/SonicRelay.Windows.Presentation/PublisherWorkflow.cs:149-150,196-197`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/DashboardShellViewModel.cs:91-135`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs:382`
- Test: `tests/SonicRelay.Windows.Desktop.Tests/DashboardShellViewModelTests.cs:19,70-71`

**Interfaces:**
- Consumes: nothing.
- Produces: `DashboardShellViewModel.AccountLabel` (string, device name or
  `"No device identity"`), `AccountInitials` (string, two chars or `"–"`).
  `AccountStatusHeader.axaml` keeps binding both names, so no XAML change is
  needed. `PublisherSnapshot.UserEmail` and `UserDisplayName` no longer exist —
  Task 4 must not reintroduce them.

- [ ] **Step 1: Rewrite the two existing assertions**

In `tests/SonicRelay.Windows.Desktop.Tests/DashboardShellViewModelTests.cs`,
the fixture snapshot at line 19 sets `UserEmail = "vitor.hugo@sonicrelay.app"`
and lines 70-71 assert that email and its `VH` initials. Replace the fixture
line with `DeviceName = "VITOR-DESKTOP",` (delete the `UserEmail` line
entirely) and replace the two assertions with:

```csharp
Assert.Equal("VITOR-DESKTOP", vm.AccountLabel);
Assert.Equal("VI", vm.AccountInitials);
```

Then add:

```csharp
[Fact]
public void Account_label_reports_a_missing_device_identity_rather_than_a_sign_in_state()
{
    var vm = new DashboardShellViewModel();

    vm.Update(new PublisherSnapshot(), diagnostics: null, forceRelay: false);

    Assert.Equal("No device identity", vm.AccountLabel);
    Assert.Equal("–", vm.AccountInitials);
    Assert.DoesNotContain("signed in", vm.AccountLabel, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests --filter FullyQualifiedName~DashboardShellViewModelTests`
Expected: FAIL — compile error on the removed `UserEmail` initialiser, or
`Assert.Equal() Failure: Expected: VITOR-DESKTOP, Actual: Not signed in`.

- [ ] **Step 3: Remove the user fields from the snapshot**

In `src/SonicRelay.Windows.Presentation/PublisherSnapshot.cs`, delete these two
properties:

```csharp
public string? UserDisplayName { get; init; }
public string? UserEmail { get; init; }
```

In `src/SonicRelay.Windows.Presentation/PublisherWorkflow.cs`, delete the four
assignment lines (two in `LogoutAsync`, two in the unauthorized branch of
`ExecuteAsync`):

```csharp
UserDisplayName = null,
UserEmail = null,
```

In `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs:382`,
delete `UserEmail = "publisher@sonicrelay.app",` from `PreviewSnapshot`.
`DeviceName = Environment.MachineName` is already set there.

- [ ] **Step 4: Reproject the account properties onto the device**

In `src/SonicRelay.Windows.Desktop/ViewModels/DashboardShellViewModel.cs`,
delete the `accountEmail` field and its `AccountEmail` property, and change the
`DeviceName` setter to raise the account properties instead:

```csharp
public string? DeviceName
{
    get => deviceName;
    private set
    {
        if (SetProperty(ref deviceName, value))
        {
            RaisePropertyChanged(nameof(AccountLabel));
            RaisePropertyChanged(nameof(AccountInitials));
        }
    }
}

// Identity was removed from the backend, so there is no user to name here: the top bar
// identifies the *device*. The previous label read snapshot.UserEmail, which has been
// permanently null since then and could only ever render "Not signed in".
public string AccountLabel => string.IsNullOrWhiteSpace(deviceName) ? "No device identity" : deviceName;
public string AccountInitials => Initials(deviceName);
```

In `Update`, delete the `AccountEmail = snapshot?.UserEmail;` line. `DeviceName`
is already assigned on the next line.

Replace `Initials` so it reads a machine name rather than an email:

```csharp
private static string Initials(string? deviceName)
{
    if (string.IsNullOrWhiteSpace(deviceName)) return "–";
    var parts = deviceName.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
    var initials = parts.Length >= 2
        ? $"{parts[0][0]}{parts[1][0]}"
        : deviceName[..Math.Min(2, deviceName.Length)];
    return initials.ToUpper(CultureInfo.CurrentCulture);
}
```

`AccountStatusHeader.axaml` binds `AccountLabel`, `AccountInitials` and
`UiStateText` and needs no change — the second line keeps rendering
`UiStateText`, which needs no pairing lookup.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test SonicRelay.Windows.slnx`
Expected: PASS across every project — the snapshot change touches
`SonicRelay.Windows.Presentation.Tests` too, so run the whole solution here.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Identify the device in the top bar instead of a user that cannot exist

AccountLabel read snapshot.UserEmail, which has been permanently null since
Identity was removed from the backend, so the top bar was hardcoded to
'Not signed in' in practice — including after a successful pairing. The
label now names the device, and UserEmail/UserDisplayName are removed from
PublisherSnapshot so nothing can depend on them again."
```

---

### Task 3: Gate the shell on device identity

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/NavigationItem.cs:5-9` (doc comment only)
- Test: `tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelStateTests.cs`

**Interfaces:**
- Consumes: `PublisherSnapshot.HasDeviceIdentity` (unchanged, already
  `IsAuthenticated && DeviceId.HasValue`).
- Produces: `MainWindowViewModel.HasDeviceIdentity` (bool, change-notifying).
  Task 4 reads it after an unpair to confirm the shell re-locks.

- [ ] **Step 1: Write the failing tests**

Append to `tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelStateTests.cs`:

```csharp
[Fact]
public void Without_a_device_identity_only_pairing_and_settings_are_reachable()
{
    var vm = new MainWindowViewModel();

    Assert.False(vm.HasDeviceIdentity);
    Assert.Equal(PageKey.Pairing, vm.CurrentPage);
    Assert.True(vm.Navigation.Single(item => item.Key == PageKey.Pairing).IsEnabled);
    Assert.True(vm.Navigation.Single(item => item.Key == PageKey.Settings).IsEnabled);
    Assert.All(
        vm.Navigation.Where(item => item.Key is not (PageKey.Pairing or PageKey.Settings)),
        item => Assert.False(item.IsEnabled));
}

[Fact]
public void A_bootstrapped_device_identity_unlocks_the_shell_and_opens_the_dashboard()
{
    var vm = MainWindowViewModel.CreatePreview();

    Assert.True(vm.HasDeviceIdentity);
    Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
    Assert.All(vm.Navigation, item => Assert.True(item.IsEnabled));
}

[Fact]
public void Pairing_stays_reachable_after_the_shell_unlocks()
{
    var vm = MainWindowViewModel.CreatePreview();

    vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Pairing);

    Assert.True(vm.IsPairing);
}
```

`CreatePreview` already builds a snapshot with `IsAuthenticated = true` and a
`SessionId`, but it does not set `DeviceId`, so `HasDeviceIdentity` is false
today. Add `DeviceId = Guid.NewGuid(),` to `PreviewSnapshot` in
`MainWindowViewModel.cs` as part of Step 3 — the preview is meant to show the
fully-populated shell.

The first test also inverts two existing assertions in this same file:
`Fresh_view_model_opens_on_the_dashboard` and
`Preview_view_model_opens_on_the_dashboard`. Delete
`Fresh_view_model_opens_on_the_dashboard` (a fresh view model now opens on
Pairing, which the new test covers) and leave the preview one, which still
holds.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests --filter FullyQualifiedName~MainWindowViewModelStateTests`
Expected: FAIL — `MainWindowViewModel` has no `HasDeviceIdentity` member.

- [ ] **Step 3: Implement the gate**

In `MainWindowViewModel`, add the backing field next to the others:

```csharp
private bool hasDeviceIdentity;
```

Add the property and the gate helper:

```csharp
/// <summary>
/// Whether this device has bootstrapped an identity. While false the shell is gated to
/// Pairing plus Settings: Settings must stay reachable so a wrong backend URL is always
/// correctable from inside the app, which is exactly what the old full-shell pairing gate
/// got wrong. Active pairings are deliberately not part of this — a device with an identity
/// but no pairing still gets the full shell.
/// </summary>
public bool HasDeviceIdentity
{
    get => hasDeviceIdentity;
    private set
    {
        if (!SetProperty(ref hasDeviceIdentity, value)) return;
        ApplyShellGate();
    }
}

private void ApplyShellGate()
{
    foreach (var item in Navigation)
    {
        item.IsEnabled = hasDeviceIdentity || item.Key is PageKey.Pairing or PageKey.Settings;
    }

    if (!hasDeviceIdentity && SelectedNavigation.Key is not (PageKey.Pairing or PageKey.Settings))
    {
        SelectedNavigation = Navigation.Single(item => item.Key == PageKey.Pairing);
    }
    else if (hasDeviceIdentity && SelectedNavigation.Key == PageKey.Pairing)
    {
        SelectedNavigation = Navigation.Single(item => item.Key == PageKey.Dashboard);
    }
}
```

In the constructor, after `selectedNavigation = Navigation[0];`, start gated:

```csharp
        selectedNavigation = Navigation.Single(item => item.Key == PageKey.Pairing);
        ApplyShellGate();
```

Note the constructor assigns the backing field directly, so `ApplyShellGate`
must be called explicitly there — the property setter's call only fires on a
change.

In `Apply`, drive it from the snapshot, next to the existing
`Settings.UpdateAuthentication` call:

```csharp
        HasDeviceIdentity = state?.HasDeviceIdentity ?? false;
```

Add `DeviceId = Guid.NewGuid(),` to `PreviewSnapshot` so the preview shell is
unlocked.

Finally, update the `NavigationItem` class comment, which currently claims all
destinations are always enabled:

```csharp
/// <summary>
/// A sidebar navigation entry. Pairing and Settings are always enabled; the remaining
/// destinations are disabled until the device has bootstrapped an identity
/// (<see cref="MainWindowViewModel.HasDeviceIdentity"/>). Settings in particular must stay
/// reachable while gated, so a wrong backend URL is always correctable in-app.
/// </summary>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test SonicRelay.Windows.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Gate the shell on device identity, keeping Settings reachable

The dashboard, audio, session and diagnostics pages were reachable before the
device had any identity. They are now disabled until bootstrap succeeds, with
the selection parked on Pairing. Settings stays enabled throughout, which is
what the previous full-shell pairing gate got wrong: it hid Settings too, so
a bad backend URL left no way back in."
```

---

### Task 4: Unpair instead of silently orphaning pairings

`Sign out` calls `PublisherWorkflow.LogoutAsync`, which calls
`deviceIdentity.ResetAsync` and deletes the stored credential. The automatic
re-bootstrap then registers a **new** device with a new `DeviceId`, so every
`DevicePairing` still points at the old publisher and is dead — which is what
made the phone report "invalid code" for a perfectly good code.

**Files:**
- Modify: `src/SonicRelay.Windows.Presentation/PublisherWorkflow.cs` (`LogoutAsync`)
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs` (`LogoutCommand`, `LogoutAsync`)
- Modify: `src/SonicRelay.Windows.Desktop/Views/MainWindow.axaml:38-40`
- Test: `tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelStateTests.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.HasDeviceIdentity` from Task 3;
  `IPairingApiClient.ListPairingsAsync(Guid, CancellationToken)` and
  `RevokePairingAsync(Guid, CancellationToken)` from
  `src/SonicRelay.Windows.ApiClient/Pairing/PairingApiClient.cs` (both already
  exist and are unchanged).
- Produces: `PublisherWorkflow.UnpairAsync(CancellationToken)` replacing
  `LogoutAsync`; `MainWindowViewModel.UnpairCommand` replacing `LogoutCommand`;
  `MainWindowViewModel.UnpairConfirmationArmed` (bool) for the two-click
  confirmation.

- [ ] **Step 1: Write the failing tests**

Append to `MainWindowViewModelStateTests`:

```csharp
[Fact]
public void Unpair_requires_a_confirmation_before_it_acts()
{
    var vm = MainWindowViewModel.CreatePreview();

    Assert.False(vm.UnpairConfirmationArmed);
    vm.ArmUnpair();
    Assert.True(vm.UnpairConfirmationArmed);
    vm.DisarmUnpair();
    Assert.False(vm.UnpairConfirmationArmed);
}
```

And in `tests/SonicRelay.Windows.Presentation.Tests`, add a test to the file
covering `PublisherWorkflow` (follow the existing fakes in that project for
`IPairingApiClient`, `ISessionApiClient`, `ISignalingClient`,
`IAudioCaptureService`, `IDeviceAccessTokenProvider` and
`IDeviceCredentialStore`):

```csharp
[Fact]
public async Task Unpair_revokes_active_pairings_before_clearing_the_local_identity()
{
    var pairings = new FakePairingApiClient
    {
        Pairings = [new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null)]
    };
    var workflow = CreateWorkflow(pairings);
    await workflow.InitializeDeviceIdentityAsync();

    await workflow.UnpairAsync();

    Assert.Single(pairings.RevokedIds);
    Assert.False(workflow.State.IsAuthenticated);
    Assert.Null(workflow.State.DeviceId);
}

[Fact]
public async Task Unpair_still_clears_the_local_identity_when_revocation_fails()
{
    var pairings = new FakePairingApiClient { ThrowOnList = true };
    var workflow = CreateWorkflow(pairings);
    await workflow.InitializeDeviceIdentityAsync();

    await workflow.UnpairAsync();

    Assert.False(workflow.State.IsAuthenticated);
    Assert.Contains(workflow.State.ActivityLog, line => line.Contains("could not be revoked", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test SonicRelay.Windows.slnx --filter FullyQualifiedName~Unpair`
Expected: FAIL — `UnpairAsync`, `UnpairCommand` and `UnpairConfirmationArmed`
do not exist.

- [ ] **Step 3: Rework the workflow**

`PublisherWorkflow` needs the pairing client, so add it to the constructor
alongside the existing dependencies:

```csharp
    private readonly IPairingApiClient pairings;
```

assigned from a new `IPairingApiClient pairings` parameter with the same
`?? throw new ArgumentNullException(nameof(pairings))` guard the others use.
Update `PublisherRuntime` (and any test factory) to pass the client it already
constructs for `PairingViewModel`.

Then rename `LogoutAsync` to `UnpairAsync` and revoke before resetting:

```csharp
/// <summary>
/// Unpairs this device: tears down any active session, revokes this device's active
/// pairings on the backend, then forgets the local identity so a fresh one — and a fresh
/// pairing challenge — can be bootstrapped without restarting.
///
/// Revocation comes first because clearing the identity re-bootstraps into a *new* DeviceId,
/// which would leave every existing pairing row pointing at a publisher that no longer
/// exists — the viewer would keep reporting "invalid code" for a perfectly good code.
///
/// A failed revocation does not block the reset: the whole point of this action is
/// recovering from a rejected or unreachable credential, so an unreachable backend must not
/// trap the user. The failure is logged instead of swallowed.
/// </summary>
public Task UnpairAsync(CancellationToken cancellationToken = default) =>
    ExecuteAsync(async token =>
    {
        if (State.SessionId is { } sessionId)
        {
            if (audio.State is not AudioCaptureState.Stopped) await audio.StopAsync(token);
            await signaling.CloseAsync(token);
            try { await sessions.EndSessionAsync(sessionId, token); } catch { }
        }

        if (State.DeviceId is { } deviceId)
        {
            try
            {
                var active = await pairings.ListPairingsAsync(deviceId, token);
                foreach (var pairing in active.Where(x => x.Status == "active"))
                {
                    await pairings.RevokePairingAsync(pairing.PairingId, token);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddLog($"Pairings could not be revoked: {exception.Message}");
            }
        }

        await deviceIdentity.ResetAsync(token);

        SetState(state => state with
        {
            IsAuthenticated = false,
            DeviceId = null,
            DeviceName = null,
            SessionId = null,
            SessionCode = null,
            ViewerCount = 0
        }, "Device unpaired.");
    }, cancellationToken);
```

Note the `UserDisplayName`/`UserEmail` assignments are gone — Task 2 removed
those properties.

- [ ] **Step 4: Rework the view model and the button**

In `MainWindowViewModel`, rename the command and add the confirmation, mirroring
the existing `ClearLogsArmed` pattern in the same class:

```csharp
private bool unpairConfirmationArmed;

public bool UnpairConfirmationArmed
{
    get => unpairConfirmationArmed;
    private set => SetProperty(ref unpairConfirmationArmed, value);
}

public void ArmUnpair() => UnpairConfirmationArmed = true;
public void DisarmUnpair() => UnpairConfirmationArmed = false;
```

Replace the `LogoutCommand` field, its constructor line and `LogoutAsync` with:

```csharp
UnpairCommand = new RelayCommand(UnpairAsync, () => ShellCommandAvailability.Unpair(snapshot, Shell.Capabilities, HasWorkflow));
```

```csharp
public RelayCommand UnpairCommand { get; }

/// <summary>
/// Two-click confirmation, matching the Clear-logs affordance in this same view model
/// rather than introducing a modal dialog dependency: the first click arms, the second
/// acts. Unpairing forces every paired phone to pair again, so it must not be a
/// single stray click on the top bar.
/// </summary>
internal async Task UnpairAsync()
{
    if (workflow is null) return;
    if (!UnpairConfirmationArmed)
    {
        ArmUnpair();
        return;
    }

    DisarmUnpair();
    await workflow.UnpairAsync();
    SelectedNavigation = Navigation.Single(item => item.Key == PageKey.Pairing);
    if (runtime is not null)
    {
        try { await runtime.InitializeDeviceIdentityAsync(); }
        catch { }
    }
}
```

Rename `ShellCommandAvailability.Logout` to `Unpair` in
`src/SonicRelay.Windows.Desktop/ViewModels/ShellCommandAvailability.cs` and in
`ShellCommandAvailabilityTests`, keeping its predicate unchanged. Update
`RaiseCommandStates` to call `UnpairCommand.RaiseCanExecuteChanged()`.

In `src/SonicRelay.Windows.Desktop/Views/MainWindow.axaml`, replace the sign-out
button. Bind a plain label property rather than reusing `ClearLogsLabelConverter`,
which is specific to the clear-logs wording:

```xml
          <Button Grid.Column="2" Margin="16,0,0,0" Classes="ghost"
                  Content="{Binding UnpairButtonLabel}" Command="{Binding UnpairCommand}" />
```

with, in `MainWindowViewModel`:

```csharp
public string UnpairButtonLabel => unpairConfirmationArmed
    ? "Confirm unpair — phones must pair again"
    : "Unpair this device";
```

and `RaisePropertyChanged(nameof(UnpairButtonLabel));` added to the
`UnpairConfirmationArmed` setter.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test SonicRelay.Windows.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Turn the destructive sign-out into a confirmed unpair

Sign out cleared the device credential, and the automatic re-bootstrap then
registered a new DeviceId — leaving every DevicePairing row pointing at a
publisher that no longer existed. Paired phones kept reporting 'invalid code'
for perfectly good codes, with nothing in the UI hinting why. Unpair now
revokes the pairings first and requires a confirmation click, and still
clears the local identity if the backend cannot be reached, since recovering
from a rejected credential is the reason this action exists."
```

---

### Task 5: Local relay preferences and a local coturn override

**Files:**
- Delete: `src/SonicRelay.Windows.ApiClient/Settings/RelaySettingsApiClient.cs`
- Modify: `src/SonicRelay.Windows.Core/Configuration/RelayPreferenceStore.cs`
- Modify: `src/SonicRelay.Windows.ApiClient/WebRtc/BackendIceServersProvider.cs`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/SettingsViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs` (`Attach`, `SelectedNavigation`)
- Modify: `src/SonicRelay.Windows.Desktop/Controls/SettingsView.axaml`
- Modify: `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs` (`RelaySettingsApi` member)
- Test: `tests/SonicRelay.Windows.Core.Tests` (store), `tests/SonicRelay.Windows.ApiClient.Tests` (provider), `tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: nothing from Tasks 1-4.
- Produces: `RelayPreferenceStore.CoturnUrlOverride` (`string?`, null when
  unset), `RelayPreferenceStore.SetCoturnUrlOverrideAsync(string?, CancellationToken)`,
  and `BackendIceServersProvider(IWebRtcApiClient, Func<RelayPreferenceSnapshot>, TimeProvider?, bool)`
  where `RelayPreferenceSnapshot` is `record RelayPreferenceSnapshot(string RelayMode, string? CoturnUrlOverride)`.

- [ ] **Step 1: Write the failing tests**

In `tests/SonicRelay.Windows.Core.Tests`, add to the relay preference test file:

```csharp
[Fact]
public async Task Coturn_override_round_trips_and_defaults_to_null()
{
    var path = Path.Combine(Path.GetTempPath(), $"sonicrelay-plan-{Guid.NewGuid()}.json");
    try
    {
        var store = new RelayPreferenceStore(path);
        Assert.Null(store.CoturnUrlOverride);

        await store.SetCoturnUrlOverrideAsync("turn:my-relay.example.com:3478?transport=udp");

        Assert.Equal("turn:my-relay.example.com:3478?transport=udp", new RelayPreferenceStore(path).CoturnUrlOverride);
    }
    finally
    {
        File.Delete(path);
    }
}

[Fact]
public async Task A_blank_coturn_override_is_stored_as_no_override()
{
    var path = Path.Combine(Path.GetTempPath(), $"sonicrelay-plan-{Guid.NewGuid()}.json");
    try
    {
        var store = new RelayPreferenceStore(path);
        await store.SetCoturnUrlOverrideAsync("   ");

        Assert.Null(new RelayPreferenceStore(path).CoturnUrlOverride);
    }
    finally
    {
        File.Delete(path);
    }
}
```

In `tests/SonicRelay.Windows.ApiClient.Tests`, add to the
`BackendIceServersProvider` test file:

```csharp
[Fact]
public async Task A_coturn_override_replaces_the_turn_url_but_keeps_the_server_credentials()
{
    var api = new FakeWebRtcApiClient
    {
        Response = new IceServersResponse(
        [
            new IceServerEntry(["stun:backend.example.com:3478"]),
            new IceServerEntry(["turn:backend.example.com:3478?transport=udp"], "1700000000:device", "signed-credential")
        ], 3600)
    };
    var provider = new BackendIceServersProvider(api,
        () => new RelayPreferenceSnapshot(RelayModes.Automatic, "turn:my-relay.example.com:3478?transport=udp"));

    var servers = await provider.GetIceServersAsync();

    var turn = servers.Single(s => s.Urls[0].StartsWith("turn:", StringComparison.Ordinal));
    Assert.Equal("turn:my-relay.example.com:3478?transport=udp", turn.Urls[0]);
    Assert.Equal("1700000000:device", turn.Username);
    Assert.Equal("signed-credential", turn.Credential);
    Assert.Contains(servers, s => s.Urls[0].StartsWith("stun:", StringComparison.Ordinal));
}

[Fact]
public async Task No_override_passes_the_backend_list_through_untouched()
{
    var api = new FakeWebRtcApiClient
    {
        Response = new IceServersResponse(
            [new IceServerEntry(["turn:backend.example.com:3478?transport=udp"], "u", "c")], 3600)
    };
    var provider = new BackendIceServersProvider(api,
        () => new RelayPreferenceSnapshot(RelayModes.Automatic, null));

    var servers = await provider.GetIceServersAsync();

    Assert.Equal("turn:backend.example.com:3478?transport=udp", servers.Single().Urls[0]);
}

[Fact]
public async Task Disable_fallback_drops_the_turn_entries_client_side()
{
    var api = new FakeWebRtcApiClient
    {
        Response = new IceServersResponse(
        [
            new IceServerEntry(["stun:backend.example.com:3478"]),
            new IceServerEntry(["turn:backend.example.com:3478?transport=udp"], "u", "c")
        ], 3600)
    };
    var provider = new BackendIceServersProvider(api,
        () => new RelayPreferenceSnapshot(RelayModes.DisableFallback, null));

    var servers = await provider.GetIceServersAsync();

    Assert.DoesNotContain(servers, s => s.Urls[0].StartsWith("turn:", StringComparison.Ordinal));
    Assert.Single(servers);
}
```

If `FakeWebRtcApiClient` does not exist in that project, write it as a minimal
`IWebRtcApiClient` returning a settable `Response`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test SonicRelay.Windows.slnx --filter FullyQualifiedName~Coturn|FullyQualifiedName~Disable_fallback`
Expected: FAIL — `CoturnUrlOverride` and `RelayPreferenceSnapshot` do not exist.

- [ ] **Step 3: Extend the preference store**

In `src/SonicRelay.Windows.Core/Configuration/RelayPreferenceStore.cs`, replace
the class doc comment (it currently claims the backend is the source of truth)
and add the override. Change `PreferencesDocument` to carry the new field:

```csharp
private sealed record PreferencesDocument(string? RelayMode, bool? ForceRelay, string? CoturnUrlOverride);
```

Add the property, loaded in the constructor alongside `RelayMode`:

```csharp
/// <summary>
/// A user-supplied TURN URL that replaces the one the backend hands out, or null to use
/// the backend's. Deliberately never pre-filled with the backend's value: the deployment's
/// relay host is not disclosed through this UI, and blank means "use whatever the server
/// sends".
///
/// The TURN credential is signed by the backend as HMAC-SHA1(TURN_STATIC_AUTH_SECRET,
/// "&lt;expiry&gt;:&lt;deviceId&gt;"), and this override reuses it, so it only authenticates
/// against a coturn sharing that same static secret — i.e. another host or port of the same
/// relay deployment, not a third-party TURN server.
/// </summary>
public string? CoturnUrlOverride { get; private set; }

public Task SetCoturnUrlOverrideAsync(string? url, CancellationToken cancellationToken = default)
{
    CoturnUrlOverride = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
    return PersistAsync(RelayMode, cancellationToken);
}
```

Rework `PersistAsync` to write both fields, and `Load` to read
`CoturnUrlOverride` into the property while returning the mode as it does now.
Keep the existing legacy `ForceRelay` migration branch untouched.

Delete `ApplyFetchedRelayModeAsync` — nothing fetches a server value any more.

- [ ] **Step 4: Apply the preferences in the ICE provider**

Delete `src/SonicRelay.Windows.ApiClient/Settings/RelaySettingsApiClient.cs`
and its interface. Add the snapshot record next to `BackendIceServersProvider`:

```csharp
/// <summary>The per-device relay preferences applied to the backend's ICE list.</summary>
public sealed record RelayPreferenceSnapshot(string RelayMode, string? CoturnUrlOverride);
```

Change the provider's primary constructor to take
`Func<RelayPreferenceSnapshot> preferences` as its second parameter, and apply
them where the response is projected:

```csharp
            var response = await apiClient.GetIceServersAsync(cancellationToken).ConfigureAwait(false);
            var preference = preferences();
            cached = response.IceServers
                .Where(server => server.Urls is { Count: > 0 })
                .Where(server => preference.RelayMode != RelayModes.DisableFallback
                    || !server.Urls[0].StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
                .Select(server => new WebRtcIceServer(
                    ApplyOverride(server, preference.CoturnUrlOverride),
                    server.Username,
                    server.Credential))
                .ToArray();
```

with:

```csharp
// Replaces the backend's TURN urls with the user's override, keeping the server-issued
// username/credential — they are HMAC-signed against the deployment's static secret, so the
// override only works for a coturn sharing it. STUN entries are left alone: they carry no
// credential and the override is specifically about the relay.
private static IReadOnlyList<string> ApplyOverride(IceServerEntry server, string? overrideUrl) =>
    overrideUrl is null || !server.Urls[0].StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
        ? server.Urls
        : [overrideUrl];
```

`RelayModes` lives in `SonicRelay.Windows.Core.Configuration`; add the using.

- [ ] **Step 5: Strip the server sync from Settings**

In `SettingsViewModel`, delete the `relaySettingsApi` field, the constructor
overload that takes `IRelaySettingsApiClient`, `RefreshRelaySettingsAsync`,
`SaveRelayModeAsync`'s server call, `SaveTurnUriAsync`'s server call,
`ApplyRelaySettings`, `RelaySettingsLoaded`, `RelaySettingsError` and
`RefreshRelaySettingsCommand`. `RelayMode` and `TurnUriInput` now read and
write the store directly:

```csharp
public RelayCommand SaveRelayModeCommand { get; } = new(() => Task.CompletedTask);

public async Task SaveRelayModeAsync()
{
    if (relay is null) return;
    await relay.SetRelayModeAsync(relayMode);
}

public async Task SaveTurnUriAsync()
{
    if (relay is null) return;
    await relay.SetCoturnUrlOverrideAsync(turnUriInput);
}
```

`turnUriInput` initialises from `relay.CoturnUrlOverride ?? ""` in the connected
constructor — the user's own override, never the backend's value.
`SaveTurnUriCommand`'s canExecute drops the `RelaySettingsLoaded` gate (there is
no longer a server value that a blank field could wipe) and keeps
`HasDeviceIdentity`.

In `MainWindowViewModel`, drop the `next.RelaySettingsApi` argument from the
`SettingsViewModel` construction in `Attach`, and delete the
`_ = Settings.RefreshRelaySettingsAsync();` block from the `SelectedNavigation`
setter. Remove the `RelaySettingsApi` member from `PublisherRuntime` and the
pre-session refresh that calls it.

In `SettingsView.axaml`, change the coturn helper text from the current
"applies to every paired device" wording to:

```xml
<TextBlock Classes="metric-label"
           Text="Applies to this device only. Leave blank to use the relay the server provides." />
```

and delete the refresh button and the relay-settings error `TextBlock` bound to
`RelaySettingsError`.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test SonicRelay.Windows.slnx`
Expected: PASS. `SettingsViewModelTests` will need the tests covering
`RelaySettingsLoaded` gating and server refresh deleted — they assert behaviour
that no longer exists.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Move relay mode and the coturn override back to local preferences

The backend row these synced through was global to the whole deployment, so
one device editing the coturn URL changed the relay for every other device.
Both are per-device preferences again, and the coturn field starts blank and
never shows the backend's own value. disableFallback becomes a client-side
filter, which is what a per-device preference has to be."
```

---

## Verification

```bash
dotnet test SonicRelay.Windows.slnx
grep -rn "RelaySettings\|settings/relay\|UserEmail\|Not signed in" --include=*.cs --include=*.axaml src/ tests/
```

Expected: all tests pass and the grep returns no output.
