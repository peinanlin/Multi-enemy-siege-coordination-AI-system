using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;

public enum EnemyStates { Idle, CombatMovement, Attack, RetreatAfterAttack, Dead, GettingHit }

/// <summary>
/// ������ EnemyController��������Ȩ�� + ״̬ͬ����
/// - ��������AI/״̬��/Ŀ��ѡ��/Эͬ�߼�ֻ��һ��
/// - �ͻ��ˣ�ֻ������ʾ��λ���� NetworkTransform ͬ�������������� transform delta ���㣩
/// </summary>
[RequireComponent(typeof(NetworkIdentity))]
public class EnemyController : NetworkBehaviour
{
    public enum EnemyCombatRole { StandbyOuter, StandbyInner, Attacker }

    [Header("Aggro")]
    [SerializeField] float disengageDistance = 12f; // ��ս���루������ VisionSensor �뾶һ�£�

    [field: SerializeField] public float Fov { get; private set; } = 180f;

    // ��Щֻ�ڷ�����ά��/ʹ��
    public List<MeeleFighter> TargetsInRange { get; set; } = new List<MeeleFighter>();
    public MeeleFighter Target { get; set; }
    public float CombatMovementTimer { get; set; } = 0f;

    public StateMachine<EnemyController> StateMachine { get; private set; }
    Dictionary<EnemyStates, State<EnemyController>> stateDict;

    public NavMeshAgent NavAgent { get; private set; }
    public CharacterController CharacterController { get; private set; }
    public Animator Animator { get; private set; }
    public MeeleFighter Fighter { get; private set; }
    public SkinnedMeshHighlighter MeshHighlighter { get; private set; }
    public VisionSensor VisionSensor { get; set; }

    // === Battle circle / token ===��������Ȩ����
    public EnemyCombatRole Role { get; private set; } = EnemyCombatRole.StandbyOuter;
    public bool HasAttackToken { get; private set; } = false;

    public bool HasStandOrder { get; private set; } = false;
    public int StandSlotId { get; private set; } = -1;
    public Vector3 StandWorldPos { get; private set; } = Vector3.zero;

    // ===== ״̬ͬ������С���ϣ���ͬ�� combatMode��=====
    // combatMode ��ҪӰ�� Animator ��ս����̬/BlendTree
    [SyncVar(hook = nameof(OnCombatModeChanged))]
    bool combatModeSync;

    // ��ѡ��ͬ��һ������ǰ״̬�����������Ǹ� States ʱ���õ��������ӿڣ�
    [SyncVar]
    EnemyStates netState;

    bool _authorityInited;

    Vector3 prevPos;

    void Awake()
    {
        NavAgent = GetComponent<NavMeshAgent>();
        CharacterController = GetComponent<CharacterController>();
        Animator = GetComponent<Animator>();
        Fighter = GetComponent<MeeleFighter>();
        MeshHighlighter = GetComponent<SkinnedMeshHighlighter>();

        // �Ȼ��� state ������ã�������ִ�У�
        stateDict = new Dictionary<EnemyStates, State<EnemyController>>();
        stateDict[EnemyStates.Idle] = GetComponent<IdleState>();
        stateDict[EnemyStates.CombatMovement] = GetComponent<CombatMovementState>();
        stateDict[EnemyStates.Attack] = GetComponent<AttackState>();
        stateDict[EnemyStates.RetreatAfterAttack] = GetComponent<RetreatAfterAttackState>();
        stateDict[EnemyStates.Dead] = GetComponent<DeadState>();
        stateDict[EnemyStates.GettingHit] = GetComponent<GettingHitState>();
    }

