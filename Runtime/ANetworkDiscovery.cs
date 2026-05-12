using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityNetworkDiscovery.Runtime
{
    public abstract class ANetworkDiscovery : MonoBehaviour
    {
        public abstract bool IsAdvertising { get; }
        public abstract bool IsSearching { get; }

        public abstract void AdvertiseServer();
        public abstract void SearchForServers();
        public abstract void StopSearchingOrAdvertising();
    }
    
    public abstract class ANetworkDiscovery<TResponse> : ANetworkDiscovery
    {
        [SerializeField]
        [Tooltip("Secret to use when advertising or searching for servers. Clients with a different secret are ignored")]
        private string _Secret = "NetworkDiscovery";

        [SerializeField]
        [Tooltip("UDP port used for the discovery broadcast itself (NOT the game server port)")]
        private ushort _Port = 47777;

        [SerializeField]
        [Tooltip("How long in seconds to wait for a response when searching for servers")]
        private float _SearchTimeout = 2f;

        [SerializeField]
        [Tooltip("How long in seconds before a server is removed from the list if no response is received")]
        private float _ServerTimeout = 10f;

        [SerializeField]
        [Tooltip("Delay before retrying after a transient socket error (network down, host unreachable, etc)")]
        private float _TransientErrorRetryDelay = 2f;

        [SerializeField]
        [Tooltip("Delay before retrying to bind the listen socket (e.g. when port is briefly in use)")]
        private float _BindRetryDelay = 5f;

        [SerializeField]
        [Tooltip("Log diagnostic info to the Unity console")]
        private bool _EnableLogs;

        private readonly Dictionary<IPEndPoint, (TResponse response, float lastSeen)> _serverList = new();
        public IEnumerable<(IPEndPoint endPoint, TResponse response)> ServerList => _serverList.Select(s => (s.Key, s.Value.response)).ToList();

        public event Action ServerListUpdated;

        public override bool IsAdvertising => _isAdvertising;
        public override bool IsSearching => _isSearching;

        private bool _isAdvertising;
        private bool _isSearching;
        private float SearchTimeout => _SearchTimeout < 1.0f ? 1.0f : _SearchTimeout;

        private SynchronizationContext _mainThreadSynchronizationContext;
        private CancellationTokenSource _cancellationTokenSource;

        private const int LINUX_SO_REUSEPORT = 15;
        private const int MACOS_SO_REUSEPORT = 0x0200;

        protected abstract TResponse CreateResponse(IPEndPoint endpoint);
        protected abstract byte[] SerializeResponse(TResponse response);
        protected abstract bool TryDeserializeResponse(byte[] data, out TResponse response);

        /// <summary>
        /// Returns the broadcast addresses to send discovery requests to. Default is the limited
        /// broadcast (<see cref="IPAddress.Broadcast"/>, 255.255.255.255). Override to send to
        /// subnet-directed broadcasts (e.g. 192.168.1.255) when limited broadcast is dropped, or
        /// when the machine has multiple network interfaces.
        /// </summary>
        protected virtual IEnumerable<IPAddress> GetBroadcastAddresses()
        {
            yield return IPAddress.Broadcast;
        }

        private void Awake()
        {
            _mainThreadSynchronizationContext = SynchronizationContext.Current;
        }

        private void OnDisable() => StopSearchingOrAdvertising();
        private void OnDestroy() => StopSearchingOrAdvertising();
        private void OnApplicationQuit() => StopSearchingOrAdvertising();

        public override void AdvertiseServer()
        {
            if (_isAdvertising)
            {
                LogInformation("Server is already being advertised.");
                return;
            }

            StopSearchingOrAdvertising();
            _isAdvertising = true;
            var cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;
            ObserveAndForget(RunAdvertiseAsync(cts));
        }

        public override void SearchForServers()
        {
            if (_isSearching)
            {
                LogInformation("Already searching for servers.");
                return;
            }

            StopSearchingOrAdvertising();
            _isSearching = true;
            var cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;
            ObserveAndForget(RunSearchAsync(cts));
        }

        public override void StopSearchingOrAdvertising()
        {
            var cts = _cancellationTokenSource;
            _cancellationTokenSource = null;
            cts?.Cancel();
            _isAdvertising = false;
            _isSearching = false;
        }

        private async Task RunAdvertiseAsync(CancellationTokenSource cts)
        {
            try { await AdvertiseServerAsync(cts.Token); }
            finally { cts.Dispose(); }
        }

        private async Task RunSearchAsync(CancellationTokenSource cts)
        {
            try { await SearchForServersAsync(cts.Token); }
            finally { cts.Dispose(); }
        }

        private void HandleReceiveException(Exception exception)
        {
            var inner = exception is AggregateException agg ? agg.Flatten().InnerException : exception;
            if (inner is SocketException socketException && IsTransientSocketError(socketException))
            {
                LogInformation($"Transient socket error on receive ({socketException.SocketErrorCode}). Recycling socket.");
                return;
            }

            Debug.LogException(exception, this);
        }

        // Errors the OS surfaces when there is no usable route, the network is down, the peer is gone,
        // or ICMP rejects a previously sent datagram. None of these mean the discovery loop is broken —
        // the user just isn't on a reachable network. Log quietly and retry rather than spamming the console.
        private static bool IsTransientSocketError(SocketException socketException)
        {
            return socketException.SocketErrorCode is
                SocketError.ConnectionReset       // 10054 - ICMP port-unreachable from a previous send
                or SocketError.HostUnreachable    // 10065
                or SocketError.NetworkUnreachable // 10051
                or SocketError.NetworkDown        // 10050
                or SocketError.NetworkReset       // 10052
                or SocketError.AddressNotAvailable; // 10049 - interface vanished
        }

        private static void ObserveAndForget(Task task)
        {
            if (task.IsCompleted)
            {
                _ = task.Exception;
                return;
            }
            task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static UdpClient CreateSharedListenClient(int port)
        {
            var client = new UdpClient();
            client.ExclusiveAddressUse = false;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            var reusePort = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? LINUX_SO_REUSEPORT
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MACOS_SO_REUSEPORT
                : 0;

            if (reusePort != 0)
            {
                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)reusePort, 1);
                }
                catch (SocketException)
                {
                    Debug.LogWarning("SO_REUSEPORT not supported on this platform. Multiple instances of the server may not work correctly.");
                }
            }

            client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return client;
        }

        private async Task AdvertiseServerAsync(CancellationToken cancellationToken)
        {
            UdpClient listenClient = null;
            UdpClient sendClient = null;
            AndroidMulticastLock.Acquire();

            try
            {
                LogInformation("Started advertising server.");

                // Separate send socket on an ephemeral port so each server on the same PC
                // replies from a unique source endpoint — the client's server list is keyed
                // by IPEndPoint, so a shared source port would collapse them into one entry.
                sendClient = new UdpClient(0);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (listenClient == null)
                    {
                        try
                        {
                            listenClient = CreateSharedListenClient(_Port);
                        }
                        catch (SocketException bindException)
                        {
                            Debug.LogWarning($"[{GetType().Name}] Failed to bind discovery port {_Port}: {bindException.SocketErrorCode}. Retrying in {_BindRetryDelay}s.");
                            await Task.Delay(TimeSpan.FromSeconds(_BindRetryDelay), cancellationToken);
                            continue;
                        }
                    }

                    LogInformation("Waiting for request...");
                    var receiveTask = listenClient.ReceiveAsync();
                    var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
                    var completedTask = await Task.WhenAny(receiveTask, cancellationTask);

                    if (completedTask != receiveTask)
                    {
                        ObserveAndForget(receiveTask);
                        break;
                    }

                    try
                    {
                        if (receiveTask.IsFaulted)
                        {
                            HandleReceiveException(receiveTask.Exception);
                            listenClient.Close();
                            listenClient = null;
                            continue;
                        }

                        var result = receiveTask.Result;
                        var receivedSecret = Encoding.UTF8.GetString(result.Buffer);
                        if (receivedSecret != _Secret)
                        {
                            Debug.LogWarning($"Received invalid request from {result.RemoteEndPoint}.");
                            continue;
                        }

                        LogInformation($"Received request from {result.RemoteEndPoint}.");
                        var response = CreateResponse(result.RemoteEndPoint);
                        var bytes = SerializeResponse(response);
                        await sendClient.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                    }
                    catch (Exception exception)
                    {
                        var inner = exception is AggregateException agg ? agg.Flatten().InnerException : exception;
                        if (inner is SocketException socketException && IsTransientSocketError(socketException))
                        {
                            LogInformation($"Transient socket error while advertising ({socketException.SocketErrorCode}). Will retry.");
                        }
                        else
                        {
                            Debug.LogException(exception, this);
                        }
                        listenClient?.Close();
                        listenClient = null;
                        // Always await before looping so an exception that throws synchronously every
                        // iteration cannot pin the async state machine without yielding its Task.
                        try { await Task.Delay(TimeSpan.FromSeconds(_TransientErrorRetryDelay), cancellationToken); }
                        catch (OperationCanceledException) { break; }
                    }
                }

                LogInformation("Stopped advertising server.");
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                LogInformation("Closing UDP clients...");
                listenClient?.Close();
                sendClient.Close();
                AndroidMulticastLock.Release();
            }
        }

        private async Task SearchForServersAsync(CancellationToken cancellationToken)
        {
            UdpClient udpClient = null;
            AndroidMulticastLock.Acquire();
            // Compute locally so a not-yet-run Awake on the discovery component can't NRE us into
            // a tight synchronous exception loop that prevents the async state machine from yielding.
            var secretBytes = Encoding.UTF8.GetBytes(_Secret);
            try
            {
                LogInformation("Started searching for servers.");

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (udpClient == null)
                    {
                        udpClient = new UdpClient();
                        udpClient.EnableBroadcast = true;
                    }
                    try
                    {
                        // Recompute each iteration so subclasses that watch network interface changes
                        // pick up new addresses without us needing a refresh API.
                        foreach (var address in GetBroadcastAddresses())
                        {
                            var endpoint = new IPEndPoint(address, _Port);
                            LogInformation($"Sending request to {endpoint}...");
                            await udpClient.SendAsync(secretBytes, secretBytes.Length, endpoint);
                        }

                        LogInformation("Waiting for response...");
                        var receiveTask = udpClient.ReceiveAsync();
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(SearchTimeout), cancellationToken);
                        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

                        if (completedTask == receiveTask)
                        {
                            if (receiveTask.IsFaulted)
                            {
                                HandleReceiveException(receiveTask.Exception);
                                udpClient.Close();
                                udpClient = null;
                            }
                            else
                            {
                                var result = receiveTask.Result;

                                LogInformation($"Received response from {result.RemoteEndPoint}.");
                                if (TryDeserializeResponse(result.Buffer, out var response))
                                {
                                    _mainThreadSynchronizationContext?.Post(_ => UpdateServerList(result.RemoteEndPoint, response), null);
                                }
                                else
                                {
                                    Debug.LogWarning($"Invalid response payload from {result.RemoteEndPoint}.");
                                }
                            }
                        }
                        else
                        {
                            LogInformation("Timed out. Retrying...");
                            ObserveAndForget(receiveTask);
                            udpClient.Close();
                            udpClient = null;
                        }
                        _mainThreadSynchronizationContext?.Post(_ => RemoveExpiredServers(), null);
                    }
                    catch (Exception exception)
                    {
                        var inner = exception is AggregateException agg ? agg.Flatten().InnerException : exception;
                        if (inner is SocketException socketException && IsTransientSocketError(socketException))
                        {
                            LogInformation($"Transient socket error while searching ({socketException.SocketErrorCode}). Will retry.");
                            _mainThreadSynchronizationContext?.Post(_ => RemoveExpiredServers(), null);
                        }
                        else
                        {
                            Debug.LogException(exception, this);
                        }
                        udpClient?.Close();
                        udpClient = null;
                        // Always await before looping so an exception that throws synchronously every
                        // iteration cannot pin the async state machine without yielding its Task.
                        try { await Task.Delay(TimeSpan.FromSeconds(_TransientErrorRetryDelay), cancellationToken); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                LogInformation("Stopped searching for servers.");
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                udpClient?.Close();
                AndroidMulticastLock.Release();
            }
        }

        private void UpdateServerList(IPEndPoint endpoint, TResponse data)
        {
            var changed = !_serverList.ContainsKey(endpoint) || !EqualityComparer<TResponse>.Default.Equals(_serverList[endpoint].response, data);
            _serverList[endpoint] = (data, Time.realtimeSinceStartup);
            if (changed)
                ServerListUpdated?.Invoke();
        }

        private void RemoveExpiredServers()
        {
            var changed = false;
            var now = Time.realtimeSinceStartup;

            foreach (var (endpoint, (_, lastSeen)) in _serverList.ToList())
            {
                if ((now - lastSeen) > _ServerTimeout)
                {
                    _serverList.Remove(endpoint);
                    changed = true;
                }
            }

            if (changed)
                ServerListUpdated?.Invoke();
        }

        private void LogInformation(string message)
        {
            if (!_EnableLogs) return;
            Debug.Log($"[{GetType().Name}] {message}", this);
        }
    }
}
