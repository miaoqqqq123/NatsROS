using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using MessagePack;
using NATS.Client.Core;
using NatsROS.Dashboard.Models;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Dashboard
{
    public partial class MainWindow
    {
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
            else
            {
                _publisherTimer.Stop(); BtnPubStart.Content = "▶️ 循环发送"; _isPublishing = false;
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
                var headers = new NatsHeaders { { "ros-type", $"{type.FullName}, {type.Assembly.GetName().Name}" } };
                var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                await _nats.PublishAsync(TxtPubTopic.Text, data: bytes, headers: headers, serializer: rawSerializer);
            }
            catch (Exception ex) { _publisherTimer.Stop(); _isPublishing = false; BtnPubStart.Content = "▶️ 循环发送"; MessageBox.Show($"发布失败: {ex.Message}"); }
        }

        private async void BtnCallService_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null) return;
            var reqObj = PropGridSrvReq.SelectedObject;
            var reqTypeInfo = CboSrvReqType.SelectedItem as RosMessageTypeInfo;
            var resTypeInfo = CboSrvResType.SelectedItem as RosMessageTypeInfo;
            var srvName = TxtSrvName.Text;

            if (reqObj == null || reqTypeInfo?.Type == null || resTypeInfo?.Type == null || string.IsNullOrWhiteSpace(srvName)) return;

            try
            {
                BtnCallService.IsEnabled = false; BtnCallService.Content = "⏳ 等待响应..."; PropGridSrvRes.SelectedObject = null;
                var reqBytes = MessagePackSerializer.Serialize(reqTypeInfo.Type, reqObj);
                var headers = new NatsHeaders { { "ros-type", $"{reqTypeInfo.Type.FullName}, {reqTypeInfo.Type.Assembly.GetName().Name}" } };
                var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                var rawDeserializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>();

                var reply = await _nats.RequestAsync<byte[], byte[]>(srvName, reqBytes, headers: headers, requestSerializer: rawSerializer, replySerializer: rawDeserializer, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(decimal.ToInt32((decimal)SpinSrvTimeout.Value)) });

                if (reply.Data != null)
                {
                    PropGridSrvRes.SelectedObject = MessagePackSerializer.Deserialize(resTypeInfo.Type, reply.Data);
                    AppendLog("SYSTEM", $"✅ 服务 {srvName} 调用成功！", Colors.LimeGreen);
                }
            }
            catch (OperationCanceledException) { MessageBox.Show("调用超时"); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            finally { BtnCallService.IsEnabled = true; BtnCallService.Content = "🚀 发起 RPC 调用"; }
        }
    }
}