    void Start()
    {
        // ����������ԭ�߼���ʼ����û������ Mirror ���磩
        if (!NetworkClient.active && !NetworkServer.active)
        {
            InitAuthorityIfNeeded();
        }

        prevPos = transform.position;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        InitAuthorityIfNeeded();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        if (!isServer)
        {
            if (NavAgent != null)
            {
                NavAgent.enabled = false;
                NavAgent.updatePosition = false;
                NavAgent.updateRotation = false;
            }

            if (CharacterController != null)
                CharacterController.enabled = false;

            // ��Ȩ���˹ر� Root Motion�����⶯��Ҳȥ�� transform���� NetworkTransform ��ܣ�
            if (Animator != null)
                Animator.applyRootMotion = false;
        }

        // �ÿͻ��� Animator ��̬���������ͬ��ֵ
        ApplyCombatModeToAnimator(combatModeSync);

        prevPos = transform.position;
    }

    void InitAuthorityIfNeeded()
    {
        if (_authorityInited) return;
        _authorityInited = true;

        StateMachine = new StateMachine<EnemyController>(this);
        StateMachine.ChangeState(stateDict[EnemyStates.Idle]);
        netState = EnemyStates.Idle;

        // �ܻ�����״̬�л���ֻ���������/����Ȩ����
        if (Fighter != null)
        {
            Fighter.OnGotHit += (MeeleFighter attacker) =>
            {
                if (NetworkClient.active || NetworkServer.active)
                {
                    if (!isServer) return;
                }
                ChangeState(EnemyStates.GettingHit);
            };
        }
    }

    void Update()
    {
        bool isNetworking = NetworkClient.active || NetworkServer.active;

        // ========== ������Ȩ���߼���ֻ�ڷ������� ==========
        if (!isNetworking || isServer)
        {
            // ��ս�ж�����������
            if (Target != null && !IsInState(EnemyStates.Dead))
            {
                float d = Vector3.Distance(transform.position, Target.transform.position);
                if (d > disengageDistance)
                {
                    ForceDisengage();
                }
            }

            // ״̬���ƽ���AI/Ѱ·/�������ߵȣ�
            StateMachine?.Execute();
        }

        // ========== �������������ж˶������㣨���� transform delta�� ==========
        UpdateAnimatorLocomotion();
    }

    void UpdateAnimatorLocomotion()
    {
        if (Animator == null) return;

        Vector3 deltaPos = transform.position - prevPos;
        Vector3 velocity = deltaPos / Mathf.Max(Time.deltaTime, 0.0001f);
        velocity.y = 0;

        float refSpeed = (NavAgent != null) ? Mathf.Max(NavAgent.speed, 0.01f) : 1f;

        // ✅ 前后：正=前进，负=后退（Backward Walk 的关键）
        float forward01 = Vector3.Dot(velocity, transform.forward) / refSpeed;

        // ✅ 左右：正=右移，负=左移（Strafe Left/Right 的关键）
        float strafe01  = Vector3.Dot(velocity, transform.right) / refSpeed;

        // 可选：限制到 [-1,1]，避免网络插值抖动导致超范围
        forward01 = Mathf.Clamp(forward01, -1f, 1f);
        strafe01  = Mathf.Clamp(strafe01,  -1f, 1f);

        Animator.SetFloat("forwardSpeed", forward01, 0.2f, Time.deltaTime);
        Animator.SetFloat("strafeSpeed",  strafe01,  0.2f, Time.deltaTime);

        prevPos = transform.position;
    }

    // === ״̬�л���������Ȩ�� ===
    public void ChangeState(EnemyStates state)
    {
        // ������ֻ�з����������״̬
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return;

        if (StateMachine == null) InitAuthorityIfNeeded();

        StateMachine.ChangeState(stateDict[state]);
        netState = state;

        // һ����Сͬ�����ԣ�ֻҪ����� Idle ����Ϊս����̬���������Ը���ϸ��
        if (state == EnemyStates.Idle || state == EnemyStates.Dead)
            SetCombatModeServer(false);
        else
            SetCombatModeServer(true);
    }

    public bool IsInState(EnemyStates state)
    {
        return StateMachine != null && StateMachine.CurrentState == stateDict[state];
    }

