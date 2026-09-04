using DevExpress.Xpf.Core;

namespace NatsROS.Dashboard.UI.ThemedSplashScreen
{
    /// <summary>
    /// Interaction logic for SplashScreen.xaml
    /// </summary>
    public partial class SplashScreen : SplashScreenWindow
    {
        public SplashScreen()
        {
            InitializeComponent();

            this.Topmost = true;
        }

        public SplashScreen(bool? topmost)
        {
            InitializeComponent();

            if (topmost.HasValue) this.Topmost = topmost.Value;
        }
    }
}
