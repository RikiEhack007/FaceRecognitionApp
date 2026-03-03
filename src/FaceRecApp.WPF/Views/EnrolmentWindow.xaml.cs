using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FaceRecApp.Core.Entities;
using FaceRecApp.Core.Services;
using FaceRecApp.WPF.Helpers;
using FaceRecApp.WPF.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;

namespace FaceRecApp.WPF.Views;

/// <summary>
/// Multi-step enrolment wizard: e-Consent → Demographics → Deduplication → Face Capture.
/// </summary>
public partial class EnrolmentWindow : System.Windows.Window
{
    private readonly CameraService _camera;
    private readonly RecognitionPipeline _pipeline;
    private readonly FaceRepository _repository;
    private readonly PidGenerationService _pidService;
    private readonly FingerprintService _fingerprint;
    private readonly DispatcherTimer _previewTimer;

    private int _currentStep = 1;
    private const int TotalSteps = 5;
    private float[]? _capturedEmbedding;
    private byte[]? _capturedThumbnail;
    private bool _isCapturing;

    // Fingerprint enrollment state
    private readonly byte[][] _fpRegTemplates = new byte[3][];
    private int _fpRegCount;
    private byte[]? _capturedFingerprintTemplate;
    private string _selectedFingerType = BiometricRemarks.Types.FingerR2;

