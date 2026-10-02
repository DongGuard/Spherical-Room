using System.Collections.Generic;
using System.Text;
using Mirror;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 房间阶段。
    /// </summary>
    public enum RoomPhase
    {
        None = 0,

        /// <summary>
        /// 等待中：在 RoomUI 里等人。
        /// </summary>
        Waiting = 1,

        /// <summary>
        /// 开局中：房主正在加载游戏场景。
        /// </summary>
        Starting = 2,

        /// <summary>
        /// 游戏中。
        /// </summary>
        Playing = 3
    }

    /// <summary>
    /// 房间来源。
    /// </summary>
    public enum RoomSource
    {
        /// <summary>
        /// Steam 公开大厅，全球可见，经 Steam 中继连接。
        /// </summary>
        Steam,

        /// <summary>
        /// 局域网 / 本机（KCP 直连），只在开发版或 Steam 不可用时提供。
        /// </summary>
        Lan
    }

    /// <summary>
    /// 房间协议常量和通用工具。
    /// </summary>
    public static class RoomProtocol
    {
        /// <summary>
        /// 联机协议版本：消息或流程有不兼容改动时 +1，版本不同的客户端会被主机拒绝。
        /// </summary>
        public const int Version = 1;

        public const int MaxPlayers = 4;
        public const int MaxNameLength = 16;
        public const int MaxPasswordLength = 16;

        public static string GetPhaseText(RoomPhase phase)
        {
            switch (phase)
            {
                case RoomPhase.Waiting:
                    return "等待中";
                case RoomPhase.Starting:
                    return "即将开始";
                case RoomPhase.Playing:
                    return "游戏中";
                default:
                    return "未知";
            }
        }

        /// <summary>
        /// 去掉首尾空白和控制字符，并限制长度（房间名、昵称、密码共用）。
        /// </summary>
        public static string Sanitize(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (char c in value.Trim())
            {
                if (!char.IsControl(c))
                {
                    builder.Append(c);
                }
            }

            string result = builder.ToString();
            return result.Length > maxLength ? result.Substring(0, maxLength) : result;
        }
    }

    #region 网络消息

    /// <summary>
    /// 客户端 → 主机：加入请求。连接建立后、认证通过前发送，主机据此校验版本、密码、人数和阶段。
    /// </summary>
    public struct RoomJoinRequestMessage : NetworkMessage
    {
        public int Version;
        public string Password;
        public string PlayerName;
        public ulong SteamId;

        /// <summary>是否来自 Steam 邀请/好友加入（免密码，房间列表加入仍需密码）。</summary>
        public bool FromInvite;
    }

    /// <summary>
    /// 主机 → 客户端：加入结果。拒绝时附带原因，主机随后断开连接。
    /// </summary>
    public struct RoomJoinResponseMessage : NetworkMessage
    {
        public bool Accepted;
        public string Reason;
    }

    /// <summary>
    /// 主机 → 客户端：房主即将解散房间。客户端收到后立刻弹出提示盖住画面，
    /// </summary>
    public struct HostClosedMessage : NetworkMessage
    {
    }

    /// <summary>
    /// 客户端 → 主机：已收到解散通知且提示完全显示，主机可以开始拆连接。
    /// </summary>
    public struct HostClosedAckMessage : NetworkMessage
    {
    }

    /// <summary>
    /// 房间成员（同步用）。
    /// </summary>
    public struct RoomMemberData
    {
        /// <summary>
        /// RoomUI 中的位置，房主固定为 0。
        /// </summary>
        public int Slot;
        public string Name;
        public ulong SteamId;
        public bool IsHost;
    }

    /// <summary>
    /// 主机 → 客户端：房间完整状态，成员、设置或阶段变化时整包下发。
    /// </summary>
    public struct RoomStateMessage : NetworkMessage
    {
        public string RoomName;
        public bool HasPassword;
        public string Password;
        public bool AllowMidJoin;
        public int MaxPlayers;
        public RoomPhase Phase;
        public RoomMemberData[] Members;

        /// <summary>
        /// 接收方自己的位置，-1 表示未知。
        /// </summary>
        public int YourSlot;
    }

    /// <summary>
    /// 主机 → 客户端：共享球的当前速度，玩家对象生成后补发一次。
    /// </summary>
    public struct BallVelocityMessage : NetworkMessage
    {
        /// <summary>
        /// 球的 NetworkIdentity netId，客户端据此定位刚体。
        /// </summary>
        public uint BallNetId;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
    }

    #endregion

    #region 本地数据

    /// <summary>
    /// 创建房间时的设置。
    /// </summary>
    public sealed class RoomSettings
    {
        public string Name;
        public bool HasPassword;
        public string Password;
        public bool AllowMidJoin = true;
        public int MaxPlayers = RoomProtocol.MaxPlayers;

        public RoomSettings Clone()
        {
            return (RoomSettings)MemberwiseClone();
        }
    }

    /// <summary>
    /// 房间成员（本地展示用）。
    /// </summary>
    public sealed class RoomMember
    {
        public int Slot;
        public string Name;
        public ulong SteamId;
        public bool IsHost;

        /// <summary>
        /// 是否是本机玩家。
        /// </summary>
        public bool IsLocal;
    }

    /// <summary>
    /// 当前所在房间的信息。房主和成员看到的内容一致，RoomUI 只读这里。
    /// </summary>
    public sealed class RoomInfo
    {
        public string Name;
        public bool HasPassword;
        public string Password;
        public bool AllowMidJoin;
        public int MaxPlayers;
        public RoomPhase Phase;

        /// <summary>
        /// 本机是否是房主。
        /// </summary>
        public bool IsLocalHost;

        /// <summary>
        /// 本机玩家所在位置，-1 表示未知。
        /// </summary>
        public int LocalSlot = -1;

        public readonly List<RoomMember> Members = new List<RoomMember>();

        public RoomMember GetMember(int slot)
        {
            for (int i = 0; i < Members.Count; i++)
            {
                if (Members[i].Slot == slot)
                {
                    return Members[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 房间列表中的一项（Steam 大厅或局域网主机）。
    /// </summary>
    public sealed class RoomListEntry
    {
        public RoomSource Source;

        /// <summary>
        /// 主机每次开房生成的唯一 ID，用于合并同一房间的 Steam / 局域网两条记录。
        /// </summary>
        public string RoomId;

        public string Name;
        public bool HasPassword;
        public bool AllowMidJoin;
        public RoomPhase Phase;
        public int Members;
        public int MaxPlayers;
        public int Version;

        /// <summary>
        /// Steam：大厅 ID。
        /// </summary>
        public ulong SteamLobbyId;

        /// <summary>
        /// Steam：房主 SteamID。
        /// </summary>
        public ulong HostSteamId;

        /// <summary>
        /// 局域网：主机地址。
        /// </summary>
        public string Address;

        /// <summary>
        /// 局域网：主机 KCP 端口。
        /// </summary>
        public ushort Port;

        public string SourceText => Source == RoomSource.Steam ? "Steam" : "局域网";

        /// <summary>
        /// 按列表里的信息判断能否加入（最终以主机认证结果为准）。
        /// </summary>
        public bool CanJoin(out string reason)
        {
            if (Version != RoomProtocol.Version)
            {
                reason = "游戏版本不一致，无法加入";
                return false;
            }

            if (MaxPlayers > 0 && Members >= MaxPlayers)
            {
                reason = "房间已满";
                return false;
            }

            if (Phase != RoomPhase.Waiting && !AllowMidJoin)
            {
                reason = "游戏已开始，该房间不允许中途加入";
                return false;
            }

            reason = null;
            return true;
        }
    }

    /// <summary>
    /// 房间操作结果。
    /// </summary>
    public sealed class RoomOpResult
    {
        public bool Success { get; private set; }

        /// <summary>
        /// 失败原因，可直接展示。
        /// </summary>
        public string Message { get; private set; }

        /// <summary>
        /// 用户主动取消（如关闭密码输入框），不需要提示。
        /// </summary>
        public bool IsCancelled { get; private set; }

        public static RoomOpResult Ok()
        {
            return new RoomOpResult { Success = true };
        }

        public static RoomOpResult Fail(string message)
        {
            return new RoomOpResult { Success = false, Message = message };
        }

        public static RoomOpResult Cancelled()
        {
            return new RoomOpResult { Success = false, IsCancelled = true };
        }
    }

    #endregion
}
