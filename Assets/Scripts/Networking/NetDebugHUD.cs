using UnityEngine;
using Mirror;

namespace Networking
{
    public class NetDebugHUD : MonoBehaviour
    {
        [Range(0.5f, 3f)] public float uiScale = 1.5f;

        float _fps;

        void Update()
        {
            // 平滑 FPS
            float current = 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            _fps = Mathf.Lerp(_fps, current, 0.1f);
        }

        void OnGUI()
        {
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            GUILayout.BeginArea(new Rect(10 / uiScale, 10 / uiScale, 520, 9999));
            GUILayout.Label($"FPS: {_fps:0}");

            if (NetworkClient.active)
            {
                GUILayout.Label($"RTT: {(NetworkTime.rtt * 1000):0} ms"); // Mirror 提供 :contentReference[oaicite:4]{index=4}
            }

            if (NetTickSystem.Instance != null)
            {
                GUILayout.Label($"ClientTick: {NetTickSystem.ClientTick}   ServerTick: {NetTickSystem.ServerTick}");
            }

            // 找本地玩家的 NetPlayer
            NetPlayer np = null;
            if (NetworkClient.localPlayer != null)
                np = NetworkClient.localPlayer.GetComponent<NetPlayer>();

            if (np != null && np.debugNet)
            {
                int recv = np.dbgSnapRecv;
                int miss = np.dbgSnapMiss;
                float lossPct = (recv + miss) > 0 ? (100f * miss / (recv + miss)) : 0f;

                GUILayout.Space(8);
                GUILayout.Label($"Snapshots: recv={recv}  miss~={miss}  loss~={lossPct:0.0}%");
                GUILayout.Label($"Rollbacks: {np.dbgRollbacks}  lastErr={np.dbgLastPosError:0.000}m  lastReplay={np.dbgLastReplayTicks} ticks");
            }

            GUILayout.EndArea();
            GUI.matrix = old;
        }
    }
}