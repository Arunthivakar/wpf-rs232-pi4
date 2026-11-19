using System.IO.Ports;

namespace WpfClient;

/// <summary>The serial port parameters the user can choose in the UI.</summary>
public sealed record SerialSettings(
    string PortName,
    int BaudRate = 9600,
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One,
    Handshake Handshake = Handshake.None)
{
    // Short form such as "COM4 9600 8N1"
    public override string ToString()
    {
        string stop = StopBits switch
        {
            StopBits.One => "1",
            StopBits.OnePointFive => "1.5",
            StopBits.Two => "2",
            _ => "?"
        };
        return $"{PortName} {BaudRate} {DataBits}{Parity.ToString()[0]}{stop}";
    }
}
