using DevExpress.Xpf.Bars;
using MessagePack;
using NATS.Client.Core;
using NatsROS.Core;
using NatsROS.Dashboard.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.PublisherCaller
{
    public partial class PublisherCallerView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly DispatcherTimer _publisherTimer = new();
        private bool _isPublishing = false;

        public PublisherCallerView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            // ==========================================
            // 【核心修正】：精准过滤契约类型
            // 感谢 Dashboard 的预加载，内存中已经包含了 Core、Messages 乃至 Plugins 里的所有契约。
            // ==========================================
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
                .ToList();


            // 【基因追溯魔法】：构建 RPC 与 Action 家族黑名单！
            var rpcFamilyBlacklist = new System.Collections.Generic.HashSet<Type>();
            var reqTypesInfo = new System.Collections.Generic.List<RosMessageTypeInfo>();

            foreach (var t in allTypes)
            {
                var interfaces = t.GetInterfaces();
                foreach (var i in interfaces)
                {
                    if (i.IsGenericType)
                    {
                        var genericDef = i.GetGenericTypeDefinition();

                        // 如果它是 Request
                        if (genericDef == typeof(NatsROS.Core.IRosRequest<>))
                        {
                            reqTypesInfo.Add(new RosMessageTypeInfo { FullName = t.FullName ?? "", Type = t });

                            rpcFamilyBlacklist.Add(t); // 把它自己拉黑（Req）
                            rpcFamilyBlacklist.Add(i.GetGenericArguments()[0]); // 把它的响应拉黑（Res）
                        }
                        // 如果它是 Action Goal
                        else if (genericDef == typeof(NatsROS.Core.IRosActionGoal<,>))
                        {
                            reqTypesInfo.Add(new RosMessageTypeInfo { FullName = t.FullName ?? "", Type = t });

                            rpcFamilyBlacklist.Add(t); // 把自己拉黑 (Goal)
                            rpcFamilyBlacklist.Add(i.GetGenericArguments()[0]); // 把反馈拉黑 (Feedback)
                            rpcFamilyBlacklist.Add(i.GetGenericArguments()[1]); // 把结果拉黑 (Result)
                        }
                    }
                }
            }

            // 1. 扫描纯粹的 Publish 消息
            var pubTypes = allTypes
                .Where(t => typeof(NatsROS.Core.IRosMessage).IsAssignableFrom(t))
                .Where(t => !rpcFamilyBlacklist.Contains(t)) // 【核心拦截】：如果是 RPC 家族的任何一员，统统滚蛋！
                .Where(t => !t.Name.StartsWith("Action") && !t.Name.EndsWith("Req") && !t.Name.EndsWith("Res")) // 【兜底拦截】：干掉底层系统级的隐患消息
                .Select(t => new RosMessageTypeInfo { FullName = t.FullName ?? "", Type = t })
                .OrderBy(t => t.FullName).ToList();

            CboPubType.ItemsSource = pubTypes;

            // 2. 扫描 RPC 请求和 Action 目标
            CboSrvReqType.ItemsSource = reqTypesInfo.OrderBy(t => t.FullName).ToList();

            _publisherTimer.Tick += PublisherTimer_Tick;
        }

        private void CboPubType_SelectedIndexChanged(object sender, RoutedEventArgs e)
        {
            if (CboPubType.SelectedItem is RosMessageTypeInfo typeInfo && typeInfo.Type != null)
                PropGridPublisher.SelectedObject = CreateDefaultInstance(typeInfo.Type);
        }

        private void CboSrvReqType_SelectedIndexChanged(object sender, RoutedEventArgs e)
        {
            if (CboSrvReqType.SelectedItem is RosMessageTypeInfo typeInfo && typeInfo.Type != null)
            {
                PropGridSrvReq.SelectedObject = CreateDefaultInstance(typeInfo.Type);
                PropGridSrvRes.SelectedObject = null;
            }
        }

        private object? CreateDefaultInstance(Type type)
        {
            // 1. 如果是字符串，直接返回空字符串
            if (type == typeof(string)) return "";

            // 2. 如果是值类型(struct) 或者 有明确的无参构造函数，直接安全创建！
            if (type.IsValueType || type.GetConstructor(Type.EmptyTypes) != null)
            {
                return Activator.CreateInstance(type);
            }

            // 3. 如果没有无参构造 (比如 Record 类型)，找到参数最多的那个构造函数来捏造数据
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor != null)
            {
                var parameters = ctor.GetParameters();
                var defaultArgs = new object?[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var pt = parameters[i].ParameterType;
                    // 递归调用，帮子属性也把坑填上
                    defaultArgs[i] = CreateDefaultInstance(pt);
                }

                try
                {
                    return ctor.Invoke(defaultArgs);
                }
                catch { }
            }

            // 4. 终极兜底：强行在内存中划一块地皮，跳过任何构造函数 (对付极其难搞的类)
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        }

        // ==========================================
        // 按钮事件（已修改为 ItemClickEventArgs）
        // ==========================================
        private async void BtnPubOnce_Click(object sender, ItemClickEventArgs e) => await PublishCurrentPayloadAsync();

        private void BtnPubStart_Click(object sender, ItemClickEventArgs e)
        {
            if (!_isPublishing)
            {
                _publisherTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / decimal.ToDouble((decimal)SpinPubHz.Value));
                _publisherTimer.Start();
                BtnPubStart.Content = "⏹️ 停止发布 (Stop)";
                BtnPubStart.Foreground = new SolidColorBrush(Colors.DarkRed);
                _isPublishing = true;
            }
            else
            {
                _publisherTimer.Stop();
                BtnPubStart.Content = "▶️ 循环发送 (Pub Loop)";
                BtnPubStart.Foreground = new SolidColorBrush(Colors.DarkGreen);
                _isPublishing = false;
            }
        }

        private async void PublisherTimer_Tick(object? sender, EventArgs e) => await PublishCurrentPayloadAsync();

        private async Task PublishCurrentPayloadAsync()
        {
            if (_nats == null || PropGridPublisher.SelectedObject == null || string.IsNullOrWhiteSpace(TxtPubTopic.Text)) return;
            try
            {
                var type = PropGridPublisher.SelectedObject.GetType();
                var bytes = MessagePackSerializer.Serialize(type, PropGridPublisher.SelectedObject);
                var headers = new NATS.Client.Core.NatsHeaders { { "ros-type", $"{type.FullName}, {type.Assembly.GetName().Name}" } };
                var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                await _nats.PublishAsync(TxtPubTopic.Text, data: bytes, headers: headers, serializer: rawSerializer);
            }
            catch (Exception ex)
            {
                _publisherTimer.Stop();
                _isPublishing = false;
                BtnPubStart.Content = "▶️ 循环发送 (Pub Loop)";
                MessageBox.Show($"发布失败: {ex.Message}");
            }
        }

        private async void BtnCallService_Click(object sender, ItemClickEventArgs e)
        {
            var reqObj = PropGridSrvReq.SelectedObject;
            var reqTypeInfo = CboSrvReqType.SelectedItem as RosMessageTypeInfo;
            var srvName = TxtSrvName.Text?.Trim();

            if (reqObj == null || reqTypeInfo?.Type == null || string.IsNullOrWhiteSpace(srvName)) return;

            try
            {
                BtnCallService.IsEnabled = false;
                BtnCallService.Content = "⏳ 跨网调用中...";
                PropGridSrvRes.SelectedObject = null;

                Type actualReqType = reqTypeInfo.Type;
                Type actualResType = typeof(object);
                object actualPayload = reqObj;

                var interfaceTypes = reqTypeInfo.Type.GetInterfaces();

                // 情况 A: 判断是否为 Action Goal (长任务)
                var actionInterface = interfaceTypes.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(NatsROS.Core.IRosActionGoal<,>));

                if (actionInterface != null)
                {
                    var tFeedback = actionInterface.GetGenericArguments()[0];
                    var tResult = actionInterface.GetGenericArguments()[1];

                    actualReqType = typeof(NatsROS.Core.Communication.ActionGoal<,>).MakeGenericType(reqTypeInfo.Type, tResult);
                    actualResType = typeof(NatsROS.Core.Communication.ActionResult<>).MakeGenericType(tResult);

                    actualPayload = Activator.CreateInstance(actualReqType, Guid.NewGuid().ToString(), reqObj)!;

                    if (!srvName.EndsWith(".goal")) srvName += ".goal";
                }
                else
                {
                    // 情况 B: 普通 RPC
                    var reqInterface = interfaceTypes.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(NatsROS.Core.IRosRequest<>));
                    if (reqInterface != null)
                    {
                        actualResType = reqInterface.GetGenericArguments()[0];
                    }
                    else
                    {
                        throw new Exception("该类型没有实现 IRosRequest 或 IRosActionGoal 接口，无法发起调用！");
                    }
                }

                var reqBytes = MessagePackSerializer.Serialize(actualReqType, actualPayload);
                var headers = new NATS.Client.Core.NatsHeaders { { "ros-type", $"{actualReqType.FullName}, {actualReqType.Assembly.GetName().Name}" } };

                var rawSer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                var rawDeser = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>();

                int timeoutSec = decimal.ToInt32((decimal)SpinSrvTimeout.Value);
                TimeSpan timeout = timeoutSec == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(timeoutSec);

                var reply = await _nats!.RequestAsync<byte[], byte[]>(
                    subject: srvName,
                    data: reqBytes,
                    headers: headers,
                    requestSerializer: rawSer,
                    replySerializer: rawDeser,
                    replyOpts: new NatsSubOpts { Timeout = timeout });

                if (reply.Data != null)
                {
                    var resObj = MessagePackSerializer.Deserialize(actualResType, reply.Data);
                    PropGridSrvRes.SelectedObject = resObj;
                }
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show($"调用超时 ({SpinSrvTimeout.Value}秒)！请检查路由 [{srvName}] 是否正确，或底层节点是否卡死。", "超时警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"RPC 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnCallService.IsEnabled = true;
                BtnCallService.Content = "🚀 发起调用 (Call Service)";
            }
        }

        private async void BtnCancelAction_Click(object sender, ItemClickEventArgs e)
        {
            var srvName = TxtSrvName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(srvName)) return;

            try
            {
                string actionName = srvName.EndsWith(".goal") ? srvName.Substring(0, srvName.Length - 5) : srvName;

                var cancelReq = new NatsROS.Core.Communication.ActionCancelReq("*");
                var cancelBytes = MessagePackSerializer.Serialize(cancelReq);
                var headers = new NATS.Client.Core.NatsHeaders { { "ros-type", "NatsROS.Core.Communication.ActionCancelReq, NatsROS.Core" } };
                var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();

                await _nats!.PublishAsync($"{actionName}.cancel", data: cancelBytes, headers: headers, serializer: rawSerializer);

                MessageBox.Show($"已向 [{actionName}] 发送全局强行打断指令！\n如果底层节点处于运行状态，它将立即被熔断！", "急停已发送", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送打断指令失败: {ex.Message}", "错误");
            }
        }

        public void Dispose() => _publisherTimer.Stop();
    }
}