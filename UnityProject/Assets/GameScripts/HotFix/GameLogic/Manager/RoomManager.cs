using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
using Log = TEngine.Log;
#if UNITY_STANDALONE_WIN
using Steamworks;
#endif

namespace GameLogic
{
    /// <summary>
    /// 房间管理器
    /// </summary>
    public class RoomManager : Singleton<RoomManager>, IUpdate
    {
        /// <summary>
        /// 游戏场景的 YooAsset 地址。
        /// </summary>
        public const string GameSceneLocation = "Room";

        private const string GameSceneName = "Room";
        private const string PlayerPrefabLocation = "Player";
        private const string BallPrefabLocation = "Sphere";

        /// <summary>
        /// 场景里名字以此开头的物体作为出生点（按名字排序，依次分配给各位置）。
        /// </summary>
        private const string SpawnPointPrefix = "SpawnPoint";

        /// <summary>
        /// 局域网搜索窗口（秒）。窗口需大于大厅列表的刷新间隔，刷新时只延长不清空。
        /// </summary>
        private const float LanSearchDuration = 5f;

        /// <summary>
        /// 定时生成：游戏中主机每隔该时间（秒）随机生成一个球。
        /// </summary>
        private const float BallSpawnInterval = 15f;

        private const float RandomBallRadius = 6f;

        /// <summary>
        /// 定时生成的球相对出生点的高度（米），从空中落入房间。
        /// </summary>
        private const float RandomBallDropHeight = 3f;

        /// <summary>
        /// 认证通过后等待房间状态的最长时间（秒）。
        /// </summary>
        private const float RoomStateTimeout = 10f;

        /// <summary>
        /// 场景里没有出生点时的默认位置：Room 场景地面中部，各位置沿 X 轴错开。
        /// </summary>
        private static readonly Vector3 DefaultSpawnCenter = new Vector3(18.19f, 0.4f, 55.88f);

        private static readonly float[] DefaultSpawnOffsets = { 0f, 4f, -4f, 8f };

        /// <summary>
        /// 弹出密码输入框，返回输入的密码，取消返回 null。
        /// </summary>
        public delegate UniTask<string> PasswordPromptHandler(string roomName);

        public delegate void OperationFinishedHandler(RoomOpResult result);

        private sealed class ServerMember
        {
            public NetworkConnectionToClient Connection;
            public string Name;
            public ulong SteamId;
            public bool IsHost;
        }

        private SessionManager _session;
        private LanRoomDiscovery _lan;
        private string _playerName;

        private GameObject _playerPrefab;
        private GameObject _ballPrefab;
        private bool _prefabsReady;

        // 本地（房主和成员共用）
        private RoomInfo _room;
        private bool _busy;
        private bool _loadingGame;
        private bool _inGame;
        private int _roomSerial;
        private string _lastEndReason;

        // 仅房主
        private RoomSettings _settings;
        private string _roomId;
        private RoomPhase _serverPhase;
        private readonly List<ServerMember> _members = new List<ServerMember>();
        private readonly List<Transform> _spawnPoints = new List<Transform>();
        /// <summary>
        /// 房主生成的共享球（开局一个 + 定时生成的随机球）。
        /// </summary>
        private readonly List<GameObject> _balls = new List<GameObject>();

        /// <summary>
        /// 下次定时生成球的时间（unscaled）。
        /// </summary>
        private float _nextBallSpawnTime;

        /// <summary>
        /// 客户端：是否已收到并提示过"房主解散"（避免返回大厅时重复弹提示）。
        /// </summary>
        public bool HostClosedNotified { get; set; }

        private int _hostCloseAcks;

        // 房间列表
        private readonly List<RoomListEntry> _steamEntries = new List<RoomListEntry>();
        private readonly List<RoomListEntry> _roomList = new List<RoomListEntry>();
        private bool _steamListing;
        private string _listError;

#if UNITY_STANDALONE_WIN
        private CSteamID _pendingInvite = CSteamID.Nil;
#endif

        /// <summary>
        /// 当前房间信息变化（成员、设置、阶段）。
        /// </summary>
        public event Action RoomUpdated;

        /// <summary>
        /// 离开了房间。参数为原因，主动离开时为 null。
        /// </summary>
        public event Action<string> RoomClosed;

        /// <summary>
        /// 房间列表变化。
        /// </summary>
        public event Action RoomListUpdated;

        /// <summary>
        /// 开始一个耗时操作（创建 / 加入），参数为提示文本。
        /// </summary>
        public event Action<string> OperationStarted;

        /// <summary>
        /// 耗时操作结束。
        /// </summary>
        public event OperationFinishedHandler OperationFinished;

        /// <summary>
        /// 开始加载游戏场景。
        /// </summary>
        public event Action GameLoading;

        /// <summary>
        /// 已进入游戏场景并请求生成玩家。
        /// </summary>
        public event Action GameEntered;

        /// <summary>
        /// 在房间中收到 Steam 邀请，参数为提示文本，由 UI 确认后调用 AcceptPendingInvite。
        /// </summary>
        public event Action<string> InviteReceived;

        /// <summary>
        /// 游戏中按下 Esc，由 UI 确认是否离开。
        /// </summary>
        public event Action LeaveRequested;

        public PasswordPromptHandler PasswordPrompt { get; set; }

        /// <summary>
        /// 当前房间，不在房间中为 null。
        /// </summary>
        public RoomInfo Room => _room;

        public bool IsInRoom => _room != null;

        public bool IsHost => _session.IsHost;

        public bool IsBusy => _busy;

        public bool IsLoadingGame => _loadingGame;

        public bool InGame => _inGame;

