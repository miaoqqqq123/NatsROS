using NATS.Client.Core;
using NatsROS.Core.SystemMessages;
using System;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Plugin.EchoTest
{
    public partial class EchoTestView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;

        public EchoTestView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
        }

        private async void BtnBroadcast_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtMessage.Text)) return;

            try
            {
                // 调用 Core 里的标准日志广播，在大屏底部应该能看到它！
                var logMsg = new LogMsg(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RosLogLevels.Info, "EchoPlugin", $"来自外部插件的问候：{TxtMessage.Text}");

                var headers = new NATS.Client.Core.NatsHeaders
                { 
                    { "ros-type", "NatsROS.Core.SystemMessages.LogMsg, NatsROS.Core" }
                };
                var rawSer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                var bytes = MessagePack.MessagePackSerializer.Serialize(logMsg);

                await _nats.PublishAsync("rosout", data: bytes, headers: headers, serializer: rawSer);
                //TxtMessage.Text = "";
            }
            catch (Exception ex) 
            { 
                MessageBox.Show(ex.Message); 
            }
        }

        public void Dispose() { }
    }
}