using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Mirror;

public class EnemyManager : MonoBehaviour
{
    // -------------------------
    // 配置项（保留你原来的）
    // -------------------------
    [Header("Battle Circle Slots")]
    [SerializeField] int outerSlotCount = 12;
    [SerializeField] float outerRadius = 3.2f;

    [SerializeField] int innerSlotCount = 6;
    [SerializeField] float innerRadius = 2.2f;

    [Tooltip("内圈等待人数（建议 >= maxAttackers）")]
    [SerializeField] int innerStandbyCount = 3;

    [SerializeField] float slotUpdateInterval = 0.35f;

    [Header("Ring Rotation")]
    [SerializeField] float rotateSpeedDeg = 10f;
    [SerializeField] Vector2 rotateDirChangeRange = new Vector2(2f, 4f);

    [Header("Rotation Smoothing (Anti-Jitter)")]
    [SerializeField] float rotateSpeedSmooth = 6f;

    [Header("Slot Lock (Anti-Jitter)")]
    [SerializeField] float slotLockTime = 1.2f;
    [SerializeField] float standPosUpdateMinDelta = 0.25f;

    [Header("Attack Token")]
    [SerializeField] int maxAttackers = 2;
    [SerializeField] Vector2 auctionIntervalRange = new Vector2(0.8f, 2.0f);
    [SerializeField] float tokenMaxHoldTime = 5f;
    [SerializeField] bool grantTokenImmediatelyEnterAttack = true;

    [Header("Bid Scoring (Auction Evaluators)")]
    [SerializeField] float timerScoreSaturate = 4f;
    [SerializeField] float distanceTolerance = 2.0f;
    [SerializeField] float behindPenalty = 0.35f;
    [SerializeField] float pathUnreachableMultiplier = 0.2f;

    [Header("Density Score (站位分散评估)")]
    [SerializeField] bool useDensityScore = true;
    [SerializeField] int densityWindowBins = 1;
    [SerializeField] int densitySaturateCount = 4;
    [SerializeField] float innerBonusForToken = 0.12f;

    [Header("Score Weights")]
    [SerializeField] float wTimer = 0.40f;
    [SerializeField] float wDistance = 0.30f;
    [SerializeField] float wFront = 0.20f;
    [SerializeField] float wPath = 0.10f;
    [SerializeField] float wDensity = 0.20f;

    [Header("Slot Selection (稀疏优先选槽)")]
    [SerializeField] int slotSearchRange = 4;

    [Header("Avoidance")]
    [SerializeField] int attackerAvoidancePriority = 10;
    [SerializeField] int innerAvoidancePriority = 35;
    [SerializeField] int outerAvoidancePriority = 60;
    [SerializeField] float yieldRadius = 1.2f;
    [SerializeField] float yieldSpeed = 1.0f;

    [Header("Client Local Lock-On (仅本地UI用)")]
    [SerializeField] float localLockOnSearchRadius = 12f;

    [Header("Debug Visual")]
    [SerializeField] bool drawGizmos = true;
    [SerializeField] bool drawDensityHeat = true;
    [SerializeField] float gizmoSlotSize = 0.15f;
    [SerializeField] float tokenGizmoHeight = 2.0f;
    [SerializeField] float innerGizmoHeight = 1.6f;

    // -------------------------
    // 内部结构
    // -------------------------
    struct Slot
    {
        public int id;
        public float baseAngleDeg;
        public Vector3 worldPos;
        public EnemyController occupant;
    }

    class CombatGroup
    {
        public MeeleFighter player;                 // group 的中心玩家
        public readonly List<EnemyController> enemies = new List<EnemyController>();

        public readonly List<Slot> outerSlots = new List<Slot>();
        public readonly List<Slot> innerSlots = new List<Slot>();

        public int[] outerBinCount;
        public int[] innerBinCount;

        public readonly Dictionary<EnemyController, float> densityOuterCache = new Dictionary<EnemyController, float>();
        public readonly Dictionary<EnemyController, float> densityInnerCache = new Dictionary<EnemyController, float>();

        public readonly Dictionary<EnemyController, float> tokenExpireTime = new Dictionary<EnemyController, float>();

        public readonly Dictionary<EnemyController, int> innerSlotOf = new Dictionary<EnemyController, int>();
        public readonly Dictionary<EnemyController, int> outerSlotOf = new Dictionary<EnemyController, int>();
        public readonly Dictionary<EnemyController, float> slotLockUntil = new Dictionary<EnemyController, float>();

        public float slotTimer;
        public float ringAngleOffset;

        public float rotateSpeedCur;
        public float rotateSpeedTarget;
        public float rotateDirTimer;

        public float auctionTimer;
    }

    public static EnemyManager i { get; private set; }