        public string PlayerName => _playerName;

        public IReadOnlyList<RoomListEntry> RoomList => _roomList;

        public bool IsRefreshing => _steamListing || _lan.IsSearching;

        /// <summary>
        /// 最近一次刷新 Steam 列表的错误，成功时为 null。
        /// </summary>
        public string ListError => _listError;

        /// <summary>
        /// 是否可以邀请 Steam 好友（房间开放了 Steam 连接）。
        /// </summary>
        public bool CanInvite
        {
            get
            {
#if UNITY_STANDALONE_WIN
                return IsInRoom && SteamLobby.Instance.InLobby;
#else
                return false;
#endif
            }
        }

        protected override void OnInit()
        {
            base.OnInit();
            // 依赖的单例在这里提前创建：SingletonSystem 遍历 Update 列表时不能再创建新的 IUpdate 单例
            _session = SessionManager.Instance;
            _lan = new LanRoomDiscovery();
            _lan.ResultsChanged += RebuildRoomList;
            _playerName = ResolvePlayerName();

            _session.ClientStarted += OnClientStarted;
            _session.ServerPlayerJoined += OnServerPlayerJoined;
            _session.ServerPlayerLeft += OnServerPlayerLeft;
            _session.SessionEnded += OnSessionEnded;
            _session.Authenticator.Validator = ValidateJoin;
            _session.Network.CanAddPlayer = CanAddPlayer;
            _session.Network.SpawnPose = ResolveSpawnPose;
            _session.Network.ServerPlayerSpawned += OnServerPlayerSpawned;

#if UNITY_STANDALONE_WIN
            SteamLobby.Instance.JoinRequested += OnSteamJoinRequested;
#endif
        }

        public override void Active()
        {
            base.Active();
#if UNITY_STANDALONE_WIN
            // 游戏未运行时接受了 Steam 邀请
            if (SteamLobby.Instance.Available && SteamLobby.TryGetCommandLineLobby(out CSteamID lobby))
            {
                OnSteamJoinRequested(lobby);
            }
#endif
        }

        public void OnUpdate()
        {
            _lan.Poll();

            if (_inGame && Input.GetKeyDown(KeyCode.Escape))
            {
                LeaveRequested?.Invoke();
            }

            UpdateBallSpawnTimer();
        }

        /// <summary>
        /// 定时生成：由主机控制，游戏中每 15 秒在房间内随机位置生成一个球。
        /// </summary>
        private void UpdateBallSpawnTimer()
        {
            if (!IsHost || !_inGame || _serverPhase != RoomPhase.Playing)
            {
                return;
            }

            if (Time.unscaledTime < _nextBallSpawnTime)
            {
                return;
            }

            _nextBallSpawnTime = Time.unscaledTime + BallSpawnInterval;
            SpawnBallAt(RandomBallPosition());
        }

        private Vector3 RandomBallPosition()
        {
            Vector3 center = _spawnPoints.Count > 0
                ? _spawnPoints[UnityEngine.Random.Range(0, _spawnPoints.Count)].position
                : DefaultSpawnCenter;
            Vector2 offset = UnityEngine.Random.insideUnitCircle * RandomBallRadius;
            return new Vector3(center.x + offset.x, center.y + RandomBallDropHeight, center.z + offset.y);
        }

        protected override void OnRelease()
        {
            if (_session != null)
            {
                _session.ClientStarted -= OnClientStarted;
                _session.ServerPlayerJoined -= OnServerPlayerJoined;
                _session.ServerPlayerLeft -= OnServerPlayerLeft;
                _session.SessionEnded -= OnSessionEnded;
                if (_session.Network != null)
                {
                    _session.Network.ServerPlayerSpawned -= OnServerPlayerSpawned;
                }
            }

#if UNITY_STANDALONE_WIN
            if (SteamLobby.IsValid)
            {
                SteamLobby.Instance.JoinRequested -= OnSteamJoinRequested;
            }
#endif
            _lan?.Dispose();
        }

        #region 创建房间

        /// <summary>
        /// 创建房间并作为房主进入。结果同时通过 OperationFinished 通知。
        /// </summary>
        public async UniTask<RoomOpResult> CreateRoomAsync(RoomSettings settings)
        {
            if (!TryBeginOperation("正在创建房间...", out RoomOpResult blocked))
            {
                return blocked;
            }

            RoomOpResult result;
            try
            {
                result = await CreateRoomInternalAsync(settings);
            }
            catch (Exception e)
            {
                Log.Error($"[Room] 创建房间异常: {e}");
                ResetRoom(true);
                result = RoomOpResult.Fail("创建房间失败");
            }

            EndOperation(result);
            return result;
        }

        private async UniTask<RoomOpResult> CreateRoomInternalAsync(RoomSettings input)
        {
            RoomSettings settings = NormalizeSettings(input, out string error);
            if (error != null)
            {
                return RoomOpResult.Fail(error);
            }

            if (!await EnsurePrefabsAsync())
            {
                return RoomOpResult.Fail("加载联机资源失败");
            }

            bool useSteam = SessionManager.SteamAvailable;
            bool useLan = SessionManager.LanEnabled;
#if UNITY_STANDALONE_WIN
            if (useSteam)
            {
                SteamLobbyResult lobby = await SteamLobby.Instance.CreateLobbyAsync(settings.MaxPlayers);
                if (!lobby.Success)
                {
                    if (!useLan)
                    {
                        return RoomOpResult.Fail(lobby.Message);
                    }

                    Log.Warning($"[Room] {lobby.Message}，房间只在局域网可见");
                    useSteam = false;
                }
            }
#else
            useSteam = false;
#endif

            BeginServerRoom(settings);
            RoomOpResult started = _session.StartHost(BuildJoinRequest(null), useSteam, useLan);
            if (!started.Success)
            {
                ResetRoom(true);
                return started;
            }

            if (_session.HostUsesLan)
            {
                _lan.StartAdvertising(BuildLanAdvertisement);
            }

            // 房主在 StartHost 中已作为第一个成员加入，这里再发布一次（此时才知道开放了哪些连接方式）
            PublishRoomState();
            Log.Info($"[Room] 房间已创建：{settings.Name}（密码:{(settings.HasPassword ? "有" : "无")}，" +
                     $"Steam:{(_session.HostUsesSteam ? "开" : "关")}，局域网:{(_session.HostUsesLan ? _session.LanPort.ToString() : "关")}）");
            return RoomOpResult.Ok();
        }

