using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 相机管理：在场景相机和本地玩家的眼睛相机之间切换，并计算第一人称视角（Yaw / Pitch）。
    /// </summary>
    public class CameraManager : SingletonBehaviour<CameraManager>
    {
        public float MouseSensitivity { get; set; } = 2f;
        public float MinPitch { get; set; } = -80f;
        public float MaxPitch { get; set; } = 80f;
        public float FieldOfView { get; set; } = 75f;
        public float NearClip { get; set; } = 0.05f;

        private const string MainCameraTag = "MainCamera";

        /// <summary>
        /// 当前使用的本地玩家眼睛相机。
        /// </summary>
        private Camera _camera;
        private AudioListener _eyeListener;
        private string _eyeOriginalTag;

        /// <summary>
        /// 被临时关闭的场景相机（菜单 / 观察用），Detach 时恢复。
        /// </summary>
        private Camera _sceneCamera;
        private AudioListener _sceneListener;

        /// <summary>
        /// 本机玩家模型所在层。眼睛相机剔除该层，其他相机（Scene 视图等）照常渲染。
        /// </summary>
        private const string LocalPlayerLayerName = "LocalPlayer";
        private const int FallbackLocalPlayerLayer = 31;

        private GameObject _hiddenModel;
        private Transform[] _hiddenTransforms;
        private int[] _originalLayers;
        private int _eyeOriginalCullingMask;

        /// <summary>
        /// 水平朝向（度）。
        /// </summary>
        public float Yaw { get; private set; }

        /// <summary>
        /// 俯仰角（度），向下为正。
        /// </summary>
        public float Pitch { get; private set; }

        /// <summary>
        /// 是否已挂载到本地玩家。
        /// </summary>
        public bool IsAttached => _camera != null;

        public Camera Camera => _camera;

        /// <summary>
        /// 切换到本地玩家自带的眼睛相机：关闭场景相机，启用眼睛相机和 AudioListener，
        /// </summary>
        /// <param name="eyeCamera">玩家预制体上的眼睛相机（预制体中默认关闭）。</param>
        /// <param name="initialYaw">出生朝向。</param>
        /// <param name="modelRoot">需要隐藏的模型根节点，可为空。</param>
        public bool AttachFirstPerson(Camera eyeCamera, float initialYaw, GameObject modelRoot)
        {
            Detach();

            if (eyeCamera == null)
            {
                Debug.LogError("[CameraManager] 本地玩家没有眼睛相机，无法进入第一人称");
                return false;
            }

            // 场景相机只在未进入房间时使用，进入后关闭，避免双相机渲染和多个 AudioListener
            Camera sceneCamera = Camera.main;
            if (sceneCamera != null && sceneCamera != eyeCamera)
            {
                _sceneCamera = sceneCamera;
                _sceneCamera.enabled = false;
                _sceneListener = _sceneCamera.GetComponent<AudioListener>();
                if (_sceneListener != null)
                {
                    _sceneListener.enabled = false;
                }
            }

            _camera = eyeCamera;
            _eyeOriginalTag = _camera.tag;
            // 让其他代码的 Camera.main 指向本地玩家的眼睛
            _camera.tag = MainCameraTag;
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = NearClip;
            _camera.enabled = true;

            _eyeListener = _camera.GetComponent<AudioListener>();
            if (_eyeListener != null)
            {
                _eyeListener.enabled = true;
            }

            Yaw = initialYaw;
            Pitch = 0f;
            ApplyRotation();

            _eyeOriginalCullingMask = _camera.cullingMask;
            HideFromEye(modelRoot);
            CenterEyeOnCapsuleAxis();
            return true;
        }

        /// <summary>
        /// 退出第一人称：关闭眼睛相机，恢复场景相机。玩家销毁前调用。
        /// </summary>
        public void Detach()
        {
            if (_camera != null)
            {
                Debug.Log("[CameraManager][诊断] Detach（相机交还场景）", _camera);
            }

            RestoreModelLayers();

            if (_camera != null)
            {
                _camera.cullingMask = _eyeOriginalCullingMask;
                _camera.enabled = false;
                _camera.tag = string.IsNullOrEmpty(_eyeOriginalTag) ? "Untagged" : _eyeOriginalTag;
            }

            if (_eyeListener != null)
            {
                _eyeListener.enabled = false;
            }

            if (_sceneCamera != null)
            {
                _sceneCamera.enabled = true;
            }

            if (_sceneListener != null)
            {
                _sceneListener.enabled = true;
            }

            _camera = null;
            _eyeListener = null;
            _eyeOriginalTag = null;
            _sceneCamera = null;
            _sceneListener = null;
        }

        // 相机守卫缓冲（GetCameras 无分配）
        private readonly Camera[] _allCamerasBuffer = new Camera[8];

        private void LateUpdate()
        {
            if (_camera == null)
            {
                return;
            }

            SuppressSceneCameras();

            // LateUpdate 在所有 Update 之后，InputManager 本帧的鼠标位移已经就绪
            Vector2 look = InputManager.Instance.LookDelta * MouseSensitivity;
            Yaw = Mathf.Repeat(Yaw + look.x, 360f);
            Pitch = Mathf.Clamp(Pitch - look.y, MinPitch, MaxPitch);
            ApplyRotation();
        }

        /// <summary>
        /// 第一人称期间压制场景相机：场景切换后激活的主相机（如中途加入时 Room 的 Main Camera）
        /// 可能晚于 Attach 出现，Attach 时的一次性 Camera.main 查找关不到它，这里每帧兜底关闭。
        /// 只关渲染 Default 层的相机（UI 相机等专用相机不动）。
        /// </summary>
        /// <summary>
        /// 把眼相机收回胶囊中心轴（保留高度）：眼相机常挂在模型脸部的位置（胶囊表面前方），
        /// 贴球/贴墙时碰撞面会插入相机近裁剪面导致闪屏；中心线上距任何碰撞面至少一个胶囊半径。
        /// </summary>
        private void CenterEyeOnCapsuleAxis()
        {
            CharacterController controller = _camera.GetComponentInParent<CharacterController>();
            if (controller == null)
            {
                return;
            }

            Transform eye = _camera.transform;
            Vector3 world = eye.position;
            Vector3 center = controller.transform.position + controller.center;
            eye.position = new Vector3(center.x, world.y, center.z);
        }

        private void SuppressSceneCameras()
        {
            int count = Camera.GetAllCameras(_allCamerasBuffer);
            for (int i = 0; i < count; i++)
            {
                Camera cam = _allCamerasBuffer[i];
                if (cam == _camera || !cam.enabled)
                {
                    continue;
                }

                // 不渲染 Default 层的是专用相机（UI、特效），不处理
                if ((cam.cullingMask & (1 << 0)) == 0)
                {
                    continue;
                }

                if (cam != _sceneCamera)
                {
                    _sceneCamera = cam;
                    _sceneListener = cam.GetComponent<AudioListener>();
                }

                cam.enabled = false;
                if (_sceneListener != null && _sceneListener.enabled)
                {
                    _sceneListener.enabled = false;
                }
            }
        }

        private void ApplyRotation()
        {
            // 使用世界朝向，不受主机回传的身体旋转延迟影响
            _camera.transform.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
        }

        private static int GetLocalPlayerLayer()
        {
            int layer = LayerMask.NameToLayer(LocalPlayerLayerName);
            return layer >= 0 ? layer : FallbackLocalPlayerLayer;
        }

        /// <summary>
        /// 只对眼睛相机隐藏本机模型：把模型移到 LocalPlayer 层，并从眼睛相机的 cullingMask 中去掉该层。
        /// </summary>
        private void HideFromEye(GameObject modelRoot)
        {
            if (modelRoot == null)
            {
                return;
            }

            int layer = GetLocalPlayerLayer();
            _hiddenModel = modelRoot;
            _hiddenTransforms = modelRoot.GetComponentsInChildren<Transform>(true);
            _originalLayers = new int[_hiddenTransforms.Length];
            for (int i = 0; i < _hiddenTransforms.Length; i++)
            {
                _originalLayers[i] = _hiddenTransforms[i].gameObject.layer;
                _hiddenTransforms[i].gameObject.layer = layer;
            }

            _camera.cullingMask &= ~(1 << layer);
        }

        private void RestoreModelLayers()
        {
            if (_hiddenTransforms != null && _hiddenModel != null)
            {
                for (int i = 0; i < _hiddenTransforms.Length; i++)
                {
                    if (_hiddenTransforms[i] != null)
                    {
                        _hiddenTransforms[i].gameObject.layer = _originalLayers[i];
                    }
                }
            }

            _hiddenModel = null;
            _hiddenTransforms = null;
            _originalLayers = null;
        }

        protected override void OnDestroy()
        {
            Detach();
            base.OnDestroy();
        }
    }
}
