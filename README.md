# Multi-enemy-siege-coordination-AI-system
### demo: https://www.bilibili.com/video/BV1U7cRzpEbe/?spm_id_from=333.1387.upload.video_card.click
本仓库是一个面向 **Unity 游戏客户端岗位**的可演示项目，包含两条主线系统：

- **第三人称动作战斗与多敌人围攻协同 AI 系统**
- **基于 Mirror 的 Tick 状态同步、客户端预测与回滚重放对战系统**

### 1) 第三人称动作战斗 & 多敌人围攻协同 AI

通过“**内外圈站位槽位 + 攻击令牌（Token）轮换**”实现多敌人围攻协同，目标是提升战斗可读性与节奏感，避免扎堆卡位与无序冲撞。

- **内外圈槽位（Battle Circle Slots）**
  - 外圈：等待与包围站位
  - 内圈：预备攻击队列（Standby）
  - 槽位基于玩家中心等角分布，支持环绕旋转以避免固定堵点
- **攻击令牌（Attack Token）**
  - 限制同一时间的攻击人数（`maxAttackers`）
  - 令牌按间隔拍卖/分配，攻击完成或超时回收，实现轮换进攻节奏
- **拍卖式评分分配（Auction Scoring）**
  - 综合考虑：等待时间、理想距离、正/背面偏好、路径可达性、密度分散等因素
  - 避免“走不到的人拿到 token”导致节奏断档
- **密度模块（Density Bins）**
  - 将敌人分布映射到角度桶（bins），倾向选择低密度方向补位，逐渐形成更均匀的包围圈
- **避让与让路（Avoidance & Yield）**
  - 攻击者拥有更高避让优先级，周围单位侧向让路（yield），减少最后贴近阶段卡位
- **服务器权威 AI**
  - 联机时 AI/状态机/寻路仅在服务器运行，客户端只做表现（避免双驱动不同步）
  
### 2) Mirror Tick 同步 & 客户端预测与回滚重放

采用固定 Tick 驱动输入与模拟：客户端立即预测，服务器权威校正；远端对象使用快照插值平滑显示。

- **固定 Tick 驱动**
  - 统一逻辑步长：`TickDelta = 1 / tickRate`
  - 客户端预测、服务器权威、回滚重放使用同一 dt，保证可重放一致性
- **输入提前量（Lead Ticks）**
  - 根据 RTT 估算 one-way delay，将输入发送到未来 tick，减少服务器缺输入
- **客户端预测（Client Prediction）**
  - 本地输入立即 `Simulate(cmd, dt)`，不等待服务器回包，保证低输入延迟手感
- **服务器权威快照（Authoritative Snapshots）**
  - 服务器按 tick 消费输入并推进模拟，定期广播快照（`tick + position + rotation + velocity`）
- **误差阈值对齐（Reconciliation）**
  - 客户端收到快照后比对历史预测状态
  - 误差超过阈值：回滚到快照 tick=T 的权威状态，然后从 **T+1 重放到当前 tick**
  - 限制最大重放 tick（`maxReplayTicks`），防止延迟过大导致客户端卡死
- **远端插值（Interpolation）**
  - 远端对象使用快照缓冲 + `renderTick` 滞后插值吸收网络 jitter，获得平滑观感
  - 
## 📁 Scripts

# 1) Combat System（战斗系统）

## `Combat System/AttackData.cs`
- **作用**：攻击配置的 `ScriptableObject`（可在 Inspector 创建多套攻击数据）。
- **内容**：
  - `AnimName`：攻击动画名（CrossFade 用）
  - `HitboxToUse`：启用哪个命中盒（手/脚/剑）
  - `ImpactStartTime/ImpactEndTime`：命中窗口（在这个区间启用 hitbox）
  - `MoveToTarget` 及相关参数：是否在攻击期间“贴近目标”（移动插值区间、最大移动距离等）
- **项目意义**：把攻击行为从代码中剥离成可调参的数据资产，方便做连招/不同武器的扩展。

---

## `Combat System/MeeleFighter.cs`
- **作用**：角色（玩家/敌人）的近战战斗核心组件（攻击、受击、反击、命中盒控制）。
- **联机策略**（重点）：
  - **服务器权威**：联机时只有服务器执行“真正的攻击逻辑 / 命中判定 / 受击触发”
  - 客户端只通过 RPC **播放动画表现**，不会各自做碰撞判定（避免重复受击/不同步）
