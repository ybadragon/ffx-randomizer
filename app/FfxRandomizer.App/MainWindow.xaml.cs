using FfxRandomizer.App.ViewModels;
using MahApps.Metro.Controls;

namespace FfxRandomizer.App;

public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
