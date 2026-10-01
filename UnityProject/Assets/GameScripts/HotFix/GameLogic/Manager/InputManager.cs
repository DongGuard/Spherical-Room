using UnityEngine;
using UnityEngine.EventSystems;

namespace GameLogic
{
    /// <summary>
    /// 输入管理：统一采集键鼠输入（旧版 Input Manager），并管理鼠标锁定。
    /// 只负责"读输入"，不关心输入给谁用；玩家控制器和相机管理器从这里取值。
    /// </summary>
    public class InputManager : SingletonBehaviour<InputManager>
    {
        /// <summary>是否处于玩法输入状态（进入房间后由本地玩家开启）。</summary>
        public bool GameplayEnabled { get; private set; }

        /// <summary>玩法输入已开启且鼠标已锁定时才接受操作。</summary>
        public bool IsInputActive => GameplayEnabled && Cursor.lockState == CursorLockMode.Locked;

        /// <summary>移动方向：x = 左右，y = 前后，长度不超过 1。</summary>
        public Vector2 Move { get; private set; }

        /// <summary>本帧鼠标位移（未乘灵敏度）。</summary>
        public Vector2 LookDelta { get; private set; }

        public bool Sprint { get; private set; }

        /// <summary>跳跃在 Update 中锁存，由物理步消费，避免 FixedUpdate 漏掉单帧按下。</summary>
        private bool _jumpLatched;

        /// <summary>开启 / 关闭玩法输入，同时锁定 / 释放鼠标。</summary>
        public void SetGameplayEnabled(bool enabled)
        {
            GameplayEnabled = enabled;
            SetCursorLocked(enabled);
            if (!enabled)
            {
                ClearState();
            }
        }

        /// <summary>
        /// 读取并清除跳跃请求。
        /// </summary>
        /// <returns></returns>
        public bool ConsumeJump()
        {
            bool jump = _jumpLatched;
            _jumpLatched = false;
            return jump;
        }

        private void Update()
        {
            if (!GameplayEnabled)
            {
                return;
            }

            HandleCursor();
            if (!IsInputActive)
            {
                ClearState();
                return;
            }

            Move = Vector2.ClampMagnitude(
                new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
            LookDelta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
            Sprint = Input.GetKey(KeyCode.LeftShift);

            if (Input.GetButtonDown("Jump"))
            {
                _jumpLatched = true;
            }
        }

        private void HandleCursor()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                SetCursorLocked(false);
            }
            else if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
            {
                bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                if (!overUi)
                {
                    SetCursorLocked(true);
                }
            }
        }

        private void ClearState()
        {
            Move = Vector2.zero;
            LookDelta = Vector2.zero;
            Sprint = false;
            _jumpLatched = false;
        }

        private static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        protected override void OnDestroy()
        {
            SetCursorLocked(false);
            base.OnDestroy();
        }
    }
}
