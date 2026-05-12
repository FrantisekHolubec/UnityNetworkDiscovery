#if PURRNET
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace UnityNetworkDiscovery.Runtime
{
    public class PurrNetDiscoveryBinding : ANetworkDiscoveryBinding
    {
        [SerializeField]
        [Tooltip("Explicit NetworkManager reference. Leave empty to auto-resolve from parent or NetworkManager.main.")]
        private NetworkManager _NetworkManager;

        protected override bool IsServerRunning => _NetworkManager != null && _NetworkManager.isServer;
        protected override bool IsClientRunning => _NetworkManager != null && _NetworkManager.isClient;

        protected override void Awake()
        {
            base.Awake();

            if (_NetworkManager == null)
                _NetworkManager = GetComponentInParent<NetworkManager>(true);

            if (_NetworkManager == null)
                _NetworkManager = NetworkManager.main;

            if (_NetworkManager == null)
            {
                Debug.LogError($"[{nameof(PurrNetDiscoveryBinding)}] No NetworkManager found.", this);
                enabled = false;
            }
        }

        protected override void Subscribe()
        {
            if (_NetworkManager == null) return;
            _NetworkManager.onServerConnectionState += OnServerConnectionState;
            _NetworkManager.onClientConnectionState += OnClientConnectionState;
        }

        protected override void Unsubscribe()
        {
            if (_NetworkManager == null) return;
            _NetworkManager.onServerConnectionState -= OnServerConnectionState;
            _NetworkManager.onClientConnectionState -= OnClientConnectionState;
        }

        private void OnServerConnectionState(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Connected: NotifyServerStarted(); break;
                case ConnectionState.Disconnected: NotifyServerStopped(); break;
            }
        }

        private void OnClientConnectionState(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Connected: NotifyClientStarted(); break;
                case ConnectionState.Disconnected: NotifyClientStopped(); break;
            }
        }
    }
}
#endif
