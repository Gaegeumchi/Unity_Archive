using UnityEngine;

namespace Building
{
    [RequireComponent(typeof(Collider))]
    public sealed class PlaceableEquipment : MonoBehaviour
    {
        public Vector2 GetLocalFootprint()
        {
            Vector3 scale = transform.lossyScale;
            if (TryGetComponent(out BoxCollider box))
                return new Vector2(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.z * scale.z));

            var renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Vector2(scale.x, scale.z);

            Quaternion inverse = Quaternion.Inverse(transform.rotation);
            Bounds localBounds = default;
            bool found = false;
            foreach (Renderer r in renderers)
            {
                Bounds b = r.bounds;
                Vector3 center = inverse * (b.center - transform.position);
                Vector3 extents = inverse * b.extents;
                extents = new Vector3(Mathf.Abs(extents.x), Mathf.Abs(extents.y), Mathf.Abs(extents.z));
                var local = new Bounds(center, extents * 2f);
                if (!found) { localBounds = local; found = true; }
                else localBounds.Encapsulate(local);
            }
            return new Vector2(localBounds.size.x, localBounds.size.z);
        }
    }
}
