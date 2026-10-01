using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using kcp2k;
using Mirror;
using UnityEngine;
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

        /// <summary>正在启动主机，或正在连接 / 认证。</summary>
        Starting,

        Host,
        Client
    }

    /// <summary>
    /// 联机会话（Mirror 连接生命周期）：创建 NetworkManager、选择传输层、启动 Host / 加入 / 离开、断线处理。
    /// 只管连接，不含房间规则和玩法；房间流程见 RoomManager。
    /// <para>
    /// 传输层：Steam 可用时主机开放 FizzySteamworks（Steam Networking Sockets，经 Steam 中继，全球可连，不用开端口）；
    /// 编辑器 / Development Build，或 Steam 不可用时，主机额外监听 KCP（UDP 7777 起），用于同一台电脑多开或局域网测试
    /// （同一个 Steam 账号不能连接自己）。两种同时开放时用 MultiplexTransport 合并，客户端只用其中一种。
    /// </para>
    /// <para>
    /// 场景：不使用 Mirror 的 online / offline 场景，进入游戏时由 RoomManager 通过 TEngine 场景模块（YooAsset）加载。
    /// </para>
    /// </summary>
    public class SessionManager : Singleton<SessionManager>
    {
        public const ushort DefaultPort = 7777;

        /// <summary>同一台电脑多开时依次尝试的端口数量。</summary>
        private const int PortSearchCount = 10;

        /// <summary>网络发送频率与物理步长对齐，每次物理模拟对应一次状态下发。</summary>
        public const int TickRate = 60;

        /// <summary>加入时等待连接 + 认证的最长时间，传输层自身也有超时，这里兜底。</summary>
        private const int JoinTimeoutMs = 40000;

        public delegate void StateChangedHandler(SessionState state);

        public delegate void ServerPlayerJoinedHandler(NetworkConnectionToClient conn, RoomJoinRequestMessage request);

        private RoomNetworkManager _manager;
        private RoomAuthenticator _authenticator;
        private KcpTransport _kcp;
        private MultiplexTransport _multiplex;
#if UNITY_STANDALONE_WIN
        private FizzySteamworks _steam;
#endif

        /// <summary>本地正在主动停止网络，期间的断开回调不算异常。</summary>
        private bool _stopping;

        private bool _quitting;
        private string _lastTransportError;
        private int _sessionSerial;
        private UniTaskCompletionSource<RoomOpResult> _joinTcs;

        /// <summary>仅服务端：已通过认证的连接（含 Host 本地连接）。</summary>
        private readonly HashSet<int> _serverMembers = new HashSet<int>();

        public event StateChangedHandler StateChanged;

        /// <summary>已加入房间后会话意外结束（房主离开、网络中断等），参数为可直接展示的原因。主动离开不触发。</summary>
        public event Action<string> SessionEnded;

        /// <summary>客户端启动（含 Host），此时注册客户端消息处理。</summary>
        public event Action ClientStarted;

        /// <summary>仅服务端：玩家通过认证进入房间（含 Host 自己的本地连接）。</summary>
        public event ServerPlayerJoinedHandler ServerPlayerJoined;

        /// <summary>仅服务端：已进入房间的玩家离开。主机自己关闭房间时不触发。</summary>
        public event Action<NetworkConnectionToClient> ServerPlayerLeft;

        public SessionState State { get; private set; } = SessionState.Offline;

        public bool IsHost => State == SessionState.Host;

        public bool IsClient => State == SessionState.Client;

        public bool IsOnline => IsHost || IsClient;

        public RoomNetworkManager Network => _manager;

        public RoomAuthenticator Authenticator => _authenticator;

        /// <summary>仅服务端：当前主机是否开放了 Steam 连接。</summary>
        public bool HostUsesSteam { get; private set; }

        /// <summary>仅服务端：当前主机是否开放了 KCP 直连。</summary>
        public bool HostUsesLan { get; private set; }

        /// <summary>仅服务端：KCP 监听端口，未开放时为 0。</summary>
        public ushort LanPort { get; private set; }

        /// <summary>Steam 是否已启动并登录。</summary>
        public static bool SteamAvailable
        {
            get
            {
#if UNITY_STANDALONE_WIN
                return SteamManager.Initialized;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// 是否提供 KCP / 局域网入口：编辑器、Development Build，或 Steam 不可用时。
        /// 正式版有 Steam 时只走 Steam，避免弹防火墙提示和暴露端口。
        /// </summary>
        public static bool LanEnabled => Application.isEditor || Debug.isDebugBuild || !SteamAvailable;

        protected override void OnInit()
        {
            base.OnInit();
            CreateNetworkManager();
            Application.quitting += OnApplicationQuitting;
        }

        /// <summary>设置玩家预制体（必须带 NetworkIdentity）。必须在启动主机或加入之前设置，且各端一致。</summary>
        public void SetPlayerPrefab(GameObject playerPrefab)
        {
            if (playerPrefab != null && playerPrefab.GetComponent<NetworkIdentity>() == null)
            {
                Log.Error($"[Session] 玩家预制体 {playerPrefab.name} 缺少 NetworkIdentity 组件");
                return;
            }

            _manager.playerPrefab = playerPrefab;
        }

        /// <summary>注册运行时 Spawn 的网络预制体（如共享球）。必须在启动主机或加入之前注册，且各端一致。</summary>
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

        #region 启动 / 加入 / 离开

        /// <summary>
        /// 作为主机启动（主机即服务端）。同步完成，返回时本地房主已经通过认证。
        /// </summary>
        /// <param name="hostRequest">房主自己的成员信息（昵称等）。</param>
        /// <param name="useSteam">是否开放 Steam 连接（需要 Steam 已登录）。</param>
        /// <param name="useLan">是否开放 KCP 直连（开发测试 / 局域网）。</param>
        public RoomOpResult StartHost(RoomJoinRequestMessage hostRequest, bool useSteam, bool useLan)
        {
            if (State != SessionState.Offline || _manager.isNetworkActive)
            {
                return RoomOpResult.Fail("已经在房间中");
            }

#if UNITY_STANDALONE_WIN
            useSteam &= _steam != null && SteamManager.Initialized;
#else
            useSteam = false;
#endif
            ushort port = 0;
            if (useLan && !TryFindFreeUdpPort(DefaultPort, PortSearchCount, out port))
            {
                Log.Warning($"[Session] UDP 端口 {DefaultPort}-{DefaultPort + PortSearchCount - 1} 都被占用，不开放局域网连接");
                useLan = false;
            }

            if (!useSteam && !useLan)
            {
                return RoomOpResult.Fail("没有可用的联机方式：Steam 未启动，且局域网端口被占用");
            }

            Transport transport = SelectHostTransport(useSteam, useLan);
            _kcp.port = useLan ? port : DefaultPort;
            BeginSession();
            _authenticator.HostRequest = hostRequest;
            UseTransport(transport);
            SetState(SessionState.Starting);

            try
            {
                _manager.StartHost();
            }
            catch (Exception e)
            {
                Log.Error($"[Session] StartHost 异常: {e}");
            }

            if (!NetworkServer.active || !transport.ServerActive())
            {
                SetState(SessionState.Offline);
                CleanupFailedHost();
                return RoomOpResult.Fail("创建房间失败：网络服务启动失败（端口被占用或 Steam 网络不可用）");
            }

            HostUsesSteam = useSteam;
            HostUsesLan = useLan;
            LanPort = useLan ? port : (ushort)0;
            SetState(SessionState.Host);
            Log.Info($"[Session] Host 已启动：Steam={(useSteam ? "开" : "关")}，局域网={(useLan ? "UDP " + port : "关")}");
            return RoomOpResult.Ok();
        }

        /// <summary>通过 KCP 连接局域网 / 本机主机，等待连接和认证完成。</summary>
        public UniTask<RoomOpResult> JoinLanAsync(string address, ushort port, RoomJoinRequestMessage request)
        {
            if (string.IsNullOrWhiteSpace(address) || port == 0)
            {
                return UniTask.FromResult(RoomOpResult.Fail("房间地址无效"));
            }

            _kcp.port = port;
            return JoinAsync(_kcp, address.Trim(), request);
        }

#if UNITY_STANDALONE_WIN
        /// <summary>通过 Steam（FizzySteamworks）连接房主，等待连接和认证完成。</summary>
        public UniTask<RoomOpResult> JoinSteamAsync(CSteamID host, RoomJoinRequestMessage request)
        {
            if (_steam == null || !SteamManager.Initialized)
            {
                return UniTask.FromResult(RoomOpResult.Fail("Steam 未启动，无法加入 Steam 房间"));
            }

            // FizzySteamworks 的地址就是房主 SteamID
            return JoinAsync(_steam, host.m_SteamID.ToString(), request);
        }
#endif

        /// <summary>
        /// 主动离开：房主调用会关闭房间（所有成员断开），成员调用只断开自己。主动离开不触发 SessionEnded。
        /// </summary>
        public void Leave()
        {
            bool joining = State == SessionState.Starting && !NetworkServer.active;
            // 先切到 Offline，随后 Mirror 的断开回调都按主动离开处理
            SetState(SessionState.Offline);
            StopNetwork();
            if (joining)
            {
                CompleteJoin(RoomOpResult.Fail("已取消加入"));
            }
        }

        protected override void OnRelease()
        {
            Application.quitting -= OnApplicationQuitting;
            if (_manager == null)
            {
                return;
            }

            UnregisterCallbacks();
            if (!_quitting)
            {
                State = SessionState.Offline;
                StopNetwork();
            }

            _joinTcs = null;
            UnityEngine.Object.Destroy(_manager.gameObject);
            _manager = null;
        }

        private async UniTask<RoomOpResult> JoinAsync(Transport transport, string address, RoomJoinRequestMessage request)
        {
            if (State != SessionState.Offline || _manager.isNetworkActive)
            {
                return RoomOpResult.Fail("已经在房间中");
            }

            BeginSession();
            _authenticator.ClientRequest = request;
            UseTransport(transport);
            _manager.networkAddress = address;
            var tcs = new UniTaskCompletionSource<RoomOpResult>();
            _joinTcs = tcs;
            SetState(SessionState.Starting);

            try
            {
                _manager.StartClient();
            }
            catch (Exception e)
            {
                Log.Error($"[Session] StartClient 异常: {e}");
            }

            // 启动即失败且传输层没有回调断开（例如传输层不可用）
            if (!NetworkClient.active && State == SessionState.Starting && _joinTcs == tcs)
            {
                _joinTcs = null;
                SetState(SessionState.Offline);
                StopNetwork();
                return RoomOpResult.Fail("无法启动网络连接");
            }

            Log.Info($"[Session] 正在连接 {address}（{transport.GetType().Name}）");
            JoinTimeoutAsync(tcs, _sessionSerial).Forget();
            return await tcs.Task;
        }

        private async UniTaskVoid JoinTimeoutAsync(UniTaskCompletionSource<RoomOpResult> tcs, int serial)
        {
            await UniTask.Delay(JoinTimeoutMs, true);
            if (_joinTcs != tcs || serial != _sessionSerial || State != SessionState.Starting)
            {
                return;
            }

            Log.Warning("[Session] 加入超时");
            SetState(SessionState.Offline);
            StopNetwork();
            CompleteJoin(RoomOpResult.Fail("连接超时，请检查网络后重试"));
        }

        private void BeginSession()
        {
            _sessionSerial++;
            _lastTransportError = null;
            _serverMembers.Clear();
            HostUsesSteam = false;
            HostUsesLan = false;
            LanPort = 0;
        }

        private Transport SelectHostTransport(bool useSteam, bool useLan)
        {
#if UNITY_STANDALONE_WIN
            if (useSteam && useLan)
            {
                _multiplex.transports = new Transport[] { _steam, _kcp };
                return _multiplex;
            }

            if (useSteam)
            {
                return _steam;
            }
#endif
            return _kcp;
        }

        /// <summary>
        /// 切换传输层。NetworkManager 只在首次初始化时设置 Transport.active，之后要手动同步。
        /// </summary>
        private void UseTransport(Transport transport)
        {
            _manager.transport = transport;
            Transport.active = transport;
        }

        private void StopNetwork()
        {
            if (_manager == null)
            {
                return;
            }

            if (_manager.isNetworkActive)
            {
                _stopping = true;
                try
                {
                    if (NetworkServer.active)
                    {
                        _manager.StopHost();
                    }
                    else
                    {
                        _manager.StopClient();
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"[Session] 停止网络时出错: {e.Message}");
                }
                finally
                {
                    _stopping = false;
                }
            }

            _serverMembers.Clear();
        }

        private void CleanupFailedHost()
        {
            _stopping = true;
            try
            {
                if (NetworkServer.active || NetworkClient.active)
                {
                    _manager.StopHost();
                }
                else
                {
                    // NetworkServer.Listen 抛异常时 Mirror 还没激活，StopHost 不会清理传输层和认证监听
                    Transport.active?.Shutdown();
                    _authenticator.OnServerAuthenticated.RemoveAllListeners();
                    _authenticator.OnStopServer();
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Session] 清理启动失败的主机时出错: {e.Message}");
            }
            finally
            {
                _stopping = false;
            }

            _serverMembers.Clear();
        }

        /// <summary>找一个空闲的 UDP 端口（同一台电脑开多个主机时 7777 可能已被占用）。</summary>
        private static bool TryFindFreeUdpPort(ushort start, int count, out ushort port)
        {
            for (int i = 0; i < count; i++)
            {
                var candidate = (ushort)(start + i);
                try
                {
                    using (new UdpClient(new IPEndPoint(IPAddress.Any, candidate)))
                    {
                    }

                    port = candidate;
                    return true;
                }
                catch (SocketException)
                {
                    // 被占用，试下一个
                }
            }

            port = 0;
            return false;
        }

        #endregion

        #region NetworkManager 创建与回调

        private void CreateNetworkManager()
        {
            // 先保持未激活，等传输层和参数都配置好再激活，避免 NetworkManager / MultiplexTransport 的 Awake 找不到配置
            var go = new GameObject(nameof(RoomNetworkManager));
            go.SetActive(false);

            _kcp = go.AddComponent<KcpTransport>();
            _kcp.port = DefaultPort;

            var transports = new List<Transport>();
#if UNITY_STANDALONE_WIN
            // FizzySteamworks 启用时就会调用 Steam 接口，Steam 不可用时不添加
            if (SteamManager.Initialized)
            {
                _steam = go.AddComponent<FizzySteamworks>();
                transports.Add(_steam);
            }
#endif
            transports.Add(_kcp);
            _multiplex = go.AddComponent<MultiplexTransport>();
            _multiplex.transports = transports.ToArray();

            _manager = go.AddComponent<RoomNetworkManager>();
            _manager.transport = _kcp;
            // 多留两个连接给正在认证的玩家，满员时由认证返回明确的原因，而不是直接被传输层断开
            _manager.maxConnections = RoomProtocol.MaxPlayers + 2;
            _manager.sendRate = TickRate;
            _manager.dontDestroyOnLoad = true;
            _manager.runInBackground = true;
            // 场景由 RoomManager 通过 YooAsset 加载，不走 Mirror 的场景切换
            _manager.offlineScene = string.Empty;
            _manager.onlineScene = string.Empty;
            // 开局进入游戏场景后才手动 AddPlayer
            _manager.autoCreatePlayer = false;

            // NetworkAuthenticator 要求同物体上已有 NetworkManager
            _authenticator = go.AddComponent<RoomAuthenticator>();
            _manager.authenticator = _authenticator;

            // 物理步长与网络发送频率对齐
            Time.fixedDeltaTime = 1f / TickRate;

            RegisterCallbacks();
            go.SetActive(true);
        }

        private void RegisterCallbacks()
        {
            _manager.HostStopped += OnHostStopped;
            _manager.ClientStarted += OnClientStarted;
            _manager.ClientConnected += OnClientConnected;
            _manager.ClientDisconnected += OnClientDisconnected;
            _manager.ClientErrored += OnClientErrored;
            _manager.ServerConnected += OnServerConnected;
            _manager.ServerDisconnected += OnServerDisconnected;
            _manager.ApplicationQuitting += OnApplicationQuitting;
        }

        private void UnregisterCallbacks()
        {
            _manager.HostStopped -= OnHostStopped;
            _manager.ClientStarted -= OnClientStarted;
            _manager.ClientConnected -= OnClientConnected;
            _manager.ClientDisconnected -= OnClientDisconnected;
            _manager.ClientErrored -= OnClientErrored;
            _manager.ServerConnected -= OnServerConnected;
            _manager.ServerDisconnected -= OnServerDisconnected;
            _manager.ApplicationQuitting -= OnApplicationQuitting;
        }

        private void OnClientStarted()
        {
            ClientStarted?.Invoke();
        }

        private void OnClientConnected()
        {
            // Host 的本地客户端也会触发，Host 状态在 StartHost 中设置
            if (NetworkServer.active || State != SessionState.Starting)
            {
                return;
            }

            SetState(SessionState.Client);
            Log.Info("[Session] 已加入房间");
            CompleteJoin(RoomOpResult.Ok());
        }

        private void OnClientDisconnected()
        {
            // Host 停止时本地客户端也会断开，由 Host 的流程处理；主动离开时状态已经是 Offline
            if (NetworkServer.active || _quitting || _stopping || State == SessionState.Offline)
            {
                return;
            }

            bool joining = State == SessionState.Starting;
            string reason = BuildDisconnectReason(joining);
            SetState(SessionState.Offline);
            _serverMembers.Clear();

            if (joining)
            {
                Log.Warning($"[Session] 加入失败: {reason}");
                CompleteJoin(RoomOpResult.Fail(reason));
                return;
            }

            NotifyEndedAsync(reason, _sessionSerial).Forget();
        }

        private void OnHostStopped()
        {
            // 正常流程中 Host 只会由本地主动停止，这里兜底
            if (_stopping || _quitting || State == SessionState.Offline)
            {
                return;
            }

            Log.Warning("[Session] Host 意外停止");
            SetState(SessionState.Offline);
            _serverMembers.Clear();
            NotifyEndedAsync("房间已关闭", _sessionSerial).Forget();
        }

        private void OnClientErrored(TransportError error, string reason)
        {
            _lastTransportError = DescribeTransportError(error);
            Log.Warning($"[Session] 传输层错误: {error} {reason}");
        }

        private void OnServerConnected(NetworkConnectionToClient conn)
        {
            if (!NetworkServer.active || State == SessionState.Offline)
            {
                return;
            }

            if (!_serverMembers.Add(conn.connectionId))
            {
                return;
            }

            RoomAuthenticator.TryGetRequest(conn, out RoomJoinRequestMessage request);
            Log.Info(conn is LocalConnectionToClient
                ? "[Session] 房主进入房间"
                : $"[Session] 玩家 {conn.connectionId} 进入房间（{conn.address}）");
            ServerPlayerJoined?.Invoke(conn, request);
        }

        private void OnServerDisconnected(NetworkConnectionToClient conn)
        {
            _authenticator.ForgetConnection(conn);
            if (!_serverMembers.Remove(conn.connectionId))
            {
                // 未通过认证的连接
                return;
            }

            if (_stopping || State != SessionState.Host)
            {
                return;
            }

            Log.Info($"[Session] 玩家 {conn.connectionId} 离开房间");
            ServerPlayerLeft?.Invoke(conn);
        }

        private void OnApplicationQuitting()
        {
            _quitting = true;
        }

        #endregion

        #region 结果通知

        private void CompleteJoin(RoomOpResult result)
        {
            UniTaskCompletionSource<RoomOpResult> tcs = _joinTcs;
            _joinTcs = null;
            if (tcs != null)
            {
                CompleteNextFrameAsync(tcs, result).Forget();
            }
        }

        /// <summary>
        /// Mirror 回调里客户端状态还没清理完（NetworkClient.Shutdown 在 OnClientDisconnect 之后执行），
        /// 下一帧再通知，等待方可以安全地重新连接。
        /// </summary>
        private static async UniTaskVoid CompleteNextFrameAsync(UniTaskCompletionSource<RoomOpResult> tcs, RoomOpResult result)
        {
            await UniTask.Yield();
            tcs.TrySetResult(result);
        }

        private async UniTaskVoid NotifyEndedAsync(string reason, int serial)
        {
            await UniTask.Yield();
            if (serial != _sessionSerial || _quitting)
            {
                return;
            }

            Log.Warning($"[Session] 会话结束: {reason}");
            SessionEnded?.Invoke(reason);
        }

        private string BuildDisconnectReason(bool joining)
        {
            string reject = _authenticator.LastRejectReason;
            if (!string.IsNullOrEmpty(reject))
            {
                return reject;
            }

            if (!joining)
            {
                return "与房间的连接已断开（房主离开或网络中断）";
            }

            return string.IsNullOrEmpty(_lastTransportError)
                ? "无法连接到房间（房间已关闭或网络不通）"
                : $"无法连接到房间：{_lastTransportError}";
        }

        private static string DescribeTransportError(TransportError error)
        {
            switch (error)
            {
                case TransportError.DnsResolve:
                    return "无法解析房间地址";
                case TransportError.Refused:
                    return "连接被拒绝";
                case TransportError.Timeout:
                    return "连接超时";
                case TransportError.Congestion:
                    return "网络拥堵";
                case TransportError.ConnectionClosed:
                    return "连接已关闭";
                default:
                    return error.ToString();
            }
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
