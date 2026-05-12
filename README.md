# Unity Network Discovery

UDP broadcast LAN server discovery for Unity with networking-library-agnostic core.
Available Bindings for [PurrNet](https://github.com/PurrNet/PurrNet) and [FishNet](https://github.com/FirstGearGames/FishNet).

## Features

- Pure C# UDP broadcast loop, no external dependencies in the core
- Per-instance serialization — override two abstract methods, use JSON/MessagePack/binary or whatever you want
- Automatic start/stop based on the bound networking library's connection state
- Android Wi-Fi `MulticastLock` acquired automatically while discovery is running
- Multiple servers on the same machine supported (`SO_REUSEPORT` on Linux/macOS)

## Install

Add as a UPM package via Git URL, or copy the folder into your project's `Assets/` or `Packages/`.
Bindings are gated by define symbols `PURRNET` / `FISHNET`. 

## Architecture

```
ANetworkDiscovery                         // non-generic abstract base — what bindings reference
  └── ANetworkDiscovery<TResponse>        // UDP loop + abstract response/serialize methods
        ├── SampleNetworkDiscovery        // ready-made sample using SampleDiscoveryResponse
        └── YourDiscovery                 // your subclass, your response type, your serializer

ANetworkDiscoveryBinding                  // abstract MonoBehaviour — drives the discovery
      ├── PurrNetDiscoveryBinding         // compiles when PURRNET is defined
      └── FishNetDiscoveryBinding         // compiles when FISHNET is defined
```

Three places where you customize:

- **Response type** — subclass `ANetworkDiscovery<YourResponse>`. The only required inheritance.
- **Serialization** — override `SerializeResponse` and `TryDeserializeResponse` in your subclass. Use whatever you want (the sample uses `JsonUtility`).
- **Binding** — composition. Drop a `PurrNetDiscoveryBinding` (or `FishNetDiscoveryBinding`, or your own subclass) on the same GameObject as the discovery.


## Quick start
1. Add your discovery component (e.g. `SampleNetworkDiscovery` or your subclass) to the same GameObject.
2. Add a your `DiscoveryBinding` (e.g. `PurrNetDiscoveryBinding`) to the same GameObject, or write your own subclass of `ANetworkDiscoveryBinding` if you have a different networking library or want custom behavior.
3. If binding is set to `Automatic` (= default), it starts/stops the discovery based on the network manager's connection state. Otherwise, call `AdvertiseServer()` / `SearchForServers()` manually.

## Defining your own response

```csharp
using System;
using System.Net;
using System.Text;
using UnityEngine;
using UnityNetworkDiscovery.Runtime;

[Serializable]
public class MyResponse
{
    public string ServerName;
    public ushort Port;
    public int CurrentPlayers;
    public int MaxPlayers;
}

public class MyDiscovery : ANetworkDiscovery<MyResponse>
{
    [SerializeField] private string _ServerName = "My Server";
    [SerializeField] private ushort _Port = 7777;
    [SerializeField] private int _MaxPlayers = 16;

    protected override MyResponse CreateResponse(IPEndPoint endpoint)
    {
        return new MyResponse
        {
            ServerName = _ServerName,
            Port = _Port,
            MaxPlayers = _MaxPlayers,
        };
    }

    protected override byte[] SerializeResponse(MyResponse response)
        => Encoding.UTF8.GetBytes(JsonUtility.ToJson(response));

    protected override bool TryDeserializeResponse(byte[] data, out MyResponse response)
    {
        try
        {
            response = JsonUtility.FromJson<MyResponse>(Encoding.UTF8.GetString(data));
            return response != null;
        }
        catch { response = null; return false; }
    }
}
```

Swap `JsonUtility` for `Newtonsoft.Json`, MessagePack, or a hand-rolled binary writer — the discovery never sees the wire format.

## Reading the server list

```csharp
discovery.ServerListUpdated += () =>
{
    foreach (var (endpoint, response) in discovery.ServerList)
        Debug.Log($"{response.ServerName} @ {endpoint}");
};
```

## Manual control

If you turn off `Automatic` on the binding (or don't use a binding at all):

```csharp
discovery.AdvertiseServer();          // on the host
discovery.SearchForServers();         // on clients
discovery.StopSearchingOrAdvertising();
```

## Writing a custom binding

Subclass `ANetworkDiscoveryBinding`:

```csharp
public class MyBinding : ANetworkDiscoveryBinding
{
    protected override bool IsServerRunning => /* ... */;
    protected override bool IsClientRunning => /* ... */;

    protected override void Subscribe()
    {
        // Wire up your networking library's events here.
        // Call NotifyServerStarted/NotifyServerStopped/NotifyClientStarted/NotifyClientStopped
        // from your handlers — the base class handles the rest.
    }

    protected override void Unsubscribe() { /* ... */ }
}
```

## Mobile platforms

### Android

The discovery automatically acquires a Wi-Fi `MulticastLock` while either the advertise or search loop is running, and releases it when both stop. Without this, the OS drops broadcast/multicast packets to save battery and discovery silently never receives anything.

`CHANGE_WIFI_MULTICAST_STATE` permission is required and is auto-granted (no manifest changes needed in modern Android). If you have a custom `AndroidManifest.xml`, make sure these are present:

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
<uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE" />
```

### iOS

iOS 14+ requires user permission for any local-network traffic. Without the right Info.plist entries, UDP send/receive to the LAN is silently blocked and the system logs nothing in Unity's console.

Add the following to your Info.plist (e.g. via Unity's iOS player settings, or a `PostProcessBuild` script):

```xml
<key>NSLocalNetworkUsageDescription</key>
<string>Used to find game servers on your local network.</string>
<key>NSBonjourServices</key>
<array>
  <string>_unity-discovery._udp.</string>
</array>
```

The `NSBonjourServices` array doesn't need to match anything real — iOS just needs *some* service type declared to authorize LAN traffic. Use whatever string is descriptive of your app.

The first time the app broadcasts, iOS shows a system permission prompt. If the user denies it, broadcasts continue to fail silently — handle that as best you can in your UX.

### Both

Broadcast discovery only works on Wi-Fi (same subnet). It will not work over cellular or VPN. Sockets are torn down when the app is backgrounded — on resume the loop's transient-error retry path rebinds automatically.

## Notes

- All log output is gated by the `EnableLogs` field on the discovery component — off by default, turn on per-instance for debugging.
- `SO_REUSEPORT` is enabled on Linux/macOS so multiple servers can run on one machine. Not needed on Android/iOS (one server per device).
- The default broadcast address is `255.255.255.255` (limited broadcast). If you run into routers that drop it, or your machine has multiple network interfaces and limited broadcast goes out the wrong one, override `GetBroadcastAddresses()` to return subnet-directed broadcasts instead. Each address you yield is broadcast to once per search iteration. See "Custom broadcast addresses" below.

## Custom broadcast addresses

Override `GetBroadcastAddresses()` to control where search requests are sent. Returning multiple addresses sends one packet per address per iteration — useful for multi-homed machines.

```csharp
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public class MyDiscovery : ANetworkDiscovery<MyResponse>
{
    protected override IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.IPv4Mask == null) continue;

                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    // ... CreateResponse, SerializeResponse, TryDeserializeResponse as before
}
```

The method is called each search iteration so the result can change at runtime (e.g. when the user switches Wi-Fi networks). If you yield zero addresses, no broadcast is sent that iteration.

## License

MIT
