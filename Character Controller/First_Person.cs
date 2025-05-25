using UnityEngine;
using UnityEngine.InputSystem;

public class First_PersonController : MonoBehaviour
{
    public float mouseSensitivity = 80f;
    public float moveSpeed = 10f;
    public float jumpForce = 7f;
    public Camera playerCamera;
    public bool Sprint = false;
    public float SprintSpeed = 15f;

    private Rigidbody rb;
    private float xRotation = 0f;
    private bool isGrounded = false;
    private bool isSprinting = false;

    private PlayerInput playerInput;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        Cursor.lockState = CursorLockMode.Locked;

        playerInput = GetComponent<PlayerInput>();
    }

    void Update()
    {
        Vector2 mouseDelta = playerInput.actions["Look"].ReadValue<Vector2>();
        float mouseX = mouseDelta.x * mouseSensitivity * Time.deltaTime;
        float mouseY = mouseDelta.y * mouseSensitivity * Time.deltaTime;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        playerCamera.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);

        if (playerInput.actions["Jump"].WasPressedThisFrame() && isGrounded)
        {
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, jumpForce, rb.linearVelocity.z);
        }
        
        isSprinting = Sprint && playerInput.actions["Sprint"].IsPressed();
    }

    void FixedUpdate()
    {
        Vector2 moveInput = playerInput.actions["Move"].ReadValue<Vector2>();
        float x = moveInput.x;
        float z = moveInput.y;
        
        float currentSpeed = isSprinting ? SprintSpeed : moveSpeed;
        
        Vector3 move = transform.right * x + transform.forward * z;
        Vector3 velocity = new Vector3(move.x * currentSpeed, rb.linearVelocity.y, move.z * currentSpeed);

        rb.linearVelocity = velocity;
    }

    private void OnTriggerStay(Collider collision)
    {
        isGrounded = true;
    }

    private void OnTriggerExit(Collider collision)
    {
        isGrounded = false;
    }
}