        private RoomSettings NormalizeSettings(RoomSettings input, out string error)
        {
            RoomSettings settings = input != null ? input.Clone() : new RoomSettings();
            settings.Name = RoomProtocol.Sanitize(settings.Name, RoomProtocol.MaxNameLength);
            if (settings.Name.Length == 0)
            {
                settings.Name = RoomProtocol.Sanitize($"{_playerName}的房间", RoomProtocol.MaxNameLength);
            }

            settings.Password = settings.HasPassword
                ? RoomProtocol.Sanitize(settings.Password, RoomProtocol.MaxPasswordLength)
                : string.Empty;
            settings.MaxPlayers = Mathf.Clamp(settings.MaxPlayers, 2, RoomProtocol.MaxPlayers);

            error = settings.HasPassword && settings.Password.Length == 0 ? "请输入房间密码" : null;
            return settings;
        }

        private void BeginServerRoom(RoomSettings settings)
        {
            _roomSerial++;
            _settings = settings;
            _roomId = Guid.NewGuid().ToString("N");
            _serverPhase = RoomPhase.Waiting;
            _members.Clear();
            _spawnPoints.Clear();
            _balls.Clear();
            _nextBallSpawnTime = 0f;
            _room = null;
            _loadingGame = false;
            _inGame = false;
        }

        #endregion

        #region 房间列表

        /// <summary>
        /// 刷新房间列表：Steam 全球搜索 + 开发版局域网搜索，结果通过 RoomListUpdated 通知。
        /// </summary>
        public void RefreshRoomList()
        {
            if (SessionManager.LanEnabled)
            {
                _lan.StartSearch(LanSearchDuration);
            }

#if UNITY_STANDALONE_WIN
            if (!_steamListing && SteamLobby.Instance.Available)
            {
                RefreshSteamListAsync().Forget();
            }
#endif
            RebuildRoomList();
        }

#if UNITY_STANDALONE_WIN
        private async UniTaskVoid RefreshSteamListAsync()
        {
            _steamListing = true;
            RebuildRoomList();
            SteamLobbyListResult result = await SteamLobby.Instance.RequestLobbyListAsync();
            _steamListing = false;
            _steamEntries.Clear();
            _listError = result.Success ? null : result.Message;
            if (result.Success)
            {
                _steamEntries.AddRange(result.Entries);
            }

            RebuildRoomList();
        }
#endif

        /// <summary>
        /// 合并 Steam 和局域网结果：只保留能加入的房间，同一房间两条记录时保留局域网那条。
        /// </summary>
        private void RebuildRoomList()
        {
            _roomList.Clear();
            IReadOnlyList<RoomListEntry> lan = _lan.Results;
            for (int i = 0; i < lan.Count; i++)
            {
                TryAddListEntry(lan[i]);
            }

            for (int i = 0; i < _steamEntries.Count; i++)
            {
                TryAddListEntry(_steamEntries[i]);
            }

            _roomList.Sort(CompareListEntries);
            RoomListUpdated?.Invoke();
        }

        private void TryAddListEntry(RoomListEntry entry)
        {
            if (entry == null || !entry.CanJoin(out _))
            {
                return;
            }

            if (!string.IsNullOrEmpty(entry.RoomId))
            {
                for (int i = 0; i < _roomList.Count; i++)
                {
                    if (_roomList[i].RoomId == entry.RoomId)
                    {
                        return;
                    }
                }
            }

            _roomList.Add(entry);
        }

        private static int CompareListEntries(RoomListEntry a, RoomListEntry b)
        {
            // 等待中的房间排在前面
            int phaseA = a.Phase == RoomPhase.Waiting ? 0 : 1;
            int phaseB = b.Phase == RoomPhase.Waiting ? 0 : 1;
            if (phaseA != phaseB)
            {
                return phaseA.CompareTo(phaseB);
            }

            return string.CompareOrdinal(a.Name, b.Name);
        }

        #endregion

        #region 加入房间

        /// <summary>
        /// 加入列表中的房间，有密码时通过 PasswordPrompt 询问。结果同时通过 OperationFinished 通知。
        /// </summary>
        public async UniTask<RoomOpResult> JoinRoomAsync(RoomListEntry entry)
        {
            if (entry == null)
            {
                return RoomOpResult.Fail("无效的房间");
            }

            if (!TryBeginOperation("正在加入房间...", out RoomOpResult blocked))
            {
                return blocked;
            }

            RoomOpResult result;
            try
            {
                result = await JoinRoomInternalAsync(entry);
            }
            catch (Exception e)
            {
                Log.Error($"[Room] 加入房间异常: {e}");
                ResetRoom(true);
                result = RoomOpResult.Fail("加入房间失败");
            }

            EndOperation(result);
            return result;
        }

