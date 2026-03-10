using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Combat System/Create a new attack")]
public class AttackData : ScriptableObject
{
    [field: SerializeField] public string AnimName { get; private set; }
    [field: SerializeField] public AttackHitbox HitboxToUse { get; private set; }
    [field: SerializeField] public float ImpactStartTime { get; private set; }
    [field: SerializeField] public float ImpactEndTime { get; private set; }
    
    [field: Header("Move To Target")]
    [field: SerializeField] public bool MoveToTarget { get; private set; }
    [field: SerializeField] public float DistanceFromTarget { get; private set; } = 1f;
    [field: SerializeField] public float MaxMoveDistance { get; private set; } = 3f;
    [field: SerializeField] public float MoveStartTime { get; private set; } = 0f;
    [field: SerializeField] public float MoveEndTime { get; private set; } = 1f;

}

public enum AttackHitbox { LeftHand, RightHand, LeftFoot, RightFoot, Sword }
