using UnityEngine;
using Mirror;

public class RetreatAfterAttackState : State<EnemyController>
{
    [SerializeField] float backwardWalkSpeed = 1.5f;
    [SerializeField] float distanceToRetreat = 3f;
    [SerializeField] float faceSpeedDeg = 720f;

    EnemyController enemy;

    Vector3 startPos;
    bool prevUpdateRotation;

    public override void Enter(EnemyController owner)
    {
        enemy = owner;
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        startPos = enemy.transform.position;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
        {
            prevUpdateRotation = enemy.NavAgent.updateRotation;
            enemy.NavAgent.updateRotation = false;      // ✅策略A：禁止agent自动旋转
            enemy.NavAgent.isStopped = false;
        }
    }

    public override void Execute()
    {
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.Target == null)
        {
            enemy.ChangeState(EnemyStates.CombatMovement);
            return;
        }

        // ✅后撤固定距离：从进入后撤的起点开始算
        float moved = Vector3.Distance(enemy.transform.position, startPos);
        if (moved >= distanceToRetreat)
        {
            enemy.ChangeState(EnemyStates.CombatMovement);
            return;
        }

        Vector3 toTarget = enemy.Target.transform.position - enemy.transform.position;
        toTarget.y = 0f;

        // 始终面向目标（手动旋转）
        if (toTarget.sqrMagnitude > 0.0001f)
        {
            enemy.transform.rotation = Quaternion.RotateTowards(
                enemy.transform.rotation,
                Quaternion.LookRotation(toTarget),
                faceSpeedDeg * Time.deltaTime
            );
        }

        // 后退：沿着“远离目标”的方向移动
        Vector3 backDir = (toTarget.sqrMagnitude > 0.0001f) ? (-toTarget.normalized) : (-enemy.transform.forward);

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
            enemy.NavAgent.Move(backDir * backwardWalkSpeed * Time.deltaTime);
        else
            enemy.transform.position += backDir * backwardWalkSpeed * Time.deltaTime;
    }

    public override void Exit()
    {
        if (enemy == null) return;
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
            enemy.NavAgent.updateRotation = prevUpdateRotation; // ✅恢复
    }
}