        private async UniTask<RoomOpResult> JoinRoomInternalAsync(RoomListEntry entry)
        {
            if (!entry.CanJoin(out string reason))
            {
                return RoomOpResult.Fail(reason);
            }

            if (!await EnsurePrefabsAsync())
            {
                return RoomOpResult.Fail("加载联机资源失败");
            }

            string password = null;
            if (entry.HasPassword)
            {
                password = await PromptPasswordAsync(entry.Name, "正在加入房间...");
                if (password == null)
                {
                    return RoomOpResult.Cancelled();
                }
            }

            BeginClientRoom();
            if (entry.Source == RoomSource.Lan)
            {
                return await ConnectAsync(_session.JoinLanAsync(entry.Address, entry.Port, BuildJoinRequest(password)));
            }

#if UNITY_STANDALONE_WIN
            SteamLobbyResult lobby = await SteamLobby.Instance.JoinLobbyAsync(new CSteamID(entry.SteamLobbyId));
            if (!lobby.Success)
            {
                return RoomOpResult.Fail(lobby.Message);
            }

            return await ConnectSteamAsync(lobby, password);
#else
            return RoomOpResult.Fail("当前平台不支持 Steam 房间");
#endif
        }

#if UNITY_STANDALONE_WIN
        private void OnSteamJoinRequested(CSteamID lobby)
        {
            if (IsInRoom && SteamLobby.Instance.CurrentLobby == lobby)
            {
                return;
            }

            if (_busy)
            {
                Log.Warning("[Room] 正在处理其他操作，忽略本次邀请");
                return;
            }

            if (IsInRoom || _session.State != SessionState.Offline)
            {
                _pendingInvite = lobby;
                InviteReceived?.Invoke("收到好友的房间邀请，是否离开当前房间并加入？");
                return;
            }

            JoinInviteAsync(lobby).Forget();
        }

        private async UniTask<RoomOpResult> JoinInviteAsync(CSteamID lobby)
        {
            if (!TryBeginOperation("正在加入好友的房间...", out RoomOpResult blocked))
            {
                return blocked;
            }

            RoomOpResult result;
            try
            {
                result = await JoinInviteInternalAsync(lobby);
            }
            catch (Exception e)
            {
                Log.Error($"[Room] 加入邀请的房间异常: {e}");
                ResetRoom(true);
                result = RoomOpResult.Fail("加入房间失败");
            }

            EndOperation(result);
            return result;
        }

        private async UniTask<RoomOpResult> JoinInviteInternalAsync(CSteamID lobbyId)
        {
            if (!await EnsurePrefabsAsync())
            {
                return RoomOpResult.Fail("加载联机资源失败");
            }

            // 邀请只知道大厅 ID：先进入大厅读取房间信息，再决定是否需要密码
            SteamLobbyResult lobby = await SteamLobby.Instance.JoinLobbyAsync(lobbyId);
            if (!lobby.Success)
            {
                return RoomOpResult.Fail(lobby.Message);
            }

            if (!lobby.Entry.CanJoin(out string reason))
            {
                SteamLobby.Instance.LeaveLobby();
                return RoomOpResult.Fail(reason);
            }

            string password = null;
            if (lobby.Entry.HasPassword)
            {
                password = await PromptPasswordAsync(lobby.Entry.Name, "正在加入好友的房间...");
                if (password == null)
                {
                    SteamLobby.Instance.LeaveLobby();
                    return RoomOpResult.Cancelled();
                }
            }

            BeginClientRoom();
            return await ConnectSteamAsync(lobby, password);
        }

        private async UniTask<RoomOpResult> ConnectSteamAsync(SteamLobbyResult lobby, string password)
        {
            if (lobby.Host == SteamUser.GetSteamID())
            {
                SteamLobby.Instance.LeaveLobby();
                return RoomOpResult.Fail("不能通过 Steam 加入同一个 Steam 账号创建的房间。\n同一台电脑测试请加入标记为“局域网”的房间。");
            }

            RoomOpResult result = await ConnectAsync(_session.JoinSteamAsync(lobby.Host, BuildJoinRequest(password)));
            if (!result.Success)
            {
                SteamLobby.Instance.LeaveLobby();
            }

            return result;
        }
#endif

        /// <summary>
        /// 接受在房间中收到的邀请：离开当前房间，再加入邀请的房间。
        /// </summary>
        public void AcceptPendingInvite()
        {
#if UNITY_STANDALONE_WIN
            CSteamID lobby = _pendingInvite;
            _pendingInvite = CSteamID.Nil;
            if (!lobby.IsValid())
            {
                return;
            }

            if (IsInRoom || _session.State != SessionState.Offline)
            {
                ResetRoom(true);
                RoomClosed?.Invoke(null);
            }

            JoinInviteNextFrameAsync(lobby).Forget();
#endif
        }

        public void DeclinePendingInvite()
        {
#if UNITY_STANDALONE_WIN
            _pendingInvite = CSteamID.Nil;
#endif
        }

#if UNITY_STANDALONE_WIN
        private async UniTaskVoid JoinInviteNextFrameAsync(CSteamID lobby)
        {
            // 等 Mirror 清理完上一个连接
            await UniTask.Yield();
            await JoinInviteAsync(lobby);
        }
#endif

        private void BeginClientRoom()
        {
            _roomSerial++;
            _room = null;
            _loadingGame = false;
            _inGame = false;
        }

