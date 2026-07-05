using MessagePack;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core;
using NatsROS.Dashboard.Models;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

            string binPath = AppDomain.CurrentDomain.BaseDirectory;
            var dllFiles = Directory.GetFiles(binPath, "NatsROS.*.dll").Concat(Directory.GetFiles(binPath, "ScrewMachine.*.dll"));
            foreach (var dllPath in dllFiles) { try { Assembly.LoadFrom(dllPath); } catch { } }

            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .Where(t => !t.IsInterface && !t.IsAbstract && !t.IsGenericTypeDefinition).ToList();

            // 1. 扫描普通的 Publish 消息 (排除底层 Action 包装类)
            var pubTypes = allTypes
                .Where(t => typeof(IRosMessage).IsAssignableFrom(t))
                .Where(t => !t.Name.StartsWith("ActionGoal") && !t.Name.StartsWith("ActionResult") && !t.Name.StartsWith("ActionFeedback"))
                .Select(t => new RosMessageTypeInfo { FullName = t.FullName ?? "", Type = t })
                .OrderBy(t => t.FullName).ToList();

            CboPubType.ItemsSource = pubTypes;

            // 2. 【核心魔法】：精准扫描 RPC 请求和 Action 目标！
            var reqTypes = allTypes
                .Where(t => t.GetInterfaces().Any(i => i.IsGenericType &&
                                                       (i.GetGenericTypeDefinition() == typeof(NatsROS.Core.IRosRequest<>) ||
                                                        i.GetGenericTypeDefinition() == typeof(NatsROS.Core.IRosActionGoal<,>))))
                .Select(t => new RosMessageTypeInfo { FullName = t.FullName ?? "", Type = t })
                .OrderBy(t => t.FullName).ToList();

            CboSrvReqType.ItemsSource = reqTypes;
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
            try { return Activator.CreateInstance(type); } catch { }
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor != null)
            {
                var parameters = ctor.GetParameters();
                var defaultArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pt = parameters[i].ParameterType;
                    if (pt == typeof(string)) defaultArgs[i] = "";
                    else if (pt.IsValueType) defaultArgs[i] = Activator.CreateInstance(pt);
                    else defaultArgs[i] = CreateDefaultInstance(pt);
                }
                try { return ctor.Invoke(defaultArgs); } catch { }
            }
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        }

        private async void BtnPubOnce_Click(object sender, RoutedEventArgs e) => await PublishCurrentPayloadAsync();
        private void BtnPubStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPublishing)
            {
                _publisherTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / decimal.ToDouble((decimal)SpinPubHz.Value));
                _publisherTimer.Start(); BtnPubStart.Content = "⏹️ 停止发布"; _isPublishing = true;
            }
            else { _publisherTimer.Stop(); BtnPubStart.Content = "▶️ 循环发送"; _isPublishing = false; }
        }
        private async void PublisherTimer_Tick(object? sender, EventArgs e) => await PublishCurrentPayloadAsync();

        private async Task PublishCurrentPayloadAsync()
        {
            if (_nats == null || PropGridPublisher.SelectedObject == null || string.IsNullOrWhiteSpace(TxtPubTopic.Text)) return;
            try
            {
                var type = PropGridPublisher.SelectedObject.GetType();
                var bytes = MessagePackSerializer.Serialize(type, PropGridPublisher.SelectedObject);
                var headers = new NatsHeaders { { "ros-type", $"{type.FullName}, {type.Assembly.GetName().Name}" } };
                var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                await _nats.PublishAsync(TxtPubTopic.Text, data: bytes, headers: headers, serializer: rawSerializer);
            }
            catch (Exception ex) { _publisherTimer.Stop(); _isPublishing = false; BtnPubStart.Content = "▶️ 循环发送"; MessageBox.Show($"发布失败: {ex.Message}"); }
        }

        private async void BtnCallService_Click(object sender, RoutedEventArgs e)
        {
            var reqObj = PropGridSrvReq.SelectedObject;
            var reqTypeInfo = CboSrvReqType.SelectedItem as RosMessageTypeInfo;
            var srvName = TxtSrvName.Text;

            if (reqObj == null || reqTypeInfo?.Type == null || string.IsNullOrWhiteSpace(srvName)) return;

            try
            {
                BtnCallService.IsEnabled = false;
                BtnCallService.Content = "⏳ 跨网调用中...";
                PropGridSrvRes.SelectedObject = null;

                Type actualReqType = reqTypeInfo.Type;
                Type actualResType = typeof(object);
                object actualPayload = reqObj;

                // ==========================================
                // 【架构魔法】：全自动推导基因并组装外衣！
                // ==========================================
                var interfaceTypes = reqTypeInfo.Type.GetInterfaces();

                // 情况 A: 判断是否为 Action Goal (长任务)
                var actionInterface = interfaceTypes.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(NatsROS.Core.IRosActionGoal<,>));

                if (actionInterface != null)
                {
                    // 从接口基因中提取出 TResult
                    var tFeedback = actionInterface.GetGenericArguments()[0];
                    var tResult = actionInterface.GetGenericArguments()[1];

                    // 动态生成带有包装的 ActionGoal<TGoal, TResult> 和 ActionResult<TResult>
                    actualReqType = typeof(NatsROS.Core.Communication.ActionGoal<,>).MakeGenericType(reqTypeInfo.Type, tResult);
                    actualResType = typeof(NatsROS.Core.Communication.ActionResult<>).MakeGenericType(tResult);

                    // 强行实例化包装类，塞入自动生成的 GoalId 和用户填写的 Data
                    actualPayload = Activator.CreateInstance(actualReqType, Guid.NewGuid().ToString(), reqObj)!;

                    // 智能补全路由：如果用户忘了加 .goal，我们帮他加上
                    if (!srvName.EndsWith(".goal")) srvName += ".goal";
                }
                else
                {
                    // 情况 B: 这是一个普通的 RPC Service Request (瞬间请求)
                    var reqInterface = interfaceTypes.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(NatsROS.Core.IRosRequest<>));
                    if (reqInterface != null)
                    {
                        // 直接从接口提取绑定的 TResponse
                        actualResType = reqInterface.GetGenericArguments()[0];
                    }
                    else
                    {
                        throw new Exception("该类型没有实现 IRosRequest 或 IRosActionGoal 接口，无法发起调用！");
                    }
                }

                // ==========================================
                // 发起底层 NATS 裸二进制请求
                // ==========================================
                var reqBytes = MessagePackSerializer.Serialize(actualReqType, actualPayload);
                var headers = new NatsHeaders { { "ros-type", $"{actualReqType.FullName}, {actualReqType.Assembly.GetName().Name}" } };

                var rawSer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                var rawDeser = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>();

                // 【解析 0 秒 = 无限等待】
                int timeoutSec = decimal.ToInt32((decimal)SpinSrvTimeout.Value);
                TimeSpan timeout = timeoutSec == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(timeoutSec);

                var reply = await _nats!.RequestAsync<byte[], byte[]>(
                    subject: srvName,
                    data: reqBytes,
                    headers: headers,
                    requestSerializer: rawSer,
                    replySerializer: rawDeser,
                    replyOpts: new NatsSubOpts { Timeout = timeout });

                // ==========================================
                // 接收响应并渲染到右侧 UI
                // ==========================================
                if (reply.Data != null)
                {
                    // 使用刚才全自动推导出的 actualResType 进行盲解！
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
                BtnCallService.Content = "🚀 发起调用";
            }
        }

        public void Dispose() => _publisherTimer.Stop();

        private async void BtnCancelAction_Click(object sender, RoutedEventArgs e)
        {
            var srvName = TxtSrvName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(srvName)) return;

            try
            {
                // 智能提取 Action 的基础名字 (剥离可能存在的 .goal 后缀)
                string actionName = srvName.EndsWith(".goal") ? srvName.Substring(0, srvName.Length - 5) : srvName;

                // 构建 GoalId = "*" 的全局急停指令
                var cancelReq = new NatsROS.Core.Communication.ActionCancelReq("*");
                var cancelBytes = MessagePackSerializer.Serialize(cancelReq);
                var headers = new NatsHeaders { { "ros-type", "NatsROS.Core.Communication.ActionCancelReq, NatsROS.Core" } };
                var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();

                // 瞬间向底层节点发射急停网络包
                await _nats!.PublishAsync($"{actionName}.cancel", data: cancelBytes, headers: headers, serializer: rawSerializer);

                MessageBox.Show($"已向 [{actionName}] 发送全局强行打断指令！\n如果底层节点处于运行状态，它将立即被熔断！", "急停已发送", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送打断指令失败: {ex.Message}", "错误");
            }
        }
    }
}
