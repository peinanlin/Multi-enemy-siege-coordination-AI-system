using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

public enum AttackStates { Idle, Windup, Impact, Cooldown }

public class MeeleFighter : NetworkBehaviour
{
    [SerializeField] List<AttackData> attacks;
    [SerializeField] List<AttackData> longRangeAttacks;
    [SerializeField] float longRangeAttackThreshold = 1.5f;
    [SerializeField] GameObject sword;

    [SerializeField] float rotationSpeed = 500f;

    public bool IsTakingHit { get; private set; } = false;

    public event Action<MeeleFighter> OnGotHit;   // 只建议服务器侧监听（例如 EnemyController 改状态）
    public event Action OnHitComplete;            // 同上

    BoxCollider swordCollider;
    SphereCollider leftHandCollider, rightHandCollider, leftFootCollider, rightFootCollider;

    Animator animator;

    public AttackStates AttackState { get; private set; }
    bool doCombo;
    int comboCount = 0;
    public bool InAction { get; private set; } = false;
    public bool InCounter { get; set; } = false;

    // 服务器侧用于“命中确认：只打自己锁定的目标”
    MeeleFighter currTarget;

    bool IsNetworking => NetworkClient.active || NetworkServer.active;
    bool IsAuthoritative => !IsNetworking || isServer; // 离线 or 服务器

    bool HasPlayerMotorNet => GetComponent<Networking.PlayerMotorNet>() != null;

    void Awake()
    {
        animator = GetComponent<Animator>();
    }

    void Start()
    {
        if (sword != null)
            swordCollider = sword.GetComponent<BoxCollider>();

        if (animator != null && animator.isHuman)
        {
            TryGetBoneCollider(HumanBodyBones.LeftHand, out leftHandCollider);
            TryGetBoneCollider(HumanBodyBones.RightHand, out rightHandCollider);
            TryGetBoneCollider(HumanBodyBones.LeftFoot, out leftFootCollider);
            TryGetBoneCollider(HumanBodyBones.RightFoot, out rightFootCollider);
        }

        DisableAllHitboxes();

        // ? 关键：联机时客户端不参与 hitbox 碰撞判定（避免每个客户端都触发受击）
        if (IsNetworking && !isServer)
        {
            DisableAllHitboxes();
        }
    }

    void TryGetBoneCollider(HumanBodyBones bone, out SphereCollider col)
    {
        col = null;
        var t = animator.GetBoneTransform(bone);
        if (t != null) col = t.GetComponent<SphereCollider>();
    }

    // ============================================================
    // 对外接口：TryToAttack
    // ============================================================
    public void TryToAttack(MeeleFighter target = null)
    {
        // 联机：只允许服务器驱动“真正的攻击逻辑”
        if (IsNetworking)
        {
            if (!isServer) return;

            if (!InAction)
            {
                StartCoroutine(ServerAttackRoutine(target));
            }
            else if (AttackState == AttackStates.Impact || AttackState == AttackStates.Cooldown)
            {
                doCombo = true;
            }
            return;
        }

        // 离线：保持原行为
        if (!InAction)
        {
            StartCoroutine(OfflineAttackRoutine(target));
        }
        else if (AttackState == AttackStates.Impact || AttackState == AttackStates.Cooldown)
        {
            doCombo = true;
        }
    }