    // group 管理：每个玩家一套圈
    readonly Dictionary<MeeleFighter, CombatGroup> groups = new Dictionary<MeeleFighter, CombatGroup>();

    // 反向映射：ForceDisengage 时 Target 已经为空，Remove 需要靠它找到原 group
    readonly Dictionary<EnemyController, MeeleFighter> enemyToGroupKey = new Dictionary<EnemyController, MeeleFighter>();

    bool IsNetworking => NetworkClient.active || NetworkServer.active;
    bool IsAuthoritative => !IsNetworking || NetworkServer.active; // 离线 or 服务器

    void Awake() => i = this;

    void Start()
    {
        // 一些参数防呆
        maxAttackers = Mathf.Clamp(maxAttackers, 1, 4);
        innerStandbyCount = Mathf.Clamp(innerStandbyCount, 0, 32);
        timerScoreSaturate = Mathf.Max(0.1f, timerScoreSaturate);
        distanceTolerance = Mathf.Max(0.1f, distanceTolerance);
        densityWindowBins = Mathf.Clamp(densityWindowBins, 0, 4);
        densitySaturateCount = Mathf.Max(1, densitySaturateCount);
        slotSearchRange = Mathf.Clamp(slotSearchRange, 1, 12);
        rotateSpeedSmooth = Mathf.Max(0.1f, rotateSpeedSmooth);
        slotLockTime = Mathf.Max(0f, slotLockTime);
        standPosUpdateMinDelta = Mathf.Max(0f, standPosUpdateMinDelta);

        outerSlotCount = Mathf.Max(3, outerSlotCount);
        innerSlotCount = Mathf.Max(3, innerSlotCount);
    }

    // -------------------------
    // EnemyController 调用接口（只在服务器/离线有效）
    // -------------------------
    public void AddEnemyInRange(EnemyController enemy)
    {
        if (enemy == null) return;
        if (!IsAuthoritative) return;

        // enemy.Target 作为 groupKey（多人时：围着被锁定的玩家转圈）
        var key = enemy.Target;
        if (key == null) return;

        // 若之前在别的 group，先移走
        if (enemyToGroupKey.TryGetValue(enemy, out var oldKey) && oldKey != null && oldKey != key)
        {
            if (groups.TryGetValue(oldKey, out var oldG))
            {
                oldG.enemies.Remove(enemy);
                CleanupEnemyFromGroup(oldG, enemy);
            }
        }

        var g = GetOrCreateGroup(key);

        if (!g.enemies.Contains(enemy))
            g.enemies.Add(enemy);

        enemyToGroupKey[enemy] = key;
    }

    public void RemoveEnemyInRange(EnemyController enemy)
    {
        if (enemy == null) return;
        if (!IsAuthoritative) return;

        if (enemyToGroupKey.TryGetValue(enemy, out var key) && key != null)
        {
            if (groups.TryGetValue(key, out var g))
            {
                g.enemies.Remove(enemy);
                CleanupEnemyFromGroup(g, enemy);
            }
            enemyToGroupKey.Remove(enemy);
        }
    }

    CombatGroup GetOrCreateGroup(MeeleFighter playerKey)
    {
        if (groups.TryGetValue(playerKey, out var g)) return g;

        g = new CombatGroup();
        g.player = playerKey;

        InitSlotsAndBins(g);

        // rotation target 初始化
        g.rotateSpeedTarget = (Random.Range(0, 2) == 0 ? 1f : -1f) * rotateSpeedDeg;
        g.rotateSpeedCur = g.rotateSpeedTarget;
        g.rotateDirTimer = Random.Range(rotateDirChangeRange.x, rotateDirChangeRange.y);

        g.auctionTimer = Random.Range(auctionIntervalRange.x, auctionIntervalRange.y);
        g.slotTimer = slotUpdateInterval;

        groups[playerKey] = g;
        return g;
    }

    void InitSlotsAndBins(CombatGroup g)
    {
        g.outerSlots.Clear();
        g.innerSlots.Clear();

        float outerStep = 360f / outerSlotCount;
        for (int k = 0; k < outerSlotCount; k++)
            g.outerSlots.Add(new Slot { id = k, baseAngleDeg = k * outerStep, worldPos = Vector3.zero, occupant = null });

        float innerStep = 360f / innerSlotCount;
        for (int k = 0; k < innerSlotCount; k++)
            g.innerSlots.Add(new Slot { id = k, baseAngleDeg = k * innerStep, worldPos = Vector3.zero, occupant = null });

        g.outerBinCount = new int[outerSlotCount];
        g.innerBinCount = new int[innerSlotCount];
    }

