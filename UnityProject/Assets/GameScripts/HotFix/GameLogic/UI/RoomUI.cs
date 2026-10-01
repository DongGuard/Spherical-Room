using Cysharp.Threading.Tasks;
using TEngine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Log = TEngine.Log;

namespace GameLogic
{
    public sealed class SlotView
    {
        public RectTransform Root;

        //名字标签
        public GameObject Tag;

        public TMP_Text NameText;

        //PlayerUI 形象
        public GameObject Avatar;
    }
    
    /// <summary>
    /// 房间
    /// </summary>
    [Window(UILayer.UI)]
    class RoomUI : UIWindow
    {
        #region 脚本工具生成的代码
        private RectTransform _rectPlayers;
        private RectTransform _rectPoint;
        private RectTransform _rectPoint2;
        private RectTransform _rectPoint3;
        private RectTransform _rectPoint4;
        private Toggle _toggleJob;
        private TMP_Text _mtmp_textPassValue;
        private Button _btnCreate;
        protected override void ScriptGenerator()
        {
            _rectPlayers = FindChildComponent<RectTransform>("Back/m_rectPlayers");
            _rectPoint = FindChildComponent<RectTransform>("Back/m_rectPlayers/Viewport/Content/m_rectPoint");
            _rectPoint2 = FindChildComponent<RectTransform>("Back/m_rectPlayers/Viewport/Content/m_rectPoint2");
            _rectPoint3 = FindChildComponent<RectTransform>("Back/m_rectPlayers/Viewport/Content/m_rectPoint3");
            _rectPoint4 = FindChildComponent<RectTransform>("Back/m_rectPlayers/Viewport/Content/m_rectPoint4");
            _toggleJob = FindChildComponent<Toggle>("Back/m_toggleJob");
            _mtmp_textPassValue = FindChildComponent<TMP_Text>("Back/mtmp_textPassValue");
            _btnCreate = FindChildComponent<Button>("Back/m_btnCreate");
        }
        #endregion

        private readonly SlotView[] _slots = new SlotView[RoomProtocol.MaxPlayers];

        private bool _starting;

        protected override void OnCreate()
        {
            _btnCreate.onClick.AddListener(OnClickCreateBtn);
            _toggleJob.onValueChanged.AddListener(OnToggleJobChange);
            BuildSlots();

            RoomManager manager = RoomManager.Instance;
            manager.RoomUpdated += Refresh;
            manager.GameLoading += OnGameLoading;
            manager.GameEntered += OnGameEntered;
            Refresh();
        }

        protected override void OnDestroy()
        {
            _btnCreate.onClick.RemoveListener(OnClickCreateBtn);
            _toggleJob.onValueChanged.RemoveListener(OnToggleJobChange);
            if (RoomManager.IsValid)
            {
                RoomManager manager = RoomManager.Instance;
                manager.RoomUpdated -= Refresh;
                manager.GameLoading -= OnGameLoading;
                manager.GameEntered -= OnGameEntered;
            }
        }

        #region 槽位构建

        private void BuildSlots()
        {
            _slots[0] = BuildSlotView(_rectPoint);
            RectTransform[] roots = { _rectPoint2, _rectPoint3, _rectPoint4 };
            for (int i = 0; i < roots.Length; i++)
            {
                _slots[i + 1] = CloneSlotView(_slots[0], roots[i]);
            }
        }

        private static SlotView BuildSlotView(RectTransform root)
        {
            Transform tag = root.Find("Image");
            Transform avatar = root.Find("PlayerUI");
            if (avatar != null)
            {
                // UICamera 只渲染 UI 层，形象在 Default 层 Game 视图里看不到
                SetLayer(avatar, LayerMask.NameToLayer("UI"));
            }

            var view = new SlotView
            {
                Root = root,
                Tag = tag != null ? tag.gameObject : null,
                NameText = tag != null ? tag.GetComponentInChildren<TMP_Text>(true) : null,
                Avatar = avatar != null ? avatar.gameObject : null
            };

            if (view.NameText != null)
            {
                // 名字长短不一，自动缩小字号适配小标签
                view.NameText.fontSizeMax = view.NameText.fontSize;
                view.NameText.fontSizeMin = 14f;
                view.NameText.enableAutoSizing = true;
            }

            return view;
        }

        private static void SetLayer(Transform node, int layer)
        {
            node.gameObject.layer = layer;
            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
            {
                SetLayer(node.GetChild(i), layer);
            }
        }

        private static SlotView CloneSlotView(SlotView template, RectTransform root)
        {
            var view = new SlotView { Root = root };
            if (template.Tag != null)
            {
                view.Tag = GameObject.Instantiate(template.Tag, root);
                view.NameText = view.Tag.GetComponentInChildren<TMP_Text>(true);
            }

            if (template.Avatar != null)
            {
                view.Avatar = Object.Instantiate(template.Avatar, root);
            }

            return view;
        }

        #endregion

        #region 刷新

        private void Refresh()
        {
            if (IsDestroyed)
            {
                return;
            }

            RoomInfo room = RoomManager.Instance.Room;
            if (room == null)
            {
                return;
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                SlotView slot = _slots[i];
                RoomMember member = room.GetMember(i);
                bool occupied = member != null;
                if (slot.Tag != null)
                {
                    slot.Tag.SetActive(occupied);
                }

                if (slot.Avatar != null)
                {
                    slot.Avatar.SetActive(occupied);
                }

                if (occupied && slot.NameText != null)
                {
                    slot.NameText.text = member.IsHost ? "房主" : member.Name;
                }
            }

            bool isHost = RoomManager.Instance.IsHost;
            bool waiting = room.Phase == RoomPhase.Waiting;

            _toggleJob.SetIsOnWithoutNotify(room.AllowMidJoin);
            _toggleJob.interactable = isHost && waiting;

            _mtmp_textPassValue.gameObject.SetActive(room.HasPassword);
            if (room.HasPassword)
            {
                _mtmp_textPassValue.text = $"当前密码: {room.Password}";
            }

            _btnCreate.gameObject.SetActive(isHost);
            _btnCreate.interactable = waiting && !_starting;
        }

        #endregion

        #region 事件

        private void OnToggleJobChange(bool isOn)
        {
            if (RoomManager.Instance.IsHost)
            {
                RoomManager.Instance.SetAllowMidJoin(isOn);
            }
        }

        private void OnClickCreateBtn()
        {
            if (!RoomManager.Instance.IsHost || _starting)
            {
                return;
            }

            _starting = true;
            Refresh();
            StartGameAsync().Forget();
        }

        private async UniTaskVoid StartGameAsync()
        {
            RoomOpResult result = await RoomManager.Instance.StartGameAsync();
            if (IsDestroyed)
            {
                return;
            }

            if (!result.Success)
            {
                _starting = false;
                Log.Warning($"[RoomUI] 开始游戏失败: {result.Message}");
                Refresh();
            }
        }

        private void OnGameLoading()
        {
            _starting = true;
            Refresh();
        }

        private void OnGameEntered()
        {
            Close();
        }

        #endregion
    }
}
