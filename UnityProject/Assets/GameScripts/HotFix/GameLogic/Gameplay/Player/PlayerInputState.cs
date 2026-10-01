using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 客户端每个物理步上传给主机的输入。只包含操作意图，不包含位置，
    /// 位置完全由主机根据输入模拟得出（主机权威）。
    /// 字段均为 Mirror 内置支持的类型，Weaver 会自动生成读写器。
    /// </summary>
    public struct PlayerInputState
    {
        /// <summary>递增序号，主机据此丢弃乱序到达的旧输入（输入走不可靠通道）。</summary>
        public uint Sequence;

        /// <summary>移动方向，x = 左右，y = 前后，长度不超过 1。</summary>
        public Vector2 Move;

        /// <summary>本地视角的水平朝向（度）。</summary>
        public float Yaw;

        public bool Sprint;
    }
}
