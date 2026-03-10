using System.Collections;
using UnityEngine;
using Mirror;

public class GettingHitState : State<EnemyController>
{
    [SerializeField] float stunnTime = 0.5f;

    EnemyController enemy;
    System.Action _onHitComplete;

    public override void Enter(EnemyController owner)
    {
        enemy = owner;

        // ������ֻ���������ִ��
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        StopAllCoroutines();

        // �����ܻ�ʱͣһ�� agent����ֹ�ܻ������ܣ�
        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
        {
            enemy.NavAgent.ResetPath();
            enemy.NavAgent.isStopped = true;
        }

        // ��ֹ������������¼�
        if (enemy.Fighter != null)
        {
            if (_onHitComplete == null)
                _onHitComplete = () => StartCoroutine(GoToCombatMovement());

            enemy.Fighter.OnHitComplete -= _onHitComplete;
            enemy.Fighter.OnHitComplete += _onHitComplete;
        }   
    }

    IEnumerator GoToCombatMovement()
    {
        // ������ֻ���������ִ��
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) yield break;

        yield return new WaitForSeconds(stunnTime);

        if (enemy != null && enemy.IsInState(EnemyStates.GettingHit))
            enemy.ChangeState(EnemyStates.CombatMovement);
    }

    public override void Exit()
    {
        if (enemy == null) return;
        if ((NetworkClient.active || NetworkServer.active) && !enemy.isServer) return;

        StopAllCoroutines();

        if (enemy.Fighter != null && _onHitComplete != null)
            enemy.Fighter.OnHitComplete -= _onHitComplete;

        if (enemy.NavAgent != null && enemy.NavAgent.enabled)
            enemy.NavAgent.isStopped = false;
    }
}