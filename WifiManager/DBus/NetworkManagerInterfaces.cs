using Tmds.DBus.Protocol;
using System.Text;

namespace WifiManager.DBus;

// ─────────────────────────────────────────────────────────────
// Constants
// ─────────────────────────────────────────────────────────────
public static class NMConstants
{
    public const string Service         = "org.freedesktop.NetworkManager";
    public const string RootPath        = "/org/freedesktop/NetworkManager";
    public const string NMIface         = "org.freedesktop.NetworkManager";
    public const string DeviceIface     = "org.freedesktop.NetworkManager.Device";
    public const string WirelessIface   = "org.freedesktop.NetworkManager.Device.Wireless";
    public const string APInterface     = "org.freedesktop.NetworkManager.AccessPoint";
    public const string ActiveConnIface = "org.freedesktop.NetworkManager.Connection.Active";
    public const string IP4ConfigIface  = "org.freedesktop.NetworkManager.IP4Config";
    public const string PropsIface      = "org.freedesktop.DBus.Properties";

    public const uint DeviceTypeWifi       = 2;
    public const uint DeviceStateActivated = 100;
}

// ─────────────────────────────────────────────────────────────
// Utility helpers
// ─────────────────────────────────────────────────────────────
public static class NMHelper
{
    public static string SsidToString(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return "";
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return BitConverter.ToString(bytes); }
    }

    public static string DetectSecurity(uint flags, uint wpaFlags, uint rsnFlags)
    {
        if ((rsnFlags & 0x1000) != 0) return "WPA3";
        if (rsnFlags  != 0)           return "WPA2";
        if (wpaFlags  != 0)           return "WPA";
        if ((flags & 0x1) != 0)       return "WEP";
        return "Open";
    }
}

// ─────────────────────────────────────────────────────────────
// NetworkManagerProxy
// ─────────────────────────────────────────────────────────────
public sealed class NetworkManagerProxy
{
    private readonly Connection _conn;
    public NetworkManagerProxy(Connection conn) => _conn = conn;

    // GetAllDevices() → ao
    public Task<string[]> GetAllDevicesAsync()
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        NMConstants.RootPath,
                @interface:  NMConstants.NMIface,
                member:      "GetAllDevices");
            msg = writer.CreateMessage();
        }

        return _conn.CallMethodAsync(msg,
            (Message reply, object? _) => ReadObjectPathArray(reply),
            null);
    }

    // GetDevices() → ao
    public Task<string[]> GetDevicesAsync()
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        NMConstants.RootPath,
                @interface:  NMConstants.NMIface,
                member:      "GetDevices");
            msg = writer.CreateMessage();
        }

        return _conn.CallMethodAsync(msg,
            (Message reply, object? _) => ReadObjectPathArray(reply),
            null);
    }

    // AddAndActivateConnection(a{sa{sv}} connection, o device, o specificObject) → (o path, o activeConnection)
    public Task<string> AddAndActivateConnectionAsync(
        Dictionary<string, Dictionary<string, Variant>> settings,
        string devicePath,
        string specificObjectPath)
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        NMConstants.RootPath,
                @interface:  NMConstants.NMIface,
                signature:   "a{sa{sv}}oo",
                member:      "AddAndActivateConnection");

            // Write outer dict: a{s a{sv}}
            var outerStart = writer.WriteDictionaryStart();
            foreach (var (section, props) in settings)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(section);

                // Write inner dict: a{sv}
                var innerStart = writer.WriteDictionaryStart();
                foreach (var (key, val) in props)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(key);
                    writer.WriteVariant(val);
                }
                writer.WriteDictionaryEnd(innerStart);
            }
            writer.WriteDictionaryEnd(outerStart);

            writer.WriteObjectPath(devicePath);
            writer.WriteObjectPath(specificObjectPath);

            msg = writer.CreateMessage();
        }

        return _conn.CallMethodAsync(msg,
            (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                reader.ReadObjectPath();        // connection path (discard)
                return reader.ReadObjectPath().ToString(); // active connection path
            },
            null);
    }

    // DeactivateConnection(o activeConnection)
    public Task DeactivateConnectionAsync(string activeConnPath)
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        NMConstants.RootPath,
                @interface:  NMConstants.NMIface,
                signature:   "o",
                member:      "DeactivateConnection");
            writer.WriteObjectPath(activeConnPath);
            msg = writer.CreateMessage();
        }

        return _conn.CallMethodAsync(msg);
    }

    // ── Shared helper ────────────────────────────────────────

    private static string[] ReadObjectPathArray(Message reply)
    {
        var reader = reply.GetBodyReader();
        var paths  = new List<string>();
        ArrayEnd end = reader.ReadArrayStart(DBusType.ObjectPath);
        while (reader.HasNext(end))
            paths.Add(reader.ReadObjectPath());
        return paths.ToArray();
    }
}

