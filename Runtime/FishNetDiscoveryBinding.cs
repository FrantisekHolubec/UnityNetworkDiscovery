#if FISHNET
using FishNet;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace UnityNetworkDiscovery.Runtime
{
    public class FishNetDiscoveryBinding : ANetworkDiscoveryBinding
    {
        [SerializeField]
        [Tooltip("Explicit NetworkManager reference. Leave empty to auto-resolve from parent or InstanceFinder.NetworkManager.")]
        private NetworkManager _NetworkManager;

        protected override bool IsServerRunning => _NetworkManager != null && _NetworkManager.ServerManager != null && _NetworkManager.ServerManager.Started;
        protected override bool IsClientRunning => _NetworkManager != null && _NetworkManager.ClientManager != null && _NetworkManager.ClientManager.Started;

        protected override void Awake()
        {
            base.Awake();

            if (_NetworkManager == null)
                _NetworkManager = GetComponentInParent<NetworkManager>(true);

            if (_NetworkManager == null)
                _NetworkManager = InstanceFinder.NetworkManager;

            if (_NetworkManager == null)
            {
                Debug.LogError($"[{nameof(FishNetDiscoveryBinding)}] No NetworkManager found.", this);
                enabled = false;
            }
        }

        protected override void Subscribe()
        {
            if (_NetworkManager == null) return;
            _NetworkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _NetworkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        protected override void Unsubscribe()
        {
            if (_NetworkManager == null) return;
            if (_NetworkManager.ServerManager != null)
                _NetworkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            if (_NetworkManager.ClientManager != null)
                _NetworkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            switch (args.ConnectionState)
            {
                case LocalConnectionState.Started: NotifyServerStarted(); break;
                case LocalConnectionState.Stopped: NotifyServerStopped(); break;
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            switch (args.ConnectionState)
            {
                case LocalConnectionState.Started: NotifyClientStarted(); break;
                case LocalConnectionState.Stopped: NotifyClientStopped(); break;
            }
        }
    }
}
#endif