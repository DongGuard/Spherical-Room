#if UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Steamworks;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Steam 大厅操作结果。
    /// </summary>
    public sealed class SteamLobbyResult
    {
        public bool Success;
        public string Message;
        public CSteamID Lobby = CSteamID.Nil;

        /// <summary>
        /// 加入成功时：房主 SteamID（Mirror 主机）。
        /// </summary>
        public CSteamID Host = CSteamID.Nil;

        /// <summary>
        /// 加入成功时：大厅里的房间信息。
        /// </summary>
        public RoomListEntry Entry;

        public static SteamLobbyResult Ok(CSteamID lobby)
        {
            return new SteamLobbyResult { Success = true, Lobby = lobby };
        }

        public static SteamLobbyResult Fail(string message)
        {
            return new SteamLobbyResult { Success = false, Message = message };
        }
    }

    /// <summary>
    /// Steam 大厅列表查询结果。
    /// </summary>
    public sealed class SteamLobbyListResult
    {
        public bool Success;
        public string Message;
        public readonly List<RoomListEntry> Entries = new List<RoomListEntry>();
    }

    /// <summary>
    /// Steam 大厅：负责"找到房间"——创建公开大厅、按游戏标记全球搜索、加入、好友邀请。
    /// </summary>
    public class SteamLobby : Singleton<SteamLobby>
    {
        /// <summary>
        /// 游戏标记。测试 AppID 480（Spacewar）被大量游戏共用，搜索时必须按这个标记过滤。
        /// </summary>
        public const string GameTag = "SphericalRoom";

        private const string KeyGame = "sr_game";
        private const string KeyVersion = "sr_ver";
        private const string KeyRoomId = "rid";
        private const string KeyName = "name";
        private const string KeyPassword = "pwd";
        private const string KeyMidJoin = "mid";
        private const string KeyPhase = "phase";
        private const string KeyMembers = "members";
        private const string KeyMax = "max";
        private const string KeyHost = "host";

        /// <summary>
        /// 游戏未运行时接受邀请，Steam 会带 +connect_lobby &lt;lobbyId&gt; 参数启动游戏。
        /// </summary>
        private const string ConnectLobbyArg = "+connect_lobby";

        private const int RequestTimeoutMs = 15000;
        private const int MaxListResults = 50;

        public delegate void JoinRequestHandler(CSteamID lobby, CSteamID inviter);

        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private CallResult<LobbyCreated_t> _lobbyCreated;
        private CallResult<LobbyMatchList_t> _lobbyList;
        private CallResult<LobbyEnter_t> _lobbyEnter;

        private UniTaskCompletionSource<SteamLobbyResult> _createTcs;
        private UniTaskCompletionSource<SteamLobbyListResult> _listTcs;
        private UniTaskCompletionSource<SteamLobbyResult> _joinTcs;

        /// <summary>
        /// 已写入大厅的数据，避免重复写（每次写入都会通知所有成员）。
        /// </summary>
        private readonly Dictionary<string, string> _publishedData = new Dictionary<string, string>();

        private bool _publishedJoinable = true;

        /// <summary>
        /// 收到好友邀请，或在 Steam 好友列表点了"加入游戏"。
        /// </summary>
        public event JoinRequestHandler JoinRequested;

        public CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;

        public bool InLobby => CurrentLobby.IsValid();

        public bool Available => SteamManager.Initialized && _lobbyCreated != null;

        protected override void OnInit()
        {
            base.OnInit();
            if (!SteamManager.Initialized)
            {
                return;
            }

            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyList = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
            _lobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        }

        protected override void OnRelease()
        {
            if (SteamManager.Initialized)
            {
                LeaveLobby();
            }

            _joinRequested?.Dispose();
            _lobbyCreated?.Dispose();
            _lobbyList?.Dispose();
            _lobbyEnter?.Dispose();
        }

        #region 创建 / 搜索 / 加入 / 离开

        /// <summary>
        /// 创建公开大厅（全球可搜索），成功后本机即为大厅主人。
        /// </summary>
        public async UniTask<SteamLobbyResult> CreateLobbyAsync(int maxMembers)
        {
            if (!Available)
            {
                return SteamLobbyResult.Fail("Steam 未启动");
            }

            if (_createTcs != null)
            {
                return SteamLobbyResult.Fail("正在创建房间，请稍候");
            }

            LeaveLobby();
            var tcs = new UniTaskCompletionSource<SteamLobbyResult>();
            _createTcs = tcs;
            _lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, maxMembers));
            CreateTimeoutAsync(tcs).Forget();

            SteamLobbyResult result = await tcs.Task;
            // Steam 回调在 RunCallbacks 内触发，回到下一帧再继续后面的联机流程
            await UniTask.Yield();
            if (!result.Success)
            {
                return result;
            }

            CurrentLobby = result.Lobby;
            _publishedData.Clear();
            _publishedJoinable = true;
            SetData(KeyGame, GameTag);
            SetData(KeyVersion, RoomProtocol.Version.ToString());
            SetData(KeyHost, SteamUser.GetSteamID().m_SteamID.ToString());
            Debug.Log($"[SteamLobby] 大厅已创建 {CurrentLobby}");
            return result;
        }

        /// <summary>
        /// 全球搜索本游戏、同版本、有空位且可加入的大厅。
        /// </summary>
        public async UniTask<SteamLobbyListResult> RequestLobbyListAsync()
        {
            if (!Available)
            {
                return new SteamLobbyListResult { Success = false, Message = "Steam 未启动" };
            }

            if (_listTcs != null)
            {
                return new SteamLobbyListResult { Success = false, Message = "正在刷新房间列表" };
            }

            var tcs = new UniTaskCompletionSource<SteamLobbyListResult>();
            _listTcs = tcs;
            // 过滤条件只对紧接着的一次 RequestLobbyList 生效
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyGame, GameTag, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyVersion, RoomProtocol.Version.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(MaxListResults);
            _lobbyList.Set(SteamMatchmaking.RequestLobbyList());
            ListTimeoutAsync(tcs).Forget();

            SteamLobbyListResult result = await tcs.Task;
            await UniTask.Yield();
            return result;
        }

        /// <summary>
        /// 加入大厅，成功后返回房主 SteamID 和大厅里的房间信息。
        /// </summary>
        public async UniTask<SteamLobbyResult> JoinLobbyAsync(CSteamID lobby)
        {
            if (!Available)
            {
                return SteamLobbyResult.Fail("Steam 未启动");
            }

            if (!lobby.IsValid())
            {
                return SteamLobbyResult.Fail("无效的房间");
            }

            if (_joinTcs != null)
            {
                return SteamLobbyResult.Fail("正在加入房间，请稍候");
            }

            LeaveLobby();
            var tcs = new UniTaskCompletionSource<SteamLobbyResult>();
            _joinTcs = tcs;
            _lobbyEnter.Set(SteamMatchmaking.JoinLobby(lobby));
            JoinTimeoutAsync(tcs).Forget();

            SteamLobbyResult result = await tcs.Task;
            await UniTask.Yield();
            return result;
        }

        public void LeaveLobby()
        {
            if (!InLobby)
            {
                return;
            }

            try
            {
                if (SteamManager.Initialized)
                {
                    SteamMatchmaking.LeaveLobby(CurrentLobby);
                }
            }
            catch (Exception e)
            {
                // Steam 未初始化/已失效时也不能打断房间关闭流程
                Debug.LogWarning($"[SteamLobby] 离开大厅失败: {e.Message}");
            }

            Debug.Log($"[SteamLobby] 离开大厅 {CurrentLobby}");
            CurrentLobby = CSteamID.Nil;
            _publishedData.Clear();
        }

        /// <summary>
        /// 打开 Steam 覆盖层的好友邀请对话框（需要 Steam 覆盖层可用）。
        /// </summary>
        public void OpenInviteDialog()
        {
            if (InLobby && SteamManager.Initialized)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
            }
        }

        /// <summary>
        /// 房主：把当前房间信息写入大厅数据，并设置是否可加入。
        /// </summary>
        public void PublishRoom(string roomId, RoomInfo room, bool joinable)
        {
            if (!InLobby || room == null || !SteamManager.Initialized)
            {
                return;
            }

            try
            {
                PublishRoomInternal(roomId, room, joinable);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SteamLobby] 发布房间数据失败: {e.Message}");
            }
        }

        private void PublishRoomInternal(string roomId, RoomInfo room, bool joinable)
        {
            SetData(KeyRoomId, roomId ?? string.Empty);
            SetData(KeyName, room.Name ?? string.Empty);
            SetData(KeyPassword, room.HasPassword ? "1" : "0");
            SetData(KeyMidJoin, room.AllowMidJoin ? "1" : "0");
            SetData(KeyPhase, ((int)room.Phase).ToString());
            SetData(KeyMembers, room.Members.Count.ToString());
            SetData(KeyMax, room.MaxPlayers.ToString());

            if (_publishedJoinable != joinable)
            {
                _publishedJoinable = joinable;
                SteamMatchmaking.SetLobbyJoinable(CurrentLobby, joinable);
            }
        }

        /// <summary>
        /// 从大厅数据读取房间信息，不是本游戏的大厅返回 null。
        /// </summary>
        public RoomListEntry ReadLobbyEntry(CSteamID lobby)
        {
            if (SteamMatchmaking.GetLobbyData(lobby, KeyGame) != GameTag)
            {
                return null;
            }

            var entry = new RoomListEntry
            {
                Source = RoomSource.Steam,
                SteamLobbyId = lobby.m_SteamID,
                RoomId = SteamMatchmaking.GetLobbyData(lobby, KeyRoomId),
                Name = RoomProtocol.Sanitize(SteamMatchmaking.GetLobbyData(lobby, KeyName), RoomProtocol.MaxNameLength),
                HasPassword = SteamMatchmaking.GetLobbyData(lobby, KeyPassword) == "1",
                AllowMidJoin = SteamMatchmaking.GetLobbyData(lobby, KeyMidJoin) == "1",
                Phase = (RoomPhase)ParseInt(SteamMatchmaking.GetLobbyData(lobby, KeyPhase), 0),
                Members = ParseInt(SteamMatchmaking.GetLobbyData(lobby, KeyMembers), SteamMatchmaking.GetNumLobbyMembers(lobby)),
                MaxPlayers = ParseInt(SteamMatchmaking.GetLobbyData(lobby, KeyMax), SteamMatchmaking.GetLobbyMemberLimit(lobby)),
                Version = ParseInt(SteamMatchmaking.GetLobbyData(lobby, KeyVersion), 0)
            };

            entry.HostSteamId = ulong.TryParse(SteamMatchmaking.GetLobbyData(lobby, KeyHost), out ulong host)
                ? host
                : SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;

            if (string.IsNullOrEmpty(entry.Name))
            {
                entry.Name = "未命名房间";
            }

            return entry;
        }

        /// <summary>
        /// 游戏是否由 Steam 邀请启动（命令行带 +connect_lobby）。
        /// </summary>
        public static bool TryGetCommandLineLobby(out CSteamID lobby)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == ConnectLobbyArg && ulong.TryParse(args[i + 1], out ulong lobbyId))
                {
                    lobby = new CSteamID(lobbyId);
                    return true;
                }
            }

            lobby = CSteamID.Nil;
            return false;
        }

        #endregion

        #region Steam 回调

        private void OnLobbyCreated(LobbyCreated_t data, bool ioFailure)
        {
            UniTaskCompletionSource<SteamLobbyResult> tcs = _createTcs;
            _createTcs = null;
            bool ok = !ioFailure && data.m_eResult == EResult.k_EResultOK;
            var lobby = new CSteamID(data.m_ulSteamIDLobby);
            if (tcs == null)
            {
                // 已经超时放弃了：大厅晚到，直接退出，避免留下一个没有主机的大厅
                if (ok)
                {
                    SteamMatchmaking.LeaveLobby(lobby);
                }

                return;
            }

            tcs.TrySetResult(ok
                ? SteamLobbyResult.Ok(lobby)
                : SteamLobbyResult.Fail($"创建 Steam 房间失败（{(ioFailure ? "网络错误" : data.m_eResult.ToString())}）"));
        }

        private void OnLobbyList(LobbyMatchList_t data, bool ioFailure)
        {
            UniTaskCompletionSource<SteamLobbyListResult> tcs = _listTcs;
            _listTcs = null;
            if (tcs == null)
            {
                return;
            }

            var result = new SteamLobbyListResult();
            if (ioFailure)
            {
                result.Success = false;
                result.Message = "获取 Steam 房间列表失败（网络错误）";
                tcs.TrySetResult(result);
                return;
            }

            result.Success = true;
            int count = (int)data.m_nLobbiesMatching;
            for (int i = 0; i < count; i++)
            {
                RoomListEntry entry = ReadLobbyEntry(SteamMatchmaking.GetLobbyByIndex(i));
                if (entry != null)
                {
                    result.Entries.Add(entry);
                }
            }

            tcs.TrySetResult(result);
        }

        private void OnLobbyEntered(LobbyEnter_t data, bool ioFailure)
        {
            UniTaskCompletionSource<SteamLobbyResult> tcs = _joinTcs;
            _joinTcs = null;
            var lobby = new CSteamID(data.m_ulSteamIDLobby);
            bool ok = !ioFailure && data.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
            if (tcs == null)
            {
                // 超时后才进入：直接退出
                if (ok)
                {
                    SteamMatchmaking.LeaveLobby(lobby);
                }

                return;
            }

            if (!ok)
            {
                tcs.TrySetResult(SteamLobbyResult.Fail(ioFailure
                    ? "加入 Steam 房间失败（网络错误）"
                    : DescribeEnterResponse(data.m_EChatRoomEnterResponse)));
                return;
            }

            RoomListEntry entry = ReadLobbyEntry(lobby);
            if (entry == null)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                tcs.TrySetResult(SteamLobbyResult.Fail("这不是本游戏的房间"));
                return;
            }

            CurrentLobby = lobby;
            _publishedData.Clear();
            SteamLobbyResult result = SteamLobbyResult.Ok(lobby);
            result.Entry = entry;
            result.Host = new CSteamID(entry.HostSteamId);
            if (!result.Host.IsValid())
            {
                result.Host = SteamMatchmaking.GetLobbyOwner(lobby);
            }

            Debug.Log($"[SteamLobby] 已进入大厅 {lobby}，房主 {result.Host}");
            tcs.TrySetResult(result);
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t data)
        {
            Debug.Log($"[SteamLobby] 收到加入请求 {data.m_steamIDLobby}");
            JoinRequested?.Invoke(data.m_steamIDLobby, data.m_steamIDFriend);
        }

        #endregion

        #region 超时

        private async UniTaskVoid CreateTimeoutAsync(UniTaskCompletionSource<SteamLobbyResult> tcs)
        {
            await UniTask.Delay(RequestTimeoutMs, true);
            if (_createTcs != tcs)
            {
                return;
            }

            _createTcs = null;
            tcs.TrySetResult(SteamLobbyResult.Fail("创建 Steam 房间超时，请检查 Steam 网络"));
        }

        private async UniTaskVoid ListTimeoutAsync(UniTaskCompletionSource<SteamLobbyListResult> tcs)
        {
            await UniTask.Delay(RequestTimeoutMs, true);
            if (_listTcs != tcs)
            {
                return;
            }

            _listTcs = null;
            tcs.TrySetResult(new SteamLobbyListResult { Success = false, Message = "获取 Steam 房间列表超时" });
        }

        private async UniTaskVoid JoinTimeoutAsync(UniTaskCompletionSource<SteamLobbyResult> tcs)
        {
            await UniTask.Delay(RequestTimeoutMs, true);
            if (_joinTcs != tcs)
            {
                return;
            }

            _joinTcs = null;
            tcs.TrySetResult(SteamLobbyResult.Fail("加入 Steam 房间超时，请检查 Steam 网络"));
        }

        #endregion

        private void SetData(string key, string value)
        {
            if (_publishedData.TryGetValue(key, out string old) && old == value)
            {
                return;
            }

            _publishedData[key] = value;
            SteamMatchmaking.SetLobbyData(CurrentLobby, key, value);
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, out int result) ? result : fallback;
        }

        private static string DescribeEnterResponse(uint response)
        {
            switch (response)
            {
                case 2:
                    return "房间不存在或已解散";
                case 3:
                    return "房间当前不允许加入（游戏已开始或已满员）";
                case 4:
                    return "房间已满";
                case 6:
                    return "你被禁止加入该房间";
                case 7:
                    return "你的 Steam 账号受限，无法加入房间";
                case 15:
                    return "操作太频繁，请稍后再试";
                default:
                    return $"加入 Steam 房间失败（错误码 {response}）";
            }
        }
    }
}
#endif
