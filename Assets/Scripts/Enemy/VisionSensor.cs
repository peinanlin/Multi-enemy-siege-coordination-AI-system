using UnityEngine;
using Mirror;

[RequireComponent(typeof(SphereCollider))]
public class VisionSensor : MonoBehaviour
{
    [SerializeField] EnemyController enemy;

    [Header("Debug Gizmos")]
    [SerializeField] bool drawGizmos = true;
    [SerializeField] bool drawFov = true;
    [SerializeField] int fovSegments = 24;
    [SerializeField] float gizmoHeight = 0.05f;

    SphereCollider sphere;

    bool IsNetworking => NetworkClient.active || NetworkServer.active;
    bool IsAuthoritative => !IsNetworking || NetworkServer.active; // 离线 or 服务器

    void Awake()
    {
        sphere = GetComponent<SphereCollider>();
        sphere.isTrigger = true;

        if (enemy == null) enemy = GetComponentInParent<EnemyController>();
        if (enemy != null) enemy.VisionSensor = this;
    }

    void Start()
    {
        // 联机纯客户端：不需要传感器参与逻辑（服务器权威）
        if (IsNetworking && !NetworkServer.active)
        {
            // 可选：彻底禁用触发器，避免客户端无意义的触发开销
            if (sphere != null) sphere.enabled = false;
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (!IsAuthoritative) return;
        if (enemy == null) return;

        var fighter = other.GetComponent<MeeleFighter>();
        if (fighter != null)
        {
            if (!enemy.TargetsInRange.Contains(fighter))
                enemy.TargetsInRange.Add(fighter);
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (!IsAuthoritative) return;
        if (enemy == null) return;

        var fighter = other.GetComponent<MeeleFighter>();
        if (fighter != null)
        {
            enemy.TargetsInRange.Remove(fighter);

            if (enemy.Target == fighter)
            {
                // 服务器权威脱战
                enemy.ForceDisengage();
            }
        }
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        if (enemy == null) enemy = GetComponentInParent<EnemyController>();
        if (sphere == null) sphere = GetComponent<SphereCollider>();
        if (enemy == null || sphere == null) return;

        Vector3 center = enemy.transform.position;
        center.y += gizmoHeight;

        Gizmos.color = (enemy.Target != null)
            ? new Color(1f, 0.3f, 0.3f, 0.8f)
            : new Color(0.2f, 1f, 0.2f, 0.8f);

        float radius = sphere.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.z);
        Gizmos.DrawWireSphere(center, radius);

        if (!drawFov) return;

        float halfFov = enemy.Fov * 0.5f;
        Vector3 forward = enemy.transform.forward;
        forward.y = 0;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 leftDir = Quaternion.Euler(0, -halfFov, 0) * forward;
        Vector3 rightDir = Quaternion.Euler(0, halfFov, 0) * forward;

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.DrawLine(center, center + leftDir * radius);
        Gizmos.DrawLine(center, center + rightDir * radius);

        Vector3 prev = center + leftDir * radius;
        int seg = Mathf.Max(4, fovSegments);
        for (int i = 1; i <= seg; i++)
        {
            float t = (float)i / seg;
            float ang = Mathf.Lerp(-halfFov, halfFov, t);
            Vector3 dir = Quaternion.Euler(0, ang, 0) * forward;
            Vector3 p = center + dir * radius;
            Gizmos.DrawLine(prev, p);
            prev = p;
        }

        Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
        Gizmos.DrawLine(center, center + forward * (radius * 0.6f));
    }
}
