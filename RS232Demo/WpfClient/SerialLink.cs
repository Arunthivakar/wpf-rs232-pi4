using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace WpfClient;

public readonly record struct SerialReply(string Text, TimeSpan Elapsed);

/// <summary>
/// Request/response layer on top of SerialPort.
/// Each request is sent as "#id command" and only the reply carrying the same id is accepted,
/// so a late reply to an earlier (timed-out) request is ignored instead of being mistaken
/// for the answer to the current request.
/// </summary>
public sealed class SerialLink : IDisposable
{
    private SerialPort? _port;
    private readonly SemaphoreSlim _gate = new(1, 1);   // one request at a time
    private int _nextId;

    /// <summary>Raised from background threads. kind: TX, RX, IGN (ignored line), ERR.</summary>
    public event Action<string, string>? Traffic;

    public bool IsOpen => _port?.IsOpen == true;

    public void Open(SerialSettings s)
    {
        Close();

        var port = new SerialPort(s.PortName, s.BaudRate, s.Parity, s.DataBits, s.StopBits)
        {
            Handshake = s.Handshake,
            NewLine = "\n",
            Encoding = Encoding.ASCII,
            ReadTimeout = 2000,
            WriteTimeout = 2000
        };

        // Frame / parity / overrun errors show up here, e.g. when the two sides use different settings
        port.ErrorReceived += (_, e) => Traffic?.Invoke("ERR", $"Serial error: {e.EventType}");

        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        _port = port;
    }

    public void Close()
    {
        var port = _port;
        _port = null;
        if (port is null) return;

        try { if (port.IsOpen) port.Close(); } catch { /* ignore */ }
        port.Dispose();
    }

    /// <summary>Sends a command and waits up to timeoutMs for the matching reply.</summary>
    /// <exception cref="TimeoutException">No matching reply in time.</exception>
    public async Task<SerialReply> RequestAsync(string command, int timeoutMs)
    {
        var port = _port ?? throw new InvalidOperationException("Port is not open.");

        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => Transact(port, command, timeoutMs));
        }
        finally
        {
            _gate.Release();
        }
    }

    private SerialReply Transact(SerialPort port, string command, int timeoutMs)
    {
        string id = "#" + Interlocked.Increment(ref _nextId);
        string request = $"{id} {command}";

        var sw = Stopwatch.StartNew();
        port.WriteLine(request);
        Traffic?.Invoke("TX", request);

        while (true)
        {
            int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0)
                throw new TimeoutException($"No reply to {id} within {timeoutMs} ms");

            port.ReadTimeout = remaining;

            string line;
            try
            {
                line = port.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No reply to {id} within {timeoutMs} ms");
            }

            if (line.StartsWith(id + " ", StringComparison.Ordinal))
            {
                Traffic?.Invoke("RX", line);
                return new SerialReply(line[(id.Length + 1)..], sw.Elapsed);
            }

            // Reply to an older request (e.g. one that already timed out) or noise
            Traffic?.Invoke("IGN", $"Ignored (not for {id}): {line}");
        }
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }
}