    // === �����ӽǲ�׷����IdleState����ã� ===
    public MeeleFighter FindTargetInFov()
    {
        // ֻ���������/����Ȩ����Ŀ��
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return null;

        for (int i = 0; i < TargetsInRange.Count; i++)
        {
            var target = TargetsInRange[i];
            if (target == null) continue;

            var vecToTarget = target.transform.position - transform.position;
            vecToTarget.y = 0;

            float angle = Vector3.Angle(transform.forward, vecToTarget);
            if (angle <= Fov * 0.5f)
                return target;
        }
        return null;
    }

    public bool TryAcquireTarget()
    {
        // ֻ���������/����Ȩ����ȡĿ��
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return false;

        if (Target != null) return true;

        var t = FindTargetInFov();
        if (t != null)
        {
            Target = t;

            // ֻ����������Ŀ��ż��� EnemyManager��ֻ�ڷ�������
            if (EnemyManager.i != null)
                EnemyManager.i.AddEnemyInRange(this);

            SetCombatModeServer(true);
            return true;
        }
        return false;
    }

    // === ǿ����ս����Ŀ��/��token/��Idle/��manager�Ƴ� ===
    public void ForceDisengage()
    {
        // ֻ���������/����Ȩ����ս
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return;

        Target = null;
        CombatMovementTimer = 0f;

        RevokeAttackToken();
        ClearStandOrder();

        SetCombatModeServer(false);

        if (EnemyManager.i != null)
            EnemyManager.i.RemoveEnemyInRange(this);

        if (!IsInState(EnemyStates.Dead))
            ChangeState(EnemyStates.Idle);
    }

    // ===== combatMode ͬ����������д SyncVar���ͻ��� hook ������� Animator��=====
    void SetCombatModeServer(bool on)
    {
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return;

        combatModeSync = on;                 // SyncVar��������д -> ���пͻ���ͬ��
        ApplyCombatModeToAnimator(on);       // ����������ҲҪ������Ч
    }

    void OnCombatModeChanged(bool oldVal, bool newVal)
    {
        ApplyCombatModeToAnimator(newVal);
    }

    void ApplyCombatModeToAnimator(bool on)
    {
        if (Animator != null)
            Animator.SetBool("combatMode", on);
    }

    // === վλ����������Ȩ���� ===
    public void SetStandOrder(int slotId, Vector3 worldPos, EnemyCombatRole role)
    {
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return;

        StandSlotId = slotId;
        StandWorldPos = worldPos;
        HasStandOrder = true;
        Role = role;
    }
// ������֪ͨ��������ͻ���Ҳ��Ҫ������ײ/��������
    [Server]
    public void ServerNotifyDead()
    {
        RpcApplyDeadLocal();
    }

    [ClientRpc]
    void RpcApplyDeadLocal()
    {
        if (VisionSensor != null) VisionSensor.gameObject.SetActive(false);
        if (NavAgent != null) NavAgent.enabled = false;
        if (CharacterController != null) CharacterController.enabled = false;

        // �ͻ��˲�Ҫ�� root motion �� transform������� NetworkTransform ��ܣ�
        if (Animator != null) Animator.applyRootMotion = false;
    }

    public void ClearStandOrder()
    {
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return;

        StandSlotId = -1;
        StandWorldPos = Vector3.zero;
        HasStandOrder = false;

        if (Role != EnemyCombatRole.Attacker)
            Role = EnemyCombatRole.StandbyOuter;
    }

    // === token��������Ȩ���� ===
    public void GrantAttackToken()
    {
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return;

        HasAttackToken = true;
        Role = EnemyCombatRole.Attacker;
    }

    public void RevokeAttackToken()
    {
        if ((NetworkClient.active || NetworkServer.active) && !isServer) return;

        HasAttackToken = false;

        if (Role == EnemyCombatRole.Attacker)
            Role = EnemyCombatRole.StandbyOuter;
    }

    public float DisengageDistance => disengageDistance;
}
