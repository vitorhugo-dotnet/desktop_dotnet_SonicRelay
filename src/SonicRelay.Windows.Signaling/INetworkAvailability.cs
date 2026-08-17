using System.Net.NetworkInformation;

namespace SonicRelay.Windows.Signaling;

/// <summary>
/// Reports whether the machine currently has a usable network interface.
/// </summary>
/// <remarks>
/// This is transport availability, not backend reachability — a machine behind a captive portal
/// still reads as available. That is the right granularity for the reconnect loop: the signal is
/// only ever used to decide whether an attempt is worth spending budget on, and an attempt that
/// fails anyway simply falls back to the normal backoff.
/// </remarks>
public interface INetworkAvailability
{
    bool IsAvailable { get; }

    /// <summary>Raised when availability flips, carrying the new value.</summary>
    event Action<bool>? AvailabilityChanged;
}

/// <summary>
/// <see cref="INetworkAvailability"/> backed by the OS's own view of the machine's interfaces.
/// </summary>
public sealed class SystemNetworkAvailability : INetworkAvailability, IDisposable
{
    private bool disposed;

    public SystemNetworkAvailability() =>
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

    public bool IsAvailable => NetworkInterface.GetIsNetworkAvailable();

    public event Action<bool>? AvailabilityChanged;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        AvailabilityChanged?.Invoke(e.IsAvailable);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}

/// <summary>
/// A gate that is always open, for hosts with no interface monitoring wired up. Callers keep the
/// plain backoff behavior they had before the gate existed.
/// </summary>
public sealed class AlwaysAvailableNetwork : INetworkAvailability
{
    public static AlwaysAvailableNetwork Instance { get; } = new();

    private AlwaysAvailableNetwork()
    {
    }

    public bool IsAvailable => true;

    public event Action<bool>? AvailabilityChanged
    {
        add { }
        remove { }
    }
}
