using UnityEngine;
using UnityEngine.InputSystem;

namespace Building
{
    public sealed class BuildModeController : MonoBehaviour
    {
        [SerializeField] Camera playerCamera;
        [SerializeField] Key pickupKey = Key.F;
        [SerializeField] Key cancelKey = Key.Escape;
        [SerializeField, Min(1f)] float maxRayDistance = 30f;
        [SerializeField] LayerMask groundMask = ~0;

        PlaceableEquipment current;
        Collider[] currentColliders;
        Vector3 originalPosition;
        Quaternion originalRotation;
        Quaternion previewRotation;

        public bool IsPlacing => current != null;

        void Awake()
        {
            if (playerCamera == null) playerCamera = Camera.main;
        }

        void Update()
        {
            if (Keyboard.current == null) return;

            if (!IsPlacing)
            {
                if (Keyboard.current[pickupKey].wasPressedThisFrame) TryBeginPlacing();
                return;
            }

            UpdatePlacementPreview();

            if (Mouse.current != null)
            {
                float scroll = Mouse.current.scroll.ReadValue().y;
                if (scroll > 0f) previewRotation *= Quaternion.Euler(0f, 90f, 0f);
                else if (scroll < 0f) previewRotation *= Quaternion.Euler(0f, -90f, 0f);
            }

            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                ConfirmPlacement();
            else if (Keyboard.current[cancelKey].wasPressedThisFrame)
                CancelPlacement();
        }

        void TryBeginPlacing()
        {
            if (Interactable.Current == null) return;
            PlaceableEquipment target = Interactable.Current.GetComponent<PlaceableEquipment>();
            if (target != null) BeginPlacing(target);
        }

        public void BeginPlacing(PlaceableEquipment target)
        {
            if (target == null || IsPlacing) return;

            current = target;
            originalPosition = target.transform.position;
            originalRotation = target.transform.rotation;
            previewRotation = target.transform.rotation;

            currentColliders = current.GetComponentsInChildren<Collider>(true);
            SetCollidersEnabled(false);

            BuildGrid.Instance?.BeginForceVisible();
        }

        void UpdatePlacementPreview()
        {
            if (current == null || playerCamera == null) return;
            if (!Physics.Raycast(playerCamera.transform.position, playerCamera.transform.forward,
                    out RaycastHit hit, maxRayDistance, groundMask, QueryTriggerInteraction.Ignore))
                return;

            Vector3 snapped = hit.point;
            if (BuildGrid.Instance != null)
            {
                Vector2 footprint = current.GetLocalFootprint();
                bool swapped = Mathf.Approximately(Mathf.Repeat(previewRotation.eulerAngles.y, 180f), 90f);
                if (swapped) footprint = new Vector2(footprint.y, footprint.x);
                snapped = BuildGrid.Instance.SnapToGridFootprint(hit.point, footprint);
            }
            snapped.y = originalPosition.y;
            current.transform.SetPositionAndRotation(snapped, previewRotation);
        }

        void ConfirmPlacement()
        {
            SetCollidersEnabled(true);
            BuildGrid.Instance?.EndForceVisible();
            current = null;
            currentColliders = null;
        }

        void CancelPlacement()
        {
            current.transform.SetPositionAndRotation(originalPosition, originalRotation);
            SetCollidersEnabled(true);
            BuildGrid.Instance?.EndForceVisible();
            current = null;
            currentColliders = null;
        }

        void SetCollidersEnabled(bool value)
        {
            if (currentColliders == null) return;
            foreach (Collider c in currentColliders)
                if (c != null) c.enabled = value;
        }
    }
}
