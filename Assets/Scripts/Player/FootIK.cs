using UnityEngine;

[RequireComponent(typeof(Animator))]
public class FootIK : MonoBehaviour
{
    [Header("Ground")]
    public LayerMask groundMask = ~0;
    public float rayStartHeight = 0.5f;     // 射线起点：脚骨骼上方多高
    public float raycastDistance = 1.2f;    // 射线长度
    public float footOffset = 0.02f;        // 脚底离地偏移，避免穿插

    [Header("Weights")]
    [Range(0, 1)] public float ikWeight = 1.0f;        // 总体IK权重

    [Header("Smoothing")]
    public float footPosLerp = 20f;
    public float footRotLerp = 20f;
    public float pelvisLerp = 10f;

    [Header("Pelvis")]
    public float pelvisUpDownLimit = 0.25f;            // 骨盆上下最大修正，避免抖太大

    [Header("Animator Param Names")]
    public string leftFootParam = "LeftFootIK";
    public string rightFootParam = "RightFootIK";

    Animator anim;

    Vector3 leftFootIKPos, rightFootIKPos;
    Quaternion leftFootIKRot, rightFootIKRot;

    bool leftHit, rightHit;

    float lastPelvisOffset;
    Vector3 lastLeftPos, lastRightPos;
    Quaternion lastLeftRot, lastRightRot;

    bool leftInited, rightInited;

    void Awake()
    {
        anim = GetComponent<Animator>();
        leftInited = rightInited = false;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (anim == null) return;

        // 读取动画曲线驱动的 Animator 参数（0~1）
        float curveL = Mathf.Clamp01(anim.GetFloat(leftFootParam));
        float curveR = Mathf.Clamp01(anim.GetFloat(rightFootParam));

        // 每只脚独立 IK 权重：总权重 * 曲线权重
        float wL = ikWeight * curveL;
        float wR = ikWeight * curveR;

        // 解算脚的目标点（Raycast）
        leftHit = SolveFoot(HumanBodyBones.LeftFoot, ref leftFootIKPos, ref leftFootIKRot);
        rightHit = SolveFoot(HumanBodyBones.RightFoot, ref rightFootIKPos, ref rightFootIKRot);

        // 没打到地面就不要硬贴（避免脚飞起来/悬空时乱拉）
        if (!leftHit) wL = 0f;
        if (!rightHit) wR = 0f;

        // 骨盆高度修正：只参考“有效支撑脚”（权重>0 且 hit）
        ApplyPelvis(wL, wR);

        // 应用 IK（注意：这里修掉了你原来 LeftFoot 重复调用的 bug）
        ApplyFootIK(AvatarIKGoal.LeftFoot, wL, ref lastLeftPos, ref lastLeftRot, ref leftInited, leftFootIKPos, leftFootIKRot);
        ApplyFootIK(AvatarIKGoal.RightFoot, wR, ref lastRightPos, ref lastRightRot, ref rightInited, rightFootIKPos, rightFootIKRot);
    }

    bool SolveFoot(HumanBodyBones bone, ref Vector3 outPos, ref Quaternion outRot)
    {
        Transform footT = anim.GetBoneTransform(bone);
        if (footT == null) return false;

        Vector3 origin = footT.position + Vector3.up * rayStartHeight;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, raycastDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            outPos = hit.point + hit.normal * footOffset;

            // 旋转：脚底 up 对齐法线，同时尽量保留“脚尖朝向”（yaw）来自动画
            Vector3 footForward = Vector3.ProjectOnPlane(transform.forward, hit.normal).normalized;
            if (footForward.sqrMagnitude < 1e-6f)
                footForward = Vector3.ProjectOnPlane(footT.forward, hit.normal).normalized;

            outRot = Quaternion.LookRotation(footForward, hit.normal);
            return true;
        }

        // 没打到地面：返回 false（位置旋转给动画值，但会在上层把权重置 0）
        outPos = footT.position;
        outRot = footT.rotation;
        return false;
    }

    void ApplyFootIK(
        AvatarIKGoal goal, float w,
        ref Vector3 lastPos, ref Quaternion lastRot, ref bool inited,
        Vector3 targetPos, Quaternion targetRot)
    {
        // 权重为 0 时可以直接不设（也可以设权重0；这里保持明确）
        anim.SetIKPositionWeight(goal, w);
        anim.SetIKRotationWeight(goal, w);

        if (w <= 0f) return;

        // 平滑，避免脚抖（用 bool 初始化更安全，避免 targetPos 恰好等于 Vector3.zero 的误判）
        if (!inited)
        {
            lastPos = targetPos;
            lastRot = targetRot;
            inited = true;
        }
        else
        {
            lastPos = Vector3.Lerp(lastPos, targetPos, Time.deltaTime * footPosLerp);
            lastRot = Quaternion.Slerp(lastRot, targetRot, Time.deltaTime * footRotLerp);
        }

        anim.SetIKPosition(goal, lastPos);
        anim.SetIKRotation(goal, lastRot);
    }

    void ApplyPelvis(float wL, float wR)
    {
        // 这帧动画原始 bodyPosition（不要在已有偏移基础上再叠加）
        Vector3 baseBodyPos = anim.bodyPosition;

        Transform lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rf = anim.GetBoneTransform(HumanBodyBones.RightFoot);
        if (lf == null || rf == null)
        {
            // 没脚骨，直接归零
            lastPelvisOffset = Mathf.Lerp(lastPelvisOffset, 0f, Time.deltaTime * pelvisLerp);
            anim.bodyPosition = new Vector3(baseBodyPos.x, baseBodyPos.y + lastPelvisOffset, baseBodyPos.z);
            return;
        }

        // 动画脚骨位置
        Vector3 leftFootAnimPos = lf.position;
        Vector3 rightFootAnimPos = rf.position;

        // 目标脚位置（IK）
        float leftOffset = leftFootIKPos.y - leftFootAnimPos.y;
        float rightOffset = rightFootIKPos.y - rightFootAnimPos.y;

        // 只让“有效支撑脚”参与 pelvis 计算：权重>0 且 Raycast hit
        bool leftValid = (wL > 0.001f) && leftHit;
        bool rightValid = (wR > 0.001f) && rightHit;

        float targetPelvisOffset;

        if (leftValid && rightValid)
        {
            // 两脚都有效：取更低的那个，避免踩台阶时骨盆被另一脚拉过头
            targetPelvisOffset = Mathf.Min(leftOffset, rightOffset);
        }
        else if (leftValid)
        {
            targetPelvisOffset = leftOffset;
        }
        else if (rightValid)
        {
            targetPelvisOffset = rightOffset;
        }
        else
        {
            // 两脚都不有效（例如跳跃/腾空/没命中地面）：不做骨盆修正
            targetPelvisOffset = 0f;
        }

        targetPelvisOffset = Mathf.Clamp(targetPelvisOffset, -pelvisUpDownLimit, pelvisUpDownLimit);
        lastPelvisOffset = Mathf.Lerp(lastPelvisOffset, targetPelvisOffset, Time.deltaTime * pelvisLerp);

        anim.bodyPosition = new Vector3(baseBodyPos.x, baseBodyPos.y + lastPelvisOffset, baseBodyPos.z);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(leftFootIKPos, 0.05f);
        Gizmos.DrawSphere(rightFootIKPos, 0.05f);
    }
#endif
}
