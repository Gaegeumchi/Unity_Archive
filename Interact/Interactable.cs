// Simple Interactable Component Example Code
// Requirements:
// - Camera (Player camera for sight & range checks)
// - Trigger Collider (Automatically generated for proximity check)
// - Renderer & Collider (Target object mesh for bounds and outline effect)
// - Custom Shader ("Shader/Interact/InteractableOutlineGlow.shader" for highlight effect)
  
using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class Interactable : MonoBehaviour
{
    public enum LookMode { SphereCast, DotProduct }

    [Header("Range Trigger")]
    [SerializeField, Min(.1f)] float interactionRange = 3f;
    [SerializeField] string playerTag = "Player";
    [SerializeField] LayerMask playerLayers = ~0;

    [Header("Look Detection")]
    [SerializeField] LookMode lookMode = LookMode.SphereCast;
    [SerializeField] Camera playerCamera;
    [SerializeField] Transform focusPoint;
    [SerializeField, Min(.01f)] float castRadius = .2f;
    [SerializeField, Range(1f, 89f)] float viewAngle = 20f;
    [SerializeField] LayerMask sightLayers = ~0;
    [SerializeField] bool checkOcclusion = true;

    [Header("Outline Glow")]
    [SerializeField] Color glowColor = new Color(.1f, .8f, 1f, 1f);
    [Tooltip("화면에 표시되는 외곽선 두께(픽셀)")]
    [SerializeField, Range(1f, 12f)] float outlineWidth = 3f;
    [SerializeField, Min(1f)] float glowIntensity = 3f;
    [SerializeField] Renderer[] targetRenderers;

    public bool CanInteract { get; private set; }
    public event Action<Interactable> AvailabilityChanged;

    readonly HashSet<Transform> nearbyPlayers = new HashSet<Transform>();
    readonly Dictionary<Renderer, Material[]> savedMaterials = new Dictionary<Renderer, Material[]>();
    readonly RaycastHit[] hits = new RaycastHit[24];
    readonly Collider[] overlaps = new Collider[24];
    [SerializeField, HideInInspector] SphereCollider rangeTrigger;
    Collider[] targetColliders;
    float lastPlayerSeenTime = float.NegativeInfinity;
    Material outlineMaterial;

    void Reset()
    {
        targetRenderers = GetComponentsInChildren<Renderer>(true);
        ConfigureTrigger(true);
    }

    void Awake()
    {
        if (playerCamera == null) playerCamera = Camera.main;
        if (targetRenderers == null || targetRenderers.Length == 0)
            targetRenderers = GetComponentsInChildren<Renderer>(true);
        ConfigureTrigger(true);
        targetColliders = GetComponentsInChildren<Collider>(true);
        CreateOutline();
    }

    void Update()
    {
        nearbyPlayers.RemoveWhere(t => t == null || !t.gameObject.activeInHierarchy);
        if (playerCamera == null) playerCamera = Camera.main;
        bool recentlyInsideTrigger = nearbyPlayers.Count > 0 || Time.time - lastPlayerSeenTime <= .2f;
        bool insideExactRange = playerCamera != null
            && DistanceToTarget(playerCamera.transform.position) <= interactionRange;
        SetAvailable(recentlyInsideTrigger && insideExactRange && IsLookedAt());
    }

    void OnTriggerEnter(Collider other)
    {
        Transform player = GetPlayer(other);
        if (player != null)
        {
            nearbyPlayers.Add(player);
            lastPlayerSeenTime = Time.time;
        }
    }

    void OnTriggerStay(Collider other)
    {
        Transform player = GetPlayer(other);
        if (player != null)
        {
            nearbyPlayers.Add(player);
            lastPlayerSeenTime = Time.time;
        }
    }

    void OnTriggerExit(Collider other)
    {
        Transform player = GetPlayer(other);
        if (player != null) nearbyPlayers.Remove(player);
    }

    void OnDisable()
    {
        nearbyPlayers.Clear();
        SetAvailable(false);
        RestoreMaterials();
    }

    void OnDestroy()
    {
        if (outlineMaterial != null) Destroy(outlineMaterial);
    }

    void OnValidate()
    {
        interactionRange = Mathf.Max(.1f, interactionRange);
        castRadius = Mathf.Max(.01f, castRadius);
        outlineWidth = outlineWidth < 1f ? 3f : Mathf.Clamp(outlineWidth, 1f, 12f);
        ConfigureTrigger(false);
        ApplyOutlineSettings();
    }

    void ConfigureTrigger(bool createIfMissing)
    {
        if (rangeTrigger == null)
        {
            foreach (SphereCollider sphere in GetComponents<SphereCollider>())
            {
                if (!sphere.isTrigger) continue;
                rangeTrigger = sphere;
                break;
            }
        }

        if (rangeTrigger == null && createIfMissing)
            rangeTrigger = gameObject.AddComponent<SphereCollider>();
        if (rangeTrigger == null) return;

        rangeTrigger.isTrigger = true;
        if (TryGetTargetBounds(out Bounds bounds))
        {
            rangeTrigger.center = transform.InverseTransformPoint(bounds.center);
            float worldRadius = bounds.extents.magnitude + interactionRange;
            Vector3 scale = transform.lossyScale;
            float largestScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            rangeTrigger.radius = worldRadius / Mathf.Max(largestScale, .0001f);
        }
        else
        {
            rangeTrigger.center = Vector3.zero;
            rangeTrigger.radius = interactionRange;
        }
    }

    Transform GetPlayer(Collider other)
    {
        Transform root = other.attachedRigidbody != null ? other.attachedRigidbody.transform : other.transform.root;
        bool correctLayer = (playerLayers.value & (1 << root.gameObject.layer)) != 0;
        bool correctTag = string.IsNullOrEmpty(playerTag) || root.CompareTag(playerTag);
        return correctLayer && correctTag ? root : null;
    }

    bool IsLookedAt()
    {
        return lookMode == LookMode.SphereCast ? SphereCastHitsThis() : PassesDotTest();
    }

    bool SphereCastHitsThis()
    {
        Transform cam = playerCamera.transform;
        int overlapCount = Physics.OverlapSphereNonAlloc(cam.position, castRadius, overlaps,
            sightLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < overlapCount; i++)
            if (overlaps[i].GetComponentInParent<Interactable>() == this) return true;

        float castDistance = Vector3.Distance(cam.position, FocusPosition()) + castRadius + .5f;
        int count = Physics.SphereCastNonAlloc(cam.position, castRadius, cam.forward, hits,
            castDistance, sightLayers, QueryTriggerInteraction.Ignore);
        return FindNearestTarget(count) == this;
    }

    bool PassesDotTest()
    {
        Transform cam = playerCamera.transform;
        Vector3 toTarget = FocusPosition() - cam.position;
        if (toTarget.sqrMagnitude < Mathf.Epsilon) return true;
        if (Vector3.Dot(cam.forward, toTarget.normalized) < Mathf.Cos(viewAngle * Mathf.Deg2Rad)) return false;
        if (!checkOcclusion) return true;

        int count = Physics.RaycastNonAlloc(cam.position, toTarget.normalized, hits,
            toTarget.magnitude, sightLayers, QueryTriggerInteraction.Ignore);
        return HasClearLineOfSight(count);
    }

    bool HasClearLineOfSight(int count)
    {
        float nearestDistance = float.PositiveInfinity;
        Collider nearest = null;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = hits[i];
            Transform root = hit.rigidbody != null ? hit.rigidbody.transform : hit.collider.transform.root;
            if (nearbyPlayers.Contains(root) || hit.distance >= nearestDistance) continue;
            nearestDistance = hit.distance;
            nearest = hit.collider;
        }
        return nearest == null || nearest.GetComponentInParent<Interactable>() == this;
    }

    Interactable FindNearestTarget(int count)
    {
        float nearestDistance = float.PositiveInfinity;
        Interactable nearest = null;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = hits[i];
            Transform root = hit.rigidbody != null ? hit.rigidbody.transform : hit.collider.transform.root;
            if (nearbyPlayers.Contains(root) || hit.distance >= nearestDistance) continue;
            nearestDistance = hit.distance;
            nearest = hit.collider.GetComponentInParent<Interactable>();
        }
        return nearest;
    }

    Vector3 FocusPosition()
    {
        if (focusPoint != null) return focusPoint.position;
        return TryGetTargetBounds(out Bounds bounds) ? bounds.center : transform.position;
    }

    bool TryGetTargetBounds(out Bounds bounds)
    {
        bool found = false;
        bounds = default;
        if (targetRenderers == null) return false;

        foreach (Renderer item in targetRenderers)
        {
            if (item == null) continue;
            if (!found) { bounds = item.bounds; found = true; }
            else bounds.Encapsulate(item.bounds);
        }
        return found;
    }

    float DistanceToTarget(Vector3 point)
    {
        float nearest = float.PositiveInfinity;
        if (targetColliders != null)
        {
            foreach (Collider item in targetColliders)
            {
                if (item == null || item == rangeTrigger || item.isTrigger || !item.enabled) continue;
                nearest = Mathf.Min(nearest, Vector3.Distance(point, item.ClosestPoint(point)));
            }
        }

        if (!float.IsPositiveInfinity(nearest)) return nearest;
        if (TryGetTargetBounds(out Bounds bounds))
            return Vector3.Distance(point, bounds.ClosestPoint(point));
        return Vector3.Distance(point, transform.position);
    }

    void SetAvailable(bool value)
    {
        if (CanInteract == value) return;
        CanInteract = value;
        SetOutline(value);
        if (value) Debug.Log($"상호작용 가능: {name}", this);
        AvailabilityChanged?.Invoke(this);
    }

    // 실제 상호작용은 추후 이 메서드에 구현하세요.
    public void Interact() { }

    void CreateOutline()
    {
        Shader shader = Shader.Find("Hidden/Interaction/URPOutlineGlow");
        if (shader == null)
        {
            Debug.LogError("Interaction outline shader를 찾을 수 없습니다.", this);
            return;
        }
        outlineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        ApplyOutlineSettings();
    }

    void ApplyOutlineSettings()
    {
        if (outlineMaterial == null) return;
        Color hdr = glowColor * glowIntensity;
        hdr.a = glowColor.a;
        outlineMaterial.SetColor("_OutlineColor", hdr);
        outlineMaterial.SetFloat("_OutlineWidth", outlineWidth);
    }

    void SetOutline(bool visible)
    {
        if (!visible) { RestoreMaterials(); return; }
        if (outlineMaterial == null || targetRenderers == null) return;

        foreach (Renderer item in targetRenderers)
        {
            if (item == null || savedMaterials.ContainsKey(item)) continue;
            Material[] materials = item.sharedMaterials;
            savedMaterials.Add(item, materials);
            Array.Resize(ref materials, materials.Length + 1);
            materials[materials.Length - 1] = outlineMaterial;
            item.sharedMaterials = materials;

            var properties = new MaterialPropertyBlock();
            item.GetPropertyBlock(properties, materials.Length - 1);
            properties.SetVector("_OutlineCenterOS", item.localBounds.center);
            item.SetPropertyBlock(properties, materials.Length - 1);
        }
    }

    void RestoreMaterials()
    {
        foreach (var pair in savedMaterials)
        {
            if (pair.Key == null) continue;
            pair.Key.SetPropertyBlock(null, pair.Key.sharedMaterials.Length - 1);
            pair.Key.sharedMaterials = pair.Value;
        }
        savedMaterials.Clear();
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(.1f, .8f, 1f, .5f);
        Gizmos.DrawWireSphere(transform.position, interactionRange);
    }
}
