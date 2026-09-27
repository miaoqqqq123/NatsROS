using NATS.Client.Core;
using NatsROS.Core.Parameters;
using NatsROS.Messages.Hardware;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace ScrewMachine.Dashboard.Plugins.HardwareStudio
{
    // ==========================================
    // UI 数据绑定模型
    // ==========================================
    public class IoMapItem : INotifyPropertyChanged
    {
        private string _tagName = "NEW_TAG";
        private int _physicalPin = 0;
        private NatsROS.Messages.Hardware.IoType _type = NatsROS.Messages.Hardware.IoType.Input;
        private string _description = "";

        public string TagName { get => _tagName; set { _tagName = value; OnPropertyChanged(); } }
        public int PhysicalPin { get => _physicalPin; set { _physicalPin = value; OnPropertyChanged(); } }
        public NatsROS.Messages.Hardware.IoType Type { get => _type; set { _type = value; OnPropertyChanged(); } }
        public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 强类型的轴物理参数模型 (让 PropertyGrid 渲染得漂漂亮亮)
    /// </summary>
    public class AxisConfigModel
    {
        [Category("1. 硬件绑定")]
        [DisplayName("控制卡物理轴号 (Axis ID)")]
        [Description("在雷赛或固高板卡上的实际插槽编号 (0-7)")]
        public ushort PhysicalAxisId { get; set; } = 0;

        [Category("2. 机械传动参数")]
        [DisplayName("脉冲当量 (Pulse/mm)")]
        [Description("电机旋转一圈所需脉冲数 / 丝杆导程。用于将物理单位(mm)转化为底层脉冲")]
        public double MotionScale { get; set; } = 10000.0;

        [Category("3. 软限位保护")]
        [DisplayName("正向软限位 (mm)")]
        [Description("软件保护极限，超出此坐标拒绝运动")]
        public double SoftLimitPositive { get; set; } = 1000.0;

        [Category("3. 软限位保护")]
        [DisplayName("负向软限位 (mm)")]
        public double SoftLimitNegative { get; set; } = -1000.0;

        [Category("4. 默认动力学")]
        [DisplayName("默认加速度 (mm/s²)")]
        public double DefaultAcceleration { get; set; } = 500.0;
    }

    public partial class HardwareStudioView : UserControl
    {
        private readonly INatsClient _nats;
        public ObservableCollection<IoMapItem> IoMappings { get; set; } = new();
        private AxisConfigModel _currentAxisConfig = new();

        public HardwareStudioView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            GridIoMap.ItemsSource = IoMappings;
        }

        // ==========================================
        // 1. IO 映射字典操作逻辑
        // ==========================================
        private async void BtnRefreshIo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string nodeName = TxtIoNodeName.Text.Trim();

                // 【发 RPC 获取强类型字典】
                var res = await _nats.RequestAsync<GetIoDictReq, GetIoDictRes>($"{nodeName}.io.get_dict", new GetIoDictReq(), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });

                if (res.Data != null && res.Data.Success)
                {
                    IoMappings.Clear();
                    foreach (var p in res.Data.IoPoints)
                    {
                        IoMappings.Add(new IoMapItem { TagName = p.TagName, PhysicalPin = p.PhysicalPin, Type = p.Type, Description = p.Description });
                    }
                }
                else MessageBox.Show("获取失败或节点不在线");
            }
            catch (Exception ex) { MessageBox.Show($"拉取 IO 字典失败: {ex.Message}"); }
        }

        private void BtnAddIo_Click(object sender, RoutedEventArgs e) => IoMappings.Add(new IoMapItem());

        private void BtnDelIo_Click(object sender, RoutedEventArgs e)
        {
            if (GridIoMap.SelectedItem is IoMapItem item) IoMappings.Remove(item);
        }

        private async void BtnSaveIo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string nodeName = TxtIoNodeName.Text.Trim();

                // 将 UI 的列表转换为 RPC 请求对象
                var reqPoints = IoMappings.Select(i => new NatsROS.Messages.Hardware.IoPointDefinition(i.TagName, i.PhysicalPin, i.Type, i.Description)).ToList();

                // 【发 RPC 下发保存】
                var res = await _nats.RequestAsync<SaveIoDictReq, SaveIoDictRes>($"{nodeName}.io.save_dict", new SaveIoDictReq(reqPoints), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });

                if (res.Data != null && res.Data.Success)
                    MessageBox.Show("✅ IO 标签字典已保存并下发底层！接线映射瞬间热重载。");
                else
                    MessageBox.Show($"保存失败: {res.Data?.Message}");
            }
            catch (Exception ex) { MessageBox.Show($"保存失败: {ex.Message}"); }
        }

        // ==========================================
        // 2. 轴物理参数操作逻辑
        // ==========================================
        private async void CboAxisNodeName_SelectedIndexChanged(object sender, RoutedEventArgs e)
        {
            string nodeName = CboAxisNodeName.Text.Trim();
            if (string.IsNullOrEmpty(nodeName)) return;

            PropGridAxis.SelectedObject = null;
            try
            {
                var paramClient = new RosParameterClient(_nats, nodeName);
                _currentAxisConfig = new AxisConfigModel(); // 实例化默认对象

                // 从参数服务器分别拉取字段，灌入强类型对象
                if (ushort.TryParse(await paramClient.GetAsync("PhysicalAxisId"), out ushort id)) _currentAxisConfig.PhysicalAxisId = id;
                if (double.TryParse(await paramClient.GetAsync("MotionScale"), out double ms)) _currentAxisConfig.MotionScale = ms;
                if (double.TryParse(await paramClient.GetAsync("SoftLimitPositive"), out double sp)) _currentAxisConfig.SoftLimitPositive = sp;
                if (double.TryParse(await paramClient.GetAsync("SoftLimitNegative"), out double sn)) _currentAxisConfig.SoftLimitNegative = sn;
                if (double.TryParse(await paramClient.GetAsync("DefaultAcceleration"), out double acc)) _currentAxisConfig.DefaultAcceleration = acc;

                PropGridAxis.SelectedObject = _currentAxisConfig;
            }
            catch { }
        }

        private async void BtnSaveAxis_Click(object sender, RoutedEventArgs e)
        {
            string nodeName = CboAxisNodeName.Text.Trim();
            if (string.IsNullOrEmpty(nodeName) || PropGridAxis.SelectedObject == null) return;

            try
            {
                var paramClient = new RosParameterClient(_nats, nodeName);

                // 将强类型对象的属性打散，逐一保存到参数服务器
                await paramClient.SetAsync("PhysicalAxisId", _currentAxisConfig.PhysicalAxisId.ToString());
                await paramClient.SetAsync("MotionScale", _currentAxisConfig.MotionScale.ToString("F3"));
                await paramClient.SetAsync("SoftLimitPositive", _currentAxisConfig.SoftLimitPositive.ToString("F3"));
                await paramClient.SetAsync("SoftLimitNegative", _currentAxisConfig.SoftLimitNegative.ToString("F3"));
                await paramClient.SetAsync("DefaultAcceleration", _currentAxisConfig.DefaultAcceleration.ToString("F3"));

                MessageBox.Show($"✅ 轴 [{nodeName}] 物理标定参数已保存至沙盒！");
            }
            catch (Exception ex) { MessageBox.Show($"保存失败: {ex.Message}"); }
        }
    }
}