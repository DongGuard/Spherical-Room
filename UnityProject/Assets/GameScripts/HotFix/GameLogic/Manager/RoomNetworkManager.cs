using System;
using Mirror;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Mirror NetworkManager 子类：只把 Mirror 的虚函数回调转成事件，并按出生点生成玩家。
    /// 会话状态、断线提示等逻辑统一放在 SessionManager，这里不写玩法。
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomNetworkManager : NetworkManager
    {
        public event Action HostStarted;
        public event Action HostStopped;

        /// <summary>客户端（含 Host 的本地客户端）连接成功。</summary>
        public event Action ClientConnected;

        /// <summary>客户端断开或连接失败。</summary>
        public event Action ClientDisconnected;

        /// <summary>客户端传输层错误，参数为错误描述。</summary>
        public event Action<string> ClientErrored;

        /// <summary>仅服务端：有连接接入 / 断开。</summary>
        public event Action<NetworkConnectionToClient> ServerConnected;
        public event Action<NetworkConnectionToClient> ServerDisconnected;

        /// <summary>仅服务端：新玩家出生位置，未设置时出生在原点上方。</summary>
        public Func<Vector3> SpawnPositionProvider { get; set; }

        /// <summary>仅服务端：新玩家出生朝向，未设置时朝向世界 +Z。</summary>
        public Func<Quaternion> SpawnRotationProvider { get; set; }

        public override void OnStartHost()
        {
            base.OnStartHost();
            HostStarted?.Invoke();
        }

        public override void OnStopHost()
        {
            base.OnStopHost();
            HostStopped?.Invoke();
        }

        public override void OnClientConnect()
        {
            // base 负责 Ready + AddPlayer
            base.OnClientConnect();
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
            ClientErrored?.Invoke($"{error}: {reason}");
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

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            if (conn.identity != null)
            {
                Debug.LogWarning($"[RoomNetworkManager] 连接 {conn.connectionId} 已有玩家对象");
                return;
            }

            Vector3 position = SpawnPositionProvider?.Invoke() ?? Vector3.up;
            Quaternion rotation = SpawnRotationProvider?.Invoke() ?? Quaternion.identity;
            GameObject player = Instantiate(playerPrefab, position, rotation);
            player.name = $"{playerPrefab.name} [connId={conn.connectionId}]";
            NetworkServer.AddPlayerForConnection(conn, player);
        }
    }
}
