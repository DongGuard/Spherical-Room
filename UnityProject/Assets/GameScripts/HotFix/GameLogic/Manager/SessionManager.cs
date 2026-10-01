using System;
using Cysharp.Threading.Tasks;
using kcp2k;
using Mirror;
using TEngine;
using UnityEngine;
using UnityEngine.SceneManagement;
// kcp2k 也有 Log 类，这里统一使用 TEngine 的日志
using Log = TEngine.Log;
#if UNITY_STANDALONE_WIN
using Mirror.FizzySteam;
using Steamworks;
#endif

namespace GameLogic
{
    /// <summary>
    /// 联机会话状态。
    /// </summary>
    public enum SessionState
    {
        Offline,
        Starting,
        Host,
        Client,
        Stopping
    }

    /// <summary>
    /// 联机会话管理（Mirror）：创建 NetworkManager、启动 Host / 加入、人数上限、断线处理。
    /// 只负责连接生命周期，不包含玩法逻辑；UI 通过事件订阅状态变化。
    /// <para>
    /// 传输层：局域网 / IP 直连用 KcpTransport；Steam 可用时额外挂 FizzySteamworks（P2P + Steam Relay），
    /// 每次启动前切换，上层代码不感知。
    /// </para>
    /// <para>
    /// 场景策略：不使用 Mirror 的 online/offline 场景，各端先通过 TEngine 场景模块（YooAsset）
    /// 本地加载 Room 再连接。地形是静态物体，玩家和球动态生成，中途加入由 Mirror 自动同步已生成对象。
    /// </para>
    /// </summary>
    public class SessionManager : Singleton<SessionManager>
    {
        public const int MaxPlayers = 4;
        public const ushort DefaultPort = 7777;
        public const string DefaultAddress = "127.0.0.1";

        /// <summary>网络发送频率与物理步长对齐，每次物理模拟对应一次状态下发。</summary>
        public const int TickRate = 60;

        /// <summary>Room 场景的 YooAsset 定位地址。</summary>
        public const string RoomSceneLocation = "Room";

        private const string RoomSceneName = "Room";

        private RoomNetworkManager _manager;
        private KcpTransport _kcp;
#if UNITY_STANDALONE_WIN
        private FizzySteamworks _steam;
#endif
        private bool _shutdownRequested;
        private bool _clientConnected;
        private string _lastError;

        /// <summary>状态变化。</summary>
        public event Action<SessionState> StateChanged;

        /// <summary>会话非主动结束（加入失败、主机断开等），参数为可直接展示的原因。</summary>
        public event Action<string> SessionEnded;

        /// <summary>仅服务端：远端玩家连入（参数为 connectionId，不含 Host 自己）。</summary>
        public event Action<int> PlayerJoined;

        /// <summary>仅服务端：远端玩家离开。</summary>
        public event Action<int> PlayerLeft;

        public SessionState State { get; private set; } = SessionState.Offline;

        public NetworkManager Network => _manager;

        public bool IsHost => State == SessionState.Host;

        public bool IsOnline => State == SessionState.Host || State == SessionState.Client;

        /// <summary>仅服务端有效：当前连接人数（含 Host 自己）。</summary>
        public int ServerPlayerCount => NetworkServer.active ? NetworkServer.connections.Count : 0;

        /// <summary>仅服务端：新玩家出生位置，由 RoomManager 提供。</summary>
        public Func<Vector3> SpawnPositionProvider
        {
            get => _manager.SpawnPositionProvider;
            set => _manager.SpawnPositionProvider = value;
        }

        /// <summary>仅服务端：新玩家出生朝向。</summary>
        public Func<Quaternion> SpawnRotationProvider
        {
            get => _manager.SpawnRotationProvider;
            set => _manager.SpawnRotationProvider = value;
        }

        protected override void OnInit()
        {
            base.OnInit();
            CreateNetworkManager();
        }

        /// <summary>设置玩家预制体（必须带 NetworkIdentity），连接后自动为每个玩家生成。</summary>
        public void SetPlayerPrefab(GameObject playerPrefab)
        {
            if (playerPrefab != null && playerPrefab.GetComponent<NetworkIdentity>() == null)
            {
                Log.Error($"[Session] 玩家预制体 {playerPrefab.name} 缺少 NetworkIdentity 组件");
                return;
            }

            _manager.playerPrefab = playerPrefab;
        }

        /// <summary>注册运行时 Spawn 的网络预制体（如共享球）。必须在启动前、且各端一致。</summary>
        public void RegisterNetworkPrefab(GameObject prefab)
        {
            if (prefab == null || prefab.GetComponent<NetworkIdentity>() == null)
            {
                Log.Error($"[Session] 网络预制体 {prefab?.name} 缺少 NetworkIdentity 组件");
                return;
            }

            if (!_manager.spawnPrefabs.Contains(prefab))
            {
                _manager.spawnPrefabs.Add(prefab);
            }
        }

        #region 启动 / 离开

