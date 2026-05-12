using UnityEngine;

namespace UnityNetworkDiscovery.Runtime
{
    public abstract class ANetworkDiscoveryBinding : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The discovery to drive. Auto-resolved from the same GameObject if left empty.")]
        private ANetworkDiscovery _Discovery;

        [SerializeField]
        [Tooltip("Automatically drive the discovery based on connection state. Disable to call AdvertiseServer/SearchForServers manually.")]
        private bool _Automatic = true;

        protected abstract bool IsServerRunning { get; }
        protected abstract bool IsClientRunning { get; }

        protected ANetworkDiscovery Discovery => _Discovery;
        protected bool Automatic => _Automatic;

        protected abstract void Subscribe();
        protected abstract void Unsubscribe();

        protected virtual void Awake()
        {
            if (_Discovery == null)
                _Discovery = GetComponent<ANetworkDiscovery>();

            if (_Discovery == null)
                Debug.LogError($"[{GetType().Name}] No ANetworkDiscovery found on this GameObject and none assigned.", this);
        }

        protected virtual void OnEnable()
        {
            if (_Discovery == null) return;
            Subscribe();
            if (!_Automatic) return;
            
            if (IsServerRunning) NotifyServerStarted();
            else if (!IsClientRunning) NotifyClientStopped();
        }

        protected virtual void OnDisable()
        {
            if (_Discovery == null) return;
            Unsubscribe();
            if (_Automatic)
                _Discovery.StopSearchingOrAdvertising();
        }
        
        protected void NotifyServerStarted()
        {
            if (_Automatic && _Discovery != null) 
                _Discovery.AdvertiseServer();
        }
        
        protected void NotifyServerStopped()
        {
            if (_Automatic && _Discovery != null) 
                _Discovery.StopSearchingOrAdvertising();
        }
        
        protected void NotifyClientStarted()
        {
            if (_Automatic && _Discovery != null && !IsServerRunning) 
                _Discovery.StopSearchingOrAdvertising();
        }
        
        protected void NotifyClientStopped()
        {
            if (_Automatic && _Discovery != null && !IsServerRunning)
                _Discovery.SearchForServers();
        }
    }
}
