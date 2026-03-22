using FaceRecApp.Core.Entities;

namespace FaceRecApp.WPF.Services;

public interface IDialogService
{
    bool? ShowEnrolmentDialog();
    bool? ShowEnrolmentDialog(Patient editPatient);
    bool? ShowDatabaseDialog();
    bool? ShowVisitDialog(Patient patient);
    bool? ShowRegisterDialog();
    bool? ShowBenchmarkDialog();
    string? ShowOpenFileDialog(string title, string filter);
    bool ShowConfirm(string message, string title);
    void ShowError(string message, string title = "Error");
    void ShowInfo(string message, string title = "Information");
}
