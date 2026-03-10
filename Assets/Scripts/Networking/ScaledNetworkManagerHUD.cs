using UnityEngine;
using Mirror;

namespace Networking
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Network/Scaled Network Manager HUD")]
    [RequireComponent(typeof(NetworkManager))]
    public class ScaledNetworkManagerHUD : MonoBehaviour
    {
        NetworkManager manager;

        public int offsetX;
        public int offsetY;

        [Range(0.5f, 4f)]
        public float uiScale = 2.0f;

        public int width = 300;

        void Awake()
        {
            manager = GetComponent<NetworkManager>();
        }

        void OnGUI()
        {
            // 保存旧 matrix（严谨：不影响其它 OnGUI）
            Matrix4x4 old = GUI.matrix;

            float s = Mathf.Max(0.01f, uiScale);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));

            // 注意：因为 matrix 放大后坐标也会被放大
            // 所以为了让位置仍然是屏幕左上角附近，要除以 s
            float x = (10 + offsetX) / s;
            float y = (40 + offsetY) / s;

            GUILayout.BeginArea(new Rect(x, y, width, 9999));

            if (!NetworkClient.isConnected && !NetworkServer.active)
                StartButtons();
            else
                StatusLabels();

            if (NetworkClient.isConnected && !NetworkClient.ready)
            {
                if (GUILayout.Button("Client Ready"))
                {
                    NetworkClient.Ready();
                    if (NetworkClient.localPlayer == null)
                        NetworkClient.AddPlayer();
                }
            }

            StopButtons();

            GUILayout.EndArea();

            // 恢复
            GUI.matrix = old;
        }

        void StartButtons()
        {
            if (!NetworkClient.active)
            {
#if UNITY_WEBGL
                if (GUILayout.Button("Single Player"))
                {
                    NetworkServer.listen = false;
                    manager.StartHost();
                }
#else
                if (GUILayout.Button("Host (Server + Client)"))
                    manager.StartHost();
#endif

                GUILayout.BeginHorizontal();

                if (GUILayout.Button("Client"))
                    manager.StartClient();

                manager.networkAddress = GUILayout.TextField(manager.networkAddress);

                if (Transport.active is PortTransport portTransport)
                {
                    if (ushort.TryParse(GUILayout.TextField(portTransport.Port.ToString()), out ushort port))
                        portTransport.Port = port;
                }

                GUILayout.EndHorizontal();

#if UNITY_WEBGL
                GUILayout.Box("( WebGL cannot be server )");
#else
                if (GUILayout.Button("Server Only"))
                    manager.StartServer();
#endif
            }
            else
            {
                GUILayout.Label($"Connecting to {manager.networkAddress}..");
                if (GUILayout.Button("Cancel Connection Attempt"))
                    manager.StopClient();
            }
        }

        void StatusLabels()
        {
            if (NetworkServer.active && NetworkClient.active)
            {
                GUILayout.Label($"<b>Host</b>: running via {Transport.active}");
            }
            else if (NetworkServer.active)
            {
                GUILayout.Label($"<b>Server</b>: running via {Transport.active}");
            }
            else if (NetworkClient.isConnected)
            {
                GUILayout.Label($"<b>Client</b>: connected to {manager.networkAddress} via {Transport.active}");
            }
        }

        void StopButtons()
        {
            if (NetworkServer.active && NetworkClient.isConnected)
            {
                GUILayout.BeginHorizontal();
#if UNITY_WEBGL
                if (GUILayout.Button("Stop Single Player"))
                    manager.StopHost();
#else
                if (GUILayout.Button("Stop Host"))
                    manager.StopHost();

                if (GUILayout.Button("Stop Client"))
                    manager.StopClient();
#endif
                GUILayout.EndHorizontal();
            }
            else if (NetworkClient.isConnected)
            {
                if (GUILayout.Button("Stop Client"))
                    manager.StopClient();
            }
            else if (NetworkServer.active)
            {
                if (GUILayout.Button("Stop Server"))
                    manager.StopServer();
            }
        }
    }
}
