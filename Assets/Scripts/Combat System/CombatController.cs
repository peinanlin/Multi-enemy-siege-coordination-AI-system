using UnityEngine;
using Mirror;

public class CombatController : NetworkBehaviour
{
    [Header("Counter Search")]
    [SerializeField] float counterSearchRadius = 6.0f;

    [Header("Server Target Search")]
    [SerializeField] float attackSearchRadius = 10f;

    // ====== 网络同步状态 ======
    [SyncVar(hook = nameof(OnCombatModeChanged))]
    bool combatModeSync;

    // 目标敌人的 netId（要求敌人 prefab 有 NetworkIdentity 并且被 Spawn）
    [SyncVar(hook = nameof(OnTargetNetIdChanged))]
    uint targetEnemyNetId;

    EnemyController targetEnemy;
    public EnemyController TargetEnemy => targetEnemy;
    public bool CombatMode => combatModeSync;

    MeeleFighter meeleFighter;
    Animator animator;
    CameraController cam;

    bool IsNetworking => NetworkClient.active || NetworkServer.active;

    void Awake()
    {
        meeleFighter = GetComponent<MeeleFighter>();
        animator = GetComponent<Animator>();

        var mainCam = Camera.main;
        if (mainCam != null)
            cam = mainCam.GetComponent<CameraController>();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        SetCombatModeLocal(combatModeSync);
        ResolveTargetByNetId(targetEnemyNetId);
    }

    void Start()
    {
        // 受击换目标：让服务器决定，再 SyncVar 同步
        if (meeleFighter != null)
        {
            meeleFighter.OnGotHit += (MeeleFighter attacker) =>
            {
                if (!isServer) return;
                if (!combatModeSync) return;
                if (attacker == null) return;

                var ec = attacker.GetComponent<EnemyController>();
                if (ec != null)
                    ServerSetTargetEnemy(ec);
            };
        }
    }

    void Update()
    {
        // -------- 离线：保持你原来逻辑（仍然可以依赖 PlayerController / EnemyManager）
        if (!IsNetworking)
        {
            OfflineUpdate();
            return;
        }

        // -------- 联机：只有 owner 才能读输入
        if (!isOwned) return;

        if (Input.GetButtonDown("Attack") && !meeleFighter.IsTakingHit)
        {
            Vector3 intentDir = GetIntentDirectionFromInput();
            CmdRequestAttack(intentDir);
        }

        bool lockPressed = Input.GetButtonDown("LockOn");
        if (!lockPressed && JoystickHelper.i != null)
            lockPressed = JoystickHelper.i.GetAxisDown("LockOnTrigger");

        if (lockPressed)
        {
            CmdToggleCombatMode();
        }
    }

    void OfflineUpdate()
    {
        if (Input.GetButtonDown("Attack") && !meeleFighter.IsTakingHit)
        {
            var counterableEnemy = FindCounterableEnemyNearby_Local();

            if (counterableEnemy != null && !meeleFighter.InAction)
            {
                StartCoroutine(meeleFighter.PerformCounterAttack(counterableEnemy));
            }
            else
            {
                EnemyController enemyToAttack = null;
                if (EnemyManager.i != null && PlayerController.i != null)
                    enemyToAttack = EnemyManager.i.GetClosestEnemyToDirection(PlayerController.i.GetIntentDirection());

                meeleFighter.TryToAttack(enemyToAttack?.Fighter);

                combatModeSync = true;
                targetEnemy = enemyToAttack;
                targetEnemyNetId = GetNetId(enemyToAttack);
                SetCombatModeLocal(true);
            }
        }

        bool lockPressed = Input.GetButtonDown("LockOn");
        if (!lockPressed && JoystickHelper.i != null)
            lockPressed = JoystickHelper.i.GetAxisDown("LockOnTrigger");

        if (lockPressed)
        {
            combatModeSync = !combatModeSync;
            SetCombatModeLocal(combatModeSync);
        }
    }

    // 联机时不要在这里改 transform（会和 NetPlayer/预测回滚打架）
    void OnAnimatorMove()
    {
        if (IsNetworking) return;
        if (animator == null) return;
        if (!meeleFighter.InCounter)
            transform.position += animator.deltaPosition;
        transform.rotation *= animator.deltaRotation;
    }

    // ============================================================
    // Cmd：攻击请求（服务器权威判定）
    // ============================================================
    [Command(channel = Channels.Unreliable)]
    void CmdRequestAttack(Vector3 intentDir)
    {
        if (meeleFighter == null) return;
        if (meeleFighter.IsTakingHit) return;

        // 1) 服务器：优先判定反击对象
        var counterable = FindCounterableEnemyNearby_Server();
        if (counterable != null && !meeleFighter.InAction)
        {
            ServerSetTargetEnemy(counterable);
            combatModeSync = true;

            // 服务器触发反击（权威）
            meeleFighter.ServerRequestCounter(counterable);
            return;
        }

        // 2) 服务器：否则选一个攻击目标（按方向挑最近“横向距离最小”的）
        var enemyToAttack = FindEnemyToAttack_Server(intentDir);

        if (enemyToAttack != null)
        {
            ServerSetTargetEnemy(enemyToAttack);
            combatModeSync = true;

            meeleFighter.TryToAttack(enemyToAttack.Fighter); // 注意：TryToAttack 在联机只允许服务器执行
        }
        else
        {
            // 没目标也允许“挥空”
            targetEnemyNetId = 0;
            targetEnemy = null;
            combatModeSync = false;

            meeleFighter.TryToAttack(null);
        }
    }