        /// <summary>加载 Room 并作为主机启动（KCP），其他玩家通过本机 IP + 端口加入。</summary>
        public async UniTask<bool> StartHostAsync(ushort port = DefaultPort)
        {
            if (!await PrepareStartAsync())
            {
                return false;
            }

            _kcp.port = port;
            UseTransport(_kcp);
            return TryStartHost($"主机启动失败，端口 {port} 可能被占用");
        }

        /// <summary>
        /// 加载 Room 并连接到指定主机（KCP）。返回 true 只表示开始连接，
        /// 连接成功后状态变为 Client，失败时触发 SessionEnded。
        /// </summary>
        public async UniTask<bool> JoinAsync(string address, ushort port = DefaultPort)
        {
            address = string.IsNullOrWhiteSpace(address) ? DefaultAddress : address.Trim();
            if (!await PrepareStartAsync())
            {
                return false;
            }

            _kcp.port = port;
            UseTransport(_kcp);
            _manager.networkAddress = address;
            _manager.StartClient();
            Log.Info($"[Session] 正在连接 {address}:{port}");
            return true;
        }

#if UNITY_STANDALONE_WIN
        /// <summary>Steam 是否可用于联机（客户端已启动并登录）。</summary>
        public bool IsSteamAvailable => SteamManager.Initialized;

        /// <summary>使用 Steam P2P（FizzySteamworks）作为主机启动。一般由 SteamLobby 在大厅创建后调用。</summary>
        public async UniTask<bool> StartSteamHostAsync()
        {
            if (!IsSteamAvailable)
            {
                SessionEnded?.Invoke("Steam 未启动，无法创建 Steam 房间");
                return false;
            }

            if (!await PrepareStartAsync())
            {
                return false;
            }

            UseTransport(_steam);
            return TryStartHost("Steam 主机启动失败");
        }

        /// <summary>通过 Steam P2P 连接到指定主机。一般由 SteamLobby 在进入大厅后调用。</summary>
        public async UniTask<bool> JoinSteamAsync(CSteamID hostSteamId)
        {
            if (!IsSteamAvailable)
            {
                SessionEnded?.Invoke("Steam 未启动，无法加入 Steam 房间");
                return false;
            }

            if (!await PrepareStartAsync())
            {
                return false;
            }

            UseTransport(_steam);
            // FizzySteamworks 的地址就是主机 SteamID
            _manager.networkAddress = hostSteamId.m_SteamID.ToString();
            _manager.StartClient();
            Log.Info($"[Session] 正在通过 Steam 连接主机 {hostSteamId}");
            return true;
        }
#endif

        /// <summary>主动离开会话（Host 调用会让所有客户端断开）。主动离开不触发 SessionEnded。</summary>
        public void Leave()
        {
            if (!_manager.isNetworkActive)
            {
                SetState(SessionState.Offline);
                return;
            }

            _shutdownRequested = true;
            SetState(SessionState.Stopping);
            if (NetworkServer.active)
            {
                _manager.StopHost();
            }
            else
            {
                _manager.StopClient();
            }
        }

        protected override void OnRelease()
        {
            if (_manager == null)
            {
                return;
            }

            UnregisterCallbacks();
            if (_manager.isNetworkActive)
            {
                _shutdownRequested = true;
                if (NetworkServer.active)
                {
                    _manager.StopHost();
                }
                else
                {
                    _manager.StopClient();
                }
            }

            UnityEngine.Object.Destroy(_manager.gameObject);
            _manager = null;
        }

        #endregion

        #region 启动流程

        private async UniTask<bool> PrepareStartAsync()
        {
            if (State != SessionState.Offline)
            {
                Log.Warning($"[Session] 当前状态 {State}，不能重复启动");
                return false;
            }

            _shutdownRequested = false;
            _clientConnected = false;
            _lastError = null;
            SetState(SessionState.Starting);

            if (!await LoadRoomAsync())
            {
                FailStart("Room 场景加载失败");
                return false;
            }

            return true;
        }

        private bool TryStartHost(string failReason)
        {
            try
            {
                _manager.StartHost();
            }
            catch (Exception e)
            {
                Log.Error($"[Session] StartHost 异常: {e}");
            }

            if (!NetworkServer.active)
            {
                FailStart(failReason);
                return false;
            }

            SetState(SessionState.Host);
            Log.Info("[Session] Host 已启动");
            return true;
        }

        private void FailStart(string reason)
        {
            Log.Error($"[Session] {reason}");
            if (_manager.isNetworkActive)
            {
                _shutdownRequested = true;
                _manager.StopHost();
            }

            SetState(SessionState.Offline);
            SessionEnded?.Invoke(reason);
        }

        /// <summary>通过 TEngine 场景模块加载 Room；已经在 Room 中则跳过（例如断线后重新 Host）。</summary>
        private async UniTask<bool> LoadRoomAsync()
        {
            if (SceneManager.GetActiveScene().name == RoomSceneName)
            {
                return true;
            }

            Scene scene = await GameModule.Scene.LoadSceneAsync(RoomSceneLocation);
            return scene.IsValid() && scene.isLoaded;
        }

