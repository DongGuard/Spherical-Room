using Mirror;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 第一人称玩家控制（主机权威，Mirror）。
    /// <para>本地玩家：从 InputManager 取输入、从 CameraManager 取视角朝向，每个物理步通过 Command 发给主机。</para>
    /// <para>Server：用最新输入驱动 CharacterController 移动，位置和朝向由 NetworkTransform（ServerToClient）下发。</para>
    /// 客户端从不自行修改位置，所以各端看到的玩家位置只有主机这一个数据源。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public class PlayerController : NetworkBehaviour
    {
        [Header("Move")]
        [SerializeField] private float walkSpeed = 3f;
        [SerializeField] private float sprintSpeed = 5.5f;
        [Tooltip("水平加减速度（米/秒²），避免起步停步瞬变")]
        [SerializeField] private float acceleration = 25f;
        [SerializeField] private float jumpHeight = 1.2f;
        [SerializeField] private float gravity = -20f;

        [Header("First Person")]
        [Tooltip("眼睛相机（预制体中默认关闭），只有本地玩家会启用")]
        [SerializeField] private Camera eyeCamera;
        [Tooltip("本机玩家需要隐藏的模型根节点")]
        [SerializeField] private GameObject modelRoot;

        [Header("Push")]
        [Tooltip("推球加速度，越大起步越快")]
        [SerializeField] private float pushAccel = 12f;

        private CharacterController _controller;
        private PlayerAnimationController _animation;
        private uint _sequence;

        // Server 模拟状态
        private PlayerInputState _serverInput;
        private bool _hasServerInput;
        private bool _serverJumpRequested;
        private Vector3 _planarVelocity;
        private float _verticalVelocity;

        /// <summary>本机控制的玩家，未生成时为 null。</summary>
        public static PlayerController Local { get; private set; }

        /// <summary>Server：当前模拟速度，推球结算使用。</summary>
        public Vector3 Velocity => _planarVelocity + Vector3.up * _verticalVelocity;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _animation = GetComponentInChildren<PlayerAnimationController>(true);

            if (eyeCamera == null)
            {
                eyeCamera = GetComponentInChildren<Camera>(true);
            }

            SetEyeEnabled(false);
        }

        private void SetEyeEnabled(bool enabled)
        {
            if (eyeCamera == null)
            {
                return;
            }

            eyeCamera.enabled = enabled;
            AudioListener listener = eyeCamera.GetComponent<AudioListener>();
            if (listener != null)
            {
                listener.enabled = enabled;
            }
        }

        public override void OnStartServer()
        {
            _serverInput.Yaw = transform.eulerAngles.y;
        }

        public override void OnStartClient()
        {
            // 纯客户端不做模拟，位置完全来自 NetworkTransform，关闭 CharacterController 避免与插值冲突
            if (!isServer)
            {
                _controller.enabled = false;
            }
        }

        public override void OnStartLocalPlayer()
        {
            Local = this;
            InputManager.Instance.SetGameplayEnabled(true);
            CameraManager.Instance.AttachFirstPerson(eyeCamera, transform.eulerAngles.y, modelRoot);
        }

        public override void OnStopLocalPlayer()
        {
            if (Local != this)
            {
                return;
            }

            Local = null;
            // 玩家销毁前把相机还给场景、释放鼠标
            if (CameraManager.IsValid)
            {
                CameraManager.Instance.Detach();
            }

            if (InputManager.IsValid)
            {
                InputManager.Instance.SetGameplayEnabled(false);
            }
        }

        private void FixedUpdate()
        {
            if (isLocalPlayer)
            {
                SendInput();
            }

            if (isServer)
            {
                Simulate(Time.fixedDeltaTime);
            }
        }

        #region 本地玩家：输入上行

        private void SendInput()
        {
            InputManager input = InputManager.Instance;
            var state = new PlayerInputState
            {
                Sequence = ++_sequence,
                Move = input.Move,
                Sprint = input.Sprint,
                // 移动朝向以本地相机为准，转视角不等主机回传
                Yaw = CameraManager.Instance.IsAttached ? CameraManager.Instance.Yaw : transform.eulerAngles.y
            };
            bool jump = input.ConsumeJump();

            if (isServer)
            {
                // Host 自己的输入直接生效，不经过网络
                ApplyInput(state, jump);
                return;
            }

            // 移动输入每步都发，丢一包下一包就补上，走不可靠通道降低延迟
            CmdSubmitInput(state);
            if (jump)
            {
                // 跳跃是一次性事件，必须可靠送达
                CmdJump();
            }
        }

        [Command(channel = Channels.Unreliable)]
        private void CmdSubmitInput(PlayerInputState input)
        {
            ApplyInput(input, false);
        }

        [Command]
        private void CmdJump()
        {
            _serverJumpRequested = true;
        }

        #endregion

        #region Server：模拟

        [Server]
        private void ApplyInput(PlayerInputState input, bool jump)
        {
            _serverJumpRequested |= jump;

            // 不可靠通道可能乱序，只接受更新的输入
            if (_hasServerInput && input.Sequence <= _serverInput.Sequence)
            {
                return;
            }

            // 防止客户端发超长向量加速
            input.Move = Vector2.ClampMagnitude(input.Move, 1f);
            _serverInput = input;
            _hasServerInput = true;
        }

        [Server]
        private void Simulate(float deltaTime)
        {
            Quaternion facing = Quaternion.Euler(0f, _serverInput.Yaw, 0f);
            transform.rotation = facing;

            float speed = _serverInput.Sprint ? sprintSpeed : walkSpeed;
            Vector3 move = new Vector3(_serverInput.Move.x, 0f, _serverInput.Move.y);
            Vector3 targetVelocity = facing * move * speed;
            _planarVelocity = Vector3.MoveTowards(_planarVelocity, targetVelocity, acceleration * deltaTime);

            bool grounded = _controller.isGrounded;
            if (grounded && _verticalVelocity < 0f)
            {
                // 保持轻微下压，isGrounded 才稳定
                _verticalVelocity = -2f;
            }

            if (_serverJumpRequested && grounded)
            {
                _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            _serverJumpRequested = false;

            _verticalVelocity += gravity * deltaTime;
            _controller.Move((_planarVelocity + Vector3.up * _verticalVelocity) * deltaTime);

            if (_animation != null)
            {
                // 服务端位置只在物理步更新，直接把实际速度（撞墙时为 0）交给动画，不让动画按渲染帧差分估算
                _animation.ReportSimulatedVelocity(_controller.velocity);
            }
        }

        /// <summary>
        /// 推动共享球体：CharacterController 是运动学控制器，移动时不会对动态刚体施加力，
        /// 需要在碰撞回调里手动加力。回调只在移动 CharacterController 的一端触发，
        /// 而玩家移动只在服务器模拟（含 Host），所以推力天然全部施加在服务端：
        /// 多名玩家同一物理帧推球时，力叠加在同一个 PhysX 世界里，球只有一个权威状态。
        /// </summary>
        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (!isServer)
            {
                return;
            }

            Rigidbody body = hit.collider.attachedRigidbody;
            if (body == null || body.isKinematic)
            {
                return;
            }

            // 站到球上时不把球踩走
            if (hit.moveDirection.y < -0.3f)
            {
                return;
            }

            // 速度趋近式推力：把球的水平速度推向玩家当前速度（走路推慢球、疾跑推快球），
            // 球比玩家快时不拽回，滚远后追上再推
            Vector3 playerVelocity = new Vector3(_planarVelocity.x, 0f, _planarVelocity.z);
            Vector3 delta = playerVelocity - new Vector3(body.linearVelocity.x, 0f, body.linearVelocity.z);
            if (Vector3.Dot(delta, playerVelocity) <= 0f)
            {
                return;
            }

            body.AddForce(delta * (pushAccel * body.mass));
        }

        #endregion
    }
}
