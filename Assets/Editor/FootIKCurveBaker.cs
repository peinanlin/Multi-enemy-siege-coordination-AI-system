#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class FootIKCurveBaker : EditorWindow
{
    [Header("Input")]
    public AnimationClip clip;
    public GameObject humanoidPrefabOrModel;   // 需要带 Animator 且是 Humanoid Avatar

    [Header("Sampling")]
    public float sampleRate = 60f;             // 采样频率（建议 60）
    
    [Header("Contact Detection (relative to groundY)")]
    public float landThreshold = 0.06f;        // 小于此高度(米)认为“可能落地”
    public float liftThreshold = 0.12f;        // 高于此高度(米)认为“抬脚离地”（滞回）
    public float velThreshold = 0.25f;         // 垂直速度阈值 |vy| < 此值 才允许落地（避免误判）
    
    [Header("Smoothing")]
    public float fadeTime = 0.10f;             // 0->1 / 1->0 过渡时间（秒）
    public float epsilon = 0.0025f;            // 压缩曲线用的误差

    [MenuItem("Tools/Foot IK/Foot IK Curve Baker (Method B)")]
    public static void Open()
    {
        GetWindow<FootIKCurveBaker>("Foot IK Curve Baker");
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Bake LeftFootIK / RightFootIK curves into the clip", EditorStyles.boldLabel);

        clip = (AnimationClip)EditorGUILayout.ObjectField("Animation Clip", clip, typeof(AnimationClip), false);
        humanoidPrefabOrModel = (GameObject)EditorGUILayout.ObjectField("Humanoid Prefab/Model", humanoidPrefabOrModel, typeof(GameObject), false);

        EditorGUILayout.Space(8);
        sampleRate = EditorGUILayout.Slider("Sample Rate", sampleRate, 15f, 120f);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Detection (meters)", EditorStyles.boldLabel);
        landThreshold = EditorGUILayout.Slider("Land Threshold", landThreshold, 0.0f, 0.25f);
        liftThreshold = EditorGUILayout.Slider("Lift Threshold", liftThreshold, 0.0f, 0.35f);
        velThreshold  = EditorGUILayout.Slider("Vel Threshold",  velThreshold, 0.0f, 1.0f);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Smoothing", EditorStyles.boldLabel);
        fadeTime = EditorGUILayout.Slider("Fade Time (s)", fadeTime, 0.0f, 0.35f);
        epsilon  = EditorGUILayout.Slider("Compress Epsilon", epsilon, 0.0f, 0.02f);

        EditorGUILayout.Space(12);

        using (new EditorGUI.DisabledScope(clip == null || humanoidPrefabOrModel == null))
        {
            if (GUILayout.Button("Bake Curves"))
            {
                BakeCurves();
            }
        }

        EditorGUILayout.HelpBox(
            "Prerequisites:\n" +
            "1) Animator Controller must have Float params: LeftFootIK, RightFootIK\n" +
            "2) Your model must be Humanoid (Avatar configured)\n" +
            "3) If the clip is read-only (FBX), extract it to .anim first\n",
            MessageType.Info
        );
    }

    void BakeCurves()
    {
        if (clip == null || humanoidPrefabOrModel == null) return;

        // 如果是 FBX 内 clip 只读，可能改不了曲线
        if (AssetDatabase.IsSubAsset(clip))
        {
            Debug.LogWarning("This clip looks like a sub-asset (often inside FBX). If curves can't be saved, please Extract to a standalone .anim first.");
        }

        var temp = (GameObject)PrefabUtility.InstantiatePrefab(humanoidPrefabOrModel);
        if (temp == null) temp = Instantiate(humanoidPrefabOrModel);

        temp.hideFlags = HideFlags.HideAndDontSave;
        temp.transform.position = Vector3.zero;
        temp.transform.rotation = Quaternion.identity;

        var animator = temp.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            DestroyImmediate(temp);
            EditorUtility.DisplayDialog("Error", "No Animator found on the prefab/model.", "OK");
            return;
        }
        if (!animator.isHuman)
        {
            DestroyImmediate(temp);
            EditorUtility.DisplayDialog("Error", "Animator is not Humanoid (isHuman == false). Make sure Avatar is Humanoid.", "OK");
            return;
        }

        // 先做一次全局 groundY 估计：取整个 clip 中两脚最低点的最小值
        float dt = 1f / Mathf.Max(1f, sampleRate);
        float len = Mathf.Max(clip.length, dt);
        int steps = Mathf.CeilToInt(len / dt) + 1;

        var lfY = new float[steps];
        var rfY = new float[steps];
        var times = new float[steps];

        float groundY = float.PositiveInfinity;

        for (int i = 0; i < steps; i++)
        {
            float t = Mathf.Min(i * dt, clip.length);
            times[i] = t;

            clip.SampleAnimation(temp, t);

            var lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf == null || rf == null)
            {
                DestroyImmediate(temp);
                EditorUtility.DisplayDialog("Error", "Could not find LeftFoot/RightFoot bones. Check Humanoid mapping.", "OK");
                return;
            }

            lfY[i] = lf.position.y;
            rfY[i] = rf.position.y;

            float minY = Mathf.Min(lfY[i], rfY[i]);
            if (minY < groundY) groundY = minY;
        }

        // 生成权重（带滞回+速度约束+平滑fade）
        var leftW  = BuildWeight(times, lfY, groundY, dt, landThreshold, liftThreshold, velThreshold, fadeTime);
        var rightW = BuildWeight(times, rfY, groundY, dt, landThreshold, liftThreshold, velThreshold, fadeTime);

        // 压缩 key（减少曲线冗余）
        var leftCurve  = BuildCurveCompressed(times, leftW, epsilon);
        var rightCurve = BuildCurveCompressed(times, rightW, epsilon);

        // 写入 AnimationClip：绑定到 Animator 的两个参数
        Undo.RecordObject(clip, "Bake Foot IK Curves");

        var bindL = EditorCurveBinding.FloatCurve("", typeof(Animator), "LeftFootIK");
        var bindR = EditorCurveBinding.FloatCurve("", typeof(Animator), "RightFootIK");

        AnimationUtility.SetEditorCurve(clip, bindL, leftCurve);
        AnimationUtility.SetEditorCurve(clip, bindR, rightCurve);

        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();

        DestroyImmediate(temp);

        Debug.Log($"[FootIKCurveBaker] Done. groundY={groundY:F3}, steps={steps}, keysL={leftCurve.length}, keysR={rightCurve.length}");
        EditorUtility.DisplayDialog("Done", "Baked LeftFootIK / RightFootIK curves into the clip.", "OK");
    }

    static float[] BuildWeight(
        float[] t, float[] y, float groundY, float dt,
        float landTh, float liftTh, float velTh, float fadeTime)
    {
        int n = t.Length;
        var w = new float[n];

        bool grounded = true; // 初始默认落地（走路clip通常从支撑相开始）
        float curW = grounded ? 1f : 0f;

        float prevY = y[0];

        // MoveTowards 的速度（每秒变化多少）
        float speed = (fadeTime <= 1e-5f) ? 9999f : (1f / fadeTime);

        for (int i = 0; i < n; i++)
        {
            float h = y[i] - groundY;               // 相对地面高度
            float vy = (i == 0) ? 0f : (y[i] - prevY) / Mathf.Max(dt, 1e-6f);
            prevY = y[i];

            // 滞回：避免在阈值附近跳来跳去
            if (grounded)
            {
                if (h > liftTh) grounded = false;   // 抬脚离地
            }
            else
            {
                // 落地判定：高度低 + 垂直速度小（更像脚踩稳）
                if (h < landTh && Mathf.Abs(vy) < velTh) grounded = true;
            }

            float target = grounded ? 1f : 0f;
            curW = Mathf.MoveTowards(curW, target, speed * dt);

            w[i] = curW;
        }

        return w;
    }

    static AnimationCurve BuildCurveCompressed(float[] t, float[] w, float eps)
    {
        // 简单压缩：剔除“连续近似共线/平坦”的点
        List<Keyframe> keys = new List<Keyframe>();
        keys.Add(new Keyframe(t[0], w[0]));

        for (int i = 1; i < t.Length - 1; i++)
        {
            float w0 = w[i - 1];
            float w1 = w[i];
            float w2 = w[i + 1];

            // 三点几乎平坦且变化很小 -> 跳过中间点
            if (Mathf.Abs(w1 - w0) < eps && Mathf.Abs(w2 - w1) < eps)
                continue;

            keys.Add(new Keyframe(t[i], w1));
        }

        keys.Add(new Keyframe(t[^1], w[^1]));

        var curve = new AnimationCurve(keys.ToArray());

        // 让曲线更平滑：自动切线
        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
        }

        return curve;
    }
}
#endif