        /// <summary>
        /// 切换传输层。NetworkManager 只在首次初始化时设置 Transport.active，之后要手动同步。
        /// </summary>
        private void UseTransport(Transport transport)
        {
            _manager.transport = transport;
            Transport.active = transport;
        }

        #endregion

        #region NetworkManager 创建与回调

        private void CreateNetworkManager()
        {
            // 先保持未激活，等传输层和参数都配置好再激活，避免 NetworkManager.Awake 找不到 Transport
            var go = new GameObject(nameof(RoomNetworkManager));
            go.SetActive(false);

            _kcp = go.AddComponent<KcpTransport>();
            _kcp.port = DefaultPort;

#if UNITY_STANDALONE_WIN
            // FizzySteamworks 启用时就会调用 Steam 接口，Steam 不可用时不添加
            if (SteamManager.Initialized)
            {
                _steam = go.AddComponent<FizzySteamworks>();
            }
#endif

            _manager = go.AddComponent<RoomNetworkManager>();
            _manager.transport = _kcp;
            _manager.maxConnections = MaxPlayers;
            _manager.sendRate = TickRate;
            _manager.dontDestroyOnLoad = true;
            _manager.runInBackground = true;
            // 场景由各端通过 YooAsset 本地加载，不走 Mirror 的场景切换
            _manager.offlineScene = string.Empty;
            _manager.onlineScene = string.Empty;
            _manager.autoCreatePlayer = true;

            // 物理步长与网络发送频率对齐
            Time.fixedDeltaTime = 1f / TickRate;

            RegisterCallbacks();
            go.SetActive(true);
        }

        private void RegisterCallbacks()
        {
            _manager.HostStopped += OnHostStopped;
            _manager.ClientConnected += OnClientConnected;
            _manager.ClientDisconnected += OnClientDisconnected;
            _manager.ClientErrored += OnClientErrored;
            _manager.ServerConnected += OnServerConnected;
            _manager.ServerDisconnected += OnServerDisconnected;
        }

        private void UnregisterCallbacks()
        {
            _manager.HostStopped -= OnHostStopped;
            _manager.ClientConnected -= OnClientConnected;
            _manager.ClientDisconnected -= OnClientDisconnected;
            _manager.ClientErrored -= OnClientErrored;
            _manager.ServerConnected -= OnServerConnected;
            _manager.ServerDisconnected -= OnServerDisconnected;
        }

        private void OnClientConnected()
        {
            // Host 的本地客户端也会触发，Host 状态已在 TryStartHost 设置
            if (NetworkServer.active)
            {
                return;
            }

            _clientConnected = true;
            SetState(SessionState.Client);
            Log.Info("[Session] 已加入主机");
        }

        private void OnClientDisconnected()
        {
            // Host 停止时本地客户端也会断开，统一由 OnHostStopped 处理
            if (NetworkServer.active)
            {
                return;
            }

            EndSession(false);
        }

        private void OnHostStopped()
        {
            EndSession(true);
        }

        private void OnClientErrored(string reason)
        {
            _lastError = reason;
            Log.Warning($"[Session] 传输层错误: {reason}");
        }

        private void OnServerConnected(NetworkConnectionToClient conn)
        {
            if (conn is LocalConnectionToClient)
            {
                return;
            }

            Log.Info($"[Session] 玩家 {conn.connectionId} 加入，当前 {ServerPlayerCount}/{MaxPlayers}");
            PlayerJoined?.Invoke(conn.connectionId);
        }

        private void OnServerDisconnected(NetworkConnectionToClient conn)
        {
            if (conn is LocalConnectionToClient)
            {
                return;
            }

            Log.Info($"[Session] 玩家 {conn.connectionId} 离开");
            PlayerLeft?.Invoke(conn.connectionId);
        }

        /// <summary>会话结束。非主动离开时触发 SessionEnded 并附带原因。</summary>
        private void EndSession(bool wasHost)
        {
            if (State == SessionState.Offline)
            {
                return;
            }

            bool expected = _shutdownRequested;
            string reason = BuildEndReason(wasHost);
            _shutdownRequested = false;
            SetState(SessionState.Offline);

            if (!expected)
            {
                Log.Warning($"[Session] 会话结束: {reason}");
                SessionEnded?.Invoke(reason);
            }
        }

        private string BuildEndReason(bool wasHost)
        {
            if (wasHost)
            {
                return "主机已关闭";
            }

            if (_clientConnected)
            {
                return "主机已断开连接";
            }

            // 房间已满时 Mirror 服务端直接断开连接，客户端只能看到连接失败
            return string.IsNullOrEmpty(_lastError)
                ? "无法连接到主机（地址错误、主机未启动或房间已满）"
                : $"无法连接到主机: {_lastError}";
        }

        #endregion

        private void SetState(SessionState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(state);
        }
    }
}