// ─────────────────────────────────────────────────────────────
// DeviceProxy   (org.freedesktop.NetworkManager.Device)
// ─────────────────────────────────────────────────────────────
public sealed class DeviceProxy
{
    private readonly Connection _conn;
    public string Path { get; }

    public DeviceProxy(Connection conn, string path)
    { _conn = conn; Path = path; }

    public Task<uint>   GetDeviceTypeAsync()           => GetPropUInt32(NMConstants.DeviceIface, "DeviceType");
    public Task<string> GetInterfaceNameAsync()        => GetPropString(NMConstants.DeviceIface, "Interface");
    public Task<uint>   GetStateAsync()                => GetPropUInt32(NMConstants.DeviceIface, "State");
    public Task<string> GetActiveConnectionPathAsync() => GetPropObjectPath(NMConstants.DeviceIface, "ActiveConnection");
    public Task<string> GetIp4ConfigPathAsync()        => GetPropObjectPath(NMConstants.DeviceIface, "Ip4Config");

    private async Task<uint> GetPropUInt32(string iface, string prop)
    {
        var vv = await GetPropVariant(iface, prop);
        return vv.Type == VariantValueType.UInt32 ? vv.GetUInt32() : 0u;
    }

    private async Task<string> GetPropString(string iface, string prop)
    {
        var vv = await GetPropVariant(iface, prop);
        return vv.Type == VariantValueType.String ? vv.GetString() : "";
    }

    private async Task<string> GetPropObjectPath(string iface, string prop)
    {
        var vv = await GetPropVariant(iface, prop);
        if (vv.Type == VariantValueType.ObjectPath) return vv.GetObjectPath();
        if (vv.Type == VariantValueType.String)     return vv.GetString();
        return "/";
    }

    private async Task<VariantValue> GetPropVariant(string iface, string prop)
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        Path,
                @interface:  NMConstants.PropsIface,
                signature:   "ss",
                member:      "Get");
            writer.WriteString(iface);
            writer.WriteString(prop);
            msg = writer.CreateMessage();
        }

        return await _conn.CallMethodAsync(msg,
            (Message reply, object? _) => reply.GetBodyReader().ReadVariantValue(),
            null);
    }
}

// ─────────────────────────────────────────────────────────────
// WirelessDeviceProxy
// ─────────────────────────────────────────────────────────────
public sealed class WirelessDeviceProxy
{
    private readonly Connection _conn;
    public string Path { get; }

    public WirelessDeviceProxy(Connection conn, string path)
    { _conn = conn; Path = path; }

    // RequestScan(a{sv} options)  — empty options dict
    public async Task RequestScanAsync()
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        Path,
                @interface:  NMConstants.WirelessIface,
                signature:   "a{sv}",
                member:      "RequestScan");
            // Write empty a{sv}
            var start = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(start);
            msg = writer.CreateMessage();
        }
        await _conn.CallMethodAsync(msg);
    }

    // GetAllAccessPoints() → ao
    public Task<string[]> GetAllAccessPointsAsync()
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        Path,
                @interface:  NMConstants.WirelessIface,
                member:      "GetAllAccessPoints");
            msg = writer.CreateMessage();
        }

        return _conn.CallMethodAsync(msg,
            (Message reply, object? _) => ReadObjectPathArray(reply),
            null);
    }

    // GetAccessPoints() → ao  (fallback for older NM)
    public Task<string[]> GetAccessPointsAsync()
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        Path,
                @interface:  NMConstants.WirelessIface,
                member:      "GetAccessPoints");
            msg = writer.CreateMessage();
        }

        return _conn.CallMethodAsync(msg,
            (Message reply, object? _) => ReadObjectPathArray(reply),
            null);
    }

    // ActiveAccessPoint property → o
    public async Task<string> GetActiveAccessPointPathAsync()
    {
        VariantValue vv = await GetPropVariant(NMConstants.WirelessIface, "ActiveAccessPoint");
        if (vv.Type == VariantValueType.ObjectPath) return vv.GetObjectPath();
        if (vv.Type == VariantValueType.String)     return vv.GetString();
        return "/";
    }

    // AccessPoints property → ao
    public async Task<string[]> GetAccessPointsPropertyAsync()
    {
        VariantValue vv = await GetPropVariant(NMConstants.WirelessIface, "AccessPoints");
        if (vv.Type != VariantValueType.Array) return [];

        // For ao, Tmds can return string[] directly.
        try
        {
            var paths = vv.GetArray<string>();
            return paths ?? [];
        }
        catch
        {
            // Fallback: read as VariantValue array.
            var result = new List<string>();
            var arr = vv.GetArray<VariantValue>();
            foreach (var item in arr)
            {
                if (item.Type == VariantValueType.ObjectPath)
                    result.Add(item.GetObjectPath());
                else if (item.Type == VariantValueType.String)
                    result.Add(item.GetString());
            }
            return result.ToArray();
        }
    }

    private async Task<VariantValue> GetPropVariant(string iface, string prop)
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        Path,
                @interface:  NMConstants.PropsIface,
                signature:   "ss",
                member:      "Get");
            writer.WriteString(iface);
            writer.WriteString(prop);
            msg = writer.CreateMessage();
        }

        return await _conn.CallMethodAsync(msg,
            (Message reply, object? _) => reply.GetBodyReader().ReadVariantValue(),
            null);
    }

    private static string[] ReadObjectPathArray(Message reply)
    {
        var reader = reply.GetBodyReader();
        var list   = new List<string>();
        ArrayEnd end = reader.ReadArrayStart(DBusType.ObjectPath);
        while (reader.HasNext(end))
            list.Add(reader.ReadObjectPath());
        return list.ToArray();
    }
}

