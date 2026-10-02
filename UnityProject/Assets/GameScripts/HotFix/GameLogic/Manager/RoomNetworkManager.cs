using System;
using Mirror;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Mirror NetworkManager 子类：只把 Mirror 的虚函数回调转成事件，并按房间规则生成玩家。
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomNetworkManager : NetworkManager
    {
        /// <summary>
        /// 服务端：是否允许为该连接生成玩家。
        /// </summary>
        public delegate bool AddPlayerFilter(NetworkConnectionToClient conn);

        /// <summary>
        /// 服务端：玩家出生位置和朝向。
        /// </summary>
        public delegate void SpawnPoseResolver(NetworkConnectionToClient conn, out Vector3 position, out Quaternion rotation);

        /// <summary>
        /// 客户端传输层错误。
        /// </summary>
        public delegate void ClientErrorHandler(TransportError error, string reason);

        public event Action HostStopped;

        /// <summary>
        /// 客户端启动（含 Host 的本地客户端），用于注册客户端消息。NetworkClient 每次关闭都会清空消息处理。
        /// </summary>
        public event Action ClientStarted;

        /// <summary>
        /// 客户端（含 Host 的本地客户端）认证通过。
        /// </summary>
        public event Action ClientConnected;

        /// <summary>
        /// 客户端断开或连接失败。
        /// </summary>
        public event Action ClientDisconnected;

        public event ClientErrorHandler ClientErrored;

        /// <summary>
        /// 服务端：连接认证通过。
        /// </summary>
        public event Action<NetworkConnectionToClient> ServerConnected;

        /// <summary>
        /// 服务端：连接断开（含未通过认证的连接）。
        /// </summary>
        public event Action<NetworkConnectionToClient> ServerDisconnected;

        /// <summary>
        /// 服务端：已为该连接生成玩家对象（含中途加入）。
        /// </summary>
        public event Action<NetworkConnectionToClient> ServerPlayerSpawned;

        /// <summary>
        /// 程序退出，Mirror 随后会停止所有连接。
        /// </summary>
        public event Action ApplicationQuitting;

        public AddPlayerFilter CanAddPlayer { get; set; }

        public SpawnPoseResolver SpawnPose { get; set; }

        public override void OnStopHost()
        {
            base.OnStopHost();
            HostStopped?.Invoke();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            ClientStarted?.Invoke();
        }

        public override void OnClientConnect()
        {
            // 不调用 base：base 会立即 Ready + AddPlayer，这里要等进入游戏场景后再做
            ClientConnected?.Invoke();
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            ClientDisconnected?.Invoke();
        }

        public override void OnClientError(TransportError error, string reason)
        {
            base.OnClientError(error, reason);
            ClientErrored?.Invoke(error, reason);
        }

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            ServerConnected?.Invoke(conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            // 先通知再调用 base：base 会销毁该连接的玩家对象
            ServerDisconnected?.Invoke(conn);
            base.OnServerDisconnect(conn);
        }

        public override void OnServerReady(NetworkConnectionToClient conn)
        {
            // 中途加入时，客户端加载完场景才会 Ready。这里直接生成玩家，
            // 避免只标记就绪却没有本地角色，客户端一直停在场景相机上。
            if (conn.identity == null)
            {
                TrySpawnPlayer(conn);
            }

            if (!conn.isReady)
            {
                base.OnServerReady(conn);
            }
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            if (conn.identity != null)
            {
                // 场景切换可能把客户端上的玩家销毁了，服务器上还在。把出生包再发一次。
                ResendPlayerSpawn(conn);
                return;
            }

            TrySpawnPlayer(conn);
        }

        private bool TrySpawnPlayer(NetworkConnectionToClient conn)
        {
            if (conn == null || conn.identity != null)
            {
                return conn != null && conn.identity != null;
            }

            if (playerPrefab == null)
            {
                Debug.LogError("[RoomNetworkManager] 未设置玩家预制体");
                return false;
            }

            if (CanAddPlayer != null && !CanAddPlayer(conn))
            {
                Debug.LogWarning($"[RoomNetworkManager] 连接 {conn.connectionId} 当前不能生成玩家（游戏未开始或不在房间中）");
                return false;
            }

            Vector3 position = Vector3.up;
            Quaternion rotation = Quaternion.identity;
            if (SpawnPose != null)
            {
                SpawnPose(conn, out position, out rotation);
            }

            GameObject player = Instantiate(playerPrefab, position, rotation);
            player.name = $"{playerPrefab.name} [connId={conn.connectionId}]";
            if (!NetworkServer.AddPlayerForConnection(conn, player))
            {
                Destroy(player);
                return false;
            }

            ServerPlayerSpawned?.Invoke(conn);
            return true;
        }

        /// <summary>
        /// 客户端没拿到本地玩家时，把已有玩家再同步一次，而不是再生成一个。
        /// </summary>
        private static void ResendPlayerSpawn(NetworkConnectionToClient conn)
        {
            NetworkIdentity identity = conn.identity;
            if (identity == null)
            {
                return;
            }

            identity.observers.Remove(conn.connectionId);
            conn.observing.Remove(identity);
            NetworkServer.RebuildObservers(identity, true);
            Debug.Log($"[RoomNetworkManager] 已向连接 {conn.connectionId} 重发玩家 {identity.name}");
        }

        public override void OnApplicationQuit()
        {
            ApplicationQuitting?.Invoke();
            base.OnApplicationQuit();
        }
    }
}
