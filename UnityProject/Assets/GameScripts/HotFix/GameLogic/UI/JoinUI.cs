using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using TEngine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Log = TEngine.Log;

namespace GameLogic
{
    /// <summary>
    /// 加入房间窗口
    /// </summary>
    [Window(UILayer.UI)]
    class JoinUI : UIWindow
    {
        #region 脚本工具生成的代码
        private RectTransform _rectEnterPass;
        private TMP_InputField _mtmp_InputFieldpass;
        private Button _btnCreate;
        private Button _btnExit;
        private RectTransform _content;
        protected override void ScriptGenerator()
        {
            _rectEnterPass = FindChildComponent<RectTransform>("Back/m_rectEnterPass");
            _mtmp_InputFieldpass = FindChildComponent<TMP_InputField>("Back/m_rectEnterPass/Image/mtmp_InputFieldpass");
            _btnCreate = FindChildComponent<Button>("Back/m_btnCreate");
            _btnExit = FindChildComponent<Button>("Back/m_btnExit");
            _content = FindChildComponent<RectTransform>("Back/Scroll View/Viewport/Content");
        }
        #endregion

        /// <summary>
        /// 列表条目（RoomItem.prefab 实例）。
        /// </summary>
        private sealed class RoomItem
        {
            public RoomListEntry Entry;
            public GameObject Root;
            public Image Background;
            public Color NormalColor;
        }

        /// <summary>
        /// 房间条目预制体地址。不能叫 Room：与游戏场景的资源地址冲突。
        /// </summary>
        private const string RoomItemLocation = "RoomItem";

        /// <summary>
        /// 选中态高亮色。
        /// </summary>
        private static readonly Color SelectedColor = new Color(1f, 0.8f, 0.35f);

        private const float TipDuration = 3f;

        private readonly List<RoomItem> _items = new List<RoomItem>();

        private GameObject _entryTemplate;

        private RoomListEntry _selectedEntry;
        private GameObject _selectedItem;
        private Image _selectedBackground;
        private Color _selectedNormalColor = Color.white;

        private bool _joining;

        /// <summary>
        /// 等待玩家输入密码的任务，非空表示密码面板正在等待提交。
        /// </summary>
        private UniTaskCompletionSource<string> _passwordTcs;

        private TMP_Text _textTip;
        private float _tipHideAt;

        /// <summary>
        /// 大厅打开期间的列表刷新间隔（秒），让后创建的房间也能被发现。
        /// </summary>
        private const float ListRefreshInterval = 3f;
        private float _nextListRefresh;

        protected override void OnCreate()
        {
            _btnCreate.onClick.AddListener(OnClickCreateBtn);
            _btnExit.onClick.AddListener(OnClickExitBtn);
            _mtmp_InputFieldpass.onSubmit.AddListener(OnPasswordSubmitted);

            // 密码输入由 RoomManager 的加入流程通过该委托回调，这里负责弹出面板并等待提交
            RoomManager.Instance.PasswordPrompt = PromptPasswordAsync;
            RoomManager.Instance.RoomListUpdated += RebuildItems;

            // 预制体里可能默认显示，运行时强制收起；没有房间数据前不显示"加入房间"
            _rectEnterPass.gameObject.SetActive(false);
            _btnCreate.gameObject.SetActive(false);
            CreateTip();

            LoadTemplateAsync().Forget();
            _nextListRefresh = Time.unscaledTime + ListRefreshInterval;
            RoomManager.Instance.RefreshRoomList();
        }

        protected override void OnRefresh()
        {
            // 窗口被复用时重新拉取一次列表
            RoomManager.Instance.RefreshRoomList();
        }

        protected override void OnUpdate()
        {
            if (_textTip != null && _textTip.gameObject.activeSelf && Time.unscaledTime >= _tipHideAt)
            {
                _textTip.gameObject.SetActive(false);
            }

            // 周期刷新：先开大厅、后建的房间也要能出现在列表里
            if (!_joining && Time.unscaledTime >= _nextListRefresh)
            {
                _nextListRefresh = Time.unscaledTime + ListRefreshInterval;
                RoomManager.Instance.RefreshRoomList();
            }
        }

        protected override void OnDestroy()
        {
            _btnCreate.onClick.RemoveListener(OnClickCreateBtn);
            _btnExit.onClick.RemoveListener(OnClickExitBtn);
            _mtmp_InputFieldpass.onSubmit.RemoveListener(OnPasswordSubmitted);

            if (RoomManager.IsValid)
            {
                RoomManager manager = RoomManager.Instance;
                manager.RoomListUpdated -= RebuildItems;
                if (manager.PasswordPrompt == PromptPasswordAsync)
                {
                    manager.PasswordPrompt = null;
                }
            }

            // 可能在等待密码输入时窗口被关闭，放行等待方避免悬挂
            _passwordTcs?.TrySetResult(null);
            _passwordTcs = null;
        }

        #region 房间列表

        private async UniTaskVoid LoadTemplateAsync()
        {
            try
            {
                _entryTemplate = await GameModule.Resource.LoadAssetAsync<GameObject>(RoomItemLocation);
            }
            catch (Exception e)
            {
                Log.Error($"[JoinUI] 加载房间条目预制体失败: {e}");
            }

            if (!IsDestroyed && _entryTemplate == null)
            {
                Log.Error($"[JoinUI] 房间条目预制体 {RoomItemLocation} 加载失败");
            }

            RebuildItems();
        }

        private void RebuildItems()
        {
            if (IsDestroyed || _entryTemplate == null || _content == null)
            {
                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Root != null)
                {
                    Object.Destroy(_items[i].Root);
                }
            }

            _items.Clear();
            _selectedItem = null;
            _selectedBackground = null;
            RoomListEntry lastSelected = _selectedEntry;
            _selectedEntry = null;

