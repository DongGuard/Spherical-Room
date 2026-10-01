#if UNITY_STANDALONE_WIN
using Steamworks;
using TEngine;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Steam模块
    /// </summary>
    public class SteamModule : Singleton<SteamModule>, IUpdate
    {
        public SteamCloudStorage SteamCloudStorage => SteamCloudStorage.Instance; //云存储
        public SteamAchievement SteamAchievement => SteamAchievement.Instance; //成就
        public SteamWishlist SteamWishlist => SteamWishlist.Instance; //愿望单
        public SteamLobby SteamLobby => SteamLobby.Instance; //联机大厅（好友邀请 / P2P 联机）

        protected override void OnInit()
        {
            base.OnInit();
            if (!SteamManager.Initialized)
            {
                Debug.LogError("Steam API 未初始化，SteamModule 不可用");
                GameEvent.Send(Constant.GameEvent.CloseLoading);
                return;
            }

            SteamCloudStorage.Active();
            SteamAchievement.Active();
            SteamWishlist.Active();
            SteamLobby.Active();
            Application.quitting += OnApplicationQuit;
            string playerName = SteamFriends.GetPersonaName();
            Debug.Log($"SteamSDK 初始化成功，玩家昵称: {playerName}");
            GameEvent.Send(Constant.GameEvent.CloseLoading);
        }

        public void OnUpdate()
        {
            if (SteamManager.Initialized)
            {
                // 每帧运行回调
                SteamAPI.RunCallbacks();
            }
        }

        private void OnApplicationQuit()
        {
            if (SteamManager.Initialized)
            {
                // 确保在退出时正确关闭 Steam API
                SteamAPI.Shutdown();
                Debug.Log("回收SteamSDK缓存成功!");
            }
        }
    }
}

#endif