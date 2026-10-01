using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 局域网房间发现（UDP 广播）。只在开发版或 Steam 不可用时使用，正式联机走 Steam 大厅。
    /// <para>
    /// 主机监听 <see cref="DiscoveryPort"/> 回应查询；搜索方向广播地址和本机回环地址发查询并收集回应，
    /// 所以同一台电脑上的编辑器和打包程序也能互相看到。
    /// </para>
    /// 不开线程：由 RoomManager 每帧调用 <see cref="Poll"/> 在主线程非阻塞收发。
    /// </summary>
    public sealed class LanRoomDiscovery : IDisposable
    {
        public const int DiscoveryPort = 47777;

        private const string QueryMessage = "SR_QUERY";
        private const string RoomMessage = "SR_ROOM";
        private const char Separator = '|';
        private const int FieldCount = 10;

        /// <summary>搜索期间重发查询的间隔（UDP 可能丢包）。</summary>
        private const float QueryInterval = 0.5f;

        /// <summary>
        /// SIO_UDP_CONNRESET：Windows 上向没有监听的端口发 UDP 会收到 ICMP 端口不可达，
        /// 之后 Receive 会抛 10054 异常，关闭这个行为。
        /// </summary>
        private const int SioUdpConnReset = -1744830452;

        /// <summary>主机：生成当前房间的广播信息，返回 null 表示当前不对外公开（满员、不可加入等）。</summary>
        public delegate RoomListEntry AdvertisementProvider();

        private UdpClient _server;
        private AdvertisementProvider _provider;

        private UdpClient _client;
        private float _searchEnd;
        private float _nextQuery;

        private readonly List<RoomListEntry> _results = new List<RoomListEntry>();
        private readonly byte[] _queryBytes = Encoding.UTF8.GetBytes(QueryMessage);

        /// <summary>搜索结果变化（发现新房间、搜索开始或结束）。</summary>
        public event Action ResultsChanged;

        public bool IsAdvertising => _server != null;

        public bool IsSearching => _client != null;

        /// <summary>最近一次搜索到的房间。</summary>
        public IReadOnlyList<RoomListEntry> Results => _results;

        #region 主机

        public bool StartAdvertising(AdvertisementProvider provider)
        {
            StopAdvertising();
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                // 同一台电脑多开主机时共用发现端口
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                DisableConnReset(udp);
                _server = udp;
                _provider = provider;
                return true;
            }
            catch (Exception e)
            {
                udp?.Close();
                Debug.LogWarning($"[LAN] 无法监听房间发现端口 {DiscoveryPort}，局域网内其他玩家看不到这个房间: {e.Message}");
                return false;
            }
        }

        public void StopAdvertising()
        {
            if (_server != null)
            {
                _server.Close();
                _server = null;
            }

            _provider = null;
        }

        #endregion

        #region 搜索

        /// <summary>开始搜索，持续 <paramref name="duration"/> 秒，结果通过 <see cref="ResultsChanged"/> 通知。</summary>
        public void StartSearch(float duration)
        {
            if (_client != null)
            {
                _searchEnd = Time.unscaledTime + duration;
                return;
            }

            CloseClient();
            _results.Clear();
            try
            {
                var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
                DisableConnReset(udp);
                _client = udp;
                _searchEnd = Time.unscaledTime + duration;
                _nextQuery = 0f;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LAN] 无法搜索局域网房间: {e.Message}");
            }

            ResultsChanged?.Invoke();
        }

        public void StopSearch()
        {
            if (_client == null)
            {
                return;
            }

            CloseClient();
            ResultsChanged?.Invoke();
        }

        #endregion

        /// <summary>每帧调用：主机回应查询，搜索方收集回应。</summary>
        public void Poll()
        {
            if (_server != null)
            {
                PollServer();
            }

            if (_client != null)
            {
                PollClient();
            }
        }

        public void Dispose()
        {
            StopAdvertising();
            CloseClient();
            ResultsChanged = null;
        }

        private void PollServer()
        {
            try
            {
                while (_server != null && _server.Available > 0)
                {
                    IPEndPoint remote = null;
                    byte[] data = _server.Receive(ref remote);
                    if (!IsQuery(data))
                    {
                        continue;
                    }

                    RoomListEntry entry = _provider?.Invoke();
                    if (entry == null)
                    {
                        continue;
                    }

                    byte[] reply = Encoding.UTF8.GetBytes(Format(entry));
                    _server.Send(reply, reply.Length, remote);
                }
            }
            catch (SocketException)
            {
                // 单个包出错不影响主机，下一帧继续
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void PollClient()
        {
            float now = Time.unscaledTime;
            if (now >= _searchEnd)
            {
                StopSearch();
                return;
            }

            if (now >= _nextQuery)
            {
                _nextQuery = now + QueryInterval;
                SendQuery(IPAddress.Broadcast);
                SendQuery(IPAddress.Loopback);
            }

            bool changed = false;
            try
            {
                while (_client != null && _client.Available > 0)
                {
                    IPEndPoint remote = null;
                    byte[] data = _client.Receive(ref remote);
                    RoomListEntry entry = Parse(data, remote);
                    if (entry != null && AddOrUpdate(entry))
                    {
                        changed = true;
                    }
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            if (changed)
            {
                ResultsChanged?.Invoke();
            }
        }

        private void SendQuery(IPAddress address)
        {
            try
            {
                _client.Send(_queryBytes, _queryBytes.Length, new IPEndPoint(address, DiscoveryPort));
            }
            catch (Exception)
            {
                // 没有网卡、广播被禁止等，忽略
            }
        }

        /// <summary>同一房间可能经广播和回环各回应一次，按 RoomId 合并（保留先收到的地址）。</summary>
        private bool AddOrUpdate(RoomListEntry entry)
        {
            for (int i = 0; i < _results.Count; i++)
            {
                RoomListEntry existing = _results[i];
                if (existing.RoomId != entry.RoomId)
                {
                    continue;
                }

                bool changed = existing.Members != entry.Members || existing.Phase != entry.Phase ||
                               existing.AllowMidJoin != entry.AllowMidJoin || existing.Name != entry.Name;
                entry.Address = existing.Address;
                entry.Port = existing.Port;
                _results[i] = entry;
                return changed;
            }

            _results.Add(entry);
            return true;
        }

        private void CloseClient()
        {
            if (_client != null)
            {
                _client.Close();
                _client = null;
            }
        }

        private bool IsQuery(byte[] data)
        {
            if (data == null || data.Length != _queryBytes.Length)
            {
                return false;
            }

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != _queryBytes[i])
                {
                    return false;
                }
            }

            return true;
        }

        #region 报文格式

        // SR_ROOM|版本|房间ID|房间名|有密码|可中途加入|阶段|人数|上限|KCP端口（房间名做 URL 编码）
        private static string Format(RoomListEntry entry)
        {
            var builder = new StringBuilder(128);
            builder.Append(RoomMessage).Append(Separator)
                .Append(entry.Version).Append(Separator)
                .Append(entry.RoomId).Append(Separator)
                .Append(Uri.EscapeDataString(entry.Name ?? string.Empty)).Append(Separator)
                .Append(entry.HasPassword ? '1' : '0').Append(Separator)
                .Append(entry.AllowMidJoin ? '1' : '0').Append(Separator)
                .Append((int)entry.Phase).Append(Separator)
                .Append(entry.Members).Append(Separator)
                .Append(entry.MaxPlayers).Append(Separator)
                .Append(entry.Port);
            return builder.ToString();
        }

        private static RoomListEntry Parse(byte[] data, IPEndPoint remote)
        {
            if (data == null || data.Length == 0 || remote == null)
            {
                return null;
            }

            string[] parts;
            try
            {
                parts = Encoding.UTF8.GetString(data).Split(Separator);
            }
            catch (Exception)
            {
                return null;
            }

            if (parts.Length != FieldCount || parts[0] != RoomMessage ||
                !int.TryParse(parts[1], out int version) ||
                string.IsNullOrEmpty(parts[2]) ||
                !int.TryParse(parts[6], out int phase) ||
                !int.TryParse(parts[7], out int members) ||
                !int.TryParse(parts[8], out int maxPlayers) ||
                !ushort.TryParse(parts[9], out ushort port) || port == 0)
            {
                return null;
            }

            string name;
            try
            {
                name = Uri.UnescapeDataString(parts[3]);
            }
            catch (Exception)
            {
                name = parts[3];
            }

            return new RoomListEntry
            {
                Source = RoomSource.Lan,
                Version = version,
                RoomId = parts[2],
                Name = RoomProtocol.Sanitize(name, RoomProtocol.MaxNameLength),
                HasPassword = parts[4] == "1",
                AllowMidJoin = parts[5] == "1",
                Phase = (RoomPhase)phase,
                Members = members,
                MaxPlayers = maxPlayers,
                Address = remote.Address.ToString(),
                Port = port
            };
        }

        #endregion

        private static void DisableConnReset(UdpClient udp)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
            {
                return;
            }

            try
            {
                udp.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
                // 不支持时忽略，Receive 的异常已在轮询中处理
            }
        }
    }
}