            IReadOnlyList<RoomListEntry> list = RoomManager.Instance.RoomList;
            for (int i = 0; i < list.Count; i++)
            {
                RoomListEntry entry = list[i];
                GameObject root = Object.Instantiate(_entryTemplate, _content);
                root.name = $"Room_{i}";

                TMP_Text nameText = root.GetComponentInChildren<TMP_Text>(true);
                if (nameText != null)
                {
                    nameText.text = entry.Name;
                }

                var item = new RoomItem
                {
                    Entry = entry,
                    Root = root,
                    Background = root.GetComponent<Image>()
                };
                if (item.Background != null)
                {
                    item.NormalColor = item.Background.color;
                }

                Button button = root.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                RoomItem captured = item;
                button.onClick.AddListener(() => SelectItem(captured));
                _items.Add(item);

                // 列表刷新后按 RoomId 恢复选中态；同一房间的恢复不打断正在等待的密码输入
                if (lastSelected != null && entry.RoomId == lastSelected.RoomId)
                {
                    SelectItem(item, preservePendingPassword: true);
                }
            }

            _btnCreate.gameObject.SetActive(_items.Count > 0);
            if (_items.Count == 0)
            {
                ShowTip("暂无可加入的房间");
            }
        }

        private void SelectItem(RoomItem item, bool preservePendingPassword = false)
        {
            bool roomChanged = _selectedEntry == null || _selectedEntry.RoomId != item.Entry.RoomId;

            if (_selectedBackground != null)
            {
                _selectedBackground.color = _selectedNormalColor;
            }

            _selectedEntry = item.Entry;
            _selectedItem = item.Root;
            _selectedBackground = item.Background;
            _selectedNormalColor = item.NormalColor;
            if (item.Background != null)
            {
                item.Background.color = SelectedColor;
            }

            // 列表刷新对同一房间的选中恢复不算切换，不打断正在等待的密码输入
            if (roomChanged && !preservePendingPassword)
            {
                HidePasswordPanel();
            }
        }

        #endregion

        #region 加入流程

        private void OnClickCreateBtn()
        {
            // 密码面板等待输入时，再点"加入房间"等同于回车提交
            if (_passwordTcs != null)
            {
                SubmitPassword();
                return;
            }

            if (_joining)
            {
                return;
            }

            if (_selectedEntry == null)
            {
                ShowTip("请先选择要加入的房间");
                return;
            }

            if (!_selectedEntry.CanJoin(out string reason))
            {
                ShowTip(reason);
                return;
            }

            JoinAsync(_selectedEntry).Forget();
        }

        /// <summary>
        /// 退出：取消密码等待并回到主菜单。
        /// </summary>
        private void OnClickExitBtn()
        {
            LobbyUIFlow.Instance.ShowMenu();
        }

        private async UniTaskVoid JoinAsync(RoomListEntry entry)
        {
            _joining = true;
            RoomOpResult result = await RoomManager.Instance.JoinRoomAsync(entry);
            _joining = false;

            if (IsDestroyed)
            {
                return;
            }

            if (result.Success)
            {
                LobbyUIFlow.Instance.ShowRoom();
                return;
            }

            if (!result.IsCancelled)
            {
                ShowTip(result.Message);
            }
        }

        /// <summary>
        /// RoomManager 的密码回调：弹出 m_rectEnterPass 并等待玩家提交。
        /// </summary>
        private async UniTask<string> PromptPasswordAsync(string roomName)
        {
            _passwordTcs = new UniTaskCompletionSource<string>();
            _rectEnterPass.gameObject.SetActive(true);
            _mtmp_InputFieldpass.SetTextWithoutNotify(string.Empty);
            _mtmp_InputFieldpass.Select();
            _mtmp_InputFieldpass.ActivateInputField();
            return await _passwordTcs.Task;
        }

        private void OnPasswordSubmitted(string text)
        {
            SubmitPassword();
        }

        private void SubmitPassword()
        {
            UniTaskCompletionSource<string> tcs = _passwordTcs;
            if (tcs == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(_mtmp_InputFieldpass.text))
            {
                ShowTip("请输入密码");
                return;
            }

            _passwordTcs = null;
            _mtmp_InputFieldpass.DeactivateInputField();
            tcs.TrySetResult(_mtmp_InputFieldpass.text);
        }

        private void HidePasswordPanel()
        {
            UniTaskCompletionSource<string> tcs = _passwordTcs;
            _passwordTcs = null;
            if (tcs != null)
            {
                // 取消等待，加入流程按用户取消处理
                tcs.TrySetResult(null);
            }

            if (_rectEnterPass != null)
            {
                _rectEnterPass.gameObject.SetActive(false);
            }
        }

        #endregion

        #region 提示

        /// <summary>
        /// 克隆标题文本作为底部提示条（预制体没有现成的提示节点）。
        /// </summary>
        private void CreateTip()
        {
            Transform title = FindChild("Back/Text (TMP)");
            if (title == null)
            {
                return;
            }

            GameObject tip = Object.Instantiate(title.gameObject, title.parent);
            tip.name = "m_textTip";
            RectTransform rect = tip.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 20f);
            rect.sizeDelta = new Vector2(1100f, 60f);

            _textTip = tip.GetComponent<TMP_Text>();
            _textTip.alignment = TextAlignmentOptions.Center;
            _textTip.color = new Color(1f, 0.75f, 0.3f);
            tip.SetActive(false);
        }

        private void ShowTip(string message)
        {
            if (_textTip == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            _textTip.text = message;
            _textTip.gameObject.SetActive(true);
            _tipHideAt = Time.unscaledTime + TipDuration;
        }

        #endregion
    }
}