- **主要职责**：
  - 攻击状态机：`AttackStates { Windup, Impact, Cooldown }`，支持连招（combo）
  - 攻击流程（服务器）：`ServerAttackRoutine()`  
    - CrossFade 动画 + `RpcPlayAttack()` 广播动画  
    - 进入 `Impact` 时间段启用 hitbox，结束关闭 hitbox  
    - 根据 `AttackData` 可做 MoveToTarget（但对带 `PlayerMotorNet` 的玩家会禁用，以免和网络移动预测冲突）
  - 命中判定：`OnTriggerEnter()`（只在服务器有效）  
    - 只允许命中“攻击者锁定的目标”（`attacker.currTarget == this`），避免乱打
  - 受击表现：`ServerPlayHitReaction()` + `RpcPlayHit()`
  - 反击系统：`ServerRequestCounter()` / `ServerCounterRoutine()`  
    - 服务器触发双方反击动画并处理敌人死亡（当前版本直接切 Dead）
  - Hitbox 控制：`EnableHitbox()` / `DisableAllHitboxes()`
- **挂载位置**：玩家与敌人 Prefab 上（都需要近战能力/受击事件）。

---

## `Combat System/CombatController.cs`
- **作用**：玩家战斗“意图层”控制器：锁定模式、选敌、攻击请求、服务器目标确认。
- **同步内容**：
  - `combatModeSync`（SyncVar）：战斗模式开关（影响 Animator 的 combatMode）
  - `targetEnemyNetId`（SyncVar）：锁定目标的 netId（客户端通过 spawned 字典反查目标对象）
- **主要职责**：
  - **联机**：只有 **owner** 读取输入，通过 `CmdRequestAttack()`、`CmdToggleCombatMode()` 把请求发到服务器  
    - 服务器侧根据方向/距离在 `attackSearchRadius` 内选一个最合适敌人  
    - 可优先寻找“可反击目标”（攻击中且可 counter）
    - 最终由服务器调用 `MeeleFighter.TryToAttack(...)` 执行权威攻击
  - **离线**：保留本地旧逻辑（本地找敌人/反击/切换战斗模式）
  - `OnAnimatorMove()`：离线时可使用 root motion 推动角色；联机时禁用（避免和预测移动系统冲突）
- **挂载位置**：玩家 Prefab 上（负责 lock-on/攻击输入）。

---

# 2) Enemy（敌人 AI + 围攻协同）

## `Enemy/EnemyController.cs`
- **作用**：敌人 NetworkBehaviour 总控：目标/状态机/服务器权威执行 + 客户端表现。
- **服务器权威点**：
  - AI 状态机只在服务器执行（`ChangeState`、`StateMachine.Execute` 等）
  - `TryAcquireTarget()` 只在服务器锁定目标并加入 `EnemyManager`
- **客户端表现点**：
  - 非服务器端会禁用 `NavMeshAgent` / `CharacterController`，关闭 root motion  
  - 用 `UpdateAnimatorLocomotion()` 通过 **transform delta** 推导 `forwardSpeed/strafeSpeed`，驱动 BlendTree
- **与围攻系统对接**：
  - 保存 `StandOrder`：`StandSlotId` / `StandWorldPos` / `Role`
  - token 状态：`HasAttackToken`（Attacker/StandbyInner/StandbyOuter）
- **同步**：
  - `combatModeSync`（SyncVar hook）：同步给客户端 Animator 的 combatMode
- **死亡处理**：
  - `ServerNotifyDead()` -> `RpcApplyDeadLocal()`：客户端也关闭传感器与移动组件
- **挂载位置**：敌人 Prefab 根节点。

---

## `Enemy/EnemyManager.cs`
- **作用**：多敌人围攻协同的核心调度器（**内外圈槽位 + token 拍卖 + 密度分散 + 让路/避让**）。
- **核心设计**：每个玩家一个 `CombatGroup`（`Dictionary<MeeleFighter, CombatGroup>`）
  - 同一局多人时：敌人围绕各自 Target（玩家）独立分配圈与 token
- **主要职责**：
  1) **槽位系统（Battle Circle Slots）**
     - 内圈/外圈 slot 等角分布、环绕旋转（ringAngleOffset）
     - `AssignInnerThenOuterSlots_Sticky()`：先分内圈（待攻队列），再分外圈（包围等待）
     - sticky + slotLockTime + minDelta：减少频繁换槽造成的抖动
  2) **密度模块（Density Bins）**
     - 将敌人按角度映射到 bins，统计局部密度  
     - 给“稀疏区域”更高分，促使包围更均匀
  3) **token 拍卖（Attack Token Auction）**
     - 限制同时攻击人数：`maxAttackers`
     - 定时拍卖：`RunAuctionAndGrantTokens_ByScoring()`，按综合评分发 token
     - token 回收：超时/死亡/不在 Attack 等条件会回收
  4) **避让与让路（Avoidance & Yield）**
     - 攻击者更高优先级：`avoidancePriority`
     - 攻击者附近把非攻击者横向推开（yield），减少最后贴近阶段卡位
  5) **调试可视化**
     - Gizmos 显示内外圈槽位、密度热力、token 持有者等
