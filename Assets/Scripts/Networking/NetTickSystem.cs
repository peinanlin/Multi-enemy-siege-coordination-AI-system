using System;
using UnityEngine;
using Mirror;

namespace Networking
{
    /// <summary>
    /// ͳһ Tick ������ServerTick / ClientTick����
    /// - Server: Ȩ��ģ�⣨AI/ս��/λ�ã�
    /// - Client: ����Ԥ�⡢�ع��طš���ֵ��Ⱦ
    /// </summary>
    public class NetTickSystem : MonoBehaviour
    {
        public static NetTickSystem Instance { get; private set; }

        [Header("Tick Settings")]
        [Range(10, 120)]
        public int tickRate = 30;

        [Tooltip("�ͻ�����Ⱦ��ֵ�ӳ٣�tick����һ�� 2~6������Խ��Խ��")]
        [Range(0, 12)]
        public int interpolationDelayTicks = 3;

        public static event Action<int, float> OnServerTick;
        public static event Action<int, float> OnClientTick;

        public static int ServerTick { get; private set; }
        public static int ClientTick { get; private set; }

        public float TickDelta => 1f / Mathf.Max(1, tickRate);

        float _serverAcc;
        float _clientAcc;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            float dt = TickDelta;

            // Server tick: �� NetworkTime.time ��Ϊͳһʱ��
            if (NetworkServer.active)
            {
                int desired = Mathf.FloorToInt((float)NetworkTime.time * tickRate);
                while (ServerTick < desired)
                {
                    ServerTick++;
                    OnServerTick?.Invoke(ServerTick, dt);
                }
            }

            // Client tick: ͬ���� NetworkTime.time
            if (NetworkClient.active)
            {
                int desired = Mathf.FloorToInt((float)NetworkTime.time * tickRate);
                while (ClientTick < desired)
                {
                    ClientTick++;
                    OnClientTick?.Invoke(ClientTick, dt);
                }
            }
        }


        /// <summary>
        /// ����ֵ�ã���Ⱦʱ���� (����tick - delay)
        /// </summary>
        public int GetRenderTick(int latestTick)
        {
            return latestTick - Mathf.Max(0, interpolationDelayTicks);
        }
    }
}
