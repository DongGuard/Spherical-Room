using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 相机管理：在场景相机和本地玩家的眼睛相机之间切换，并计算第一人称视角（Yaw / Pitch）。
    /// 每个玩家预制体自带眼睛相机，只有本地玩家的会被启用。
    /// </summary>
    public class CameraManager : SingletonBehaviour<CameraManager>
    {
        public float MouseSensitivity { get; set; } = 2f;
        public float MinPitch { get; set; } = -80f;
        public float MaxPitch { get; set; } = 80f;
        public float FieldOfView { get; set; } = 75f;
        public float NearClip { get; set; } = 0.05f;

        private const string MainCameraTag = "MainCamera";

        /// <summary>当前使用的本地玩家眼睛相机。</summary>
        private Camera _camera;
        private AudioListener _eyeListener;
        private string _eyeOriginalTag;

        /// <summary>被临时关闭的场景相机（菜单 / 观察用），Detach 时恢复。</summary>
        private Camera _sceneCamera;
        private AudioListener _sceneListener;

        /// <summary>
        /// 本机玩家模型所在层。眼睛相机剔除该层，其他相机（Scene 视图等）照常渲染。
        /// 未在 Tags and Layers 中命名时回退到第 31 层（未命名的层也可以使用）。
        /// </summary>
        private const string LocalPlayerLayerName = "LocalPlayer";
        private const int FallbackLocalPlayerLayer = 31;

        private GameObject _hiddenModel;
        private Transform[] _hiddenTransforms;
        private int[] _originalLayers;
        private int _eyeOriginalCullingMask;

        /// <summary>水平朝向（度）。</summary>
        public float Yaw { get; private set; }

        /// <summary>俯仰角（度），向下为正。</summary>
        public float Pitch { get; private set; }

        /// <summary>是否已挂载到本地玩家。</summary>
        public bool IsAttached => _camera != null;

        public Camera Camera => _camera;

        /// <summary>
        /// 切换到本地玩家自带的眼睛相机：关闭场景相机，启用眼睛相机和 AudioListener，
        /// 并让眼睛相机不渲染本地玩家模型（第一人称不挡视线，其他相机仍可见）。
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
            return true;
        }

        /// <summary>
        /// 退出第一人称：关闭眼睛相机，恢复场景相机。玩家销毁前调用。
        /// 眼睛相机已随玩家销毁时，Unity 的 null 判断会跳过对应步骤。
        /// </summary>
        public void Detach()
        {
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

        private void LateUpdate()
        {
            if (_camera == null)
            {
                return;
            }

            // LateUpdate 在所有 Update 之后，InputManager 本帧的鼠标位移已经就绪
            Vector2 look = InputManager.Instance.LookDelta * MouseSensitivity;
            Yaw = Mathf.Repeat(Yaw + look.x, 360f);
            Pitch = Mathf.Clamp(Pitch - look.y, MinPitch, MaxPitch);
            ApplyRotation();
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
        /// 模型本身仍正常渲染，Scene 视图和其他相机都能看到；
        /// 其他玩家的模型不受影响（每台机器只处理自己的本地玩家）。
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