    void CleanupEnemyFromGroup(CombatGroup g, EnemyController enemy)
    {
        ReleaseAttackToken(g, enemy);

        g.innerSlotOf.Remove(enemy);
        g.outerSlotOf.Remove(enemy);
        g.slotLockUntil.Remove(enemy);

        g.densityOuterCache.Remove(enemy);
        g.densityInnerCache.Remove(enemy);
    }

    // -------------------------
    // 主循环：服务器权威更新
    // -------------------------
    void Update()
    {
        if (!IsAuthoritative) return;

        // 服务器/离线：更新所有 group
        if (groups.Count == 0) return;

        var keys = groups.Keys.ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (k == null)
            {
                groups.Remove(k);
                continue;
            }

            var g = groups[k];
            if (g == null || g.player == null)
            {
                groups.Remove(k);
                continue;
            }

            // 清理无效敌人/目标改变/死亡
            g.enemies.RemoveAll(e => e == null || e.IsInState(EnemyStates.Dead) || e.Target == null || e.Target != g.player);

            // 同步清理反向映射
            //（如果 enemy 被 Destroy，RemoveEnemyInRange 不一定会被调用）
            var deadEnemies = enemyToGroupKey.Where(p => p.Key == null).Select(p => p.Key).ToList();
            foreach (var de in deadEnemies) enemyToGroupKey.Remove(de);

            if (g.enemies.Count == 0)
            {
                // group 空了就移除（省性能）
                groups.Remove(k);
                continue;
            }

            // 1) 槽位更新与分配（低频）
            g.slotTimer -= Time.deltaTime;
            if (g.slotTimer <= 0f)
            {
                g.slotTimer = slotUpdateInterval;

                UpdateSlotsWorldPos(g);

                if (useDensityScore)
                    RebuildDensityCaches(g);

                AssignInnerThenOuterSlots_Sticky(g);
            }

            // 2) token（拍卖/分配/回收）
            CleanupTokens(g);
            RunAuctionAndGrantTokens_ByScoring(g);

            // 3) 避让
            UpdateAvoidanceAndYield(g);
        }
    }

    // ============================================================
    // Slot System (Rotation + Sticky Assign) - per group
    // ============================================================

    void UpdateSlotsWorldPos(CombatGroup g)
    {
        var playerTr = g.player.transform;
        var center = playerTr.position;
        center.y = 0;

        // 旋转目标速度（定期改变方向/速度）
        g.rotateDirTimer -= Time.deltaTime;
        if (g.rotateDirTimer <= 0f)
        {
            g.rotateSpeedTarget = (Random.Range(0, 2) == 0 ? 1f : -1f) * rotateSpeedDeg;
            g.rotateDirTimer = Random.Range(rotateDirChangeRange.x, rotateDirChangeRange.y);
        }

        // 平滑过渡到目标速度
        g.rotateSpeedCur = LerpExp(g.rotateSpeedCur, g.rotateSpeedTarget, rotateSpeedSmooth, Time.deltaTime);
        g.ringAngleOffset += g.rotateSpeedCur * Time.deltaTime;

        // 更新槽位世界坐标（occupant 每轮会重新填充）
        for (int i = 0; i < g.outerSlots.Count; i++)
        {
            var s = g.outerSlots[i];
            float ang = (s.baseAngleDeg + g.ringAngleOffset) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            s.worldPos = center + dir * outerRadius;
            s.occupant = null;
            g.outerSlots[i] = s;
        }

        for (int i = 0; i < g.innerSlots.Count; i++)
        {
            var s = g.innerSlots[i];
            float ang = (s.baseAngleDeg + g.ringAngleOffset) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            s.worldPos = center + dir * innerRadius;
            s.occupant = null;
            g.innerSlots[i] = s;
        }
    }

    static float LerpExp(float current, float target, float lambda, float dt)
    {
        float t = 1f - Mathf.Exp(-lambda * dt);
        return Mathf.Lerp(current, target, t);
    }

    void AssignInnerThenOuterSlots_Sticky(CombatGroup g)
    {
        // 在范围内、有 Target、处于 CombatMovement、没有 AttackToken
        var pool = g.enemies
            .Where(e => e != null
                        && e.Target != null
                        && e.IsInState(EnemyStates.CombatMovement)
                        && !e.HasAttackToken)
            .ToList();

        if (pool.Count == 0) return;

        pool.Sort((a, b) =>
        {
            float sb = EvaluateBidScore(g, b, forToken: false);
            float sa = EvaluateBidScore(g, a, forToken: false);
            return sb.CompareTo(sa);
        });

        int innerCount = Mathf.Min(innerStandbyCount, g.innerSlots.Count, pool.Count);
        var innerSet = new HashSet<EnemyController>();
        for (int i = 0; i < innerCount; i++)
            innerSet.Add(pool[i]);

        var outerSet = new HashSet<EnemyController>(pool);
        outerSet.ExceptWith(innerSet);

        AssignSlotsSticky(g, g.innerSlots, innerSet, g.innerSlotOf, EnemyController.EnemyCombatRole.StandbyInner, g.innerBinCount);
        AssignSlotsSticky(g, g.outerSlots, outerSet, g.outerSlotOf, EnemyController.EnemyCombatRole.StandbyOuter, g.outerBinCount);

        CleanupSlotMap(g.innerSlotOf, innerSet);
        CleanupSlotMap(g.outerSlotOf, outerSet);
    }

    void CleanupSlotMap(Dictionary<EnemyController, int> map, HashSet<EnemyController> aliveSet)
    {
        if (map.Count == 0) return;
        var keys = map.Keys.ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (k == null || !aliveSet.Contains(k))
                map.Remove(k);
        }
    }

    void AssignSlotsSticky(
        CombatGroup g,
        List<Slot> slots,
        HashSet<EnemyController> set,
        Dictionary<EnemyController, int> slotOf,
        EnemyController.EnemyCombatRole role,
        int[] densityBins)
    {
        if (set == null || set.Count == 0) return;

        var center = g.player.transform.position;
        center.y = 0;

        float step = 360f / Mathf.Max(3, slots.Count);

        var list = set.ToList();
        list.Sort((a, b) =>
        {
            float aa = GetAngle01(a.transform.position, center);
            float bb = GetAngle01(b.transform.position, center);
            return aa.CompareTo(bb);
        });

        int[] assigned = new int[slots.Count];

        // 1) 优先占用旧槽（sticky）
        foreach (var enemy in list)
        {
            if (enemy == null) continue;

            if (slotOf.TryGetValue(enemy, out int prevId))
            {
                if (prevId >= 0 && prevId < slots.Count && slots[prevId].occupant == null)
                {
                    var s = slots[prevId];
                    s.occupant = enemy;
                    slots[prevId] = s;
                    assigned[prevId]++;

                    PushStandOrderIfNeeded(enemy, s.id, s.worldPos, role);
                }
            }
        }

        // 2) 未占到槽的人再分配（尊重锁定期）
        foreach (var enemy in list)
        {
            if (enemy == null) continue;

            if (slotOf.TryGetValue(enemy, out int id) && id >= 0 && id < slots.Count && slots[id].occupant == enemy)
                continue;

            if (g.slotLockUntil.TryGetValue(enemy, out float until) && Time.time < until)
                continue;

            Vector3 v = enemy.transform.position - center;
            v.y = 0;

            float ang = Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg;
            if (ang < 0) ang += 360f;

            float localAng = ang - g.ringAngleOffset;
            while (localAng < 0) localAng += 360f;
            while (localAng >= 360f) localAng -= 360f;

            int desired = Mathf.RoundToInt(localAng / step) % slots.Count;

            // ★关键改动：用“密度（binCount）”来选槽位，优先去低密度方向补位
            int slotIndex = FindBestFreeSlotByDensity(slots, desired, assigned, densityBins, densityWindowBins, slotSearchRange);
            if (slotIndex < 0) continue;

            var s = slots[slotIndex];
            s.occupant = enemy;
            slots[slotIndex] = s;
            assigned[slotIndex]++;

            slotOf[enemy] = s.id;
            g.slotLockUntil[enemy] = Time.time + slotLockTime;

            PushStandOrderIfNeeded(enemy, s.id, s.worldPos, role);
        }
    }

    void PushStandOrderIfNeeded(EnemyController enemy, int slotId, Vector3 worldPos, EnemyController.EnemyCombatRole role)
    {
        if (enemy == null) return;

        if (enemy.HasStandOrder)
        {
            float d = Vector3.Distance(enemy.StandWorldPos, worldPos);
            if (d < standPosUpdateMinDelta && enemy.Role == role)
                return;
        }

        enemy.SetStandOrder(slotId, worldPos, role);
    }

    int FindBestFreeSlotByPressure(List<Slot> slots, int desired, int[] assigned, int window, int searchRange)
    {
        if (slots == null || slots.Count == 0) return -1;
        desired = Mathf.Clamp(desired, 0, slots.Count - 1);

        int bestIdx = -1;
        int bestPressure = int.MaxValue;
        int bestOffsetAbs = int.MaxValue;

        for (int off = 0; off <= searchRange; off++)
        {
            int a = (desired + off) % slots.Count;
            int b = (desired - off + slots.Count) % slots.Count;

            if (slots[a].occupant == null)
            {
                int p = GetLocalPressure(assigned, a, window);
                int oa = Mathf.Abs(off);
                if (p < bestPressure || (p == bestPressure && oa < bestOffsetAbs))
                {
                    bestPressure = p;
                    bestOffsetAbs = oa;
                    bestIdx = a;
                }
            }

            if (b != a && slots[b].occupant == null)
            {
                int p = GetLocalPressure(assigned, b, window);
                int ob = Mathf.Abs(off);
                if (p < bestPressure || (p == bestPressure && ob < bestOffsetAbs))
                {
                    bestPressure = p;
                    bestOffsetAbs = ob;
                    bestIdx = b;
                }
            }
        }

        if (bestIdx < 0)
        {
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].occupant == null) return i;
        }

        return bestIdx;
    }
    int FindBestFreeSlotByDensity(
        List<Slot> slots,
        int desired,
        int[] assigned,
        int[] bins,
        int binWindow,
        int searchRange)
    {
        if (slots == null || slots.Count == 0) return -1;

        int n = slots.Count;
        desired = ((desired % n) + n) % n;

        bool useBins = useDensityScore && bins != null && bins.Length == n;

        int bestIdx = -1;
        int bestBinLocal = int.MaxValue;       // 越小越好：越不挤
        int bestAssignedLocal = int.MaxValue;  // 次级：本轮分配压力
        int bestOff = int.MaxValue;            // 再次：离 desired 越近越好

        for (int off = 0; off <= searchRange; off++)
        {
            int a = (desired + off) % n;
            int b = (desired - off + n) % n;

            // 检查 a
            if (slots[a].occupant == null)
            {
                int binLocal = useBins ? GetLocalBinCount(bins, a, binWindow)
                                       : GetLocalPressure(assigned, a, binWindow); // 没 bins 就退化为旧逻辑
                int assignedLocal = GetLocalPressure(assigned, a, 0);

                if (binLocal < bestBinLocal ||
                   (binLocal == bestBinLocal && (assignedLocal < bestAssignedLocal ||
                   (assignedLocal == bestAssignedLocal && off < bestOff))))
                {
                    bestIdx = a;
                    bestBinLocal = binLocal;
                    bestAssignedLocal = assignedLocal;
                    bestOff = off;
                }
            }

            // 检查 b（避免重复）
            if (b != a && slots[b].occupant == null)
            {
                int binLocal = useBins ? GetLocalBinCount(bins, b, binWindow)
                                       : GetLocalPressure(assigned, b, binWindow);
                int assignedLocal = GetLocalPressure(assigned, b, 0);

                if (binLocal < bestBinLocal ||
                   (binLocal == bestBinLocal && (assignedLocal < bestAssignedLocal ||
                   (assignedLocal == bestAssignedLocal && off < bestOff))))
                {
                    bestIdx = b;
                    bestBinLocal = binLocal;
                    bestAssignedLocal = assignedLocal;
                    bestOff = off;
                }
            }
        }

        // 兜底：范围内都不行就全局找一个空槽
        if (bestIdx < 0)
        {
            for (int i = 0; i < n; i++)
                if (slots[i].occupant == null) return i;
        }

        return bestIdx;
    }
    int GetLocalPressure(int[] assigned, int idx, int window)
    {
        if (assigned == null || assigned.Length == 0) return 0;
        if (window <= 0) return assigned[idx];

        int n = assigned.Length;
        int sum = 0;
        for (int k = -window; k <= window; k++)
        {
            int j = (idx + k + n) % n;
            sum += assigned[j];
        }
        return sum;
    }

    float GetAngle01(Vector3 pos, Vector3 center)
    {
        Vector3 v = pos - center;
        v.y = 0;
        float ang = Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg;
        if (ang < 0) ang += 360f;
        return ang;
    }

    // ============================================================
    // Density Cache - per group
    // ============================================================

    void RebuildDensityCaches(CombatGroup g)
    {
        if (g.outerBinCount == null || g.outerBinCount.Length != outerSlotCount)
            g.outerBinCount = new int[outerSlotCount];
        if (g.innerBinCount == null || g.innerBinCount.Length != innerSlotCount)
            g.innerBinCount = new int[innerSlotCount];

        for (int i = 0; i < g.outerBinCount.Length; i++) g.outerBinCount[i] = 0;
        for (int i = 0; i < g.innerBinCount.Length; i++) g.innerBinCount[i] = 0;

        Vector3 center = g.player.transform.position;
        center.y = 0;

        for (int i = 0; i < g.enemies.Count; i++)
        {
            var e = g.enemies[i];
            if (e == null) continue;
            if (e.IsInState(EnemyStates.Dead)) continue;
            if (e.Target == null) continue;

            int outerBin = GetBinIndex(g, e.transform.position, center, outerSlotCount);
            int innerBin = GetBinIndex(g, e.transform.position, center, innerSlotCount);

            g.outerBinCount[outerBin]++;
            g.innerBinCount[innerBin]++;
        }

        g.densityOuterCache.Clear();
        g.densityInnerCache.Clear();

        for (int i = 0; i < g.enemies.Count; i++)
        {
            var e = g.enemies[i];
            if (e == null) continue;
            if (e.Target == null) continue;

            int ob = GetBinIndex(g, e.transform.position, center, outerSlotCount);
            int ib = GetBinIndex(g, e.transform.position, center, innerSlotCount);

            int outerLocal = GetLocalBinCount(g.outerBinCount, ob, densityWindowBins);
            int innerLocal = GetLocalBinCount(g.innerBinCount, ib, densityWindowBins);

            g.densityOuterCache[e] = DensityCountToScore(outerLocal);
            g.densityInnerCache[e] = DensityCountToScore(innerLocal);
        }
    }

    int GetBinIndex(CombatGroup g, Vector3 worldPos, Vector3 center, int binCount)
    {
        Vector3 v = worldPos - center;
        v.y = 0;

        float ang = Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg;
        if (ang < 0) ang += 360f;

        float localAng = ang - g.ringAngleOffset;
        while (localAng < 0) localAng += 360f;
        while (localAng >= 360f) localAng -= 360f;

        float step = 360f / Mathf.Max(3, binCount);
        int idx = Mathf.RoundToInt(localAng / step) % binCount;
        return Mathf.Clamp(idx, 0, binCount - 1);
    }

    int GetLocalBinCount(int[] bins, int idx, int window)
    {
        if (bins == null || bins.Length == 0) return 0;
        if (window <= 0) return bins[idx];

        int n = bins.Length;
        int sum = 0;
        for (int k = -window; k <= window; k++)
        {
            int j = (idx + k + n) % n;
            sum += bins[j];
        }
        return sum;
    }

    float DensityCountToScore(int count)
    {
        float t = Mathf.Clamp01((count - 1) / (float)densitySaturateCount);
        return 1f - t;
    }

    float GetDensityScore(CombatGroup g, EnemyController e, bool forToken)
    {
        if (!useDensityScore) return 0.5f;

        if (forToken)
        {
            if (g.densityInnerCache.TryGetValue(e, out var s)) return s;
            return 0.5f;
        }

        if (e.Role == EnemyController.EnemyCombatRole.StandbyInner)
        {
            if (g.densityInnerCache.TryGetValue(e, out var s)) return s;
            return 0.5f;
        }
        else
        {
            if (g.densityOuterCache.TryGetValue(e, out var s)) return s;
            return 0.5f;
        }
    }

    // ============================================================
    // Auction / Token - per group
    // ============================================================

    void RunAuctionAndGrantTokens_ByScoring(CombatGroup g)
    {
        if (maxAttackers <= 0) return;
        if (g.tokenExpireTime.Count >= maxAttackers) return;

        g.auctionTimer -= Time.deltaTime;
        if (g.auctionTimer > 0f) return;
        g.auctionTimer = Random.Range(auctionIntervalRange.x, auctionIntervalRange.y);

        int need = maxAttackers - g.tokenExpireTime.Count;
        if (need <= 0) return;

        var bidders = g.enemies
            .Where(e => e != null
                        && e.Target != null
                        && e.IsInState(EnemyStates.CombatMovement)
                        && !e.HasAttackToken)
            .ToList();

        if (bidders.Count == 0) return;

        bidders.Sort((a, b) =>
        {
            float sb = EvaluateBidScore(g, b, forToken: true);
            float sa = EvaluateBidScore(g, a, forToken: true);
            return sb.CompareTo(sa);
        });

        for (int i = 0; i < bidders.Count && need > 0; i++)
        {
            GrantAttackToken(g, bidders[i]);
            need--;
        }
    }

    void GrantAttackToken(CombatGroup g, EnemyController enemy)
    {
        enemy.GrantAttackToken();
        g.tokenExpireTime[enemy] = Time.time + tokenMaxHoldTime;

        g.innerSlotOf.Remove(enemy);
        g.outerSlotOf.Remove(enemy);
        g.slotLockUntil.Remove(enemy);

        if (grantTokenImmediatelyEnterAttack && enemy.IsInState(EnemyStates.CombatMovement))
            enemy.ChangeState(EnemyStates.Attack);
    }

    void CleanupTokens(CombatGroup g)
    {
        if (g.tokenExpireTime.Count == 0) return;

        var toRemove = new List<EnemyController>();

        foreach (var kv in g.tokenExpireTime)
        {
            var e = kv.Key;
            float expireAt = kv.Value;

            bool remove = false;

            if (e == null) remove = true;
            else if (!e.HasAttackToken) remove = true;
            else if (e.IsInState(EnemyStates.Dead)) remove = true;
            else if (Time.time > expireAt) remove = true;
            else
            {
                if (!e.IsInState(EnemyStates.Attack))
                    remove = true;
            }

            if (remove) toRemove.Add(e);
        }

        for (int i = 0; i < toRemove.Count; i++)
        {
            var e = toRemove[i];
            if (e != null) e.RevokeAttackToken();
            g.tokenExpireTime.Remove(e);
        }
    }

    void ReleaseAttackToken(CombatGroup g, EnemyController enemy)
    {
        if (enemy == null) return;
        enemy.RevokeAttackToken();
        g.tokenExpireTime.Remove(enemy);
    }

    // ============================================================
    // Bid Score Evaluator - per group
    // ============================================================

    float EvaluateBidScore(CombatGroup g, EnemyController e, bool forToken)
    {
        if (e == null || e.Target == null || g.player == null) return -999f;

        Vector3 p = g.player.transform.position; p.y = 0;
        Vector3 ep = e.transform.position; ep.y = 0;

        float timerScore = Mathf.Clamp01(e.CombatMovementTimer / timerScoreSaturate);

        float dist = Vector3.Distance(ep, p);
        float ideal = forToken ? innerRadius :
            (e.Role == EnemyController.EnemyCombatRole.StandbyInner ? innerRadius : outerRadius);

        float distScore = 1f - Mathf.Clamp01(Mathf.Abs(dist - ideal) / distanceTolerance);

        Vector3 pf = g.player.transform.forward; pf.y = 0;
        if (pf.sqrMagnitude < 0.0001f) pf = Vector3.forward;
        pf.Normalize();

        Vector3 dirFromPlayerToEnemy = ep - p;
        if (dirFromPlayerToEnemy.sqrMagnitude < 0.0001f) dirFromPlayerToEnemy = pf;
        dirFromPlayerToEnemy.Normalize();

        float dot = Vector3.Dot(pf, dirFromPlayerToEnemy);
        float frontScore = Mathf.Clamp01((dot + 1f) * 0.5f);
        if (dot < 0f) frontScore *= behindPenalty;

        Vector3 dest = forToken
            ? (p + pf * 0.6f)
            : (e.HasStandOrder ? e.StandWorldPos : ep);

        bool reachable = HasValidPath(e, dest);
        float pathScore = reachable ? 1f : 0f;

        float densityScore = GetDensityScore(g, e, forToken);

        float score =
            wTimer * timerScore +
            wDistance * distScore +
            wFront * frontScore +
            wPath * pathScore +
            wDensity * densityScore;

        if (!reachable) score *= pathUnreachableMultiplier;

        if (forToken && e.Role == EnemyController.EnemyCombatRole.StandbyInner)
            score += innerBonusForToken;

        return score;
    }

    bool HasValidPath(EnemyController e, Vector3 dest)
    {
        if (e == null || e.NavAgent == null || !e.NavAgent.enabled) return false;

        NavMeshPath path = new NavMeshPath();
        bool ok = e.NavAgent.CalculatePath(dest, path);
        if (!ok) return false;

        return path.status == NavMeshPathStatus.PathComplete;
    }

    // ============================================================
    // Avoidance (token/attack -> others yield) - per group
    // ============================================================

    void UpdateAvoidanceAndYield(CombatGroup g)
    {
        for (int i = 0; i < g.enemies.Count; i++)
        {
            var e = g.enemies[i];
            if (e == null || e.NavAgent == null || !e.NavAgent.enabled) continue;

            if (e.HasAttackToken || e.IsInState(EnemyStates.Attack))
                e.NavAgent.avoidancePriority = attackerAvoidancePriority;
            else if (e.Role == EnemyController.EnemyCombatRole.StandbyInner)
                e.NavAgent.avoidancePriority = innerAvoidancePriority;
            else
                e.NavAgent.avoidancePriority = outerAvoidancePriority;
        }

        Vector3 pf = g.player.transform.forward; pf.y = 0;
        if (pf.sqrMagnitude < 0.0001f) pf = Vector3.forward;
        pf.Normalize();

        for (int i = 0; i < g.enemies.Count; i++)
        {
            var attacker = g.enemies[i];
            if (attacker == null) continue;
            if (!(attacker.HasAttackToken || attacker.IsInState(EnemyStates.Attack))) continue;

            Vector3 aPos = attacker.transform.position; aPos.y = 0;

            for (int j = 0; j < g.enemies.Count; j++)
            {
                var other = g.enemies[j];
                if (other == null || other == attacker) continue;
                if (other.NavAgent == null || !other.NavAgent.enabled) continue;
                if (other.HasAttackToken || other.IsInState(EnemyStates.Attack)) continue;

                Vector3 oPos = other.transform.position; oPos.y = 0;
                Vector3 diff = oPos - aPos;
                float dist = diff.magnitude;
                if (dist > yieldRadius || dist < 0.0001f) continue;

                float along = Vector3.Dot(diff, pf);
                Vector3 lateral = diff - pf * along;

                Vector3 pushDir = (lateral.sqrMagnitude < 0.0001f)
                    ? Vector3.Cross(Vector3.up, pf).normalized
                    : lateral.normalized;

                other.NavAgent.Move(pushDir * yieldSpeed * Time.deltaTime);
            }
        }
    }

    // ============================================================
    // Client Local: 锁定查询（不影响网络一致性）
    // ============================================================
    public EnemyController GetClosestEnemyToDirection(Vector3 direction)
    {
        // 这个函数只用于本地 UI 选择目标，不参与服务器协同逻辑
        Transform localPlayer = GetLocalPlayerTransform();
        if (localPlayer == null) return null;

        float minDistance = Mathf.Infinity;
        EnemyController closestEnemy = null;

        var hits = Physics.OverlapSphere(localPlayer.position, localLockOnSearchRadius);
        for (int i = 0; i < hits.Length; i++)
        {
            var enemy = hits[i].GetComponentInParent<EnemyController>();
            if (enemy == null) continue;
            if (enemy.IsInState(EnemyStates.Dead)) continue;

            var vecToEnemy = enemy.transform.position - localPlayer.position;
            vecToEnemy.y = 0;

            float angle = Vector3.Angle(direction, vecToEnemy);
            float distance = vecToEnemy.magnitude * Mathf.Sin(angle * Mathf.Deg2Rad);

            if (distance < minDistance)
            {
                minDistance = distance;
                closestEnemy = enemy;
            }
        }
        return closestEnemy;
    }

    Transform GetLocalPlayerTransform()
    {
        // 联机：用 Mirror 的 localPlayer 找到本机玩家
        if (NetworkClient.active && NetworkClient.localPlayer != null)
            return NetworkClient.localPlayer.transform;

        // 离线：随便找一个 Player（你也可以换成 tag 查找）
        var cc = FindObjectOfType<CombatController>();
        return cc != null ? cc.transform : null;
    }

    // ============================================================
    // Gizmos: 显示所有 group（仅用于调试）
    // ============================================================
    void OnDrawGizmos()
    {

        foreach (var kv in groups)
        {
            var g = kv.Value;
            if (g == null || g.player == null) continue;

            for (int i = 0; i < g.outerSlots.Count; i++)
            {
                var s = g.outerSlots[i];

                if (drawDensityHeat && g.outerBinCount != null && g.outerBinCount.Length == g.outerSlots.Count)
                {
                    int c = GetLocalBinCount(g.outerBinCount, i, densityWindowBins);
                    float t = Mathf.Clamp01((c - 1) / (float)densitySaturateCount);
                    Gizmos.color = Color.Lerp(Color.green, Color.red, t);
                }
                else
                {
                    Gizmos.color = (s.occupant == null) ? Color.green : Color.yellow;
                }

                Gizmos.DrawSphere(s.worldPos + Vector3.up * 0.05f, gizmoSlotSize);
            }

            for (int i = 0; i < g.innerSlots.Count; i++)
            {
                var s = g.innerSlots[i];

                if (drawDensityHeat && g.innerBinCount != null && g.innerBinCount.Length == g.innerSlots.Count)
                {
                    int c = GetLocalBinCount(g.innerBinCount, i, densityWindowBins);
                    float t = Mathf.Clamp01((c - 1) / (float)densitySaturateCount);
                    Gizmos.color = Color.Lerp(new Color(0.2f, 1f, 1f), Color.red, t);
                }
                else
                {
                    Gizmos.color = (s.occupant == null) ? new Color(0.2f, 1f, 1f) : new Color(0.2f, 0.5f, 1f);
                }

                Gizmos.DrawSphere(s.worldPos + Vector3.up * 0.07f, gizmoSlotSize);
            }

            Gizmos.color = Color.red;
            foreach (var tkv in g.tokenExpireTime)
            {
                var e = tkv.Key;
                if (e == null) continue;
                Gizmos.DrawSphere(e.transform.position + Vector3.up * tokenGizmoHeight, gizmoSlotSize * 1.2f);
            }

            Gizmos.color = new Color(0.2f, 0.5f, 1f);
            for (int i = 0; i < g.enemies.Count; i++)
            {
                var e = g.enemies[i];
                if (e == null) continue;
                if (e.HasAttackToken) continue;
                if (e.Role == EnemyController.EnemyCombatRole.StandbyInner)
                    Gizmos.DrawSphere(e.transform.position + Vector3.up * innerGizmoHeight, gizmoSlotSize * 0.9f);
            }
        }
    }
}