        /// <summary>
        /// 等待连接和认证完成，再等房主下发第一份房间状态。
        /// </summary>
        private async UniTask<RoomOpResult> ConnectAsync(UniTask<RoomOpResult> joinTask)
        {
            RoomOpResult result = await joinTask;
            if (!result.Success)
            {
                return result;
            }

            float deadline = Time.unscaledTime + RoomStateTimeout;
            while (_room == null && _session.IsClient && Time.unscaledTime < deadline)
            {
                await UniTask.Yield();
            }

            if (_room != null)
            {
                Log.Info($"[Room] 已加入房间：{_room.Name}");
                return RoomOpResult.Ok();
            }

            if (!_session.IsClient)
            {
                return RoomOpResult.Fail(_lastEndReason ?? "与房间的连接已断开");
            }

            ResetRoom(true);
            return RoomOpResult.Fail("没有收到房间信息，请重试");
        }

        private async UniTask<string> PromptPasswordAsync(string roomName, string busyText)
        {
            if (PasswordPrompt == null)
            {
                return string.Empty;
            }

            string password = await PasswordPrompt(roomName);
            if (password != null)
            {
                // 输入框关闭后恢复等待提示
                OperationStarted?.Invoke(busyText);
            }

            return password;
        }

        #endregion

        #region 房间内操作

        /// <summary>
        /// 房主：设置是否允许中途加入（游戏中不允许时，Steam 大厅也会设为不可加入）。
        /// </summary>
        public void SetAllowMidJoin(bool allow)
        {
            if (!_session.IsHost || _settings == null || _settings.AllowMidJoin == allow)
            {
                return;
            }

            _settings.AllowMidJoin = allow;
            PublishRoomState();
        }

        /// <summary>
        /// 打开 Steam 好友邀请对话框。
        /// </summary>
        public void OpenInviteDialog()
        {
#if UNITY_STANDALONE_WIN
            SteamLobby.Instance.OpenInviteDialog();
#endif
        }

        /// <summary>
        /// 房主：开始游戏。可以不等其他人，直接开始。
        /// </summary>
        public async UniTask<RoomOpResult> StartGameAsync()
        {
            if (!_session.IsHost || _settings == null)
            {
                return RoomOpResult.Fail("只有房主可以开始游戏");
            }

            if (_serverPhase != RoomPhase.Waiting || _loadingGame || _inGame)
            {
                return RoomOpResult.Fail("游戏已经开始");
            }

            if (_busy)
            {
                return RoomOpResult.Fail("正在处理中，请稍候");
            }

            int serial = _roomSerial;
            _serverPhase = RoomPhase.Starting;
            PublishRoomState();
            _loadingGame = true;
            GameLoading?.Invoke();

            bool loaded = await LoadGameSceneAsync();
            if (serial != _roomSerial || !_session.IsHost)
            {
                return RoomOpResult.Fail("房间已关闭");
            }

            _loadingGame = false;
            if (!loaded)
            {
                _serverPhase = RoomPhase.Waiting;
                PublishRoomState();
                return RoomOpResult.Fail("游戏场景加载失败");
            }

            CollectSpawnPoints();
            // 场景里如果放了带 NetworkIdentity 的物体，在这里统一生成
            NetworkServer.SpawnObjects();
            SpawnBall();
            _nextBallSpawnTime = Time.unscaledTime + BallSpawnInterval;

            // 成员收到 Playing 后各自加载场景，再 Ready + AddPlayer
            _serverPhase = RoomPhase.Playing;
            PublishRoomState();
            RequestLocalPlayer();
            await WaitLocalPlayerReadyAsync();
            if (serial != _roomSerial || !_session.IsHost)
            {
                return RoomOpResult.Fail("房间已关闭");
            }

            _inGame = true;
            Log.Info($"[Room] 游戏开始，当前 {_members.Count} 人");
            GameEntered?.Invoke();
            return RoomOpResult.Ok();
        }

        /// <summary>
        /// 离开房间：房主离开会关闭房间（所有成员断开），成员离开只断开自己。
        /// </summary>
        public async UniTaskVoid LeaveRoom()
        {
            if (!IsInRoom && !_loadingGame && !_inGame && _session.State == SessionState.Offline)
            {
                Log.Warning($"[Room] LeaveRoom 被忽略：不在房间中（inRoom={IsInRoom} inGame={_inGame} state={_session.State}）");
                return;
            }

            Log.Info(_session.IsHost ? "[Room] 房主关闭房间" : "[Room] 离开房间");

#if UNITY_STANDALONE_WIN
            if (_session.IsHost)
            {
                // 通知所有成员弹出"房主解散"提示；等每个成员回执"提示已完全显示"再拆连接（3 秒超时兜底）
                NetworkServer.ReplaceHandler<HostClosedAckMessage>(OnHostClosedAck);
                _hostCloseAcks = 0;
                int expected = 0;
                foreach (ServerMember member in _members)
                {
                    if (member.Connection != null && !member.IsHost)
                    {
                        member.Connection.Send(new HostClosedMessage());
                        expected++;
                    }
                }

                float deadline = Time.unscaledTime + 3f;
                while (_hostCloseAcks < expected && Time.unscaledTime < deadline && _session.IsHost)
                {
                    await UniTask.Yield();
                }

                if (!_session.IsHost)
                {
                    return;
                }
            }
#endif

            ResetRoom(true);
            RoomClosed?.Invoke(null);
        }

        private void ResetRoom(bool stopSession)
        {
            _roomSerial++;
            if (stopSession)
            {
                // 先断开 Mirror：房主会关闭房间并销毁球和所有玩家
                _session.Leave();
            }

            _lan.StopAdvertising();
#if UNITY_STANDALONE_WIN
            try
            {
                SteamLobby.Instance.LeaveLobby();
            }
            catch (Exception e)
            {
                Log.Warning($"[Room] 离开 Steam 大厅异常: {e.Message}");
            }
#endif
            _settings = null;
            _roomId = null;
            _serverPhase = RoomPhase.None;
            _members.Clear();
            _spawnPoints.Clear();
            _balls.Clear();
            _nextBallSpawnTime = 0f;
            _room = null;
            _loadingGame = false;
            _inGame = false;
        }

