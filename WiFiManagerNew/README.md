# 📡 Wi-Fi Manager - Blazor .NET 10

Ứng dụng Blazor Server quản lý Wi-Fi bằng cách giao tiếp trực tiếp với `wpa_supplicant` qua Unix Domain Socket.

## Cấu trúc Project

```
WiFiManager/
├── Program.cs                        # Entry point, đăng ký service
├── WiFiManager.csproj               # Project file .NET 10
├── appsettings.json                 # Cấu hình production
├── appsettings.Development.json     # Cấu hình dev (UseMock: true)
│
├── Models/
│   └── WiFiNetwork.cs              # WiFiNetwork, WiFiStatus models
│
├── Services/
│   ├── WpaSupplicantService.cs     # Socket giao tiếp với wpa_supplicant
│   ├── MockWpaSupplicantService.cs # Mock service cho dev/test
│   └── WiFiStateService.cs        # State management cho Blazor
│
└── Components/
    ├── App.razor                   # HTML shell
    ├── Routes.razor                # Routing
    ├── _Imports.razor             # Global using
    ├── Layout/
    │   └── MainLayout.razor       # Layout navbar
    ├── Pages/
    │   ├── WiFiPage.razor         # Trang chính (/ và /wifi)
    │   └── NotFound.razor         # 404
    └── WiFi/
        ├── NetworkList.razor      # Danh sách mạng + nút scan
        ├── ConnectForm.razor      # Form nhập mật khẩu kết nối
        └── StatusPanel.razor      # Trạng thái kết nối hiện tại
```

## Yêu cầu hệ thống

- .NET 10 SDK
- Ubuntu 24.04
- `wpa_supplicant` đang chạy
- Quyền truy cập socket `/var/run/wpa_supplicant/`

## Cài đặt

```bash
# 1. Cài .NET 10 (Ubuntu 24.04)
sudo apt install dotnet-sdk-10.0

# 2. Đảm bảo wpa_supplicant đang chạy
sudo systemctl status wpa_supplicant

# 3. Cấp quyền truy cập socket cho user
sudo usermod -a -G netdev $USER
# hoặc chạy với sudo
```

## Chạy ứng dụng

```bash
# Chế độ Mock (không cần wpa_supplicant thực)
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Chế độ Production (kết nối wpa_supplicant thực)
ASPNETCORE_ENVIRONMENT=Production dotnet run

# Hoặc cấu hình trong appsettings.json:
# "WpaSupplicant": { "UseMock": false }
```

Truy cập: http://localhost:5000

## Cách hoạt động

### Giao tiếp với wpa_supplicant

```
Blazor UI → WiFiStateService → WpaSupplicantService → Unix Socket → wpa_supplicant
```

1. `WpaSupplicantService` tạo SOCK_DGRAM Unix socket
2. Bind tại `/tmp/wpa_ctrl_XXXX` (địa chỉ gửi response về)
3. Connect tới `/var/run/wpa_supplicant/wlan0`
4. Gửi lệnh ASCII (SCAN, SCAN_RESULTS, ADD_NETWORK...)
5. Nhận và parse response

### Các lệnh wpa_supplicant được dùng

| Lệnh | Mô tả |
|------|-------|
| `SCAN` | Bắt đầu quét Wi-Fi |
| `SCAN_RESULTS` | Lấy kết quả quét |
| `STATUS` | Trạng thái kết nối hiện tại |
| `ADD_NETWORK` | Tạo network entry mới |
| `SET_NETWORK <id> ssid "..."` | Đặt SSID |
| `SET_NETWORK <id> psk "..."` | Đặt mật khẩu (WPA/WPA2) |
| `SET_NETWORK <id> sae_password "..."` | Đặt mật khẩu (WPA3) |
| `ENABLE_NETWORK <id>` | Kích hoạt network |
| `SELECT_NETWORK <id>` | Chọn và kết nối |
| `DISCONNECT` | Ngắt kết nối |

### Parse SCAN_RESULTS

```
bssid / frequency / signal level / flags / ssid
aa:bb:cc:dd:ee:ff   2412    -55     [WPA2-PSK-CCMP][ESS]    HomeWiFi
```

M��i dòng được tách bằng tab → `WiFiNetwork` object với các trường tương ứng.

## Cấu hình

`appsettings.json`:
```json
{
  "WpaSupplicant": {
    "ControlSocket": "/run/wpa_supplicant/wlp0s20f3",
    "UseMock": false
  }
}
```

`appsettings.Development.json` (mặc định dùng Mock):
```json
{
  "WpaSupplicant": {
    "UseMock": true
  }
}
```
