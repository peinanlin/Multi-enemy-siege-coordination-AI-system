using UnityEngine;
using Mirror;

public class IdleState : State<EnemyController>
{
    EnemyController enemy;

    public override void Enter(EnemyController owner)
    {
        enemy = owner;

        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
        {
            enemy.NavAgent.ResetPath();
            enemy.NavAgent.isStopped = true;
        }
    }

    public override void Execute()
    {
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.TryAcquireTarget())
        {
            if (enemy.NavAgent != null && enemy.NavAgent.enabled)
                enemy.NavAgent.isStopped = false;

            enemy.ChangeState(EnemyStates.CombatMovement);
        }
    }

    public override void Exit()
    {
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
            enemy.NavAgent.isStopped = false;
    }
}