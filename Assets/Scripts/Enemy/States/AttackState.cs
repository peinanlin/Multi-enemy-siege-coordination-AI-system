using System.Collections;
using UnityEngine;
using Mirror;

public class AttackState : State<EnemyController>
{
    [SerializeField] float attackDistance = 1f;

    bool isAttacking;
    EnemyController enemy;

    public override void Enter(EnemyController owner)
    {
        enemy = owner;

        // 联机：只允许服务器执行 AI/攻击逻辑
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.NavAgent != null)
        {
            enemy.NavAgent.stoppingDistance = attackDistance;
            enemy.NavAgent.isStopped = false;
        }
        isAttacking = false;
    }

    public override void Execute()
    {
        // 联机：只允许服务器执行
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (isAttacking) return;

        if (enemy == null || enemy.Target == null)
        {
            // 目标丢失：回到 CombatMovement 或 Idle（按你项目逻辑）
            enemy.ChangeState(EnemyStates.CombatMovement);
            return;
        }

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
        {
            enemy.NavAgent.SetDestination(enemy.Target.transform.position);
        }

        float dist = Vector3.Distance(enemy.Target.transform.position, enemy.transform.position);
        if (dist <= attackDistance + 0.03f)
        {
            // comboCount 至少为 1；Random.Range(int,int) 上限不包含
            int maxCombo = Mathf.Max(1, enemy.Fighter != null ? enemy.Fighter.Attacks.Count : 1);
            int comboCount = Random.Range(1, maxCombo + 1);
            StartCoroutine(Attack(comboCount));
        }
    }

    IEnumerator Attack(int comboCount = 1)
    {
        // 联机：只允许服务器执行
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) yield break;

        isAttacking = true;

        // 攻击时建议停 agent，避免 agent 与 root motion 抢 transform
        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
        {
            enemy.NavAgent.ResetPath();
            enemy.NavAgent.isStopped = true;
        }

        // 服务器可以使用 root motion 产生攻击前冲（位置由 NetworkTransform 同步到客户端）
        if (enemy.Animator != null)
            enemy.Animator.applyRootMotion = true;

        // 真正“触发攻击/命中判定”我们后面会在 MeeleFighter 改成服务器权威
        if (enemy.Fighter != null && enemy.Target != null)
            enemy.Fighter.TryToAttack(enemy.Target);

        for (int i = 1; i < comboCount; i++)
        {
            yield return new WaitUntil(() => enemy.Fighter == null || enemy.Fighter.AttackState == AttackStates.Cooldown);
            if (enemy.Fighter != null && enemy.Target != null)
                enemy.Fighter.TryToAttack(enemy.Target);
        }

        yield return new WaitUntil(() => enemy.Fighter == null || enemy.Fighter.AttackState == AttackStates.Idle);

        if (enemy.Animator != null)
            enemy.Animator.applyRootMotion = false;

        // 恢复 agent
        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
            enemy.NavAgent.isStopped = false;

        isAttacking = false;

        if (enemy != null && enemy.IsInState(EnemyStates.Attack))
            enemy.ChangeState(EnemyStates.RetreatAfterAttack);
    }

    public override void Exit()
    {
        if (enemy == null) return;
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
        {
            enemy.NavAgent.isStopped = false;
            enemy.NavAgent.ResetPath();
        }

        if (enemy.Animator != null)
            enemy.Animator.applyRootMotion = false;

        isAttacking = false;
        StopAllCoroutines();
    }
}