        private void OnSessionEnded(string reason)
        {
            _lastEndReason = reason;
            // 创建 / 加入过程中断开：由操作本身返回失败原因
            bool notify = !_busy && (IsInRoom || _loadingGame || _inGame);
            ResetRoom(false);
            if (notify)
            {
                RoomClosed?.Invoke(reason);
            }
        }

        #endregion

        #region 进入游戏

        private void OnClientStarted()
        {
            // NetworkClient 每次关闭都会清空消息处理，每次启动重新注册
            NetworkClient.ReplaceHandler<RoomStateMessage>(OnRoomStateMessage);
            NetworkClient.ReplaceHandler<BallVelocityMessage>(OnBallVelocityMessage);
            NetworkClient.ReplaceHandler<HostClosedMessage>(OnHostClosedMessage);
            NetworkServer.ReplaceHandler<HostClosedAckMessage>(OnHostClosedAck);
        }

        /// <summary>
        /// 客户端：收到房主解散通知，立刻弹提示盖住画面；提示完全显示后回执主机。
        /// </summary>
        private void OnHostClosedMessage(HostClosedMessage msg)
        {
            HostClosedNotified = true;
            Log.Info("[Room] 收到房主解散通知");
            GameModule.UI.ShowUI<TipsUI>("主机已解散房间，正在返回大厅...");
            SendHostClosedAckAsync().Forget();
        }

        private async UniTaskVoid SendHostClosedAckAsync()
        {
            // 等提示完全显示（背景淡入到位）再回执，主机收到后才开始拆连接
            await TipsUI.WaitShownAsync();
            if (NetworkClient.active)
            {
                NetworkClient.connection.Send(new HostClosedAckMessage());
            }
        }

        private void OnHostClosedAck(HostClosedAckMessage msg)
        {
            _hostCloseAcks++;
        }

        private void OnRoomStateMessage(RoomStateMessage msg)
        {
            // 房主直接使用服务端数据
            if (NetworkServer.active || !_session.IsClient)
            {
                return;
            }

            ApplyState(msg, false);
            if (msg.Phase == RoomPhase.Playing && !_inGame && !_loadingGame)
            {
                EnterGameAsClientAsync().Forget();
            }
        }

        /// <summary>
        /// 服务端：玩家对象生成后补发所有共享球的当前速度，供中途加入同步；Host 本地连接不用发。
        /// </summary>
        private void OnServerPlayerSpawned(NetworkConnectionToClient conn)
        {
            if (conn is LocalConnectionToClient)
            {
                return;
            }

            for (int i = 0; i < _balls.Count; i++)
            {
                GameObject ball = _balls[i];
                if (ball == null)
                {
                    continue;
                }

                Rigidbody body = ball.GetComponent<Rigidbody>();
                if (body == null)
                {
                    continue;
                }

                conn.Send(new BallVelocityMessage
                {
                    BallNetId = ball.GetComponent<NetworkIdentity>().netId,
                    Velocity = body.linearVelocity,
                    AngularVelocity = body.angularVelocity
                });
            }
        }

        private void OnBallVelocityMessage(BallVelocityMessage msg)
        {
            if (!NetworkClient.spawned.TryGetValue(msg.BallNetId, out NetworkIdentity identity))
            {
                return;
            }

            Rigidbody body = identity.GetComponent<Rigidbody>();
            if (body == null)
            {
                return;
            }

            body.linearVelocity = msg.Velocity;
            body.angularVelocity = msg.AngularVelocity;
        }

        private async UniTaskVoid EnterGameAsClientAsync()
        {
            int serial = _roomSerial;
            _loadingGame = true;
            GameLoading?.Invoke();

            bool loaded = await LoadGameSceneAsync();
            if (serial != _roomSerial || !_session.IsClient)
            {
                // 加载期间已离开房间
                return;
            }

            _loadingGame = false;
            if (!loaded)
            {
                ResetRoom(true);
                RoomClosed?.Invoke("游戏场景加载失败");
                return;
            }

            RequestLocalPlayer();
            await WaitLocalPlayerReadyAsync();
            if (serial != _roomSerial || !_session.IsClient)
            {
                return;
            }

            _inGame = true;
            Log.Info("[Room] 已进入游戏");
            GameEntered?.Invoke();
        }

        /// <summary>
        /// 等待本地玩家生成到位并稍作稳定，再通知进入游戏关闭房间 UI，避免视觉穿帮。
        /// </summary>
        private static async UniTask WaitLocalPlayerReadyAsync()
        {
            // 客户端要等服务器生成玩家并同步回来；超时兜底，期间断线由外层的序号检查处理
            float deadline = Time.unscaledTime + 10f;
            while (NetworkClient.active && NetworkClient.localPlayer == null && Time.unscaledTime < deadline)
            {
                await UniTask.Yield();
            }

            await UniTask.Delay(300, true);
        }

        private static async UniTask<bool> LoadGameSceneAsync()
        {
            // 离开房间后会留在游戏场景里显示菜单，再次开局时不重新加载（球和玩家都是网络对象，断开时已销毁）
            if (SceneManager.GetActiveScene().name == GameSceneName)
            {
                return true;
            }

            try
            {
                Scene scene = await GameModule.Scene.LoadSceneAsync(GameSceneLocation);
                return scene.IsValid() && scene.isLoaded;
            }
            catch (Exception e)
            {
                Log.Error($"[Room] 加载游戏场景失败: {e}");
                return false;
            }
        }

