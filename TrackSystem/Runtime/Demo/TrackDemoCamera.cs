using UnityEngine;

namespace TrackSystem.Demo
{
    /// <summary>데모용 탑다운 카메라. WASD 이동, Q/E 회전, 휠 줌, 가운데 버튼 드래그로 회전/기울기.</summary>
    public sealed class TrackDemoCamera : MonoBehaviour
    {
        [SerializeField] Vector3 pivot;
        [SerializeField, Min(1f)] float distance = 22f;
        [SerializeField] float yaw;
        [SerializeField, Range(10f, 89f)] float pitch = 55f;
        [SerializeField, Min(0f)] float moveSpeed = 12f;
        [SerializeField, Min(0f)] float rotateSpeed = 90f;

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            Vector3 flatForward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 flatRight = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;

            Vector3 move = Vector3.zero;
            if (TrackInput.GetKey(KeyCode.W)) move += flatForward;
            if (TrackInput.GetKey(KeyCode.S)) move -= flatForward;
            if (TrackInput.GetKey(KeyCode.D)) move += flatRight;
            if (TrackInput.GetKey(KeyCode.A)) move -= flatRight;
            pivot += move.normalized * (moveSpeed * dt * Mathf.Max(.3f, distance / 20f));

            if (TrackInput.GetKey(KeyCode.Q)) yaw += rotateSpeed * dt;
            if (TrackInput.GetKey(KeyCode.E)) yaw -= rotateSpeed * dt;
            if (TrackInput.GetMouseButton(2))
            {
                Vector2 d = TrackInput.MouseDelta;
                yaw += d.x * 3f;
                pitch = Mathf.Clamp(pitch - d.y * 3f, 10f, 89f);
            }

            distance = Mathf.Clamp(distance * (1f - TrackInput.Scroll * .12f), 3f, 120f);

            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(pivot - rot * Vector3.forward * distance, rot);
        }
    }
}
