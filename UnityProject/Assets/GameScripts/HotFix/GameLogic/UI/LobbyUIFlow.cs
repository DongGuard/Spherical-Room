using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Log = TEngine.Log;

namespace GameLogic
{
    /// <summary>
    /// 大厅窗口流程：主菜单、创建房间、房间窗口之间的切换统一在这里处理。
    /// </summary>
    public class LobbyUIFlow : Singleton<LobbyUIFlow>
    {
        private const string LauncherSceneLocation = "Launcher";

        private bool _returning;

        protected override void OnInit()
        {
            base.OnInit();
            RoomManager.Instance.RoomClosed += OnRoomClosed;
            RoomManager.Instance.GameLoading += OnGameLoading;
            RoomManager.Instance.GameEntered += OnGameEntered;
        }

        protected override void OnRelease()
        {
            if (RoomManager.IsValid)
            {
                RoomManager manager = RoomManager.Instance;
                manager.RoomClosed -= OnRoomClosed;
                manager.GameLoading -= OnGameLoading;
                manager.GameEntered -= OnGameEntered;
            }

            base.OnRelease();
        }

        /// <summary>
        /// 回到主菜单（也是热更入口的初始界面）。
        /// </summary>
        public void ShowMenu()
        {
            GameModule.UI.CloseUI<RoomUI>();
            GameModule.UI.CloseUI<CreateRoom>();
            GameModule.UI.CloseUI<JoinUI>();
            GameModule.UI.CloseUI<GameUI>();
            GameModule.UI.ShowUIAsync<MenuUI>();
        }

        /// <summary>
        /// 进入游戏场景后切换到游戏内 HUD。
        /// </summary>
        public void ShowGame()
        {
            GameModule.UI.CloseUI<RoomUI>();
            GameModule.UI.ShowUI<GameUI>();
        }

        private void OnGameEntered()
        {
            ShowGame();
        }

        private void OnGameLoading()
        {
            GameModule.UI.ShowUI<TipsUI>("正在进入游戏...");
        }

        /// <summary>
        /// Launcher 场景自带 GameEntry / UIRoot 等启动对象，而启动时的原版实例一直留在
        /// </summary>
        private static void DestroyDuplicateBootObjects()
        {
            Scene active = SceneManager.GetActiveScene();
            foreach (GameObject root in active.GetRootGameObjects())
            {
                if (root.name == "GameEntry" || root.name == "UIRoot")
                {
                    Object.Destroy(root);
                }
            }
        }

        /// <summary>
        /// 创建或加入房间成功后进入房间窗口。
        /// </summary>
        public void ShowRoom()
        {
            GameModule.UI.CloseUI<CreateRoom>();
            GameModule.UI.CloseUI<JoinUI>();
            GameModule.UI.CloseUI<MenuUI>();
            GameModule.UI.ShowUI<RoomUI>();
        }

        private void OnRoomClosed(string reason)
        {
            Log.Info($"[LobbyUI] OnRoomClosed: {reason ?? "(主动离开)"}");
            if (!string.IsNullOrEmpty(reason))
            {
                Log.Warning($"[LobbyUI] 房间已关闭: {reason}");
            }

            ReturnToMenuAsync(reason).Forget();
        }

        private async UniTaskVoid ReturnToMenuAsync(string reason)
        {
            if (_returning)
            {
                return;
            }

            _returning = true;
            Log.Info("[LobbyUI] 开始返回主菜单");

            // 主动离开：GameUI 已先弹"正在解散/退出房间..."提示，这里不重复；
            // 被动断线：先弹"正在返回大厅..."并等过渡完成，再切场景弹菜单
            if (!string.IsNullOrEmpty(reason) && !RoomManager.Instance.HostClosedNotified
                && SceneManager.GetActiveScene().name != LauncherSceneLocation)
            {
                GameModule.UI.ShowUI<TipsUI>("正在返回大厅...");
                await UniTask.Delay(200, true);
            }

            if (SceneManager.GetActiveScene().name != LauncherSceneLocation)
            {
                try
                {
                    await GameModule.Scene.LoadSceneAsync(LauncherSceneLocation);
                    DestroyDuplicateBootObjects();
                    Log.Info("[LobbyUI] 已切回 Launcher 场景");
                }
                catch (Exception e)
                {
                    Log.Error($"[LobbyUI] 返回主菜单场景失败: {e}");
                }
            }

            ShowMenu();
            if (!string.IsNullOrEmpty(reason) && !RoomManager.Instance.HostClosedNotified)
            {
                GameModule.UI.ShowUI<TipsUI>(reason);
            }

            RoomManager.Instance.HostClosedNotified = false;
            _returning = false;
        }
    }
}