    // ============================================================
    // 服务器权威：攻击流程
    // ============================================================
    [Server]
    IEnumerator ServerAttackRoutine(MeeleFighter target)
    {
        InAction = true;
        currTarget = target;
        AttackState = AttackStates.Windup;

        var attack = attacks[comboCount];

        var attackDir = transform.forward;
        Vector3 startPos = transform.position;
        Vector3 targetPos = Vector3.zero;

        if (target != null)
        {
            var vecToTarget = target.transform.position - transform.position;
            vecToTarget.y = 0;

            if (vecToTarget.sqrMagnitude > 0.0001f)
                attackDir = vecToTarget.normalized;

            float distance = vecToTarget.magnitude - attack.DistanceFromTarget;

            if (distance > longRangeAttackThreshold && longRangeAttacks.Count > 0)
                attack = longRangeAttacks[0];

            if (attack.MoveToTarget)
            {
                if (distance <= attack.MaxMoveDistance)
                    targetPos = target.transform.position - attackDir * attack.DistanceFromTarget;
                else
                    targetPos = startPos + attackDir * attack.MaxMoveDistance;
            }
        }

        // ? 服务器也播动画（为了骨骼/碰撞体跟随动画）
        if (animator != null)
            animator.CrossFade(attack.AnimName, 0.2f);

        // ? 通知所有客户端播同一个动画（先不做 NetAnimator）
        RpcPlayAttack(attack.AnimName);

        yield return null;

        var animState = animator != null ? animator.GetNextAnimatorStateInfo(1) : default;
        float animLen = (animator != null && animState.length > 0.01f) ? animState.length : 0.8f;

        float timer = 0f;
        while (timer <= animLen)
        {
            if (IsTakingHit) break;

            timer += Time.deltaTime;
            float normalizedTime = timer / animLen;

            // ? 避免和玩家预测打架：联机时玩家不做 Attack.MoveToTarget 位移
            bool allowMoveThis = !(IsNetworking && HasPlayerMotorNet);

            if (target != null && attack.MoveToTarget && allowMoveThis)
            {
                float denom = Mathf.Max(attack.MoveEndTime - attack.MoveStartTime, 0.0001f);
                float percTime = (normalizedTime - attack.MoveStartTime) / denom;
                transform.position = Vector3.Lerp(startPos, targetPos, Mathf.Clamp01(percTime));
            }

            // 服务器可旋转面对攻击方向（敌人可用；玩家通常由 motor 决定朝向）
            if (attackDir.sqrMagnitude > 0.0001f && allowMoveThis)
            {
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    Quaternion.LookRotation(attackDir),
                    rotationSpeed * Time.deltaTime
                );
            }

            if (AttackState == AttackStates.Windup)
            {
                if (InCounter) break;

                if (normalizedTime >= attack.ImpactStartTime)
                {
                    AttackState = AttackStates.Impact;
                    EnableHitbox(attack); // ? 只在服务器启用
                }
            }
            else if (AttackState == AttackStates.Impact)
            {
                if (normalizedTime >= attack.ImpactEndTime)
                {
                    AttackState = AttackStates.Cooldown;
                    DisableAllHitboxes();
                }
            }
            else if (AttackState == AttackStates.Cooldown)
            {
                if (doCombo)
                {
                    doCombo = false;
                    comboCount = (comboCount + 1) % attacks.Count;

                    StartCoroutine(ServerAttackRoutine(target));
                    yield break;
                }
            }

            yield return null;
        }

        AttackState = AttackStates.Idle;
        comboCount = 0;
        InAction = false;
        currTarget = null;