        /// <summary>
        /// 场景加载完成后：Ready（开始接收网络对象）并请求生成自己的玩家。
        /// </summary>
        private static void RequestLocalPlayer()
        {
            if (!NetworkClient.isConnected)
            {
                Log.Warning("[Room] 未连接，无法生成玩家");
                return;
            }

            if (!NetworkClient.ready)
            {
                NetworkClient.Ready();
            }

            if (NetworkClient.localPlayer == null)
            {
                NetworkClient.AddPlayer();
            }
        }

        private void SpawnBall()
        {
            if (_ballPrefab == null)
            {
                return;
            }

            SpawnBallAt(_ballPrefab.transform.position);
        }

        private void SpawnBallAt(Vector3 position)
        {
            if (_ballPrefab == null)
            {
                return;
            }

            GameObject ball = UnityEngine.Object.Instantiate(_ballPrefab, position, _ballPrefab.transform.rotation);
            ball.name = _ballPrefab.name;
            NetworkServer.Spawn(ball);
            _balls.Add(ball);
        }

        private void CollectSpawnPoints()
        {
            _spawnPoints.Clear();
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                CollectSpawnPoints(root.transform);
            }

            _spawnPoints.Sort(CompareTransformName);
        }

        private void CollectSpawnPoints(Transform node)
        {
            if (node.name.StartsWith(SpawnPointPrefix, StringComparison.Ordinal))
            {
                _spawnPoints.Add(node);
            }

            for (int i = 0; i < node.childCount; i++)
            {
                CollectSpawnPoints(node.GetChild(i));
            }
        }

        private static int CompareTransformName(Transform a, Transform b)
        {
            return string.CompareOrdinal(a.name, b.name);
        }

        #endregion

        #region 服务端（房主）

        private string ValidateJoin(NetworkConnectionToClient conn, RoomJoinRequestMessage request)
        {
            if (_settings == null)
            {
                return "房间不存在";
            }

            if (request.Version != RoomProtocol.Version)
            {
                return "游戏版本不一致，请更新后再加入";
            }

            if (_members.Count >= _settings.MaxPlayers)
            {
                return "房间已满";
            }

            if (_serverPhase != RoomPhase.Waiting && !_settings.AllowMidJoin)
            {
                return "游戏已开始，该房间不允许中途加入";
            }

            if (_settings.HasPassword &&
                !string.Equals(request.Password ?? string.Empty, _settings.Password, StringComparison.Ordinal))
            {
                return "密码错误";
            }

            return null;
        }

        private void OnServerPlayerJoined(NetworkConnectionToClient conn, RoomJoinRequestMessage request)
        {
            if (_settings == null)
            {
                return;
            }

            bool isHost = conn is LocalConnectionToClient;
            string name = RoomProtocol.Sanitize(request.PlayerName, RoomProtocol.MaxNameLength);
            if (name.Length == 0)
            {
                name = isHost ? "房主" : $"玩家{conn.connectionId}";
            }

            var member = new ServerMember
            {
                Connection = conn,
                Name = name,
                SteamId = request.SteamId,
                IsHost = isHost
            };

            // 房主固定在第一个位置
            if (isHost)
            {
                _members.Insert(0, member);
            }
            else
            {
                _members.Add(member);
            }

            Log.Info($"[Room] {name} 进入房间，当前 {_members.Count}/{_settings.MaxPlayers}");
            PublishRoomState();
        }

        private void OnServerPlayerLeft(NetworkConnectionToClient conn)
        {
            int index = FindMemberIndex(conn);
            if (index < 0)
            {
                return;
            }

            Log.Info($"[Room] {_members[index].Name} 离开房间");
            _members.RemoveAt(index);
            PublishRoomState();
        }

        private bool CanAddPlayer(NetworkConnectionToClient conn)
        {
            return _serverPhase == RoomPhase.Playing && FindMemberIndex(conn) >= 0;
        }

        private void ResolveSpawnPose(NetworkConnectionToClient conn, out Vector3 position, out Quaternion rotation)
        {
            int slot = Mathf.Max(0, FindMemberIndex(conn));
            _spawnPoints.RemoveAll(IsDestroyed);
            if (_spawnPoints.Count > 0)
            {
                Transform point = _spawnPoints[slot % _spawnPoints.Count];
                position = point.position;
                rotation = Quaternion.Euler(0f, point.eulerAngles.y, 0f);
                return;
            }

            position = DefaultSpawnCenter + Vector3.right * DefaultSpawnOffsets[slot % DefaultSpawnOffsets.Length];
            rotation = Quaternion.identity;
        }

        private static bool IsDestroyed(Transform t)
        {
            return t == null;
        }

