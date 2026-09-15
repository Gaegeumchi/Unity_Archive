// Simple First-Person Example Code
// Requires a Rigidbody, Main Collider, Detection Collider (placed at the feet with 'Is Trigger' enabled), and Player Input component.
    
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

public class First_PersonController : MonoBehaviour
{
    public float mouseSensitivity = 80f;
    public float moveSpeed = 10f;
    public float jumpForce = 7f;
    public Camera playerCamera;
    public bool Sprint = false;
    public float SprintSpeed = 15f;
    [SerializeField, Range(1f, 20f)] private float maxLookDegreesPerFrame = 8f;
    [SerializeField, Range(0f, 1f)] private float groundNormalThreshold = 0.55f;

    private Rigidbody rb;
    private float xRotation = 0f;
    private bool isGrounded = false;
    private bool isSprinting = false;
    private bool jumpRequested;
    private bool ignoreNextLookFrame;
    private float pendingYaw;
    private Vector2 moveInput;
    private PhysicsMaterial noFrictionMaterial;
    private readonly Dictionary<Collider, Vector3> wallNormals = new Dictionary<Collider, Vector3>();
    private readonly HashSet<Collider> groundColliders = new HashSet<Collider>();
    private readonly Dictionary<Collider, PhysicsMaterial> originalMaterials = new Dictionary<Collider, PhysicsMaterial>();

    private PlayerInput playerInput;
    private InputAction lookAction;
    private InputAction moveAction;
    private InputAction jumpAction;
    private InputAction sprintAction;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        playerInput = GetComponent<PlayerInput>();

        if (rb == null || playerCamera == null || playerInput == null || playerInput.actions == null)
        {
            Debug.LogError("First_PersonController requires a Rigidbody, PlayerInput with an Input Actions asset, and a Player Camera.", this);
            enabled = false;
            return;
        }

        // 충돌 토크 때문에 플레이어와 카메라가 갑자기 기울거나 회전하지 않게 한다.
        rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        ApplyNoFrictionMaterial();

        lookAction = playerInput.actions.FindAction("Look");
        moveAction = playerInput.actions.FindAction("Move");
        jumpAction = playerInput.actions.FindAction("Jump");
        sprintAction = playerInput.actions.FindAction("Sprint");

        if (lookAction == null || moveAction == null || jumpAction == null || sprintAction == null)
        {
            Debug.LogError("First_PersonController could not find the Look, Move, Jump, or Sprint action in PlayerInput.", this);
            enabled = false;
        }
    }

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        if (!Application.isFocused)
        {
            pendingYaw = 0f;
            moveInput = Vector2.zero;
            jumpRequested = false;
            return;
        }

        Vector2 mouseDelta = ignoreNextLookFrame ? Vector2.zero : lookAction.ReadValue<Vector2>();
        ignoreNextLookFrame = false;
        float mouseX = Mathf.Clamp(
            mouseDelta.x * mouseSensitivity * Time.deltaTime,
            -maxLookDegreesPerFrame,
            maxLookDegreesPerFrame);
        float mouseY = Mathf.Clamp(
            mouseDelta.y * mouseSensitivity * Time.deltaTime,
            -maxLookDegreesPerFrame,
            maxLookDegreesPerFrame);

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        playerCamera.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        pendingYaw += mouseX;

        moveInput = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
        if (jumpAction.WasPressedThisFrame()) jumpRequested = true;
        isSprinting = Sprint && sprintAction.IsPressed();
    }

    void FixedUpdate()
    {
        // Y축도 충돌 힘으로 돌지 않게 하고, 아래의 입력 회전만 허용한다.
        rb.angularVelocity = Vector3.zero;

        if (Mathf.Abs(pendingYaw) > Mathf.Epsilon)
        {
            rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, pendingYaw, 0f));
            pendingYaw = 0f;
        }

        float currentSpeed = isSprinting ? SprintSpeed : moveSpeed;
        Vector3 desiredMove = rb.rotation * new Vector3(moveInput.x, 0f, moveInput.y);
        desiredMove *= currentSpeed;

        foreach (Vector3 normal in wallNormals.Values)
        {
            if (Vector3.Dot(desiredMove, normal) < 0f)
                desiredMove = Vector3.ProjectOnPlane(desiredMove, normal);
        }

        Vector3 velocity = new Vector3(desiredMove.x, rb.linearVelocity.y, desiredMove.z);
        if (jumpRequested && isGrounded)
        {
            velocity.y = jumpForce;
            isGrounded = false;
            groundColliders.Clear();
        }

        rb.linearVelocity = velocity;
        jumpRequested = false;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        pendingYaw = 0f;
        moveInput = Vector2.zero;
        jumpRequested = false;
        ignoreNextLookFrame = hasFocus;

        if (hasFocus)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void OnCollisionStay(Collision collision)
    {
        bool touchesGround = false;
        Vector3 combinedWallNormal = Vector3.zero;

        foreach (ContactPoint contact in collision.contacts)
        {
            Vector3 normal = contact.normal;
            if (normal.y >= groundNormalThreshold)
            {
                touchesGround = true;
                continue;
            }

            Vector3 horizontalNormal = new Vector3(normal.x, 0f, normal.z);
            if (horizontalNormal.sqrMagnitude > 0.01f)
                combinedWallNormal += horizontalNormal.normalized;
        }

        if (touchesGround) groundColliders.Add(collision.collider);
        else groundColliders.Remove(collision.collider);

        if (combinedWallNormal.sqrMagnitude > 0.01f)
            wallNormals[collision.collider] = combinedWallNormal.normalized;
        else
            wallNormals.Remove(collision.collider);

        isGrounded = groundColliders.Count > 0;
    }

    private void OnCollisionExit(Collision collision)
    {
        groundColliders.Remove(collision.collider);
        wallNormals.Remove(collision.collider);
        isGrounded = groundColliders.Count > 0;
    }

    private void ApplyNoFrictionMaterial()
    {
        noFrictionMaterial = new PhysicsMaterial("Player No Friction (Runtime)")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine = PhysicsMaterialCombine.Minimum,
            hideFlags = HideFlags.HideAndDontSave
        };

        foreach (Collider playerCollider in GetComponentsInChildren<Collider>(true))
        {
            if (playerCollider.isTrigger) continue;
            originalMaterials[playerCollider] = playerCollider.sharedMaterial;
            playerCollider.sharedMaterial = noFrictionMaterial;
        }
    }

    private void OnDestroy()
    {
        foreach (var pair in originalMaterials)
            if (pair.Key != null) pair.Key.sharedMaterial = pair.Value;

        if (noFrictionMaterial != null)
            Destroy(noFrictionMaterial);
    }
}