    [Command(channel = Channels.Unreliable)]
    void CmdToggleCombatMode()
    {
        // 简单：切换开关；如果打开且没目标，服务器尝试找一个最近的
        combatModeSync = !combatModeSync;

        if (combatModeSync && (targetEnemy == null))
        {
            var e = FindEnemyToAttack_Server(transform.forward);
            if (e != null) ServerSetTargetEnemy(e);
        }

        if (!combatModeSync)
        {
            targetEnemyNetId = 0;
            targetEnemy = null;
        }
    }

    // ============================================================
    // 服务器找敌人
    // ============================================================
    EnemyController FindCounterableEnemyNearby_Server()
    {
        var hits = Physics.OverlapSphere(transform.position, counterSearchRadius);

        EnemyController best = null;
        float bestDist = float.PositiveInfinity;

        for (int i = 0; i < hits.Length; i++)
        {
            var col = hits[i];
            if (col == null) continue;

            var enemy = col.GetComponentInParent<EnemyController>();
            if (enemy == null) continue;

            if (!enemy.IsInState(EnemyStates.Attack)) continue;
            if (enemy.Fighter == null || !enemy.Fighter.IsCounterable) continue;

            float d = (enemy.transform.position - transform.position).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = enemy;
            }
        }

        return best;
    }

    EnemyController FindEnemyToAttack_Server(Vector3 direction)
    {
        if (direction.sqrMagnitude < 0.0001f)
            direction = transform.forward;

        direction.y = 0;
        direction.Normalize();

        var hits = Physics.OverlapSphere(transform.position, attackSearchRadius);

        EnemyController best = null;
        float bestScore = float.PositiveInfinity;

        for (int i = 0; i < hits.Length; i++)
        {
            var enemy = hits[i].GetComponentInParent<EnemyController>();
            if (enemy == null) continue;
            if (enemy.IsInState(EnemyStates.Dead)) continue;

            Vector3 vec = enemy.transform.position - transform.position;
            vec.y = 0;
            if (vec.sqrMagnitude < 0.0001f) continue;

            float angle = Vector3.Angle(direction, vec);
            float lateral = vec.magnitude * Mathf.Sin(angle * Mathf.Deg2Rad); // 你原来的“横向距离”
            if (lateral < bestScore)
            {
                bestScore = lateral;
                best = enemy;
            }
        }

        return best;
    }

    // 离线本地用（保持原逻辑）
    EnemyController FindCounterableEnemyNearby_Local()
    {
        var hits = Physics.OverlapSphere(transform.position, counterSearchRadius);

        EnemyController best = null;
        float bestDist = float.PositiveInfinity;

        for (int i = 0; i < hits.Length; i++)
        {
            var col = hits[i];
            if (col == null) continue;

            var enemy = col.GetComponentInParent<EnemyController>();
            if (enemy == null) continue;

            if (!enemy.IsInState(EnemyStates.Attack)) continue;
            if (enemy.Fighter == null || !enemy.Fighter.IsCounterable) continue;

            float d = (enemy.transform.position - transform.position).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = enemy;
            }
        }

        return best;
    }

    // ============================================================
    // SyncVar hooks
    // ============================================================
    void OnCombatModeChanged(bool oldVal, bool newVal)
    {
        SetCombatModeLocal(newVal);
    }

    void OnTargetNetIdChanged(uint oldId, uint newId)
    {
        ResolveTargetByNetId(newId);
        if (newId != 0 && targetEnemy == null)
            SetCombatModeLocal(false);
    }

    void SetCombatModeLocal(bool on)
    {
        combatModeSync = on;
        if (animator != null)
            animator.SetBool("combatMode", on);
    }

    void ResolveTargetByNetId(uint id)
    {
        if (id == 0)
        {
            targetEnemy = null;
            return;
        }

        if (TryGetSpawned(id, out var identity))
        {
            targetEnemy = identity.GetComponent<EnemyController>();
        }
        else
        {
            targetEnemy = null;
        }
    }

    bool TryGetSpawned(uint netId, out NetworkIdentity identity)
    {
        if (NetworkServer.active && NetworkServer.spawned != null &&
            NetworkServer.spawned.TryGetValue(netId, out identity) && identity != null)
            return true;

        if (NetworkClient.active && NetworkClient.spawned != null &&
            NetworkClient.spawned.TryGetValue(netId, out identity) && identity != null)
            return true;

        identity = null;
        return false;
    }

    [Server]
    void ServerSetTargetEnemy(EnemyController ec)
    {
        targetEnemy = ec;
        targetEnemyNetId = GetNetId(ec);
        if (targetEnemyNetId == 0)
        {
            combatModeSync = false;
            targetEnemy = null;
        }
    }

    uint GetNetId(EnemyController ec)
    {
        if (ec == null) return 0;
        var id = ec.GetComponent<NetworkIdentity>();
        return id != null ? id.netId : 0;
    }

    Vector3 GetIntentDirectionFromInput()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        Vector3 moveInput = new Vector3(h, 0, v);
        if (moveInput.sqrMagnitude < 0.0001f)
            return transform.forward;

        moveInput.Normalize();
        if (cam != null) return (cam.PlanarRotation * moveInput).normalized;
        return moveInput;
    }

    public Vector3 GetTargetingDir()
    {
        if (!combatModeSync)
        {
            if (cam == null) return transform.forward;
            var vecFromCam = transform.position - cam.transform.position;
            vecFromCam.y = 0f;
            return vecFromCam.sqrMagnitude > 0.0001f ? vecFromCam.normalized : transform.forward;
        }
        return transform.forward;
    }
}
