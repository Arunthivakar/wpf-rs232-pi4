using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;

// Usage:  PiServer [port] [baud] [dataBits] [parity] [stopBits]
// Example: PiServer /dev/ttyUSB0 9600 8 None One
// These settings must match what you choose in the WPF app.

string portName = args.Length > 0 ? args[0] : "/dev/ttyUSB0";
int baud        = args.Length > 1 ? int.Parse(args[1]) : 9600;
int dataBits    = args.Length > 2 ? int.Parse(args[2]) : 8;
Parity parity   = args.Length > 3 ? Enum.Parse<Parity>(args[3], ignoreCase: true) : Parity.None;
StopBits stop   = args.Length > 4 ? Enum.Parse<StopBits>(args[4], ignoreCase: true) : StopBits.One;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var port = new SerialPort(portName, baud, parity, dataBits, stop)
{
    NewLine = "\n",
    Encoding = Encoding.ASCII,
    Handshake = Handshake.None,
    ReadTimeout = 500,     // short timeout so Ctrl+C is noticed quickly
    WriteTimeout = 2000
};

try
{
    port.Open();
}
catch (Exception ex)
{
    Console.WriteLine($"Cannot open {portName}: {ex.Message}");
    Console.WriteLine("Check: adapter plugged in (ls /dev/ttyUSB*), user in 'dialout' group, no other program using the port.");
    return 1;
}

Console.WriteLine($"Listening on {portName}  {baud} {dataBits}{parity.ToString()[0]}{StopText(stop)}  (Ctrl+C to stop)");

while (!cts.IsCancellationRequested)
{
    string line;
    try
    {
        line = port.ReadLine().Trim();
    }
    catch (TimeoutException)
    {
        continue;                       // nothing received, keep waiting
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException)
    {
        Console.WriteLine($"Port error: {ex.Message}");
        break;
    }

    if (line.Length == 0) continue;
    Console.WriteLine($"RX  {line}");

    // Request format: "#<id> <command>"   Reply format: "#<id> <OK|ERR ...>"
    string id = "";
    string command = line;
    if (line.StartsWith('#'))
    {
        int space = line.IndexOf(' ');
        if (space > 0) { id = line[..space]; command = line[(space + 1)..].Trim(); }
        else           { id = line;          command = ""; }
    }

    string body;
    try
    {
        body = await ExecuteAsync(command, cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        body = $"ERR {ex.Message}";
    }

    string response = id.Length > 0 ? $"{id} {body}" : body;
    port.WriteLine(response);
    Console.WriteLine($"TX  {response}");
}

Console.WriteLine("Stopped.");
return 0;

static async Task<string> ExecuteAsync(string command, CancellationToken ct)
{
    string[] parts = command.Split(' ', 2,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    string verb = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
    string arg  = parts.Length > 1 ? parts[1] : "";

    switch (verb)
    {
        case "PING":
            return "OK PONG";

        case "GET":
            return arg.ToUpperInvariant() switch
            {
                "TEMP" => $"OK {ReadCpuTemp():F1}",
                "TIME" => $"OK {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
                "INFO" => $"OK {Environment.MachineName} | {RuntimeInformation.OSDescription}",
                "MEM"  => $"OK {ReadMemory()}",
                _      => "ERR UNKNOWN_GET"
            };

        case "ECHO":
            return $"OK {arg}";

        case "SLOW":
            // Deliberately slow reply: lets you test the client's read timeout
            if (!int.TryParse(arg, out int seconds) || seconds < 0 || seconds > 60)
                return "ERR USAGE: SLOW <0-60>";
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            return $"OK SLOW_DONE {seconds}s";

        default:
            return "ERR UNKNOWN_COMMAND";
    }
}

static double ReadCpuTemp()
{
    // Raspberry Pi exposes the CPU temperature in millidegrees Celsius
    string raw = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp");
    return double.Parse(raw.Trim()) / 1000.0;
}

static string ReadMemory()
{
    long totalKb = 0, availKb = 0;
    foreach (string l in File.ReadLines("/proc/meminfo"))
    {
        if (l.StartsWith("MemTotal:")) totalKb = ParseKb(l);
        else if (l.StartsWith("MemAvailable:")) availKb = ParseKb(l);
    }
    return $"{(totalKb - availKb) / 1024} MB used of {totalKb / 1024} MB";
}

static long ParseKb(string line) =>
    long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

static string StopText(StopBits s) => s switch
{
    StopBits.One => "1",
    StopBits.OnePointFive => "1.5",
    StopBits.Two => "2",
    _ => "?"
};
