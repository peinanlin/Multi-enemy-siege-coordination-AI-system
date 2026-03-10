using System.Collections.Generic;
using UnityEngine;
using Mirror;

namespace Networking
{
    [RequireComponent(typeof(PlayerMotorNet))]
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetPlayer : NetworkBehaviour
    {
        [Header("Snapshot")]
        [Range(1, 6)]
        public int snapshotEveryTicks = 1;

        [Tooltip("位置误差超过该值才回滚")]
        public float reconcilePosThreshold = 0.25f;

        [Tooltip("客户端保存多少 tick 的输入/状态")]
        public int bufferSizeTicks = 256;

        [Tooltip("最多回放多少 tick（防止延迟过大造成卡顿）")]
        [Range(10, 240)]
        public int maxReplayTicks = 60;

        [Header("Input Lead (减少因延迟造成的持续回滚)")]
        public bool autoLeadFromRtt = true;

        [Tooltip("手动 lead ticks（autoLeadFromRtt=false 时使用）")]
        [Range(0, 12)]
        public int manualLeadTicks = 3;

        [Tooltip("auto 模式下 lead ticks 最小/最大限制")]
        [Range(0, 12)] public int minLeadTicks = 2;
        [Range(0, 20)] public int maxLeadTicks = 10;

        [Header("Debug")]
        public bool debugNet = true;

        public int dbgSnapRecv;
        public int dbgSnapMiss;
        public int dbgLastSnapTick = -1;

        public int dbgRollbacks;
        public float dbgLastPosError;
        public int dbgLastReplayTicks;

        public Vector3 dbgLastAuthPos;
        public Vector3 dbgLastPredPosAtSnap;

        PlayerMotorNet _motor;
        SnapshotInterpolator _interp;

        // ---------- Client buffers ----------
        readonly Dictionary<int, PlayerInputCmd> _inputBuffer = new Dictionary<int, PlayerInputCmd>(512);
        readonly Dictionary<int, MotorState> _stateBuffer = new Dictionary<int, MotorState>(512);

        // ---------- Server input buffer (关键) ----------
        readonly Dictionary<int, PlayerInputCmd> _serverInputBuffer = new Dictionary<int, PlayerInputCmd>(512);
        PlayerInputCmd _serverLastCmd;
        bool _serverHasLastCmd;

        NetTickSystem TickSys => NetTickSystem.Instance;

        void Awake()
        {
            _motor = GetComponent<PlayerMotorNet>();
            _interp = new SnapshotInterpolator(64);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            NetTickSystem.OnClientTick += HandleClientTick;
        }

        public override void OnStopClient()
        {
            NetTickSystem.OnClientTick -= HandleClientTick;
            base.OnStopClient();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            NetTickSystem.OnServerTick += HandleServerTick;
        }

        public override void OnStopServer()
        {
            NetTickSystem.OnServerTick -= HandleServerTick;
            base.OnStopServer();
        }

        void Update()
        {
            // 非 owner：插值渲染（不用 NetworkTransform）
            if (!isOwned && NetworkClient.active)
            {
                int renderTick = TickSys != null
                    ? TickSys.GetRenderTick(_interp.LatestTick)
                    : (_interp.LatestTick - 2);

                if (_interp.TrySample(renderTick, out var s))
                {
                    _motor.SetState(new MotorState
                    {
                        tick = s.tick,
                        position = s.position,
                        rotation = s.rotation,
                        velocity = s.velocity
                    });
                }
            }
        }

        int GetLeadTicks()
        {
            // Host（isServer && isOwned）不需要 lead
            if (isServer) return 0;

            if (!autoLeadFromRtt) return manualLeadTicks;

            // Mirror 的 NetworkTime.rtt 是 round-trip time（秒）
            double rtt = NetworkTime.rtt;
            double oneWay = rtt * 0.5;

            int tickRate = (TickSys != null) ? TickSys.tickRate : 30;
            int lead = Mathf.CeilToInt((float)(oneWay * tickRate)) + 1; // +1 给 transport/sendRate 留余量
            return Mathf.Clamp(lead, minLeadTicks, maxLeadTicks);
        }

        void HandleClientTick(int baseTick, float dt)
        {
            if (!isOwned) return;

            // 让客户端预测 tick 稍微“领先”服务器，减少输入到达过晚导致的持续回滚
            int lead = GetLeadTicks();
            int predTick = baseTick + lead;

            var cmd = BuildInputCmd(predTick);

            // Host：服务器自己直接拿输入跑 ServerTick（不走 Command）
            if (isServer)
            {
                _serverLastCmd = cmd;
                _serverHasLastCmd = true;
                return;
            }

            // 远端 client：发给服务器（带 tick）
            CmdSendInput(cmd);

            // 客户端本地预测
            _motor.Simulate(cmd, dt);
            _inputBuffer[predTick] = cmd;
            _stateBuffer[predTick] = _motor.GetState(predTick);
            TrimClientBuffers(predTick);
        }

        void HandleServerTick(int tick, float dt)
        {
            if (_motor == null) return;

            // 服务器按 tick 取输入（关键）
            PlayerInputCmd cmd;
            if (_serverInputBuffer.TryGetValue(tick, out var buffered))
            {
                cmd = buffered;
                _serverInputBuffer.Remove(tick);
                _serverLastCmd = cmd;
                _serverHasLastCmd = true;
            }
            else
            {
                cmd = _serverHasLastCmd ? _serverLastCmd : default;
                cmd.tick = tick; // 让 motor/state 的 tick 一致
            }

            _motor.Simulate(cmd, dt);

            if (tick % snapshotEveryTicks == 0)
            {
                var state = _motor.GetState(tick);
                var snap = new NetSnapshot
                {
                    tick = tick,
                    position = transform.position,
                    rotation = transform.rotation,
                    velocity = state.velocity
                };
                RpcReceiveSnapshot(snap);
            }

            TrimServerBuffers(tick);
        }

        void TrimClientBuffers(int currentTick)
        {
            int minTick = currentTick - bufferSizeTicks;
            if (minTick <= 0) return;

            if (_inputBuffer.Count > bufferSizeTicks * 2)
            {
                var keys = new List<int>(_inputBuffer.Keys);
                foreach (var k in keys)
                    if (k < minTick) _inputBuffer.Remove(k);
            }

            if (_stateBuffer.Count > bufferSizeTicks * 2)
            {
                var keys = new List<int>(_stateBuffer.Keys);
                foreach (var k in keys)
                    if (k < minTick) _stateBuffer.Remove(k);
            }
        }

        void TrimServerBuffers(int serverTick)
        {
            // 清掉太旧的输入，避免字典长大
            int minTick = serverTick - bufferSizeTicks;
            if (_serverInputBuffer.Count > bufferSizeTicks * 2)
            {
                var keys = new List<int>(_serverInputBuffer.Keys);
                foreach (var k in keys)
                    if (k < minTick) _serverInputBuffer.Remove(k);
            }
        }

        // -------------------------
        // Networking (Mirror)
        // -------------------------

        [Command(channel = Channels.Unreliable)]
        void CmdSendInput(PlayerInputCmd cmd)
        {
            // 服务器缓存：tick -> input
            // （如果同 tick 重复到达，后来的覆盖即可）
            _serverInputBuffer[cmd.tick] = cmd;

            // 兜底：也更新 lastCmd，供缺包时继续跑
            _serverLastCmd = cmd;
            _serverHasLastCmd = true;
        }

        [ClientRpc(channel = Channels.Unreliable)]
        void RpcReceiveSnapshot(NetSnapshot snap)
        {
            if (debugNet)
            {
                dbgLastAuthPos = snap.position;

                if (dbgLastSnapTick >= 0 && snap.tick > dbgLastSnapTick)
                {
                    int step = Mathf.Max(1, snapshotEveryTicks);
                    int delta = snap.tick - dbgLastSnapTick;
                    if (delta > step)
                    {
                        int missed = (delta / step) - 1;
                        if (missed > 0) dbgSnapMiss += missed;
                    }
                }

                if (snap.tick > dbgLastSnapTick)
                    dbgLastSnapTick = snap.tick;

                dbgSnapRecv++;
            }

            if (isOwned)
            {
                if (isServer) return; // Host 不需要 reconcile
                Reconcile(snap);
            }
            else
            {
                _interp.Add(snap);
            }
        }

        void Reconcile(NetSnapshot snap)
        {
            if (!_stateBuffer.TryGetValue(snap.tick, out var predicted))
            {
                _motor.SetState(new MotorState
                {
                    tick = snap.tick,
                    position = snap.position,
                    rotation = snap.rotation,
                    velocity = snap.velocity
                });
                return;
            }

            float posError = Vector3.Distance(predicted.position, snap.position);

            if (debugNet)
            {
                dbgLastPosError = posError;
                dbgLastPredPosAtSnap = predicted.position;

                int lead = GetLeadTicks();
                int currentPredTick = NetTickSystem.ClientTick + lead;
                dbgLastReplayTicks = currentPredTick - snap.tick;
            }

            if (posError < reconcilePosThreshold)
                return;

            int leadNow = GetLeadTicks();
            int currentTick = NetTickSystem.ClientTick + leadNow;

            if (currentTick - snap.tick > maxReplayTicks)
            {
                _motor.SetState(new MotorState
                {
                    tick = snap.tick,
                    position = snap.position,
                    rotation = snap.rotation,
                    velocity = snap.velocity
                });
                return;
            }

            if (debugNet) dbgRollbacks++;

            // 1) 回滚到权威状态
            _motor.SetState(new MotorState
            {
                tick = snap.tick,
                position = snap.position,
                rotation = snap.rotation,
                velocity = snap.velocity
            });

            // 2) 重放到 currentTick
            float dt = TickSys != null ? TickSys.TickDelta : (1f / 30f);

            for (int t = snap.tick + 1; t <= currentTick; t++)
            {
                if (_inputBuffer.TryGetValue(t, out var cmd))
                {
                    _motor.Simulate(cmd, dt);
                    _stateBuffer[t] = _motor.GetState(t);
                }
                else
                {
                    var empty = new PlayerInputCmd { tick = t };
                    _motor.Simulate(empty, dt);
                    _stateBuffer[t] = _motor.GetState(t);
                }
            }
        }

        PlayerInputCmd BuildInputCmd(int tick)
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            Vector3 fwd = Vector3.forward;
            Vector3 right = Vector3.right;

            if (Camera.main != null)
            {
                Vector3 cf = Camera.main.transform.forward; cf.y = 0;
                Vector3 cr = Camera.main.transform.right; cr.y = 0;
                if (cf.sqrMagnitude > 0.001f) fwd = cf.normalized;
                if (cr.sqrMagnitude > 0.001f) right = cr.normalized;
            }

            Vector3 moveWorld = (right * h + fwd * v);
            float amount = Mathf.Clamp01(moveWorld.magnitude);
            if (moveWorld.sqrMagnitude > 1f) moveWorld.Normalize();

            Vector3 aimDir = moveWorld;

            InputButtons buttons = InputButtons.None;
            if (Input.GetButton("Jump")) buttons |= InputButtons.Jump;
            if (Input.GetMouseButton(0)) buttons |= InputButtons.Attack;
            if (Input.GetKeyDown(KeyCode.Tab)) buttons |= InputButtons.Lock;

            return new PlayerInputCmd
            {
                tick = tick,
                moveDirXZ = new Vector2(moveWorld.x, moveWorld.z),
                moveAmount = amount,
                aimDir = aimDir,
                buttons = buttons
            };
        }

        void OnDrawGizmos()
        {
            if (!debugNet) return;

            Gizmos.color = Color.green;
            Gizmos.DrawSphere(dbgLastAuthPos, 0.15f);

            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(dbgLastPredPosAtSnap, 0.15f);

            Gizmos.color = Color.white;
            Gizmos.DrawLine(dbgLastPredPosAtSnap, dbgLastAuthPos);
        }
    }
}
