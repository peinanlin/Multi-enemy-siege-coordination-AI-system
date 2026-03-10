using UnityEngine;
using Mirror;

public class DeadState : State<EnemyController>
{
    public override void Enter(EnemyController owner)
    {
        // 联机：只允许服务器执行
        if ((NetworkClient.active || NetworkServer.active) && !owner.isServer) return;

        if (owner.VisionSensor != null)
            owner.VisionSensor.gameObject.SetActive(false);

        if (EnemyManager.i != null)
            EnemyManager.i.RemoveEnemyInRange(owner);

        if (owner.NavAgent != null)
            owner.NavAgent.enabled = false;

        if (owner.CharacterController != null)
            owner.CharacterController.enabled = false;

        // 关键：通知客户端也做“死亡后的禁用”
        // （下面我会给 EnemyController 增加一个 public 的 ServerNotifyDead() 来触发 SyncVar hook/Rpc）
        owner.ServerNotifyDead();
    }
}