- **挂载位置**：场景里一个全局管理物体（联机建议只让服务器运行其 Update）。

---

## `Enemy/VisionSensor.cs`
- **作用**：敌人仇恨感知触发器（SphereCollider trigger）。
- **联机策略**：
  - 客户端可禁用 trigger（只让服务器处理 OnTriggerEnter/Exit）
- **主要职责**：
  - `OnTriggerEnter`：把 `MeeleFighter` 加入 `enemy.TargetsInRange`
  - `OnTriggerExit`：移除；若退出的是当前 Target -> `ForceDisengage()`
  - Gizmos：显示感知半径 + FOV 扇形
- **挂载位置**：敌人子物体（SphereCollider）。

---

## `Enemy/States/IdleState.cs`
- **作用**：待机状态；停止移动并尝试在 FOV 内获取目标。
- **关键逻辑**：
  - 服务器：`TryAcquireTarget()` 成功 -> 进入 `CombatMovement`

## `Enemy/States/CombatMovementState.cs`
- **作用**：战斗移动状态（追击目标/回到槽位/可选绕圈）。
- **关键逻辑**：
  - 有 `StandOrder` 时优先追 `StandWorldPos`（回槽位）
  - 距离过远/被推离站位则进入 Chase
  - `FaceTarget()` 手动朝向（关闭 agent 自动旋转）
  - `TrySetDestination()` 做了 repath 节流（repathInterval + minDelta）
  - 目标丢失：`loseTargetGraceTime` 后退出回 Idle

## `Enemy/States/AttackState.cs`
- **作用**：攻击状态（拿到 token 的敌人进入 Attack）。
- **关键逻辑**：
  - 走近到 `attackDistance` 后停止 agent  
  - 开启 root motion（仅服务器）  
  - 调用 `enemy.Fighter.TryToAttack(enemy.Target)` 执行权威攻击/连击  
  - 攻击结束 -> 切 `RetreatAfterAttack`

## `Enemy/States/RetreatAfterAttackState.cs`
- **作用**：攻击后后撤（固定距离），保持面向目标。
- **关键逻辑**：
  - 禁用 agent 自动旋转，手动 `RotateTowards` 面向目标
  - `NavAgent.Move()` 沿远离目标方向后撤 `distanceToRetreat`

## `Enemy/States/GettingHitState.cs`
- **作用**：受击硬直（stun）。
- **关键逻辑**：
  - 停止 NavAgent
  - 监听 `Fighter.OnHitComplete`，延迟 `stunnTime` 后回到 CombatMovement

## `Enemy/States/DeadState.cs`
- **作用**：死亡收尾。
- **关键逻辑**：
  - 停用 VisionSensor，移出 EnemyManager 组
  - 禁用 NavAgent/CharacterController
  - `ServerNotifyDead()` RPC 通知客户端也做相同禁用

---

# 3) Networking（Tick 同步 + 预测 + 回滚重放）

## `Networking/NetTickSystem.cs`
- **作用**：统一 Tick 时钟（ClientTick/ServerTick），保证模拟步长固定一致。
- **关键点**：
  - 使用 `NetworkTime.time` 计算“应该到达的 tick”，循环补齐触发 tick 事件
  - `OnServerTick`：服务器权威推进  
  - `OnClientTick`：客户端采集输入/预测推进  
  - `interpolationDelayTicks`：远端渲染的滞后 tick（renderTick 用于吸收 jitter）

---

## `Networking/NetTypes.cs`
- **作用**：网络数据结构定义（全部 struct，减少 GC）。
- **内容**：
  - `PlayerInputCmd`：输入命令（tick + moveDir + aimDir + buttons）
  - `NetSnapshot`：服务器快照（tick + position + rotation + velocity）
  - `MotorState`：客户端预测缓存（用于 reconcile 回滚重放）

---

## `Networking/PlayerMotorNet.cs`
- **作用**：可重放的 Tick 驱动移动模拟器（CharacterController 运动学）。
- **关键能力**：
  - `Simulate(cmd, dt)`：推进 rotation、水平速度、重力/跳跃、位置移动
  - `GetState/SetState`：打包/恢复状态（回滚重放核心）
