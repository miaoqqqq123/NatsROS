using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatsROS.Core;

/// <summary>
/// ROS 消息的基类标记接口。所有需要在 NatsROS 中传输的数据模型都应实现此接口。
/// 建议配合 MessagePackObject 特性使用。
/// </summary>
public interface IRosMessage { }

