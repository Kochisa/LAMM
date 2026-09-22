using System.Net;
using System.Net.Sockets;

namespace LocalAIModelManager.Core.Processes;

/// <summary>
/// Hands out internal loopback ports for engine instances. Ports are only ever
/// bound on 127.0.0.1 and are never published outside the machine.
/// </summary>
public sealed class PortAllocator
{
    private readonly object _gate = new();
    private readonly HashSet<int> _reserved = new();
    private readonly int _rangeStart;
    private readonly int _rangeEnd;

    public PortAllocator(int rangeStart = 32800, int rangeEnd = 32900)
    {
        if (rangeEnd <= rangeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), "Port range end must be greater than the start.");
        }

        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;
    }

    public int RangeStart => _rangeStart;

    public int RangeEnd => _rangeEnd;

    public int ReservedCount
    {
        get
        {
            lock (_gate)
            {
                return _reserved.Count;
            }
        }
    }

    /// <summary>Reserves the first free port in the configured range, or -1 when exhausted.</summary>
    public int ReserveNext()
    {
        lock (_gate)
        {
            for (var port = _rangeStart; port <= _rangeEnd; port++)
            {
                if (_reserved.Contains(port) || !IsFree(port))
                {
                    continue;
                }

                _reserved.Add(port);
                return port;
            }

            return -1;
        }
    }

    public bool TryReserve(int port)
    {
        lock (_gate)
        {
            if (_reserved.Contains(port) || !IsFree(port))
            {
                return false;
            }

            _reserved.Add(port);
            return true;
        }
    }

    public void Release(int port)
    {
        lock (_gate)
        {
            _reserved.Remove(port);
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            _reserved.Clear();
        }
    }

    public bool IsReserved(int port)
    {
        lock (_gate)
        {
            return _reserved.Contains(port);
        }
    }

    private static bool IsFree(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
