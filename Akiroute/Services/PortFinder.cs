using System.Net;
using System.Net.Sockets;

namespace Akiroute.Services;

/// <summary>
/// Detects whether a TCP port is free on the loopback interface and finds the
/// first free port at or above a preferred value. Used at startup so the local
/// xray inbounds can move to an alternate port when 3333 is already occupied.
/// </summary>
public static class PortFinder
{
    /// <summary>
    /// Returns true when no TCP listener is bound to the given port on loopback
    /// (127.0.0.1) or any address. The check binds a temporary listener on
    /// <see cref="IPAddress.Loopback"/> and immediately releases it.
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            // The port is already bound by another process on loopback or on all interfaces.
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    /// <summary>
    /// Returns <paramref name="preferredPort"/> when it is free; otherwise probes
    /// <c>preferredPort + 1 .. preferredPort + maxAttempts - 1</c> and returns the
    /// first free port in that window. Returns 0 when every candidate is occupied
    /// or when the probe window would overflow the 1-65535 port space (the caller
    /// then shows a dialog to the user).
    /// </summary>
    public static int FindFreePort(int preferredPort = 3333, int maxAttempts = 28)
    {
        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }

        if (preferredPort is < 1 or > 65535 || preferredPort + maxAttempts - 1 > 65535)
        {
            return 0;
        }

        if (IsPortAvailable(preferredPort))
        {
            return preferredPort;
        }

        var lastCandidate = preferredPort + maxAttempts - 1;
        for (var port = preferredPort + 1; port <= lastCandidate; port++)
        {
            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        return 0;
    }

    /// <summary>
    /// Finds the first free CONSECUTIVE port pair at or above
    /// <paramref name="preferredPort"/> — the SOCKS inbound takes the first
    /// port and the HTTP inbound the second, so both must be free. Returns the
    /// first port of the pair, or 0 when no free pair exists in the probe
    /// window (the caller then surfaces the failure).
    /// </summary>
    public static int FindFreePortPair(int preferredPort = 3333, int maxAttempts = 28)
    {
        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }

        if (preferredPort is < 1 or > 65535 || preferredPort + maxAttempts - 1 > 65535)
        {
            return 0;
        }

        var lastCandidate = preferredPort + maxAttempts - 1;
        for (var port = preferredPort; port <= lastCandidate; port++)
        {
            if (port + 1 > 65535)
            {
                break;
            }

            if (IsPortAvailable(port) && IsPortAvailable(port + 1))
            {
                return port;
            }
        }

        return 0;
    }
}
