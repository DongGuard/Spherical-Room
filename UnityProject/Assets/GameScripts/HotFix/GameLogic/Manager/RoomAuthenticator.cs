using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 房间加入认证（Mirror NetworkAuthenticator）。
    /// <para>
    /// 客户端连上后先发 <see cref="RoomJoinRequestMessage"/>（版本、密码、昵称），主机校验通过后才算进入房间，
    /// 之后才会触发 OnServerConnect / OnClientConnect。校验规则由 RoomManager 通过 <see cref="Validator"/> 提供。
    /// </para>
    /// 密码只在这里校验，不会写进 Steam 大厅数据或局域网广播。Host 自己的本地连接直接通过。
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomAuthenticator : NetworkAuthenticator
    {
        /// <summary>连接后多久没发加入请求就断开（秒）。</summary>
        public const float RequestTimeout = 15f;

        /// <summary>拒绝后延迟断开（秒），让拒绝原因先送达客户端。</summary>
        public const float RejectDisconnectDelay = 0.5f;

        /// <summary>校验加入请求：返回空表示通过，否则为拒绝原因。</summary>
        public delegate string JoinValidator(NetworkConnectionToClient conn, RoomJoinRequestMessage request);

        private sealed class PendingConnection
        {
            public NetworkConnectionToClient Connection;
            public float Deadline;
            public bool Rejected;
        }

        private readonly List<PendingConnection> _pending = new List<PendingConnection>();
        private readonly List<NetworkConnectionToClient> _toDisconnect = new List<NetworkConnectionToClient>();

        /// <summary>服务端：加入校验。</summary>
        public JoinValidator Validator { get; set; }

        /// <summary>服务端：Host 本地连接使用的加入信息（昵称等）。</summary>
        public RoomJoinRequestMessage HostRequest { get; set; }

        /// <summary>客户端：连接后发送的加入请求。</summary>
        public RoomJoinRequestMessage ClientRequest { get; set; }

        /// <summary>客户端：最近一次被主机拒绝的原因，每次启动客户端时清空。</summary>
        public string LastRejectReason { get; private set; }

        /// <summary>取出认证通过时保存的加入请求。</summary>
        public static bool TryGetRequest(NetworkConnectionToClient conn, out RoomJoinRequestMessage request)
        {
            if (conn != null && conn.authenticationData is RoomJoinRequestMessage data)
            {
                request = data;
                return true;
            }

            request = default;
            return false;
        }

        #region 服务端

        public override void OnStartServer()
        {
            _pending.Clear();
            // 认证前就要能收到，所以 requireAuthentication = false
            NetworkServer.ReplaceHandler<RoomJoinRequestMessage>(OnServerRequest, false);
        }

        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<RoomJoinRequestMessage>();
            _pending.Clear();
        }

        public override void OnServerAuthenticate(NetworkConnectionToClient conn)
        {
            if (conn is LocalConnectionToClient)
            {
                conn.authenticationData = HostRequest;
                ServerAccept(conn);
                return;
            }

            _pending.Add(new PendingConnection
            {
                Connection = conn,
                Deadline = Time.unscaledTime + RequestTimeout
            });
        }

        private void OnServerRequest(NetworkConnectionToClient conn, RoomJoinRequestMessage request)
        {
            PendingConnection pending = FindPending(conn);
            // 已认证、已拒绝或已超时的连接重复发送时忽略
            if (pending == null || pending.Rejected || conn.isAuthenticated)
            {
                return;
            }

            string reason = Validator != null ? Validator(conn, request) : null;
            if (!string.IsNullOrEmpty(reason))
            {
                Reject(pending, reason);
                return;
            }

            _pending.Remove(pending);
            conn.authenticationData = request;
            // 先发结果再标记通过：通过后主机会立即下发房间状态，客户端按顺序收到
            conn.Send(new RoomJoinResponseMessage { Accepted = true });
            ServerAccept(conn);
        }

        private void Reject(PendingConnection pending, string reason)
        {
            Debug.Log($"[RoomAuth] 拒绝连接 {pending.Connection.connectionId}: {reason}");
            pending.Rejected = true;
            pending.Deadline = Time.unscaledTime + RejectDisconnectDelay;
            pending.Connection.Send(new RoomJoinResponseMessage { Accepted = false, Reason = reason });
        }

        private PendingConnection FindPending(NetworkConnectionToClient conn)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Connection == conn)
                {
                    return _pending[i];
                }
            }

            return null;
        }

        /// <summary>连接断开时清理等待记录（由 SessionManager 在 OnServerDisconnect 中调用）。</summary>
        public void ForgetConnection(NetworkConnectionToClient conn)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Connection == conn)
                {
                    _pending.RemoveAt(i);
                }
            }
        }

        private void Update()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            if (!NetworkServer.active)
            {
                _pending.Clear();
                return;
            }

            float now = Time.unscaledTime;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingConnection pending = _pending[i];
                if (now < pending.Deadline)
                {
                    continue;
                }

                if (!pending.Rejected)
                {
                    Reject(pending, "加入请求超时");
                    continue;
                }

                _pending.RemoveAt(i);
                _toDisconnect.Add(pending.Connection);
            }

            // 断开会同步回调 OnServerDisconnect → ForgetConnection，放到遍历之后
            for (int i = 0; i < _toDisconnect.Count; i++)
            {
                ServerReject(_toDisconnect[i]);
            }

            _toDisconnect.Clear();
        }

        #endregion

        #region 客户端

        public override void OnStartClient()
        {
            LastRejectReason = null;
            NetworkClient.ReplaceHandler<RoomJoinResponseMessage>(OnClientResponse, false);
        }

        public override void OnStopClient()
        {
            NetworkClient.UnregisterHandler<RoomJoinResponseMessage>();
        }

        public override void OnClientAuthenticate()
        {
            // Host 的本地客户端：服务端已经直接通过
            if (NetworkClient.activeHost)
            {
                ClientAccept();
                return;
            }

            NetworkClient.Send(ClientRequest);
        }

        private void OnClientResponse(RoomJoinResponseMessage response)
        {
            if (response.Accepted)
            {
                ClientAccept();
                return;
            }

            LastRejectReason = string.IsNullOrEmpty(response.Reason) ? "房主拒绝了加入请求" : response.Reason;
            ClientReject();
        }

        #endregion
    }
}
