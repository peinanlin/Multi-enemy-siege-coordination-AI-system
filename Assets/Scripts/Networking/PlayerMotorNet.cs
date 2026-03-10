using UnityEngine;

namespace Networking
{
    /// <summary>
    /// 可 Tick 驱动的“纯模拟移动器”
    /// - Server：权威推进
    /// - Client：本地预测与回滚重放
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotorNet : MonoBehaviour
    {
        [Header("Move")]
        public float moveSpeed = 5.5f;
        public float rotateSpeed = 720f;

        [Header("Gravity/Jump (optional)")]
        public float gravity = -18f;
        public float jumpHeight = 1.2f;

        CharacterController _cc;
        float _yVel;

        Vector3 _vel;
        public Vector3 Velocity => _vel;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }
        //把“当前角色的运动状态”打包成 MotorState（用于保存历史、回滚对账）
        public MotorState GetState(int tick)
        {
            return new MotorState
            {
                tick = tick,
                position = transform.position,
                rotation = transform.rotation,
                velocity = _vel
            };
        }
        //把角色恢复到某个历史状态（用于回滚）
        public void SetState(MotorState s)
        {
            transform.position = s.position;
            transform.rotation = s.rotation;
            _vel = s.velocity;

            // 粗略还原 y 速度（如果你不做跳跃可以忽略）
            _yVel = s.velocity.y;
        }
        //给定某个 tick 的输入 cmd 和时间步长 dt，推进角色移动/转向/跳跃/重力
        public void Simulate(in PlayerInputCmd cmd, float dt)
        {
            // 1) 方向（世界空间 XZ）
            Vector3 moveDir = new Vector3(cmd.moveDirXZ.x, 0f, cmd.moveDirXZ.y);
            if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

            // 2) 旋转（优先 aimDir，其次 moveDir）
            Vector3 faceDir = cmd.aimDir;
            faceDir.y = 0f;

            if (faceDir.sqrMagnitude < 0.0001f)
                faceDir = moveDir;

            if (faceDir.sqrMagnitude > 0.0001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(faceDir.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, rotateSpeed * dt);
            }

            // 3) 水平速度
            Vector3 horizVel = moveDir * (moveSpeed * Mathf.Clamp01(cmd.moveAmount));

            // 4) 重力 / 跳跃（可选）
            bool grounded = _cc.isGrounded;
            if (grounded && _yVel < 0f) _yVel = -2f; // 贴地

            if (grounded && (cmd.buttons & InputButtons.Jump) != 0)
            {
                _yVel = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }

            _yVel += gravity * dt;

            _vel = new Vector3(horizVel.x, _yVel, horizVel.z);

            // 5) 移动
            _cc.Move(_vel * dt);


        }
    }
}
