namespace NatsROS.Core.Motion
{
    /// <summary>
    /// 标准在线示教接口
    /// 任何需要支持“一键提取物理坐标”的数据实体都应实现此接口
    /// </summary>
    public interface ITeachable
    {
        /// <summary>
        /// 触发示教动作
        /// </summary>
        /// <param name="machineCoords">传入的当前真实机械坐标数组 (通常是 [X, Y, Z, 可能是R轴或更多])</param>
        void Teach(params double[] machineCoords);
    }
}