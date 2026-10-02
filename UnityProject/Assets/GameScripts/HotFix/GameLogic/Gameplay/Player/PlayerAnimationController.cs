using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 玩家动画状态。
    /// </summary>
    public enum PlayerAnimState
    {
        /// <summary>
        /// 地面移动：待机 / 走 / 跑混合树，由 Speed 参数驱动。
        /// </summary>
        Locomotion,

        /// <summary>
        /// 起跳上升段。
        /// </summary>
        Jump,

        /// <summary>
        /// 下落：跳跃的下落段，或从高处走下。
        /// </summary>
        Fall
    }

    /// <summary>
    /// 玩家动画统一管理：Animator 的参数和状态切换只在这里处理，其他脚本不直接操作 Animator。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimationController : MonoBehaviour
    {
        #region Animator 参数与状态（名称与 PlayerAnimator.controller 保持一致）

        private const int BaseLayer = 0;

        private static readonly int SpeedParam = Animator.StringToHash("Speed");

        private const string LocomotionStateName = "Locomotion";
        private const string JumpStateName = "Jump";
        private const string FallStateName = "Fall";

        #endregion

        [Tooltip("位移参考节点，为空时使用父节点（玩家根节点）")]
        [SerializeField] private Transform body;

        [Header("Locomotion")]
        [Tooltip("速度平滑时间（秒），只用来消除帧间抖动")]
        [SerializeField] private float speedSmoothTime = 0.1f;
        [Tooltip("单帧位移超过该距离（米）视为瞬移（出生 / 纠正），不计入速度")]
        [SerializeField] private float teleportDistance = 2f;

        [Header("Air")]
        [SerializeField] private float groundCheckDistance = 0.25f;
        [SerializeField] private LayerMask groundMask = ~0;
        [Tooltip("离地超过该时间才进入下落，避免走下台阶、斜坡时闪一下")]
        [SerializeField] private float airborneGraceTime = 0.15f;
        [Tooltip("离地时向上的速度超过该值（米/秒）视为起跳")]
        [SerializeField] private float jumpDetectSpeed = 1f;

        [Header("Transition")]
        [Tooltip("状态切换的混合时长（秒）")]
        [SerializeField] private float crossFadeTime = 0.1f;

        private Animator _animator;
        private CharacterController _characterController;

        // 控制器实际提供的参数和状态，Animator 就绪后检查一次。
        // 状态用完整路径（层名.状态名）的哈希，0 表示控制器里没有该状态
        private bool _resolved;
        private bool _hasSpeedParam;
        private int _locomotionState;
        private int _jumpState;
        private int _fallState;

        /// <summary>
        /// Animator 当前所在（或正在过渡到）的状态，0 表示需要重新读取。
        /// </summary>
        private int _playingState;

        private bool _hasSimulatedVelocity;
        private Vector3 _simulatedVelocity;
        private bool _hasLastPosition;
        private Vector3 _lastPosition;
        private Vector3 _velocity;
        private float _airTime;

        /// <summary>
        /// 当前动画状态。
        /// </summary>
        public PlayerAnimState State { get; private set; }

        /// <summary>
        /// 平滑后的水平速度（米/秒），即写入 Speed 参数的值。
        /// </summary>
        public float Speed { get; private set; }

        /// <summary>
        /// 脚下是否检测到地面（不含离地宽限时间）。
        /// </summary>
        public bool IsGrounded { get; private set; } = true;

        /// <summary>
        /// 服务端每个物理步由 PlayerController 调用，上报 CharacterController 的实际速度（撞墙时为 0）。
        /// </summary>
        public void ReportSimulatedVelocity(Vector3 velocity)
        {
            _simulatedVelocity = velocity;
            _hasSimulatedVelocity = true;
        }

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            // 移动完全由 CharacterController 决定，动画不能带位移
            _animator.applyRootMotion = false;
            if (body == null)
            {
                body = transform.parent != null ? transform.parent : transform;
            }

            _characterController = body.GetComponent<CharacterController>();
        }

        private void OnEnable()
        {
            // Animator 重新启用后会回到默认状态，运动数据也从头采样
            _playingState = 0;
            _hasLastPosition = false;
            _velocity = Vector3.zero;
            _airTime = 0f;
            Speed = 0f;
            IsGrounded = true;
            State = PlayerAnimState.Locomotion;
        }

        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f || !EnsureResolved())
            {
                return;
            }

            UpdateVelocity(deltaTime);
            IsGrounded = CheckGrounded();
            _airTime = IsGrounded ? 0f : _airTime + deltaTime;

            Speed = new Vector2(_velocity.x, _velocity.z).magnitude;
            if (_hasSpeedParam)
            {
                _animator.SetFloat(SpeedParam, Speed);
            }

            SetState(DecideState());
        }

        #region 运动采样

        private void UpdateVelocity(float deltaTime)
        {
            Vector3 position = body.position;
            Vector3 measured;
            if (_hasSimulatedVelocity)
            {
                measured = _simulatedVelocity;
            }
            else if (_hasLastPosition &&
                     (position - _lastPosition).sqrMagnitude <= teleportDistance * teleportDistance)
            {
                measured = (position - _lastPosition) / deltaTime;
            }
            else
            {
                // 首帧或瞬移：这一帧不计入速度
                measured = _velocity;
            }

            _lastPosition = position;
            _hasLastPosition = true;

            float t = speedSmoothTime > 0f ? 1f - Mathf.Exp(-deltaTime / speedSmoothTime) : 1f;
            _velocity = Vector3.Lerp(_velocity, measured, t);
        }

        private bool CheckGrounded()
        {
            // 从胶囊底部稍上方向下检测（胶囊底部不一定与根节点原点重合）；
            // 射线起点在自身胶囊内时不会命中自己。纯客户端上 CharacterController 被禁用，但尺寸仍可读取。
            const float lift = 0.1f;
            Vector3 origin = GetFootPosition() + Vector3.up * lift;
            return Physics.Raycast(origin, Vector3.down, lift + groundCheckDistance, groundMask,
                QueryTriggerInteraction.Ignore);
        }

        private Vector3 GetFootPosition()
        {
            if (_characterController == null)
            {
                return body.position;
            }

            float bottom = _characterController.center.y - _characterController.height * 0.5f;
            return body.TransformPoint(new Vector3(_characterController.center.x, bottom, _characterController.center.z));
        }

        #endregion

        #region 状态切换

        private PlayerAnimState DecideState()
        {
            if (IsGrounded)
            {
                return PlayerAnimState.Locomotion;
            }

            float verticalSpeed = _velocity.y;
            switch (State)
            {
                case PlayerAnimState.Jump:
                    // 上升段保持起跳动画，开始下落后切到下落循环
                    return verticalSpeed > 0f ? PlayerAnimState.Jump : PlayerAnimState.Fall;

                case PlayerAnimState.Locomotion:
                    // 离地且明显向上即为起跳。由运动推导而不是监听跳跃事件，各端都和插值后的位置同步
                    if (verticalSpeed > jumpDetectSpeed)
                    {
                        return PlayerAnimState.Jump;
                    }

                    // 普通离地（走下台阶、斜坡）超过宽限时间才进入下落
                    return _airTime >= airborneGraceTime ? PlayerAnimState.Fall : PlayerAnimState.Locomotion;

                default:
                    return PlayerAnimState.Fall;
            }
        }

        private void SetState(PlayerAnimState state)
        {
            State = state;
            if (_locomotionState == 0)
            {
                return;
            }

            if (_playingState == 0)
            {
                _playingState = _animator.GetCurrentAnimatorStateInfo(BaseLayer).fullPathHash;
            }

            int target = ResolveState(state);
            if (target == _playingState)
            {
                return;
            }

            _animator.CrossFadeInFixedTime(target, crossFadeTime, BaseLayer);
            _playingState = target;
        }

        /// <summary>
        /// 控制器里缺少的状态退回到已有状态：缺 Jump 用 Fall，缺 Fall 用 Locomotion。
        /// </summary>
        private int ResolveState(PlayerAnimState state)
        {
            if (state == PlayerAnimState.Jump && _jumpState != 0)
            {
                return _jumpState;
            }

            if (state != PlayerAnimState.Locomotion && _fallState != 0)
            {
                return _fallState;
            }

            return _locomotionState;
        }

        /// <summary>
        /// Animator 就绪后检查一次控制器提供了哪些参数和状态，缺失时提示一次。
        /// </summary>
        private bool EnsureResolved()
        {
            if (_resolved)
            {
                return true;
            }

            // 未配置 AnimatorController 时操作 Animator 会刷警告，直接跳过
            if (_animator.runtimeAnimatorController == null || !_animator.isInitialized)
            {
                return false;
            }

            _hasSpeedParam = HasParameter(SpeedParam, AnimatorControllerParameterType.Float);
            string layerName = _animator.GetLayerName(BaseLayer);
            _locomotionState = FindState(layerName, LocomotionStateName);
            _jumpState = FindState(layerName, JumpStateName);
            _fallState = FindState(layerName, FallStateName);
            _resolved = true;

            string missing = string.Empty;
            if (!_hasSpeedParam)
            {
                missing += " Speed(Float 参数)";
            }

            if (_locomotionState == 0)
            {
                missing += $" {LocomotionStateName}(状态)";
            }

            if (_jumpState == 0)
            {
                missing += $" {JumpStateName}(状态)";
            }

            if (_fallState == 0)
            {
                missing += $" {FallStateName}(状态)";
            }

            if (missing.Length > 0)
            {
                Debug.LogWarning($"[PlayerAnimation] {_animator.runtimeAnimatorController.name} 的 {layerName} 缺少{missing}，对应动画会退回到已有状态");
            }

            return true;
        }

        private int FindState(string layerName, string stateName)
        {
            int hash = Animator.StringToHash(layerName + "." + stateName);
            return _animator.HasState(BaseLayer, hash) ? hash : 0;
        }

        private bool HasParameter(int nameHash, AnimatorControllerParameterType type)
        {
            // parameters 每次访问都会分配数组，只在初始化时调用
            foreach (AnimatorControllerParameter parameter in _animator.parameters)
            {
                if (parameter.nameHash == nameHash && parameter.type == type)
                {
                    return true;
                }
            }

            return false;
        }

        #endregion
    }
}
