using Log = TEngine.Log;

namespace GameLogic
{
    /// <summary>
    /// 大厅窗口流程：主菜单、创建房间、房间窗口之间的切换统一在这里处理。
    /// <para>
    /// 进入房间（创建成功 / 加入成功）调 <see cref="ShowRoom"/>；房间关闭（主动离开、房主离开、断线）
    /// 由 <see cref="RoomManager.RoomClosed"/> 驱动自动回到主菜单，游戏中窗口已关闭时同样生效。
    /// </para>
    /// 各窗口只负责自己的显示逻辑，不互相引用。
    /// </summary>
    public class LobbyUIFlow : Singleton<LobbyUIFlow>
    {
        protected override void OnInit()
        {
            base.OnInit();
            RoomManager.Instance.RoomClosed += OnRoomClosed;
        }

        protected override void OnRelease()
        {
            if (RoomManager.IsValid)
            {
                RoomManager.Instance.RoomClosed -= OnRoomClosed;
            }

            base.OnRelease();
        }

        /// <summary>回到主菜单（也是热更入口的初始界面）。</summary>
        public void ShowMenu()
        {
            GameModule.UI.CloseUI<RoomUI>();
            GameModule.UI.CloseUI<CreateRoom>();
            GameModule.UI.CloseUI<JoinUI>();
            GameModule.UI.ShowUIAsync<MenuUI>();
        }

        /// <summary>创建或加入房间成功后进入房间窗口。</summary>
        public void ShowRoom()
        {
            GameModule.UI.CloseUI<CreateRoom>();
            GameModule.UI.CloseUI<JoinUI>();
            GameModule.UI.CloseUI<MenuUI>();
            GameModule.UI.ShowUI<RoomUI>();
        }

        private void OnRoomClosed(string reason)
        {
            if (!string.IsNullOrEmpty(reason))
            {
                Log.Warning($"[LobbyUI] 房间已关闭: {reason}");
            }

            ShowMenu();
        }
    }
}