- **挂载位置**：玩家网络 Prefab（联机模式下替代离线 PlayerController）。

---

## `Networking/NetPlayer.cs`
- **作用**：Mirror Tick 网络核心：输入上报、客户端预测、服务器权威、快照广播、reconcile 回滚重放、远端插值。
- **客户端（owned）**：
  - `HandleClientTick`：构建 `cmd(predTick)` -> `CmdSendInput` -> 本地 `Simulate` -> 缓存输入/状态
  - `GetLeadTicks`：根据 RTT 估算 one-way delay，将输入发到未来 tick，减少服务器缺输入
- **服务器（authority）**：
  - `HandleServerTick`：按 tick 消费输入 buffer（缺失用 lastCmd 兜底）-> `Simulate` -> 按频率发快照 `RpcReceiveSnapshot`
- **客户端对齐（reconcile）**：
  - 对比 `predictedState[tick]` 和 `authSnapshot[tick]`  
  - 误差超阈值：回滚到 tick=T 权威状态 -> 从 **T+1 重放到 currentTick**
  - `maxReplayTicks`：限制最大重放长度，防止极端延迟导致卡死
- **远端（non-owned）**：
  - 使用 `SnapshotInterpolator` 按 `renderTick = latestTick - interpolationDelayTicks` 插值采样并 `SetState`
- **挂载位置**：玩家网络 Prefab（与 PlayerMotorNet 配套）。

---

## `Networking/SnapshotInterpolator.cs`
- **作用**：快照缓冲 + 插值采样器（用于 non-owned 远端平滑显示）。
- **特性**：
  - 支持乱序插入、覆盖同 tick 快照、固定容量裁剪
  - 支持整数 tick 采样与浮点 tick（带 alpha）插值采样

---

## `Networking/NetPlayerAnimator.cs`
- **作用**：把 `PlayerMotorNet.Velocity` 转换为 Animator 参数，驱动 BlendTree，并加入 damping 防抖。
- **行为**：
  - 非战斗：用 speed(0~1) 驱动 forwardSpeed
  - 战斗：用 velocity 在 local space 的投影驱动 forward/strafe（可 clamp 到 [-1,1]）
  - `dampTime`：平滑过渡，吸收网络 jitter/插值噪声
- **挂载位置**：玩家网络 Prefab（Animator 驱动层）。

---

## `Networking/NetDebugHUD.cs`
- **作用**：运行时网络调试 UI（OnGUI）。
- **显示内容**：
  - FPS、RTT
  - ClientTick/ServerTick
  - 快照接收/估算丢失、回滚次数、最近误差、最近重放 tick 数

---

## `Networking/ScaledNetworkManagerHUD.cs`
- **作用**：缩放版 NetworkManager HUD（更适合演示/录屏）。
- **功能**：
  - 一键启动 Host/Client/Server
  - 支持编辑 networkAddress 与端口
  - UI 支持缩放与偏移

---

# 4) Player（玩家本地表现 / 离线控制 / IK）

## `Player/CameraController.cs`
- **作用**：第三人称相机控制（绕角色旋转、缩放距离、偏移 framing）。
- **联机适配**：
  - 自动绑定 `NetworkClient.localPlayer` 作为 followTarget
- **额外输出**：
  - `PlanarRotation`：给移动输入做相机朝向对齐（世界方向移动）

---

## `Player/PlayerController.cs`（离线用）
- **作用**：离线版本的玩家移动与动画驱动（CharacterController + Animator）。
- **联机处理**：
  - 当检测到 `NetworkClient/NetworkServer active` 时直接 `enabled=false`
  - 联机模式下由 `NetPlayer + PlayerMotorNet + NetPlayerAnimator` 接管
- **战斗模式**：
  - CombatMode 下移动减速、朝向锁定目标，并驱动 forward/strafe 参数

---


# 5) 挂载关系

- **玩家 Prefab（联机）**
  - `NetworkIdentity`, `NetPlayer`, `PlayerMotorNet`, `NetPlayerAnimator`, `CombatController`, `MeeleFighter`, `Animator`, `CharacterController`
- **敌人 Prefab**
  - `NetworkIdentity`, `EnemyController`, `MeeleFighter`, `NavMeshAgent`, `Animator`
  - 子物体：`VisionSensor`（SphereCollider trigger）
  - 状态组件：`IdleState/CombatMovementState/AttackState/...`
- **场景全局对象**
  - `NetTickSystem`（Tick 时钟）
  - `EnemyManager`（围攻调度）
  - `NetworkManager + ScaledNetworkManagerHUD`
  - `NetDebugHUD`（可选）
---
