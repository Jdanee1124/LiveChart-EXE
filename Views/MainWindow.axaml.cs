using Avalonia.Controls;
using LineChart.Controls;
using LineChart.ViewModels;

namespace LineChart.Views
{
    public partial class MainWindow : Window
    {
        private MyLiveChart? _chartControl;

        public MainWindow()
        {
            InitializeComponent();
        }

        protected override void OnOpened(System.EventArgs e)
        {
            base.OnOpened(e);

            _chartControl = this.FindControl<MyLiveChart>("ChartControl");

            if (DataContext is MainWindowViewModel vm && _chartControl != null)
            {
                vm.OnDataReceived += data =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _chartControl.DataInput = data;
                    });
                };

                vm.OnResetViewRequested += () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _chartControl.ResetView();
                    });
                };

                vm.OnClearDataRequested += () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _chartControl.ClearData();
                    });
                };

                _chartControl.ChartCaptured += msg =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        vm.StatusMessage = msg;
                    });
                };

                _chartControl.DataUpdated += () =>
                {
                    vm.UpdateMetrics(_chartControl.LatestValue, _chartControl.AverageValue, _chartControl.DataCount);
                };
            }
        }
    }
}
