using UnityEngine;

namespace Networking
{
    [RequireComponent(typeof(Animator))]
    [RequireComponent(typeof(PlayerMotorNet))]
    public class NetPlayerAnimator : MonoBehaviour
    {
        [Header("Animator Params")]
        [SerializeField] string forwardParam = "forwardSpeed";
        [SerializeField] string strafeParam  = "strafeSpeed";
        [SerializeField] string combatBool   = "combatMode";

        [Header("Damping")]
        [SerializeField] float dampTime = 0.2f;

        [Header("Clamp (Normalize)")]
        [SerializeField] bool clampToUnit = true;   // 是否把 forward/strafe clamp 到 [-1,1]

        Animator _anim;
        PlayerMotorNet _motor;
        MeeleFighter _fighter;
        CombatController _combat;

        int _hForward, _hStrafe, _hCombat;

        void Awake()
        {
            _anim = GetComponent<Animator>();
            _motor = GetComponent<PlayerMotorNet>();
            _fighter = GetComponent<MeeleFighter>();
            _combat = GetComponent<CombatController>();

            _hForward = Animator.StringToHash(forwardParam);
            _hStrafe  = Animator.StringToHash(strafeParam);
            _hCombat  = Animator.StringToHash(combatBool);
        }

        void Update()
        {
            // 1) 攻击/受击等动作中：停 locomotion（保持你原逻辑）
            if (_fighter != null && _fighter.InAction)
            {
                _anim.SetFloat(_hForward, 0f, dampTime, Time.deltaTime);
                _anim.SetFloat(_hStrafe,  0f, dampTime, Time.deltaTime);
                return;
            }

            // 2) combatMode：由 CombatController 控制
            bool inCombat = (_combat != null && _combat.CombatMode);
            _anim.SetBool(_hCombat, inCombat);

            // 3) 取速度（由 PlayerMotorNet 更新）
            Vector3 v = _motor.Velocity;
            v.y = 0f;

            float maxSpeed = Mathf.Max(0.01f, _motor.moveSpeed);

            float forward;
            float strafe;

            if (!inCombat)
            {
                // 非战斗：只用速度大小驱动 forward（0~1），不横移
                float speed01 = Mathf.Clamp01(v.magnitude / maxSpeed);
                forward = speed01;
                strafe = 0f;
            }
            else
            {
                // ✅ 战斗：用“真实速度”在角色局部空间的投影驱动 2D BlendTree
                // forwardSpeed: 前进为正、后退为负
                forward = Vector3.Dot(v, transform.forward) / maxSpeed;

                // strafeSpeed: 右移为正、左移为负
                strafe  = Vector3.Dot(v, transform.right) / maxSpeed;

                if (clampToUnit)
                {
                    forward = Mathf.Clamp(forward, -1f, 1f);
                    strafe  = Mathf.Clamp(strafe,  -1f, 1f);
                }
            }

            _anim.SetFloat(_hForward, forward, dampTime, Time.deltaTime);
            _anim.SetFloat(_hStrafe,  strafe,  dampTime, Time.deltaTime);
        }
    }
}