        private int FindMemberIndex(NetworkConnectionToClient conn)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].Connection == conn)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool IsJoinable()
        {
            return _settings != null &&
                   _members.Count < _settings.MaxPlayers &&
                   (_serverPhase == RoomPhase.Waiting || _settings.AllowMidJoin);
        }

        /// <summary>
        /// 房主：把房间状态同步给自己（RoomUI）、所有成员、Steam 大厅数据。
        /// </summary>
        private void PublishRoomState()
        {
            if (_settings == null || !NetworkServer.active)
            {
                return;
            }

            var members = new RoomMemberData[_members.Count];
            int hostSlot = 0;
            for (int i = 0; i < _members.Count; i++)
            {
                ServerMember member = _members[i];
                members[i] = new RoomMemberData
                {
                    Slot = i,
                    Name = member.Name,
                    SteamId = member.SteamId,
                    IsHost = member.IsHost
                };

                if (member.IsHost)
                {
                    hostSlot = i;
                }
            }

            var msg = new RoomStateMessage
            {
                RoomName = _settings.Name,
                HasPassword = _settings.HasPassword,
                Password = _settings.HasPassword ? _settings.Password : string.Empty,
                AllowMidJoin = _settings.AllowMidJoin,
                MaxPlayers = _settings.MaxPlayers,
                Phase = _serverPhase,
                Members = members,
                YourSlot = hostSlot
            };

            ApplyState(msg, true);

            // 只发给已通过认证的成员（未认证的连接看不到密码）
            for (int i = 0; i < _members.Count; i++)
            {
                ServerMember member = _members[i];
                if (member.IsHost || member.Connection == null)
                {
                    continue;
                }

                msg.YourSlot = i;
                member.Connection.Send(msg);
            }

#if UNITY_STANDALONE_WIN
            if (_session.HostUsesSteam)
            {
                SteamLobby.Instance.PublishRoom(_roomId, _room, IsJoinable());
            }
#endif
        }

        private RoomListEntry BuildLanAdvertisement()
        {
            if (_settings == null || !_session.IsHost || !IsJoinable())
            {
                return null;
            }

            return new RoomListEntry
            {
                Source = RoomSource.Lan,
                RoomId = _roomId,
                Name = _settings.Name,
                HasPassword = _settings.HasPassword,
                AllowMidJoin = _settings.AllowMidJoin,
                Phase = _serverPhase,
                Members = _members.Count,
                MaxPlayers = _settings.MaxPlayers,
                Version = RoomProtocol.Version,
                Port = _session.LanPort
            };
        }

        #endregion

        #region 通用

        private void ApplyState(RoomStateMessage msg, bool isHost)
        {
            RoomInfo room = _room ?? new RoomInfo();
            room.Name = msg.RoomName;
            room.HasPassword = msg.HasPassword;
            room.Password = msg.Password ?? string.Empty;
            room.AllowMidJoin = msg.AllowMidJoin;
            room.MaxPlayers = msg.MaxPlayers;
            room.Phase = msg.Phase;
            room.IsLocalHost = isHost;
            room.LocalSlot = msg.YourSlot;
            room.Members.Clear();
            if (msg.Members != null)
            {
                for (int i = 0; i < msg.Members.Length; i++)
                {
                    RoomMemberData data = msg.Members[i];
                    room.Members.Add(new RoomMember
                    {
                        Slot = data.Slot,
                        Name = data.Name,
                        SteamId = data.SteamId,
                        IsHost = data.IsHost,
                        IsLocal = data.Slot == msg.YourSlot
                    });
                }
            }

            _room = room;
            RoomUpdated?.Invoke();
        }

        private bool TryBeginOperation(string text, out RoomOpResult blocked)
        {
            if (_busy)
            {
                blocked = RoomOpResult.Fail("正在处理中，请稍候");
                return false;
            }

            if (IsInRoom || _session.State != SessionState.Offline)
            {
                blocked = RoomOpResult.Fail("已经在房间中");
                return false;
            }

            _busy = true;
            _lastEndReason = null;
            blocked = null;
            OperationStarted?.Invoke(text);
            return true;
        }

        private void EndOperation(RoomOpResult result)
        {
            _busy = false;
            if (!result.Success && !result.IsCancelled)
            {
                Log.Warning($"[Room] {result.Message}");
            }

            OperationFinished?.Invoke(result);
        }

        private async UniTask<bool> EnsurePrefabsAsync()
        {
            if (_prefabsReady)
            {
                return true;
            }

            try
            {
                if (_playerPrefab == null)
                {
                    _playerPrefab = await GameModule.Resource.LoadAssetAsync<GameObject>(PlayerPrefabLocation);
                }

                if (_ballPrefab == null)
                {
                    _ballPrefab = await GameModule.Resource.LoadAssetAsync<GameObject>(BallPrefabLocation);
                }
            }
            catch (Exception e)
            {
                Log.Error($"[Room] 加载联机预制体异常: {e}");
            }

            if (_playerPrefab == null)
            {
                Log.Error($"[Room] 玩家预制体 {PlayerPrefabLocation} 加载失败");
                return false;
            }

            _session.SetPlayerPrefab(_playerPrefab);
            if (_ballPrefab != null)
            {
                _session.RegisterNetworkPrefab(_ballPrefab);
            }
            else
            {
                Log.Warning($"[Room] 球预制体 {BallPrefabLocation} 加载失败，开局后不会生成球");
            }

            _prefabsReady = true;
            return true;
        }

        private RoomJoinRequestMessage BuildJoinRequest(string password)
        {
            return new RoomJoinRequestMessage
            {
                Version = RoomProtocol.Version,
                Password = RoomProtocol.Sanitize(password, RoomProtocol.MaxPasswordLength),
                PlayerName = _playerName,
                SteamId = GetLocalSteamId()
            };
        }

        private static ulong GetLocalSteamId()
        {
#if UNITY_STANDALONE_WIN
            if (SteamManager.Initialized)
            {
                return SteamUser.GetSteamID().m_SteamID;
            }
#endif
            return 0;
        }

        private static string ResolvePlayerName()
        {
#if UNITY_STANDALONE_WIN
            if (SteamManager.Initialized)
            {
                string steamName = RoomProtocol.Sanitize(SteamFriends.GetPersonaName(), RoomProtocol.MaxNameLength);
                if (steamName.Length > 0)
                {
                    return steamName;
                }
            }
#endif
            return "玩家" + UnityEngine.Random.Range(1000, 10000);
        }

        #endregion
    }
}