    public EnrolmentWindow()
    {
        InitializeComponent();

        _camera = App.Services.GetRequiredService<CameraService>();
        _pipeline = App.Services.GetRequiredService<RecognitionPipeline>();
        _repository = App.Services.GetRequiredService<FaceRepository>();
        _pidService = App.Services.GetRequiredService<PidGenerationService>();
        _fingerprint = App.Services.GetRequiredService<FingerprintService>();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _previewTimer.Tick += OnPreviewTick;

        // Load consent text from config
        var config = App.Services.GetRequiredService<IConfiguration>();
        var consentText = config.GetSection("PatientId").GetValue<string>("ConsentText");
        ConsentTextBlock.Text = consentText ?? "I agree to biometric data collection for identification purposes.";

        // Populate DOB dropdowns
        PopulateDobDropdowns();

        // Populate face remark dropdown
        FaceRemarkCombo.Items.Add("(None - capture successful)");
        foreach (var remark in BiometricRemarks.FaceRemarks)
            FaceRemarkCombo.Items.Add(remark);
        FaceRemarkCombo.SelectedIndex = 0;

        // Populate finger type dropdown
        FingerTypeCombo.Items.Add("Right Index (R2)");
        FingerTypeCombo.Items.Add("Right Thumb (R1)");
        FingerTypeCombo.Items.Add("Right Middle (R3)");
        FingerTypeCombo.Items.Add("Right Ring (R4)");
        FingerTypeCombo.Items.Add("Right Little (R5)");
        FingerTypeCombo.Items.Add("Left Index (L2)");
        FingerTypeCombo.Items.Add("Left Thumb (L1)");
        FingerTypeCombo.Items.Add("Left Middle (L3)");
        FingerTypeCombo.Items.Add("Left Ring (L4)");
        FingerTypeCombo.Items.Add("Left Little (L5)");
        FingerTypeCombo.SelectedIndex = 0;
        FingerTypeCombo.SelectionChanged += (_, _) =>
        {
            _selectedFingerType = FingerTypeCombo.SelectedIndex switch
            {
                0 => BiometricRemarks.Types.FingerR2,
                1 => BiometricRemarks.Types.FingerR1,
                2 => BiometricRemarks.Types.FingerR3,
                3 => BiometricRemarks.Types.FingerR4,
                4 => BiometricRemarks.Types.FingerR5,
                5 => BiometricRemarks.Types.FingerL2,
                6 => BiometricRemarks.Types.FingerL1,
                7 => BiometricRemarks.Types.FingerL3,
                8 => BiometricRemarks.Types.FingerL4,
                9 => BiometricRemarks.Types.FingerL5,
                _ => BiometricRemarks.Types.FingerR2
            };
        };

        // Populate fingerprint remark dropdown
        FingerprintRemarkCombo.Items.Add("(None - capture successful)");
        foreach (var remark in BiometricRemarks.FingerprintRemarks)
            FingerprintRemarkCombo.Items.Add(remark);
        FingerprintRemarkCombo.SelectedIndex = 0;

        Loaded += (_, _) =>
        {
            if (_camera.IsRunning)
            {
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
        };

        Closing += (_, _) =>
        {
            _previewTimer.Stop();
            _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;
        };
    }

    private void PopulateDobDropdowns()
    {
        // Month: 1-12 + Don't Know
        DOBMonthCombo.Items.Add("Don't Know");
        for (int i = 1; i <= 12; i++)
            DOBMonthCombo.Items.Add(new System.Globalization.DateTimeFormatInfo().GetMonthName(i));
        DOBMonthCombo.SelectedIndex = 0;

        // Day: 1-31 + Don't Know
        DOBDayCombo.Items.Add("Don't Know");
        for (int i = 1; i <= 31; i++)
            DOBDayCombo.Items.Add(i.ToString());
        DOBDayCombo.SelectedIndex = 0;
    }

    // ══════════════════════════════════════════════
    //  STEP NAVIGATION
    // ══════════════════════════════════════════════

    private void ShowStep(int step)
    {
        _currentStep = step;

        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5Panel.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = step == TotalSteps ? "Register" : "Next";

        string[] stepNames = ["e-Consent", "Demographics", "Deduplication", "Face Capture", "Fingerprint Capture"];
        StepIndicator.Text = $"Step {step} of {TotalSteps} \u2014 {stepNames[step - 1]}";

        // Start/stop camera preview for step 4
        if (step == 4 && _camera.IsRunning)
            _previewTimer.Start();
        else
            _previewTimer.Stop();

        // Start/stop fingerprint capture for step 5
        if (step == 5)
            StartFingerprintEnrollment();
        else
            _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(_currentStep - 1);
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 1: // e-Consent → Demographics
                if (!ValidateConsent()) return;
                ShowStep(2);
                break;

            case 2: // Demographics → Deduplication
                if (!ValidateDemographics()) return;
                await RunDeduplicationCheck();
                ShowStep(3);
                break;

            case 3: // Deduplication → Face Capture
                ShowStep(4);
                break;

            case 4: // Face Capture → Fingerprint Capture
                ShowStep(5);
                break;

            case 5: // Fingerprint Capture → Register
                await RegisterPatient();
                break;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ══════════════════════════════════════════════
    //  STEP 1: e-CONSENT VALIDATION
    // ══════════════════════════════════════════════

    private bool ValidateConsent()
    {
        if (ConsentCheckBox.IsChecked != true)
        {
            MessageBox.Show("Please agree to the biometric data consent to proceed.",
                "Consent Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    // ══════════════════════════════════════════════
    //  STEP 2: DEMOGRAPHICS VALIDATION
    // ══════════════════════════════════════════════

    private bool ValidateDemographics()
    {
        if (string.IsNullOrWhiteSpace(FullNameInput.Text))
        {
            MessageBox.Show("Full Name is required.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            FullNameInput.Focus();
            return false;
        }

        if (SexMale.IsChecked != true && SexFemale.IsChecked != true)
        {
            MessageBox.Show("Please select Sex.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    // ══════════════════════════════════════════════
    //  STEP 3: DEDUPLICATION CHECK
    // ══════════════════════════════════════════════

    private async Task RunDeduplicationCheck()
    {
        var fullName = FullNameInput.Text.Trim();
        DedupStatusText.Text = $"Checking for existing patients named \"{fullName}\"...";

        try
        {
            var duplicates = await _repository.CheckDuplicateByNameAsync(fullName);

            if (duplicates.Count == 0)
            {
                DedupStatusText.Text = "No duplicate patients found. You may proceed with enrolment.";
                DedupResultsList.Visibility = Visibility.Collapsed;
                DedupInstructions.Visibility = Visibility.Collapsed;
            }
            else
            {
                DedupStatusText.Text = $"Found {duplicates.Count} existing patient(s) with the same name:";
                DedupResultsList.ItemsSource = duplicates;
                DedupResultsList.Visibility = Visibility.Visible;
                DedupInstructions.Text = "If this is the same person, close this window and search for the existing record. " +
                                         "If this is a different person, click Next to continue enrolment.";
                DedupInstructions.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            DedupStatusText.Text = $"Deduplication check failed: {ex.Message}. You may proceed.";
        }
    }

    // ══════════════════════════════════════════════
    //  STEP 4: CAMERA PREVIEW & FACE CAPTURE
    // ══════════════════════════════════════════════

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (!_camera.IsRunning) return;

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null) return;

            var bitmap = WpfImageHelper.MatToFrozenBitmapSource(frame);
            PreviewImage.Source = bitmap;
        }
        catch { }
    }

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_isCapturing) return;

        if (!_camera.IsRunning)
        {
            CaptureStatusLabel.Text = "Camera is not running. Start it from the main window.";
            CaptureStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x5C, 0x56));
            return;
        }

        _isCapturing = true;
        CaptureButton.IsEnabled = false;
        CaptureStatusLabel.Text = "Capturing face... Please hold still.";
        CaptureStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0x71, 0x6C));

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null)
            {
                CaptureStatusLabel.Text = "Failed to capture frame. Try again.";
                CaptureStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x5C, 0x56));
                return;
            }

            // Use pipeline to detect face and generate embedding
            var result = await _pipeline.RegisterFromFrameAsync(frame, "__temp__");

            if (result.Success && result.Person != null)
            {
                // We got a successful detection. Extract the embedding from the person's face embeddings.
                var faceEmbedding = result.Person.FaceEmbeddings.FirstOrDefault();
                if (faceEmbedding != null)
                {
                    _capturedEmbedding = faceEmbedding.Embedding;
                    _capturedThumbnail = faceEmbedding.FaceThumbnail;

                    // Delete the temporary person created by RegisterFromFrameAsync
                    await _repository.DeletePersonAsync(result.Person.Id);

                    CaptureStatusLabel.Text = "Face captured successfully! Click Register to complete.";
                    CaptureStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x7F, 0x62));
                }
            }
            else
            {
                CaptureStatusLabel.Text = result.Error ?? "No face detected. Try again.";
                CaptureStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x5C, 0x56));
            }
        }
        catch (Exception ex)
        {
            CaptureStatusLabel.Text = $"Error: {ex.Message}";
            CaptureStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x5C, 0x56));
        }
        finally
        {
            _isCapturing = false;
            CaptureButton.IsEnabled = true;
        }
    }

    // ══════════════════════════════════════════════
    //  STEP 5: FINGERPRINT ENROLLMENT
    // ══════════════════════════════════════════════

    private void StartFingerprintEnrollment()
    {
        _fpRegCount = 0;
        _capturedFingerprintTemplate = null;
        FpCaptureProgress.Text = "0 of 3 captures";
        FingerprintPreview.Source = null;
        FingerprintPlaceholder.Visibility = Visibility.Visible;

        if (!_fingerprint.IsInitialized)
        {
            int devCount = _fingerprint.Initialize();
            if (devCount < 0)
            {
                FpStatusLabel.Text = "Fingerprint SDK failed to load. Ensure ZKFinger SDK is installed.";
                return;
            }
            if (devCount == 0)
            {
                FpStatusLabel.Text = "No fingerprint scanner detected. You may skip this step by selecting a remark.";
                return;
            }
        }

        if (!_fingerprint.IsDeviceOpen)
        {
            if (!_fingerprint.OpenDevice(0))
            {
                FpStatusLabel.Text = "Failed to open fingerprint scanner. You may skip this step.";
                return;
            }
        }

        FpStatusLabel.Text = "Place finger on scanner (capture 1 of 3)...";
        _fingerprint.FingerprintCaptured += OnFingerprintForEnrollment;
    }

    private void OnFingerprintForEnrollment(object? sender, FingerprintCapturedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // If not first capture, verify same finger
            if (_fpRegCount > 0)
            {
                int matchScore = _fingerprint.Match(e.Template, _fpRegTemplates[_fpRegCount - 1]);
                if (matchScore <= 0)
                {
                    FpStatusLabel.Text = "Different finger detected. Please use the same finger.";
                    return;
                }
            }

            _fpRegTemplates[_fpRegCount] = new byte[e.TemplateSize];
            Array.Copy(e.Template, _fpRegTemplates[_fpRegCount], e.TemplateSize);
            _fpRegCount++;

            // Show fingerprint image
            try
            {
                var bitmap = FingerprintService.RawToBitmapSource(
                    e.ImageData, _fingerprint.ImageWidth, _fingerprint.ImageHeight);
                FingerprintPreview.Source = bitmap;
                FingerprintPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch { }

            if (_fpRegCount >= 3)
            {
                // Merge 3 templates
                var merged = _fingerprint.MergeTemplates(
                    _fpRegTemplates[0], _fpRegTemplates[1], _fpRegTemplates[2]);

                if (merged != null)
                {
                    _capturedFingerprintTemplate = merged;
                    FpStatusLabel.Text = "Fingerprint captured successfully! Click Register to complete.";
                    FpCaptureProgress.Text = "3 of 3 captures \u2014 Complete";
                    _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;
                }
                else
                {
                    FpStatusLabel.Text = "Merge failed. Starting over — place finger again...";
                    _fpRegCount = 0;
                    FpCaptureProgress.Text = "0 of 3 captures";
                }
            }
            else
            {
                FpCaptureProgress.Text = $"{_fpRegCount} of 3 captures";
                FpStatusLabel.Text = $"Capture {_fpRegCount} successful. Place same finger again ({_fpRegCount + 1} of 3)...";
            }
        });
    }

    // ══════════════════════════════════════════════
    //  REGISTRATION
    // ══════════════════════════════════════════════

    private async Task RegisterPatient()
    {
        // Check if face has been captured
        if (_capturedEmbedding == null)
        {
            // Allow registration without face if a remark is selected
            if (FaceRemarkCombo.SelectedIndex <= 0)
            {
                MessageBox.Show("Please capture a face first, or select a biometric remark if capture is not possible.",
                    "Face Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        NextButton.IsEnabled = false;
        NextButton.Content = "Registering...";

        try
        {
            // Generate PID
            var pid = await _pidService.GenerateNextPidAsync();

            // Build Patient entity
            var patient = new Person
            {
                IDCard = pid,
                Site = _pidService.SiteCode,
                FullName = FullNameInput.Text.Trim(),
                Sex = SexMale.IsChecked == true ? (byte)1 : (byte)2,
                AdmissionDate = DateTime.UtcNow,
                ConsentGiven = ConsentCheckBox.IsChecked == true,
                ConsentDate = DateTime.UtcNow,
                CreatedBy = Environment.UserName,
            };

            // DOB
            if (short.TryParse(DOBYearInput.Text.Trim(), out short year))
                patient.DOBYear = year;
            patient.DOBMonth = DOBMonthCombo.SelectedIndex == 0 ? (short)-1 : (short)DOBMonthCombo.SelectedIndex;
            patient.DOBDay = DOBDayCombo.SelectedIndex == 0 ? (short)-1 : (short)DOBDayCombo.SelectedIndex;

            // Calculate age at enrolment
            if (patient.DOBYear.HasValue && patient.DOBYear > 0)
            {
                var now = DateTime.UtcNow;
                int age = now.Year - patient.DOBYear.Value;
                if (patient.DOBMonth > 0 && patient.DOBMonth <= 12)
                {
                    if (now.Month < patient.DOBMonth)
                        age--;
                    else if (now.Month == patient.DOBMonth && patient.DOBDay > 0 && now.Day < patient.DOBDay)
                        age--;
                }
                patient.AgeAtEnrolment = (byte)Math.Max(0, Math.Min(255, age));
            }

            // Optional fields
            if (!string.IsNullOrWhiteSpace(AddressCodeInput.Text))
                patient.AddressCode = AddressCodeInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(AddressOtherInput.Text))
                patient.AddressOther = AddressOtherInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(MotherNameInput.Text))
                patient.MotherName = MotherNameInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(FatherNameInput.Text))
                patient.FatherName = FatherNameInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(SpouseNameInput.Text))
                patient.SpouseName = SpouseNameInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(NotesInput.Text))
                patient.Notes = NotesInput.Text.Trim();

            Person savedPatient;

            if (_capturedEmbedding != null)
            {
                // Register with face embedding (Consent + CreatedBy set on FaceEmbedding directly)
                savedPatient = await _repository.RegisterPatientAsync(patient, _capturedEmbedding, _capturedThumbnail);
            }
            else
            {
                // Register without face (remark selected)
                patient.CreatedAt = DateTime.UtcNow;
                patient.LastSeenAt = DateTime.UtcNow;
                patient.IsActive = true;

                // Append face remark to patient notes
                var remarkText = FaceRemarkCombo.SelectedItem?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(remarkText))
                {
                    patient.Notes = string.IsNullOrEmpty(patient.Notes)
                        ? $"[Face] {remarkText}"
                        : $"{patient.Notes}\n[Face] {remarkText}";
                }

                // Save patient without embedding via direct DB save
                await using var db = await App.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<FaceRecApp.Core.Data.FaceDbContext>>()
                    .CreateDbContextAsync();
                db.Set<Person>().Add(patient);
                await db.SaveChangesAsync();
                savedPatient = patient;
            }

            // Save fingerprint template if captured
            if (_capturedFingerprintTemplate != null)
            {
                await _repository.AddFingerprintTemplateAsync(
                    savedPatient.Id,
                    _selectedFingerType,
                    _capturedFingerprintTemplate,
                    consent: ConsentCheckBox.IsChecked == true);
            }
            else if (FingerprintRemarkCombo.SelectedIndex > 0)
            {
                // Save fingerprint remark (no template — capture failed)
                var fpRemarkText = FingerprintRemarkCombo.SelectedItem?.ToString() ?? "";
                await _repository.AddFingerprintTemplateAsync(
                    savedPatient.Id,
                    _selectedFingerType,
                    template: null,
                    consent: ConsentCheckBox.IsChecked == true,
                    remark: fpRemarkText);
            }

            // Stop fingerprint capture
            _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;

            MessageBox.Show(
                $"Patient enrolled successfully!\n\nPID: {pid}\nName: {savedPatient.FullName}",
                "Enrolment Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Registration failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "Register";
        }
    }
}