// ─────────────────────────────────────────────────────────────
// AccessPointProxy
// ─────────────────────────────────────────────────────────────
public sealed class AccessPointProxy
{
    private readonly Connection _conn;
    public string Path { get; }

    public AccessPointProxy(Connection conn, string path)
    { _conn = conn; Path = path; }

    // Ssid → ay
    public async Task<byte[]> GetSsidAsync()
    {
        var vv = await GetPropVariant("Ssid");
        // VariantValue for ay: Type == Array, ItemType == Byte
        if (vv.Type == VariantValueType.Array)
            return vv.GetArray<byte>();
        return [];
    }

    public async Task<string> GetHwAddressAsync() => await GetPropString("HwAddress");
    public async Task<byte>   GetStrengthAsync()   => await GetPropByte("Strength");
    public async Task<uint>   GetFrequencyAsync()  => await GetPropUInt32("Frequency");
    public async Task<uint>   GetMaxBitrateAsync() => await GetPropUInt32("MaxBitrate");
    public async Task<uint>   GetFlagsAsync()      => await GetPropUInt32("Flags");
    public async Task<uint>   GetWpaFlagsAsync()   => await GetPropUInt32("WpaFlags");
    public async Task<uint>   GetRsnFlagsAsync()   => await GetPropUInt32("RsnFlags");

    private async Task<string> GetPropString(string prop)
    {
        var vv = await GetPropVariant(prop);
        return vv.Type == VariantValueType.String ? vv.GetString() : "";
    }

    private async Task<byte> GetPropByte(string prop)
    {
        var vv = await GetPropVariant(prop);
        return vv.Type == VariantValueType.Byte ? vv.GetByte() : (byte)0;
    }

    private async Task<uint> GetPropUInt32(string prop)
    {
        var vv = await GetPropVariant(prop);
        return vv.Type == VariantValueType.UInt32 ? vv.GetUInt32() : 0u;
    }

    private async Task<VariantValue> GetPropVariant(string prop)
    {
        MessageBuffer msg;
        using (var writer = _conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: NMConstants.Service,
                path:        Path,
                @interface:  NMConstants.PropsIface,
                signature:   "ss",
                member:      "Get");
            writer.WriteString(NMConstants.APInterface);
            writer.WriteString(prop);
            msg = writer.CreateMessage();
        }

        return await _conn.CallMethodAsync(msg,
            (Message reply, object? _) => reply.GetBodyReader().ReadVariantValue(),
            null);
    }
}

// ─────────────────────────────────────────────────────────────
// IP4ConfigProxy
// ─────────────────────────────────────────────────────────────
public sealed class IP4ConfigProxy
{
    private readonly Connection _conn;
    public string Path { get; }

    public IP4ConfigProxy(Connection conn, string path)
    { _conn = conn; Path = path; }

    /// <summary>
    /// Reads AddressData (aa{sv}) and returns the first IPv4 address string.
    /// </summary>
    public async Task<string> GetFirstIpAddressAsync()
    {
        try
        {
            MessageBuffer msg;
            using (var writer = _conn.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: NMConstants.Service,
                    path:        Path,
                    @interface:  NMConstants.PropsIface,
                    signature:   "ss",
                    member:      "Get");
                writer.WriteString(NMConstants.IP4ConfigIface);
                writer.WriteString("AddressData");
                msg = writer.CreateMessage();
            }

            VariantValue vv = await _conn.CallMethodAsync(msg,
                (Message reply, object? _) => reply.GetBodyReader().ReadVariantValue(),
                null);

            // vv is variant wrapping aa{sv}
            // Type == Array, ItemType == Dictionary
            if (vv.Type == VariantValueType.Array)
            {
                var outerArr = vv.GetArray<VariantValue>();
                if (outerArr.Length > 0)
                {
                    var firstEntry = outerArr[0];
                    if (firstEntry.Type == VariantValueType.Dictionary)
                    {
                        var dict = firstEntry.GetDictionary<string, VariantValue>();
                        if (dict.TryGetValue("address", out var addrVv))
                            return addrVv.GetString();
                    }
                }
            }
        }
        catch { /* silent */ }

        return "";
    }
}
