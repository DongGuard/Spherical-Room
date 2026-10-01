#if UNITY_STANDALONE_WIN
using System;
using Cysharp.Threading.Tasks;
using Steamworks;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Steam 联机大厅：创建 / 加入 / 好友邀请。
    /// 大厅主人即 Mirror Host，其他成员通过 FizzySteamworks 按主人 SteamID 连接（P2P + Steam Relay）。
    /// 连接本身仍由 SessionManager 管理，大厅只负责"找到主机"。
    /// </summary>
    public class SteamLobby : Singleton<SteamLobby>
    {
        private const string HostKey = "host_steam_id";

        /// <summary>从好友邀请启动游戏时，Steam 附加的命令行参数：+connect_lobby &lt;lobbyId&gt;</summary>
        private const string ConnectLobbyArg = "+connect_lobby";

        private Callback<LobbyEnter_t> _lobbyEnter;
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private CallResult<LobbyCreated_t> _lobbyCreated;

        /// <summary>收到邀请时仍在旧会话中，等会话结束后再加入。</summary>
        private CSteamID _pendingLobby = CSteamID.Nil;

        public CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;

        public bool InLobby => CurrentLobby.IsValid();

        /// <summary>大厅创建或加入失败，参数为可直接展示的原因。</summary>
        public event Action<string> LobbyFailed;

        protected override void OnInit()
        {
            base.OnInit();
            if (!SteamManager.Initialized)
            {
                return;
            }

            _lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            SessionManager.Instance.StateChanged += OnSessionStateChanged;
        }

        public override void Active()
        {
            base.Active();
            TryJoinFromCommandLine();
        }

        /// <summary>创建仅好友可见的大厅并作为主机启动。</summary>
        public void HostLobby()
        {
            if (!SteamManager.Initialized)
            {
                Fail("Steam 未初始化，无法创建大厅");
                return;
            }

            if (InLobby || SessionManager.Instance.State != SessionState.Offline)
            {
                Debug.LogWarning("[SteamLobby] 已在大厅或会话中");
                return;
            }

            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, SessionManager.MaxPlayers);
            _lobbyCreated.Set(call);
        }

        /// <summary>加入指定大厅（邀请、命令行或 UI 调用）。</summary>
        public void JoinLobby(CSteamID lobby)
        {
            if (!SteamManager.Initialized || !lobby.IsValid())
            {
                Fail("无效的大厅");
                return;
            }

            SteamMatchmaking.JoinLobby(lobby);
        }

        /// <summary>打开 Steam 覆盖层的好友邀请对话框。</summary>
        public void OpenInviteDialog()
        {
            if (InLobby)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
            }
        }

        public void LeaveLobby()
        {
            if (!InLobby)
            {
                return;
            }

            SteamMatchmaking.LeaveLobby(CurrentLobby);
            CurrentLobby = CSteamID.Nil;
        }

        protected override void OnRelease()
        {
            if (SessionManager.IsValid)
            {
                SessionManager.Instance.StateChanged -= OnSessionStateChanged;
            }

            if (SteamManager.Initialized)
            {
                LeaveLobby();
            }

            _lobbyEnter?.Dispose();
            _joinRequested?.Dispose();
            _lobbyCreated?.Dispose();
        }

        #region Steam 回调

        private void OnLobbyCreated(LobbyCreated_t data, bool ioFailure)
        {
            if (ioFailure || data.m_eResult != EResult.k_EResultOK)
            {
                Fail($"创建大厅失败: {(ioFailure ? "IO 错误" : data.m_eResult.ToString())}");
                return;
            }

            CurrentLobby = new CSteamID(data.m_ulSteamIDLobby);
            // 成员进入大厅后读取该字段，得到要连接的主机
            SteamMatchmaking.SetLobbyData(CurrentLobby, HostKey, SteamUser.GetSteamID().m_SteamID.ToString());
            Debug.Log($"[SteamLobby] 大厅已创建 {CurrentLobby}");
            StartHostAsync().Forget();
        }

        private async UniTaskVoid StartHostAsync()
        {
            if (!await SessionManager.Instance.StartSteamHostAsync())
            {
                LeaveLobby();
            }
        }

        private void OnLobbyEnter(LobbyEnter_t data)
        {
            var lobby = new CSteamID(data.m_ulSteamIDLobby);
            if (data.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Fail($"加入大厅失败: {(EChatRoomEnterResponse)data.m_EChatRoomEnterResponse}");
                return;
            }

            CurrentLobby = lobby;

            // 主人创建大厅时也会收到 LobbyEnter，此时已经在启动 Host，不需要再连接
            if (SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID())
            {
                return;
            }

            string hostValue = SteamMatchmaking.GetLobbyData(lobby, HostKey);
            if (!ulong.TryParse(hostValue, out ulong hostId))
            {
                LeaveLobby();
                Fail("大厅缺少主机信息");
                return;
            }

            Debug.Log($"[SteamLobby] 已进入大厅 {lobby}，连接主机 {hostId}");
            SessionManager.Instance.JoinSteamAsync(new CSteamID(hostId)).Forget();
        }

        /// <summary>
        /// 游戏运行中通过 Steam 好友列表"加入游戏"或接受邀请。
        /// 已在会话中时先离开，等状态回到 Offline 再加入新大厅。
        /// </summary>
        private void OnJoinRequested(GameLobbyJoinRequested_t data)
        {
            if (SessionManager.Instance.State == SessionState.Offline)
            {
                JoinLobby(data.m_steamIDLobby);
                return;
            }

            _pendingLobby = data.m_steamIDLobby;
            SessionManager.Instance.Leave();
        }

        #endregion

        private void OnSessionStateChanged(SessionState state)
        {
            if (state != SessionState.Offline)
            {
                return;
            }

            // 会话结束（主动离开、主机断开等）时同步离开大厅
            LeaveLobby();

            if (_pendingLobby.IsValid())
            {
                CSteamID lobby = _pendingLobby;
                _pendingLobby = CSteamID.Nil;
                JoinLobby(lobby);
            }
        }

        /// <summary>游戏未运行时接受邀请，Steam 会带 +connect_lobby 参数启动游戏。</summary>
        private void TryJoinFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == ConnectLobbyArg && ulong.TryParse(args[i + 1], out ulong lobbyId))
                {
                    JoinLobby(new CSteamID(lobbyId));
                    return;
                }
            }
        }

        private void Fail(string reason)
        {
            Debug.LogWarning($"[SteamLobby] {reason}");
            LobbyFailed?.Invoke(reason);
        }
    }
}
#endif
