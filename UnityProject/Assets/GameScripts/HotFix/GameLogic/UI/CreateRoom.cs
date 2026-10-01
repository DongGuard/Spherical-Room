using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine.UI;
using Log = TEngine.Log;

namespace GameLogic
{
    /// <summary>
    /// 创建房间窗口
    /// </summary>
    [Window(UILayer.UI)]
    class CreateRoom : UIWindow
    {
        #region 脚本工具生成的代码
        private TMP_InputField _inputName;
        private Toggle _toggleOpenPass;
        private TMP_InputField _inputPass;
        private Button _btnCreate;
        protected override void ScriptGenerator()
        {
            _inputName = FindChildComponent<TMP_InputField>("Back/m_inputName");
            _toggleOpenPass = FindChildComponent<Toggle>("Back/m_toggleOpenPass");
            _inputPass = FindChildComponent<TMP_InputField>("Back/m_toggleOpenPass/m_inputPass");
            _btnCreate = FindChildComponent<Button>("Back/m_btnCreate");
        }
        #endregion

        private bool _creating;

        protected override void OnCreate()
        {
            if (_inputName == null || _toggleOpenPass == null || _inputPass == null || _btnCreate == null)
            {
                Log.Error("[CreateRoom] 控件绑定失败，请检查预制体节点路径和组件类型（输入框为 TMP_InputField）");
            }

            _toggleOpenPass.onValueChanged.AddListener(OnToggleOpenPassChange);
            _btnCreate.onClick.AddListener(OnClickCreateBtn);
            OnToggleOpenPassChange(_toggleOpenPass.isOn);
        }

        protected override void OnDestroy()
        {
            _toggleOpenPass.onValueChanged.RemoveListener(OnToggleOpenPassChange);
            _btnCreate.onClick.RemoveListener(OnClickCreateBtn);
        }

        #region 事件
        private void OnToggleOpenPassChange(bool isOn)
        {
            if (_inputPass != null)
            {
                _inputPass.gameObject.SetActive(isOn);
            }
        }

        private void OnClickCreateBtn()
        {
            CreateAsync().Forget();
        }
        #endregion

        private async UniTaskVoid CreateAsync()
        {
            if (_creating)
            {
                return;
            }

            bool hasPassword = _toggleOpenPass.isOn;
            var settings = new RoomSettings
            {
                Name = _inputName != null ? _inputName.text : string.Empty,
                HasPassword = hasPassword,
                Password = hasPassword && _inputPass != null ? _inputPass.text : string.Empty,
                MaxPlayers = RoomProtocol.MaxPlayers,
                AllowMidJoin = true
            };

            _creating = true;
            if (_btnCreate != null)
            {
                _btnCreate.interactable = false;
            }

            RoomOpResult result = await RoomManager.Instance.CreateRoomAsync(settings);
            if (result.Success)
            {
                LobbyUIFlow.Instance.ShowRoom();
            }
            else
            {
                if (!result.IsCancelled)
                {
                    Log.Warning($"[CreateRoom] 创建房间失败: {result.Message}");
                }

                if (!IsDestroyed && _btnCreate != null)
                {
                    _btnCreate.interactable = true;
                }
            }

            _creating = false;
        }
    }
}
