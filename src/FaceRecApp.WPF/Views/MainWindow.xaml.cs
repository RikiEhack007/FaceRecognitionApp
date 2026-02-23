using System.Windows;
using FaceRecApp.WPF.ViewModels;

namespace FaceRecApp.WPF.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            // Clean up camera + AI resources when window closes
            if (DataContext is MainViewModel vm)
                vm.Dispose();
        };
    }

}
