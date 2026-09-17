using UnityEngine;
#if TRACKS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace TrackSystem
{
    /// <summary>
    /// Input System 패키지가 켜져 있으면 그것을, 아니면 구 Input Manager를 쓰는 얇은 입력 래퍼.
    /// 어느 프로젝트에 옮겨도 입력 설정과 상관없이 컴파일/동작하게 하려는 용도다.
    /// </summary>
    public static class TrackInput
    {
#if TRACKS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        public static Vector2 MousePosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        public static Vector2 MouseDelta => Mouse.current != null ? Mouse.current.delta.ReadValue() * .1f : Vector2.zero;

        /// <summary>한 칸 = ±1 정도로 정규화한 휠 값.</summary>
        public static float Scroll
        {
            get
            {
                if (Mouse.current == null) return 0f;
                float y = Mouse.current.scroll.ReadValue().y;
                return Mathf.Abs(y) > 1f ? Mathf.Sign(y) : y;
            }
        }

        public static bool GetMouseButtonDown(int button) => MouseButton(button)?.wasPressedThisFrame ?? false;
        public static bool GetMouseButton(int button) => MouseButton(button)?.isPressed ?? false;

        public static bool GetKeyDown(KeyCode keyCode)
        {
            Key key = ToKey(keyCode);
            return key != Key.None && Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
        }

        public static bool GetKey(KeyCode keyCode)
        {
            Key key = ToKey(keyCode);
            return key != Key.None && Keyboard.current != null && Keyboard.current[key].isPressed;
        }

        static UnityEngine.InputSystem.Controls.ButtonControl MouseButton(int button)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return null;
            switch (button)
            {
                case 0: return mouse.leftButton;
                case 1: return mouse.rightButton;
                case 2: return mouse.middleButton;
                default: return null;
            }
        }

        static Key ToKey(KeyCode k)
        {
            if (k >= KeyCode.A && k <= KeyCode.Z) return Key.A + (k - KeyCode.A);
            if (k == KeyCode.Alpha0) return Key.Digit0;
            if (k >= KeyCode.Alpha1 && k <= KeyCode.Alpha9) return Key.Digit1 + (k - KeyCode.Alpha1);
            if (k >= KeyCode.F1 && k <= KeyCode.F12) return Key.F1 + (k - KeyCode.F1);
            switch (k)
            {
                case KeyCode.None: return Key.None;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.Space: return Key.Space;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Backspace: return Key.Backspace;
                case KeyCode.Delete: return Key.Delete;
                case KeyCode.LeftShift: return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftControl: return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftAlt: return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.UpArrow: return Key.UpArrow;
                case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.LeftArrow: return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;
                default: return System.Enum.TryParse(k.ToString(), out Key parsed) ? parsed : Key.None;
            }
        }
#else
        public static Vector2 MousePosition => Input.mousePosition;
        public static Vector2 MouseDelta => new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
        public static float Scroll => Mathf.Clamp(Input.mouseScrollDelta.y, -1f, 1f);
        public static bool GetMouseButtonDown(int button) => Input.GetMouseButtonDown(button);
        public static bool GetMouseButton(int button) => Input.GetMouseButton(button);
        public static bool GetKeyDown(KeyCode key) => Input.GetKeyDown(key);
        public static bool GetKey(KeyCode key) => Input.GetKey(key);
#endif
    }
}
