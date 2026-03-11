# WiFi Manager – Blazor Server + NetworkManager DBus

A web application for managing WiFi connections on Linux using
ASP.NET Core Blazor Server (.NET 8) and the NetworkManager DBus API
via the **Tmds.DBus.Protocol** library.

---

## Architecture

```
┌─────────────────────────────────────────┐
│          Blazor Server UI               │
│  Pages/Index.razor                      │
│  Shared/ConnectForm.razor               │
└────────────────┬────────────────────────┘
                 │ calls
┌────────────────▼────────────────────────┐
│       WifiManagerService                │
│  (business logic, caching, auto-refresh)│
└────────────────┬────────────────────────┘
                 │ calls
┌────────────────▼────────────────────────┐
│         WifiDbusService                 │
│  (DBus communication wrapper)           │
└────────────────┬────────────────────────┘
                 │ uses
┌────────────────▼────────────────────────┐
│   DBus/NetworkManagerInterfaces.cs      │
│   Proxy classes per DBus interface:     │
│   • NetworkManagerProxy                 │
│   • DeviceProxy                         │
│   • WirelessDeviceProxy                 │
│   • AccessPointProxy                    │
│   • IP4ConfigProxy                      │
└────────────────┬────────────────────────┘
                 │ DBus system bus
┌────────────────▼────────────────────────┐
│         NetworkManager                  │
│  (Linux system service)                 │
└─────────────────────────────────────────┘
```

---

## Prerequisites

### 1. .NET 8 SDK

```bash
# Debian / Ubuntu
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0
```

### 2. NetworkManager

```bash
sudo apt-get install -y network-manager
sudo systemctl enable --now NetworkManager
```

Verify it is running:
```bash
systemctl status NetworkManager
```

### 3. DBus access

The app needs access to the **system DBus** to talk to NetworkManager.
NetworkManager requires **root** or a **polkit policy** for many operations.

**Option A – Run as root (simplest for dev/testing):**
```bash
sudo dotnet run
```

**Option B – PolicyKit rule (recommended for production):**

Create `/etc/polkit-1/localauthority/50-local.d/wifi-manager.pkla`:
```ini
[WiFi Manager DBus Access]
Identity=unix-user:YOUR_USERNAME
Action=org.freedesktop.NetworkManager.*
ResultAny=yes
ResultInactive=yes
ResultActive=yes
```

Replace `YOUR_USERNAME` with the Linux user running the app.

Then restart polkit:
```bash
sudo systemctl restart polkit
```

---

## Run the Application

```bash
# Clone / extract project then:
cd WifiManager

# Restore NuGet packages
dotnet restore

# Build
dotnet build

# Run (as root for full DBus access)
sudo dotnet run

# Or with specific URL
sudo dotnet run --urls "http://0.0.0.0:5000"
```

Open browser: **http://localhost:5000**

---

## Features

| Feature | Description |
|---------|-------------|
| **Scan WiFi** | Sends `RequestScan` to `org.freedesktop.NetworkManager.Device.Wireless`, then retrieves AP list via `GetAllAccessPoints` |
| **WiFi Table** | Shows SSID, BSSID, RSSI (progress bar), Frequency, Security type, Max Bitrate |
| **Connect** | Opens modal for password entry, calls `AddAndActivateConnection` |
| **Disconnect** | Calls `DeactivateConnection` |
| **Current connection** | Shows SSID, BSSID, IP address, Frequency, RSSI |
| **Auto-refresh RSSI** | Updates signal strength every 3 seconds |
| **Security detection** | Detects Open / WEP / WPA / WPA2 / WPA3 from AP flags |

---

## DBus Interfaces Used

| DBus Interface | Methods / Properties |
|----------------|---------------------|
| `org.freedesktop.NetworkManager` | `GetAllDevices`, `AddAndActivateConnection`, `DeactivateConnection` |
| `org.freedesktop.NetworkManager.Device` | `DeviceType`, `State`, `ActiveConnection`, `Ip4Config` |
| `org.freedesktop.NetworkManager.Device.Wireless` | `RequestScan`, `GetAllAccessPoints`, `ActiveAccessPoint` |
| `org.freedesktop.NetworkManager.AccessPoint` | `Ssid`, `HwAddress`, `Strength`, `Frequency`, `MaxBitrate`, `Flags`, `WpaFlags`, `RsnFlags` |
| `org.freedesktop.NetworkManager.IP4Config` | `AddressData` |

---

## Project Structure

```
WifiManager/
├── Program.cs                          # App entry point, DI setup
├── App.razor                           # Router
├── _Imports.razor                      # Global usings
├── appsettings.json
├── WifiManager.csproj
├── Models/
│   └── WifiAccessPoint.cs             # Data models
├── DBus/
│   └── NetworkManagerInterfaces.cs    # DBus proxy classes
├── Services/
│   ├── WifiDbusService.cs             # DBus communication layer
│   └── WifiManagerService.cs          # Business logic / state
├── Pages/
│   ├── _Host.cshtml                   # Blazor Server host page
│   └── Index.razor                    # Main WiFi page
├── Shared/
│   ├── MainLayout.razor               # App layout
│   └── ConnectForm.razor              # Connect modal component
└── wwwroot/
    └── css/
        └── app.css                    # Dark theme stylesheet
```

---

## Troubleshooting

**"No wireless device found"**
- Check NM is running: `systemctl status NetworkManager`
- Verify WiFi adapter is recognised: `ip link show`

**"DBus initialisation failed: Access denied"**
- Run with `sudo`, or configure polkit as described above

**"RequestScan failed"**
- Some NM versions require root for scanning
- Check journalctl: `journalctl -u NetworkManager -f`

**Connection stays "Activating"**
- Wrong password → NM will fail silently; check `journalctl -u NetworkManager`
- Try `nmcli device wifi` to verify NM state independently

---

## Security Notes

- This app runs with elevated DBus privileges to control networking.
- Do **not** expose port 5000 publicly without authentication.
- Consider binding to `127.0.0.1` only in production environments.
