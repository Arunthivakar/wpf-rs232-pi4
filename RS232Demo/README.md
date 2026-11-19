# RS-232 demo: WPF app to Raspberry Pi 4B (C# only)

```
WPF app (Windows)  <-USB->  USB-RS232 adapter  <-null-modem cable->  USB-RS232 adapter  <-USB->  Raspberry Pi 4B (PiServer)
```

Two projects:

| Folder | Runs on | What it is |
|---|---|---|
| `PiServer/` | Raspberry Pi | Console app. Reads one request line, sends one reply line. |
| `WpfClient/` | Windows PC | WPF app: connect, disconnect, send, send slow (timeout), serial port options, live data, traffic log. |

Both target `net10.0` (current LTS). If you only have .NET 8 installed, change `net10.0` to `net8.0`
(and `net10.0-windows` to `net8.0-windows`) in the two `.csproj` files, and the
`System.IO.Ports` version to `8.*`. .NET 8 went out of support on 10 Nov 2026, so .NET 10 is recommended.

## 1. Pi side

On the PC, publish and copy to the Pi:

```
cd PiServer
dotnet publish -c Release -r linux-arm64 --self-contained -o publish
scp -r publish youruser@raspi.local:~/piserver
```

On the Pi:

```
cd ~/piserver
chmod +x PiServer
./PiServer /dev/ttyUSB0 9600 8 None One
```

Arguments: `port baud dataBits parity stopBits` (all optional, defaults `/dev/ttyUSB0 9600 8 None One`).
These must match the settings you pick in the WPF app.

## 2. Windows side

```
cd WpfClient
dotnet run
```

(or open the folder in Visual Studio / Rider and press F5)

1. Pick the COM port of your USB-RS232 adapter (Device Manager > Ports) and click Connect.
2. Click Ping. You should see `TX #1 PING` and `RX #1 OK PONG` in the log.
3. Temperature, Time, Info and Memory fill the "Latest data" panel.

## Protocol

Plain text, one line per message, ends with `\n`.

| Request | Reply |
|---|---|
| `#1 PING` | `#1 OK PONG` |
| `#2 GET TEMP` | `#2 OK 48.3` |
| `#3 GET TIME` | `#3 OK 2026-11-13 10:30:00 +05:30` |
| `#4 GET INFO` | `#4 OK raspi \| Debian GNU/Linux ...` |
| `#5 GET MEM` | `#5 OK 412 MB used of 3906 MB` |
| `#6 ECHO hello` | `#6 OK hello` |
| `#7 SLOW 5` | `#7 OK SLOW_DONE 5s` (after 5 seconds) |
| anything else | `#n ERR UNKNOWN_COMMAND` |

The `#id` is added by the WPF app. The app only accepts the reply with the same id, so a late reply to an
earlier request is shown as `IGN` in the log instead of being taken as the answer to a newer request.

## Things to try

* Timeout: set Read timeout to 2000 ms and Pi reply delay to 5 s, click Send slow. After 2 s you get a
  TIMEOUT. About 3 s later the late reply arrives and is logged as `IGN`.
* Wrong settings: start `PiServer` with `9600` and connect the app at `115200`. You get timeouts or
  garbled lines, or serial errors in the log.
* Pull out an adapter while connected. The app reports a port error and returns to Disconnected.
* Custom command box: try `ECHO anything`, or an unknown command to see the `ERR` reply.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Timeout on every request | Null-modem (crossover) cable needed; same baud/parity/stop bits on both sides. |
| `Cannot open /dev/ttyUSB0` | `ls /dev/ttyUSB*`; `sudo usermod -aG dialout $USER` then log in again. |
| Pi app fails with ICU error | Already handled by `InvariantGlobalization` in `PiServer.csproj`. |
| No COM port in the list | Install the adapter driver (FTDI/Prolific/CH340), plug in, click Refresh. |
