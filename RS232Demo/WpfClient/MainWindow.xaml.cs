using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfClient;

public sealed record LogEntry(string Text, Brush Brush);

public partial class MainWindow : Window
{
    private readonly SerialLink _link = new();
    private readonly ObservableCollection<LogEntry> _log = new();
    private int _sent, _replies, _timeouts;
    private string _connectedText = "";

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = _log;

        // Serial parameter choices
        BaudBox.ItemsSource = new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" };
        BaudBox.SelectedItem = "9600";

        DataBitsBox.ItemsSource = new[] { 5, 6, 7, 8 };
        DataBitsBox.SelectedItem = 8;

        ParityBox.ItemsSource = Enum.GetValues<Parity>();
        ParityBox.SelectedItem = Parity.None;

        StopBitsBox.ItemsSource = new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two };
        StopBitsBox.SelectedItem = StopBits.One;

        FlowBox.ItemsSource = Enum.GetValues<Handshake>();
        FlowBox.SelectedItem = Handshake.None;

        // Traffic events come from background threads
        _link.Traffic += (kind, text) => Dispatcher.InvokeAsync(() => AddLog(kind, text));

        RefreshPorts();
    }

    // ---------- Connect / disconnect ----------

    private void RefreshPorts()
    {
        string? previous = PortBox.SelectedItem as string;
        string[] ports = SerialPort.GetPortNames()
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        PortBox.ItemsSource = ports;
        if (previous is not null && ports.Contains(previous))
            PortBox.SelectedItem = previous;
        else if (ports.Length > 0)
            PortBox.SelectedIndex = 0;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (PortBox.SelectedItem is not string port)
        {
            AddLog("ERR", "Select a COM port first (plug in the adapter and click Refresh).");
            return;
        }

        if (!int.TryParse(BaudBox.Text, out int baud) || baud <= 0)
        {
            AddLog("ERR", "Baud rate must be a positive number.");
            return;
        }

        var settings = new SerialSettings(
            port,
            baud,
            (int)DataBitsBox.SelectedItem,
            (Parity)ParityBox.SelectedItem,
            (StopBits)StopBitsBox.SelectedItem,
            (Handshake)FlowBox.SelectedItem);

        try
        {
            _link.Open(settings);
            SetConnected(true, settings.ToString());
            AddLog("INFO", $"Connected: {settings}");
        }
        catch (Exception ex)
        {
            SetConnected(false);
            AddLog("ERR", $"Connect failed: {ex.Message}");
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _link.Close();
        SetConnected(false);
        AddLog("INFO", "Disconnected.");
    }

    private void SetConnected(bool connected, string? description = null)
    {
        SettingsGrid.IsEnabled = !connected;
        ConnectBtn.IsEnabled = !connected;
        DisconnectBtn.IsEnabled = connected;
        CommandsGroup.IsEnabled = connected;
        StatusDot.Fill = connected ? Brushes.LimeGreen : Brushes.Gray;
        _connectedText = connected ? description ?? "" : "";
        StatusText.Text = connected ? $"Connected  {_connectedText}" : "Disconnected";
    }

    // ---------- Sending ----------

    private async void Quick_Click(object sender, RoutedEventArgs e)
    {
        await SendAsync((string)((Button)sender).Tag);
    }

    private async void Slow_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SlowBox.Text, out int seconds) || seconds < 0 || seconds > 60)
        {
            AddLog("ERR", "Pi reply delay must be a number from 0 to 60 seconds.");
            return;
        }

        AddLog("INFO", $"Asking the Pi to wait {seconds} s before replying (read timeout is {TimeoutBox.Text} ms).");
        await SendAsync($"SLOW {seconds}");
    }

    private async void CustomSend_Click(object sender, RoutedEventArgs e)
    {
        await SendCustomAsync();
    }

    private async void CustomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendCustomAsync();
        }
    }

    private async Task SendCustomAsync()
    {
        string text = CustomBox.Text.Trim();
        if (text.Length == 0)
        {
            AddLog("ERR", "Type a command first, e.g. ECHO hello");
            return;
        }
        await SendAsync(text);
    }

    private async Task SendAsync(string command)
    {
        if (!_link.IsOpen)
        {
            AddLog("ERR", "Not connected.");
            return;
        }

        if (!int.TryParse(TimeoutBox.Text, out int timeoutMs) || timeoutMs < 50)
        {
            AddLog("ERR", "Read timeout must be a number of at least 50 ms.");
            return;
        }

        CommandsGroup.IsEnabled = false;               // one request at a time
        StatusText.Text = $"Waiting for reply to \"{command}\" ...";
        _sent++;

        try
        {
            SerialReply reply = await _link.RequestAsync(command, timeoutMs);
            _replies++;
            RttText.Text = $"{reply.Elapsed.TotalMilliseconds:F0} ms";
            ShowReply(command, reply.Text);
        }
        catch (TimeoutException ex)
        {
            _timeouts++;
            RttText.Text = "timeout";
            AddLog("ERR", $"TIMEOUT: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Adapter unplugged, port closed, etc.
            AddLog("ERR", $"Port error: {ex.Message}");
            _link.Close();
            SetConnected(false);
        }
        finally
        {
            CountText.Text = $"sent {_sent} / replies {_replies} / timeouts {_timeouts}";
            if (_link.IsOpen)
            {
                CommandsGroup.IsEnabled = true;
                StatusText.Text = $"Connected  {_connectedText}";
            }
        }
    }

    // ---------- Showing data ----------

    private void ShowReply(string command, string reply)
    {
        if (!reply.StartsWith("OK", StringComparison.Ordinal))
        {
            AddLog("ERR", $"Pi returned an error: {reply}");
            return;
        }

        string data = reply[2..].Trim();

        switch (command.ToUpperInvariant())
        {
            case "GET TEMP": TempText.Text = $"{data} °C"; break;
            case "GET TIME": TimeText.Text = data; break;
            case "GET INFO": InfoText.Text = data; break;
            case "GET MEM":  MemText.Text = data; break;
        }
    }

    // ---------- Log ----------

    private void AddLog(string kind, string text)
    {
        Brush brush = kind switch
        {
            "TX"  => Brushes.RoyalBlue,
            "RX"  => Brushes.SeaGreen,
            "IGN" => Brushes.DarkOrange,
            "ERR" => Brushes.Firebrick,
            _     => Brushes.DimGray
        };

        var entry = new LogEntry($"{DateTime.Now:HH:mm:ss.fff}  {kind,-4} {text}", brush);
        _log.Add(entry);
        if (_log.Count > 1000) _log.RemoveAt(0);
        LogList.ScrollIntoView(entry);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _link.Dispose();
    }
}
