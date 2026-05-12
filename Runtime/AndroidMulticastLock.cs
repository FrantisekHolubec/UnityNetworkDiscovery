using System;
using UnityEngine;

namespace UnityNetworkDiscovery.Runtime
{
    /// <summary>
    /// Holds a Wi-Fi <c>MulticastLock</c> on Android while discovery is running. Without it the OS drops
    /// broadcast/multicast packets to save battery and <see cref="System.Net.Sockets.UdpClient.ReceiveAsync"/>
    /// silently never returns. No-op on other platforms and in the editor.
    /// </summary>
    /// <remarks>
    /// Reference-counted so it's safe to call from both the advertise and search loops concurrently.
    /// Released as soon as the last loop stops, so the lock isn't held while the app is idle.
    /// </remarks>
    internal static class AndroidMulticastLock
    {
        private static readonly object Gate = new();
        private static int _refs;

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject _lock;
        private const string LockTag = "UniLabs.NetworkDiscovery";
#endif

        public static void Acquire()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            lock (Gate)
            {
                if (_refs++ > 0) return;
                try
                {
                    using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    // Use applicationContext — the activity can be destroyed/recreated on rotation
                    // and a lock obtained via the activity would leak.
                    using var appContext = activity.Call<AndroidJavaObject>("getApplicationContext");
                    using var wifi = appContext.Call<AndroidJavaObject>("getSystemService", "wifi");
                    if (wifi == null)
                    {
                        Debug.LogWarning("[NetworkDiscovery] WIFI_SERVICE not available. Discovery may not receive broadcasts.");
                        _refs = 0;
                        return;
                    }
                    _lock = wifi.Call<AndroidJavaObject>("createMulticastLock", LockTag);
                    _lock.Call("setReferenceCounted", false);
                    _lock.Call("acquire");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NetworkDiscovery] Failed to acquire Wi-Fi multicast lock: {e.Message}");
                    _refs = 0;
                    _lock?.Dispose();
                    _lock = null;
                }
            }
#endif
        }

        public static void Release()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            lock (Gate)
            {
                if (_refs == 0) return;
                if (--_refs > 0) return;
                try
                {
                    _lock?.Call("release");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NetworkDiscovery] Failed to release Wi-Fi multicast lock: {e.Message}");
                }
                finally
                {
                    _lock?.Dispose();
                    _lock = null;
                }
            }
#endif
        }
    }
}
