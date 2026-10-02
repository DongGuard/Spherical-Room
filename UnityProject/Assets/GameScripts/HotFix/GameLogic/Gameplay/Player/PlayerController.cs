using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Mirror;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic
{
    /// <summary>
    /// 第一人称玩家控制（主机权威，Mirror）。
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

        [Header("Push Dynamics (物理手感精调)")]
        [Tooltip("持续推球加速度，建议 18-25 之间")]
        [SerializeField] private float pushAcceleration = 20f;
        [Tooltip("推球速度上限倍率，1.0~1.1 保证球平稳贴在身前不抽搐抖动")]
        [SerializeField] private float pushSpeedRatio = 1.05f;
        [Tooltip("起步撞击冲量强度，赋予跑动撞球清脆的击球感")]
        [SerializeField] private float impactImpulse = 2.5f;
        [Tooltip("给球施加自然滚动力的系数")]
        [SerializeField] private float rollingTorqueFactor = 1.5f;
        [Tooltip("推力最大速度上限（米/秒），允许反弹保留速度，仅限制推力主动加速")]
        [SerializeField] private float maxPushSpeed = 9f;

        private TMP_Text _tappedText;
        private bool _tappedVisualShowing;

        private const string TappedFontLocation = "MaoKenZhuYuanTi-MaokenZhuyuanTi-2 SDF";
        private TMP_FontAsset _tappedFont;

        private CharacterController _controller;
        private PlayerAnimationController _animation;
        private uint _sequence;

        private PlayerInputState _serverInput;
        private bool _hasServerInput;
        private bool _serverJumpRequested;
        private Vector3 _planarVelocity;
        private float _verticalVelocity;

        // 本步被暂时改成运动学的球，Move 结束后立刻还原速度，避免球被冻住
        private readonly List<GatedBall> _gatedBalls = new List<GatedBall>(4);

        private float _nextImpactTime;

        private static readonly Collider[] s_OverlapBuffer = new Collider[16];
        private static readonly RaycastHit[] s_CastBuffer = new RaycastHit[8];

        private struct GatedBall
        {
            public Rigidbody Body;
            public Vector3 LinearVelocity;
            public Vector3 AngularVelocity;
        }

        public static PlayerController Local { get; private set; }

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
            if (eyeCamera == null) return;
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
            PreloadTappedFontAsync().Forget();
        }

        private async UniTaskVoid PreloadTappedFontAsync()
        {
            _tappedFont = await GameModule.Resource.LoadAssetAsync<TMP_FontAsset>(TappedFontLocation);
        }

        public override void OnStopLocalPlayer()
        {
            if (Local != this) return;
            Local = null;

            if (CameraManager.IsValid)
            {
                CameraManager.Instance.Detach();
            }

            if (InputManager.IsValid)
            {
                InputManager.Instance.SetGameplayEnabled(false);
            }

            if (_tappedText != null)
            {
                Object.Destroy(_tappedText.gameObject);
                _tappedText = null;
            }

            _tappedVisualShowing = false;
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

        private void Update()
        {
            if (!isLocalPlayer)
            {
                return;
            }

            EnsureLocalControl();
            UpdateTappedVisual();
        }

        /// <summary>
        /// 场景切换可能在 OnStartLocalPlayer 之后把视角相机拆掉，本地玩家每帧补一次。
        /// </summary>
        private void EnsureLocalControl()
        {
            if (!InputManager.IsValid || !InputManager.Instance.GameplayEnabled)
            {
                InputManager.Instance.SetGameplayEnabled(true);
            }

            if (eyeCamera != null && !CameraManager.Instance.IsAttached)
            {
                CameraManager.Instance.AttachFirstPerson(eyeCamera, transform.eulerAngles.y, modelRoot);
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
                Yaw = CameraManager.Instance.IsAttached ? CameraManager.Instance.Yaw : transform.eulerAngles.y
            };
            bool jump = input.ConsumeJump();

            if (isServer)
            {
                ApplyInput(state, jump);
                return;
            }

            CmdSubmitInput(state);
            if (jump)
            {
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

        #region Server：物理模拟核心

        [Server]
        private void ApplyInput(PlayerInputState input, bool jump)
        {
            _serverJumpRequested |= jump;

            if (_hasServerInput && input.Sequence <= _serverInput.Sequence)
            {
                return;
            }

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
                _verticalVelocity = -2f;
            }

            if (_serverJumpRequested && grounded)
            {
                _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            _serverJumpRequested = false;

            _verticalVelocity += gravity * deltaTime;

            // 贴墙的球只在这一次 Move 里临时当作静态障碍，结束后还原速度
            BeginBallGate();
            try
            {
                _controller.Move((_planarVelocity + Vector3.up * _verticalVelocity) * deltaTime);
            }
            finally
            {
                EndBallGate();
            }

            if (_animation != null)
            {
                _animation.ReportSimulatedVelocity(_controller.velocity);
            }
        }

        /// <summary>
        /// 身前的共享球如果正前方就是墙或另一名玩家，这一次移动先改成运动学挡住角色。
        /// 斜向还能沿墙滚动的球保持动态。地面不算挡住。
        /// </summary>
        private void BeginBallGate()
        {
            _gatedBalls.Clear();
            if (_controller == null)
            {
                return;
            }

            Vector3 moveDir = new Vector3(_planarVelocity.x, 0f, _planarVelocity.z);
            float moveSpeed = moveDir.magnitude;
            if (moveSpeed < 0.2f)
            {
                return;
            }

            moveDir /= moveSpeed;
            float lookahead = moveSpeed * Time.fixedDeltaTime + 0.35f;
            Vector3 probeCenter = transform.position + moveDir * (_controller.radius + 0.5f);
            int count = Physics.OverlapSphereNonAlloc(
                probeCenter,
                _controller.radius + 3.8f,
                s_OverlapBuffer,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Rigidbody rb = s_OverlapBuffer[i].attachedRigidbody;
                if (rb == null || rb.isKinematic || rb.GetComponent<NetworkRigidbodyReliable>() == null)
                {
                    continue;
                }

                if (IsAlreadyGated(rb))
                {
                    continue;
                }

                Vector3 toBall = rb.position - transform.position;
                toBall.y = 0f;
                float dist = toBall.magnitude;
                if (dist < 0.3f || Vector3.Dot(toBall / dist, moveDir) < 0.5f)
                {
                    continue;
                }

                if (!TryGetBlockingHit(rb, moveDir, lookahead, out RaycastHit obstacle))
                {
                    continue;
                }

                Vector3 slide = Vector3.ProjectOnPlane(moveDir, obstacle.normal);
                slide.y = 0f;
                if (slide.sqrMagnitude > 0.08f)
                {
                    Vector3 wallNormal = obstacle.normal;
                    wallNormal.y = 0f;
                    if (wallNormal.sqrMagnitude < 0.0001f)
                    {
                        continue;
                    }

                    wallNormal.Normalize();
                    float intoWall = Vector3.Dot(_planarVelocity, -wallNormal);
                    if (intoWall > 0f)
                    {
                        _planarVelocity += wallNormal * intoWall;
                    }

                    continue;
                }

                Vector3 intoBall = toBall / dist;
                float inward = Vector3.Dot(_planarVelocity, intoBall);
                if (inward > 0f)
                {
                    _planarVelocity -= intoBall * inward;
                }

                _gatedBalls.Add(new GatedBall
                {
                    Body = rb,
                    LinearVelocity = rb.linearVelocity,
                    AngularVelocity = rb.angularVelocity
                });
                rb.isKinematic = true;
            }
        }

        private void EndBallGate()
        {
            for (int i = 0; i < _gatedBalls.Count; i++)
            {
                GatedBall gated = _gatedBalls[i];
                if (gated.Body == null)
                {
                    continue;
                }

                gated.Body.isKinematic = false;
                gated.Body.linearVelocity = gated.LinearVelocity;
                gated.Body.angularVelocity = gated.AngularVelocity;
            }

            _gatedBalls.Clear();
        }

        private bool IsAlreadyGated(Rigidbody rb)
        {
            for (int i = 0; i < _gatedBalls.Count; i++)
            {
                if (_gatedBalls[i].Body == rb)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 球沿 direction 前方 distance 内是否有墙、静态物体或另一名玩家。地面和其他自由球不算挡住。
        /// </summary>
        private bool TryGetBlockingHit(Rigidbody rb, Vector3 direction, float distance, out RaycastHit blocking)
        {
            blocking = default;
            float radius = GetSphereRadius(rb);
            const float castRadius = 0.35f;
            // 从球心前方、仍在球体内部的位置射出，已经贴住的墙也在射线前方
            Vector3 origin = rb.worldCenterOfMass + direction * (radius * 0.5f);
            int count = Physics.SphereCastNonAlloc(
                origin,
                castRadius,
                direction,
                s_CastBuffer,
                radius * 0.5f + distance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            float bestDistance = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_CastBuffer[i];
                if (hit.collider == null || hit.rigidbody == rb)
                {
                    continue;
                }

                Transform hitTransform = hit.collider.transform;
                if (hitTransform == transform || hitTransform.IsChildOf(transform))
                {
                    continue;
                }

                if (hit.normal.y > 0.5f)
                {
                    continue;
                }

                Rigidbody other = hit.rigidbody;
                if (other != null && !other.isKinematic && other.GetComponent<CharacterController>() == null)
                {
                    continue;
                }

                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    blocking = hit;
                    found = true;
                }
            }

            return found;
        }

        private static float GetSphereRadius(Rigidbody rb)
        {
            SphereCollider sphere = rb.GetComponent<SphereCollider>();
            if (sphere == null)
            {
                return 0.5f;
            }

            Vector3 scale = sphere.transform.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            return sphere.radius * maxScale;
        }

        /// <summary>
        /// 推动共享球体。CharacterController 不会对刚体施力，碰撞时由主机加力。
        /// 正前方是墙时不加力，斜向接触则沿墙滑。
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

            if (hit.moveDirection.y < -0.3f)
            {
                Vector3 vertical = body.linearVelocity;
                if (vertical.y > 0f)
                {
                    vertical.y = 0f;
                    body.linearVelocity = vertical;
                }

                return;
            }

            Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            if (pushDir.sqrMagnitude < 0.001f)
            {
                pushDir = -hit.normal;
                pushDir.y = 0f;
            }

            if (pushDir.sqrMagnitude < 0.001f)
            {
                return;
            }

            pushDir.Normalize();
            Vector3 effectivePushDir = pushDir;
            if (TryGetBlockingHit(body, pushDir, 0.35f, out RaycastHit wallHit))
            {
                Vector3 slideTangent = Vector3.ProjectOnPlane(pushDir, wallHit.normal);
                slideTangent.y = 0f;
                if (slideTangent.sqrMagnitude <= 0.08f)
                {
                    return;
                }

                effectivePushDir = slideTangent.normalized;
            }

            float playerSpeedInPushDir = Vector3.Dot(_planarVelocity, effectivePushDir);
            if (playerSpeedInPushDir <= 0.05f)
            {
                return;
            }

            float currentBallSpeed = Vector3.Dot(body.linearVelocity, effectivePushDir);
            float targetBallSpeed = Mathf.Min(playerSpeedInPushDir * pushSpeedRatio, maxPushSpeed);
            float speedDifference = targetBallSpeed - currentBallSpeed;
            if (speedDifference <= 0f)
            {
                return;
            }

            if (currentBallSpeed < 1f &&
                playerSpeedInPushDir > walkSpeed * 0.7f &&
                Time.time >= _nextImpactTime)
            {
                _nextImpactTime = Time.time + 0.4f;
                body.AddForce(effectivePushDir * impactImpulse, ForceMode.VelocityChange);
            }

            float accel = Mathf.Min(speedDifference / Time.fixedDeltaTime, pushAcceleration);
            body.AddForce(effectivePushDir * accel, ForceMode.Acceleration);

            float radius = GetSphereRadius(body);
            if (radius > 0.01f)
            {
                Vector3 torqueAxis = Vector3.Cross(Vector3.up, effectivePushDir);
                body.AddTorque(torqueAxis * (accel / radius * rollingTorqueFactor), ForceMode.Acceleration);
            }
        }

        #endregion

        #region 碰触提示（零 GC 极速检测）

        private void UpdateTappedVisual()
        {
            bool touching = IsTouchingBall();
            if (touching == _tappedVisualShowing)
            {
                return;
            }

            _tappedVisualShowing = touching;
            if (touching)
            {
                if (_tappedText == null)
                {
                    CreateTappedTip();
                }

                if (_tappedText != null)
                {
                    _tappedText.gameObject.SetActive(true);
                }
            }
            else if (_tappedText != null)
            {
                _tappedText.gameObject.SetActive(false);
            }
        }

        /// <summary>采用 NonAlloc 零 GC 探针，彻底根除高刷新率下的掉帧卡顿</summary>
        private bool IsTouchingBall()
        {
            if (_controller == null)
            {
                return false;
            }

            Vector3 center = transform.position + _controller.center;
            float radius = _controller.radius + 0.25f;

            int count = Physics.OverlapSphereNonAlloc(center, radius, s_OverlapBuffer);
            for (int i = 0; i < count; i++)
            {
                Rigidbody rb = s_OverlapBuffer[i].attachedRigidbody;
                if (rb != null && rb.GetComponent<NetworkRigidbodyReliable>() != null)
                {
                    return true;
                }
            }

            return false;
        }

        private void CreateTappedTip()
        {
            if (_tappedFont == null)
            {
                Debug.LogWarning("[Player] 碰触提示字体未就绪，跳过显示");
                return;
            }

            Transform root = UIModule.UIRoot;
            if (root == null)
            {
                Debug.LogWarning("[Player] UIRoot 不存在，无法显示碰触提示");
                return;
            }

            GameObject go = new GameObject("TappedTip", typeof(RectTransform));
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.font = _tappedFont;
            text.text = "Tapped";
            text.fontSize = 54;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(1f, 0.9f, 0.2f, 1f);
            text.raycastTarget = false;

            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(root, false);
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -120f);
            rect.sizeDelta = new Vector2(300f, 80f);
            go.SetActive(false);
            _tappedText = text;
        }

        #endregion
    }
}