        DisableAllHitboxes();
    }

    // ============================================================
    // 离线：原攻击流程（基本不改）
    // ============================================================
    IEnumerator OfflineAttackRoutine(MeeleFighter target = null)
    {
        InAction = true;
        currTarget = target;
        AttackState = AttackStates.Windup;

        var attack = attacks[comboCount];

        var attackDir = transform.forward;
        Vector3 startPos = transform.position;
        Vector3 targetPos = Vector3.zero;
        if (target != null)
        {
            var vecToTarget = target.transform.position - transform.position;
            vecToTarget.y = 0;

            attackDir = vecToTarget.sqrMagnitude > 0.0001f ? vecToTarget.normalized : transform.forward;
            float distance = vecToTarget.magnitude - attack.DistanceFromTarget;

            if (distance > longRangeAttackThreshold && longRangeAttacks.Count > 0)
                attack = longRangeAttacks[0];

            if (attack.MoveToTarget)
            {
                if (distance <= attack.MaxMoveDistance)
                    targetPos = target.transform.position - attackDir * attack.DistanceFromTarget;
                else
                    targetPos = startPos + attackDir * attack.MaxMoveDistance;
            }
        }

        animator.CrossFade(attack.AnimName, 0.2f);
        yield return null;

        var animState = animator.GetNextAnimatorStateInfo(1);
        float timer = 0f;
        while (timer <= animState.length)
        {
            if (IsTakingHit) break;

            timer += Time.deltaTime;
            float normalizedTime = timer / animState.length;

            if (target != null && attack.MoveToTarget)
            {
                float denom = Mathf.Max(attack.MoveEndTime - attack.MoveStartTime, 0.0001f);
                float percTime = (normalizedTime - attack.MoveStartTime) / denom;
                transform.position = Vector3.Lerp(startPos, targetPos, Mathf.Clamp01(percTime));
            }

            if (attackDir.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    Quaternion.LookRotation(attackDir),
                    rotationSpeed * Time.deltaTime
                );
            }

            if (AttackState == AttackStates.Windup)
            {
                if (InCounter) break;

                if (normalizedTime >= attack.ImpactStartTime)
                {
                    AttackState = AttackStates.Impact;
                    EnableHitbox(attack);
                }
            }
            else if (AttackState == AttackStates.Impact)
            {
                if (normalizedTime >= attack.ImpactEndTime)
                {
                    AttackState = AttackStates.Cooldown;
                    DisableAllHitboxes();
                }
            }
            else if (AttackState == AttackStates.Cooldown)
            {
                if (doCombo)
                {
                    doCombo = false;
                    comboCount = (comboCount + 1) % attacks.Count;

                    StartCoroutine(OfflineAttackRoutine(target));
                    yield break;
                }
            }

            yield return null;
        }

        AttackState = AttackStates.Idle;
        comboCount = 0;
        InAction = false;
        currTarget = null;
    }

    // ============================================================
    // 命中判定：只允许服务器触发受击（关键！）
    // ============================================================
    void OnTriggerEnter(Collider other)
    {
        if (!IsAuthoritative) return;

        if (other != null && other.CompareTag("Hitbox") && !IsTakingHit && !InCounter)
        {
            var attacker = other.GetComponentInParent<MeeleFighter>();
            if (attacker == null) return;

            // 只允许命中“攻击者锁定的目标”
            if (attacker.currTarget != this)
                return;

            StartCoroutine(ServerPlayHitReaction(attacker));
        }
    }

    [Server]
    IEnumerator ServerPlayHitReaction(MeeleFighter attacker)
    {
        InAction = true;
        IsTakingHit = true;

        var dispVec = attacker.transform.position - transform.position;
        dispVec.y = 0;
        if (dispVec.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dispVec);

        OnGotHit?.Invoke(attacker);

        if (animator != null)
            animator.CrossFade("SwordImpact", 0.2f);

        RpcPlayHit("SwordImpact");

        yield return null;

        var animState = animator != null ? animator.GetNextAnimatorStateInfo(1) : default;
        float animLen = (animator != null && animState.length > 0.01f) ? animState.length : 0.6f;

        yield return new WaitForSeconds(animLen * 0.8f);

        OnHitComplete?.Invoke();
        InAction = false;
        IsTakingHit = false;
    }

    // ============================================================
    // 反击：离线保留原方法；联机由服务器调用 ServerRequestCounter
    // ============================================================

    // 离线原版（保留）
    public IEnumerator PerformCounterAttack(EnemyController opponent)
    {
        InAction = true;

        InCounter = true;
        opponent.Fighter.InCounter = true;
        opponent.ChangeState(EnemyStates.Dead);

        var dispVec = opponent.transform.position - transform.position;
        dispVec.y = 0f;
        transform.rotation = Quaternion.LookRotation(dispVec);
        opponent.transform.rotation = Quaternion.LookRotation(-dispVec);

        var targetPos = opponent.transform.position - dispVec.normalized * 1f;

        animator.CrossFade("CounterAttack", 0.2f);
        opponent.Animator.CrossFade("CounterAttackVictim", 0.2f);
        yield return null;

        var animState = animator.GetNextAnimatorStateInfo(1);

        float timer = 0f;
        while (timer <= animState.length)
        {
            transform.position = Vector3.MoveTowards(transform.position, targetPos, 5 * Time.deltaTime);

            yield return null;
            timer += Time.deltaTime;
        }

        InCounter = false;
        opponent.Fighter.InCounter = false;
        InAction = false;
    }

    [Server]
    public void ServerRequestCounter(EnemyController opponent)
    {
        if (opponent == null) return;
        if (InAction) return;

        StartCoroutine(ServerCounterRoutine(opponent));
    }

    [Server]
    IEnumerator ServerCounterRoutine(EnemyController opponent)
    {
        InAction = true;
        InCounter = true;

        if (opponent.Fighter != null)
            opponent.Fighter.InCounter = true;

        // 这里保留你原本“反击=直接处死”逻辑（后面我们再统一改成 Damage/Health 体系）
        opponent.ChangeState(EnemyStates.Dead);

        var dispVec = opponent.transform.position - transform.position;
        dispVec.y = 0f;
        if (dispVec.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(dispVec);
            opponent.transform.rotation = Quaternion.LookRotation(-dispVec);
        }

        if (animator != null) animator.CrossFade("CounterAttack", 0.2f);
        if (opponent.Animator != null) opponent.Animator.CrossFade("CounterAttackVictim", 0.2f);

        RpcPlayAttack("CounterAttack");
        if (opponent.Fighter != null) opponent.Fighter.RpcPlayAttack("CounterAttackVictim");

        yield return null;

        var animState = animator != null ? animator.GetNextAnimatorStateInfo(1) : default;
        float animLen = (animator != null && animState.length > 0.01f) ? animState.length : 1.0f;

        yield return new WaitForSeconds(animLen);

        InCounter = false;
        if (opponent.Fighter != null) opponent.Fighter.InCounter = false;
        InAction = false;
    }

    // ============================================================
    // RPC：只负责“播动画/视觉状态”（不做游戏结算）
    // Host 上服务器已经播过了，避免双播：isServer 时直接 return
    // ============================================================
    [ClientRpc(channel = Channels.Reliable)]
    void RpcPlayAttack(string animName)
    {
        if (isServer) return;
        if (animator != null) animator.CrossFade(animName, 0.2f);
    }

    [ClientRpc(channel = Channels.Reliable)]
    void RpcPlayHit(string animName)
    {
        if (isServer) return;
        if (animator != null) animator.CrossFade(animName, 0.2f);
    }

    // ============================================================
    // Hitbox 控制
    // ============================================================
    void EnableHitbox(AttackData attack)
    {
        // ? 联机：只服务器启用 hitbox
        if (IsNetworking && !isServer) return;

        switch (attack.HitboxToUse)
        {
            case AttackHitbox.LeftHand:
                if (leftHandCollider != null) leftHandCollider.enabled = true;
                break;
            case AttackHitbox.RightHand:
                if (rightHandCollider != null) rightHandCollider.enabled = true;
                break;
            case AttackHitbox.LeftFoot:
                if (leftFootCollider != null) leftFootCollider.enabled = true;
                break;
            case AttackHitbox.RightFoot:
                if (rightFootCollider != null) rightFootCollider.enabled = true;
                break;
            case AttackHitbox.Sword:
                if (swordCollider != null) swordCollider.enabled = true;
                break;
        }
    }

    void DisableAllHitboxes()
    {
        if (swordCollider != null) swordCollider.enabled = false;
        if (leftHandCollider != null) leftHandCollider.enabled = false;
        if (rightHandCollider != null) rightHandCollider.enabled = false;
        if (leftFootCollider != null) leftFootCollider.enabled = false;
        if (rightFootCollider != null) rightFootCollider.enabled = false;
    }

    public List<AttackData> Attacks => attacks;

    // 注意：联机时“是否可反击”的权威判断应当只看服务器
    public bool IsCounterable => AttackState == AttackStates.Windup && comboCount == 0;
}
