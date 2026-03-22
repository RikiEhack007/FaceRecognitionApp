using System.Windows;
using FaceRecApp.Core.Entities;
using FaceRecApp.WPF.Views;

namespace FaceRecApp.WPF.Services;

public class DialogService : IDialogService
{
    public bool? ShowEnrolmentDialog()
    {
        var window = new EnrolmentWindow();
        window.Owner = Application.Current.MainWindow;
        return window.ShowDialog();
    }

    public bool? ShowEnrolmentDialog(Patient editPatient)
    {
        var window = new EnrolmentWindow(editPatient);
        window.Owner = Application.Current.MainWindow;
        return window.ShowDialog();
    }

    public bool? ShowDatabaseDialog()
    {
        var window = new DatabaseWindow();
        window.Owner = Application.Current.MainWindow;
        return window.ShowDialog();
    }

    public bool? ShowVisitDialog(Patient patient)
    {
        var window = new VisitWindow(patient);
        window.Owner = Application.Current.MainWindow;
        return window.ShowDialog();
    }

    public bool? ShowRegisterDialog()
    {
        var window = new RegisterWindow();
        window.Owner = Application.Current.MainWindow;
        return window.ShowDialog();
    }

    public bool? ShowBenchmarkDialog()
    {
        var window = new BenchmarkWindow();
        window.Owner = Application.Current.MainWindow;
        return window.ShowDialog();
    }

    public string? ShowOpenFileDialog(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool ShowConfirm(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowError(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
