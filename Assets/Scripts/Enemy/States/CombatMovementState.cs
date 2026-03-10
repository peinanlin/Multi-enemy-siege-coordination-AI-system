using UnityEngine;
using Mirror;

public enum AICombatStates { Idle, Chase, Circling }

public class CombatMovementState : State<EnemyController>
{
    [Header("Combat Distance (to player)")]
    [SerializeField] float distanceToStand = 3f;
    [SerializeField] float adjustDistanceThreshold = 1f;

    [Header("Return To Slot (Anti Offset)")]
    [SerializeField] float returnToStandThreshold = 0.35f;
    [SerializeField] float arriveStandThreshold = 0.20f;

    [Header("Lose Target (Exit chase range)")]
    [SerializeField] float loseTargetDistance = 8f;
    [SerializeField] float loseTargetGraceTime = 0.6f;

    [Header("Idle / Local Circling")]
    [SerializeField] Vector2 idleTimeRange = new Vector2(2, 5);
    [SerializeField] bool enableLocalCircling = false;
    [SerializeField] float circlingSpeed = 20f;
    [SerializeField] Vector2 circlingTimeRange = new Vector2(3, 6);

    [Header("Face Target")]
    [SerializeField] float faceSpeedDeg = 720f;

    [Header("Nav Optimization")]
    [SerializeField] float repathMinDelta = 0.25f;
    [SerializeField] float repathInterval = 0.12f;

    float timer = 0f;
    int circlingDir = 1;
    AICombatStates state;

    EnemyController enemy;
    float loseTimer = 0f;

    Vector3 lastDest;
    float repathTimer = 0f;

    bool prevUpdateRotation;

    public override void Enter(EnemyController owner)
    {
        enemy = owner;

        // 联机：只允许服务器执行
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
        {
            enemy.NavAgent.stoppingDistance = 0.05f;

            prevUpdateRotation = enemy.NavAgent.updateRotation;
            enemy.NavAgent.updateRotation = false;

            enemy.NavAgent.isStopped = false;
        }

        enemy.CombatMovementTimer = 0f;

        loseTimer = 0f;
        repathTimer = 0f;
        lastDest = enemy.transform.position;

        StartIdle();
    }

    public override void Execute()
    {
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        // 0) token/attack/hit 时不由 CombatMovement 驱动位置
        if (enemy.HasAttackToken || enemy.IsInState(EnemyStates.Attack) || enemy.IsInState(EnemyStates.GettingHit))
        {
            if (IsTargetStillValid()) FaceTarget();
            enemy.CombatMovementTimer += Time.deltaTime;
            return;
        }

        // 1) 目标为空：只在视野/检测内才 acquire
        if (enemy.Target == null)
        {
            bool ok = enemy.TryAcquireTarget();
            if (!ok)
            {
                enemy.ChangeState(EnemyStates.Idle);
                return;
            }
        }

        // 2) 丢失目标判定
        if (!IsTargetStillValid())
        {
            loseTimer += Time.deltaTime;
            if (loseTimer >= loseTargetGraceTime)
            {
                enemy.Target = null;
                enemy.ChangeState(EnemyStates.Idle);
                return;
            }
        }
        else loseTimer = 0f;

        // 3) 被推开后回槽位
        if (enemy.HasStandOrder)
        {
            float dStand = Vector3.Distance(enemy.transform.position, enemy.StandWorldPos);
            if (dStand > returnToStandThreshold) StartChase();
        }

        // 4) 距离目标太远：追击（追 standPos）
        float distToTarget = Vector3.Distance(enemy.Target.transform.position, enemy.transform.position);
        if (distToTarget > distanceToStand + adjustDistanceThreshold) StartChase();

        // 5) 状态机
        switch (state)
        {
            case AICombatStates.Idle:
            {
                if (timer <= 0f)
                {
                    if (enableLocalCircling && Random.Range(0, 3) == 0) StartCircling();
                    else StartIdle();
                }

                FaceTarget();

                if (enemy.HasStandOrder)
                {
                    float dStand = Vector3.Distance(enemy.transform.position, enemy.StandWorldPos);
                    if (dStand > returnToStandThreshold) StartChase();
                    else
                    {
                        if (enemy.NavAgent != null && enemy.NavAgent.enabled && enemy.NavAgent.hasPath)
                            enemy.NavAgent.ResetPath();
                    }
                }
                break;
            }

            case AICombatStates.Chase:
            {
                Vector3 chasePos = enemy.HasStandOrder ? enemy.StandWorldPos : enemy.Target.transform.position;

                float distToChasePos = Vector3.Distance(chasePos, enemy.transform.position);
                if (distToChasePos <= arriveStandThreshold)
                {
                    StartIdle();
                    break;
                }

                TrySetDestination(chasePos);
                FaceTarget();
                break;
            }

            case AICombatStates.Circling:
            {
                if (!enableLocalCircling) { StartIdle(); break; }
                if (timer <= 0f) { StartIdle(); break; }

                var vecToTarget = enemy.transform.position - enemy.Target.transform.position;
                vecToTarget.y = 0f;

                if (vecToTarget.sqrMagnitude > 0.0001f && enemy.NavAgent != null && enemy.NavAgent.enabled)
                {
                    var rotatedPos = Quaternion.Euler(0, circlingSpeed * circlingDir * Time.deltaTime, 0) * vecToTarget;
                    enemy.NavAgent.Move(rotatedPos - vecToTarget);
                }

                FaceTarget();
                break;
            }
        }

        if (timer > 0f) timer -= Time.deltaTime;
        enemy.CombatMovementTimer += Time.deltaTime;
    }

    bool IsTargetStillValid()
    {
        if (enemy == null || enemy.Target == null) return false;

        float d = Vector3.Distance(enemy.Target.transform.position, enemy.transform.position);
        if (d > loseTargetDistance) return false;

        Vector3 toTarget = enemy.Target.transform.position - enemy.transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f) return true;

        float angle = Vector3.Angle(enemy.transform.forward, toTarget);
        return angle <= enemy.Fov * 0.5f;
    }

    void FaceTarget()
    {
        if (enemy == null || enemy.Target == null) return;

        var lookDir = enemy.Target.transform.position - enemy.transform.position;
        lookDir.y = 0f;
        if (lookDir.sqrMagnitude > 0.0001f)
        {
            enemy.transform.rotation = Quaternion.RotateTowards(
                enemy.transform.rotation,
                Quaternion.LookRotation(lookDir),
                faceSpeedDeg * Time.deltaTime
            );
        }
    }

    void TrySetDestination(Vector3 dest)
    {
        if (enemy.NavAgent == null || !enemy.NavAgent.enabled) return;

        repathTimer -= Time.deltaTime;
        if (repathTimer > 0f) return;

        if ((dest - lastDest).sqrMagnitude < repathMinDelta * repathMinDelta) return;

        enemy.NavAgent.SetDestination(dest);
        lastDest = dest;
        repathTimer = repathInterval;
    }

    void StartChase() { state = AICombatStates.Chase; timer = 0f; }
    void StartIdle()  { state = AICombatStates.Idle; timer = Random.Range(idleTimeRange.x, idleTimeRange.y); }
    void StartCircling()
    {
        state = AICombatStates.Circling;
        timer = Random.Range(circlingTimeRange.x, circlingTimeRange.y);
        circlingDir = Random.Range(0, 2) == 0 ? 1 : -1;
    }

    public override void Exit()
    {
        if (enemy == null) return;
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        enemy.CombatMovementTimer = 0f;
        loseTimer = 0f;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
            enemy.NavAgent.updateRotation = prevUpdateRotation;
    }
}
