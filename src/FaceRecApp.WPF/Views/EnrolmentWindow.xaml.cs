using System.Windows;
using System.Windows.Controls;
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

// Status brushes resolved from App.xaml resources (AccentDanger, TextSecondary, StepComplete)
file static class StatusBrushes
{
    private static SolidColorBrush? _error, _info, _success;

    public static SolidColorBrush Error => _error ??= (SolidColorBrush)Application.Current.FindResource("AccentDanger");
    public static SolidColorBrush Info => _info ??= (SolidColorBrush)Application.Current.FindResource("TextSecondary");
    public static SolidColorBrush Success => _success ??= (SolidColorBrush)Application.Current.FindResource("StepComplete");
}

public enum EnrollmentMode { Create, Edit }

/// <summary>
/// Multi-step enrolment wizard: e-Consent -> Demographics -> Deduplication -> Face Capture -> Fingerprint.
/// Supports both Create and Edit modes.
/// </summary>
public partial class EnrolmentWindow : System.Windows.Window
{
    private readonly CameraService _camera;
    private readonly RecognitionPipeline _pipeline;
    private readonly FaceRepository _repository;
    private readonly PidGenerationService _pidService;
    private readonly FingerprintService _fingerprint;
    private readonly DispatcherTimer _previewTimer;

    // Mode
    private readonly EnrollmentMode _mode;
    private readonly Patient? _editingPatient;

    // State
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

    private static readonly string[] StepNames =
        ["e-Consent", "Demographics", "Dedup Check", "Face Capture", "Fingerprint"];

    /// <summary>Create mode constructor.</summary>
    public EnrolmentWindow() : this(null) { }

    /// <summary>Dual-mode constructor. Pass a Patient to enter Edit mode.</summary>
    public EnrolmentWindow(Patient? editPatient)
    {
        _mode = editPatient != null ? EnrollmentMode.Edit : EnrollmentMode.Create;
        _editingPatient = editPatient;

        InitializeComponent();

        // Resolve services
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

        PopulateDobDropdowns();
        PopulateRemarkDropdowns();
        PopulateFingerTypeDropdown();
        SetupStepIndicator();

        // DOB change events
        DOBMonthCombo.SelectionChanged += OnDobMonthChanged;
        DOBDayCombo.SelectionChanged += (_, _) => UpdateDobWarning();
        DOBYearInput.TextChanged += (_, _) => UpdateAgeDisplay();

        if (_mode == EnrollmentMode.Edit)
        {
            Title = "Edit Patient";
            WindowTitle.Text = "Edit Patient";
            LoadPatientData(_editingPatient!);
            ShowStep(2);
        }
        else
        {
            ShowStep(1);
        }

        Loaded += (_, _) =>
        {
            if (_camera.IsRunning)
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
        };

        Closing += (_, _) =>
        {
            _previewTimer.Stop();
            _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;
        };
    }

    // ══════════════════════════════════════════════
    //  SETUP
    // ══════════════════════════════════════════════

    private void SetupStepIndicator()
    {
        StepIndicatorControl.StepNames = StepNames;

        if (_mode == EnrollmentMode.Edit)
        {
            // Consent and dedup are pre-completed in edit mode
            StepIndicatorControl.CompletedSteps = new HashSet<int> { 1, 3 };
            StepIndicatorControl.SkippedSteps = new HashSet<int> { 1, 3 };
            StepIndicatorControl.AllowDirectNavigation = true;
            StepIndicatorControl.StepNavigated += OnStepNavigated;
        }
    }

    private void OnStepNavigated(int targetStep)
    {
        // In edit mode, allow jumping to any non-skipped step
        if (_mode == EnrollmentMode.Edit)
        {
            var skipped = StepIndicatorControl.SkippedSteps;
            if (skipped.Contains(targetStep)) return;

            // Validate current step before leaving
            if (_currentStep == 2 && !ValidateDemographics()) return;

            ShowStep(targetStep);
        }
    }

    private void PopulateDobDropdowns()
    {
        DOBMonthCombo.Items.Add("[DK] Don't Know");
        var dtfi = new System.Globalization.DateTimeFormatInfo();
        for (int i = 1; i <= 12; i++)
            DOBMonthCombo.Items.Add(dtfi.GetMonthName(i));
        DOBMonthCombo.SelectedIndex = 0;

        DOBDayCombo.Items.Add("[DK] Don't Know");
        for (int i = 1; i <= 31; i++)
            DOBDayCombo.Items.Add(i.ToString());
        DOBDayCombo.SelectedIndex = 0;
    }

    private void PopulateRemarkDropdowns()
    {
        FaceRemarkCombo.Items.Add("(None - capture successful)");
        foreach (var remark in BiometricRemarks.FaceRemarks)
            FaceRemarkCombo.Items.Add(remark);
        FaceRemarkCombo.SelectedIndex = 0;

        FingerprintRemarkCombo.Items.Add("(None - capture successful)");
        foreach (var remark in BiometricRemarks.FingerprintRemarks)
            FingerprintRemarkCombo.Items.Add(remark);
        FingerprintRemarkCombo.SelectedIndex = 0;
    }

    private void PopulateFingerTypeDropdown()
    {
        foreach (var (_, displayName) in BiometricRemarks.FingerTypes)
            FingerTypeCombo.Items.Add(displayName);
        FingerTypeCombo.SelectedIndex = 0;
        FingerTypeCombo.SelectionChanged += (_, _) =>
        {
            var idx = FingerTypeCombo.SelectedIndex;
            _selectedFingerType = idx >= 0 && idx < BiometricRemarks.FingerTypes.Length
                ? BiometricRemarks.FingerTypes[idx].Code
                : BiometricRemarks.Types.FingerR2;
        };
    }

    // ══════════════════════════════════════════════
    //  EDIT MODE — Load patient data into form
    // ══════════════════════════════════════════════

    private void LoadPatientData(Patient patient)
    {
        // Show edit banner + PID
        EditBanner.Visibility = Visibility.Visible;
        EditBannerPid.Text = patient.IDCard;
        PidPanel.Visibility = Visibility.Visible;
        PidDisplay.Text = patient.IDCard;
        AdmissionDateDisplay.Text = patient.AdmissionDate?.ToString("dd-MMM-yyyy HH:mm") ?? "N/A";
        PidHint.Visibility = Visibility.Collapsed;

        // Show face recapture warning in step 4
        FaceRecaptureWarning.Visibility = Visibility.Visible;

        // Demographics
        FullNameInput.Text = patient.FullName ?? "";
        BurmeseNameInput.Text = patient.BurmeseName ?? "";
        KarenNameInput.Text = patient.KarenName ?? "";

        if (patient.Sex == 1) SexMale.IsChecked = true;
        else if (patient.Sex == 2) SexFemale.IsChecked = true;

        // DOB
        DOBYearInput.Text = patient.DOB_year?.ToString() ?? "";

        if (patient.DOB_month.HasValue)
            DOBMonthCombo.SelectedIndex = patient.DOB_month == -1 ? 0 : patient.DOB_month.Value;
        else
            DOBMonthCombo.SelectedIndex = 0;

        if (patient.DOB_day.HasValue)
            DOBDayCombo.SelectedIndex = patient.DOB_day == -1 ? 0 : patient.DOB_day.Value;
        else
            DOBDayCombo.SelectedIndex = 0;

        // Age on admission
        if (patient.Age.HasValue)
        {
            AgeOnAdmissionDisplay.Text = $"Age on admission: {patient.Age} y {patient.Month ?? 0} m {patient.Day ?? 0} d";
            AgeOnAdmissionDisplay.Visibility = Visibility.Visible;
        }

        // Family
        MotherPIDInput.Text = patient.MotherPID ?? "";
        MotherNameInput.Text = patient.MotherName ?? "";
        FatherNameInput.Text = patient.FatherName ?? "";
        SpouseNameInput.Text = patient.SpouseName ?? "";

        // Contact & Address
        PhoneNumberInput.Text = patient.PhoneNumber ?? "";
        AddressCodeInput.Text = patient.AddressCode ?? "";
        AddressOtherInput.Text = patient.AddressOther ?? "";
        NotesInput.Text = patient.Note ?? "";

        UpdateDobWarning();
        UpdateAgeDisplay();
    }

    // ══════════════════════════════════════════════
    //  DOB "Don't Know" HANDLING
    // ══════════════════════════════════════════════

    private void OnDobMonthChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DOBMonthCombo.SelectedIndex == 0) // [DK] Don't Know
        {
            // Cannot know exact day if month is unknown
            DOBDayCombo.SelectedIndex = 0;
            DOBDayCombo.IsEnabled = false;
        }
        else
        {
            DOBDayCombo.IsEnabled = true;
        }

        UpdateDobWarning();
        UpdateAgeDisplay();
    }

    private void UpdateDobWarning()
    {
        bool monthDk = DOBMonthCombo.SelectedIndex == 0;
        bool dayDk = DOBDayCombo.SelectedIndex == 0;
        bool hasDk = monthDk || dayDk;

        // Set warning state on ComboBoxes (triggers amber border via Tag)
        DOBMonthCombo.Tag = monthDk ? "Warning" : null;
        DOBDayCombo.Tag = dayDk ? "Warning" : null;

        // Show warning label
        DobWarningLabel.Visibility = hasDk ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAgeDisplay()
    {
        var yearText = DOBYearInput.Text.Trim();
        if (!short.TryParse(yearText, out short year) || year < 1900 || year > DateTime.Now.Year)
        {
            AgeDisplay.Visibility = Visibility.Collapsed;
            return;
        }

        int month = DOBMonthCombo.SelectedIndex <= 0 ? -1 : DOBMonthCombo.SelectedIndex;
        int day = DOBDayCombo.SelectedIndex <= 0 ? -1 : DOBDayCombo.SelectedIndex;

        string ageText = CalculateAgeString(year, (short)month, (short)day, DateTime.Today);
        AgeDisplay.Text = $"Age today: {ageText}";
        AgeDisplay.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Calculate age components from DOB parts. Returns null if DOB is incomplete (DK).
    /// </summary>
    private static (int Years, int Months, int Days)? CalculateAgeParts(short year, short month, short day, DateTime reference)
    {
        if (month <= 0 || day <= 0) return null;

        try
        {
            var dob = new DateTime(year, month, day);
            int y = reference.Year - dob.Year;
            int m = reference.Month - dob.Month;
            int d = reference.Day - dob.Day;
            if (d < 0)
            {
                m--;
                d += DateTime.DaysInMonth(reference.Year, reference.Month == 1 ? 12 : reference.Month - 1);
            }
            if (m < 0)
            {
                y--;
                m += 12;
            }
            return (y, m, d);
        }
        catch
        {
            return null;
        }
    }

    private static string CalculateAgeString(short year, short month, short day, DateTime reference)
    {
        var parts = CalculateAgeParts(year, month, day, reference);
        if (parts == null)
            return $"~{reference.Year - year} y (estimated)";
        return $"{parts.Value.Years} y {parts.Value.Months} m {parts.Value.Days} d";
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

        // Button label
        if (step == TotalSteps)
            NextButton.Content = _mode == EnrollmentMode.Edit ? "Save Changes" : "Register";
        else
            NextButton.Content = "Next";

        // Update step indicator
        StepIndicatorControl.CurrentStep = step;

        // Mark previous steps as completed
        var completed = new HashSet<int>(StepIndicatorControl.CompletedSteps);
        for (int i = 1; i < step; i++)
            completed.Add(i);
        StepIndicatorControl.CompletedSteps = completed;

        // Camera preview management
        if (step == 4 && _camera.IsRunning)
            _previewTimer.Start();
        else
            _previewTimer.Stop();

        // Fingerprint management
        if (step == 5)
            StartFingerprintEnrollment();
        else
            _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;

        // In edit mode, hide Back button on step 2 (it's the first navigable step)
        if (_mode == EnrollmentMode.Edit && step == 2)
            BackButton.Visibility = Visibility.Collapsed;
    }

    private int GetNextStep(int current)
    {
        if (_mode == EnrollmentMode.Edit)
        {
            // Skip steps 1 and 3 in edit mode
            return current switch
            {
                2 => 4,
                4 => 5,
                _ => current + 1
            };
        }
        return current + 1;
    }

    private int GetPreviousStep(int current)
    {
        if (_mode == EnrollmentMode.Edit)
        {
            return current switch
            {
                5 => 4,
                4 => 2,
                _ => current - 1
            };
        }
        return current - 1;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        int prev = GetPreviousStep(_currentStep);
        if (prev >= 1)
            ShowStep(prev);
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 1:
                if (!ValidateConsent()) return;
                ShowStep(2);
                break;

            case 2:
                if (!ValidateDemographics()) return;
                if (_mode == EnrollmentMode.Create)
                {
                    await RunDeduplicationCheck();
                    ShowStep(3);
                }
                else
                {
                    ShowStep(GetNextStep(2)); // Skip to step 4
                }
                break;

            case 3:
                ShowStep(4);
                break;

            case 4:
                ShowStep(5);
                break;

            case 5:
                await SavePatientAsync();
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
        ClearValidationErrors();
        bool valid = true;

        // Full Name required
        if (string.IsNullOrWhiteSpace(FullNameInput.Text))
        {
            ShowFieldError(FullNameInput, FullNameError, "Full Name is required");
            valid = false;
        }

        // Sex required
        if (SexMale.IsChecked != true && SexFemale.IsChecked != true)
        {
            SexError.Text = "Please select sex";
            SexError.Visibility = Visibility.Visible;
            valid = false;
        }

        // DOB Year validation
        var yearText = DOBYearInput.Text.Trim();
        if (!string.IsNullOrEmpty(yearText))
        {
            if (!short.TryParse(yearText, out short year) || year < 1900 || year > DateTime.Now.Year)
            {
                ShowFieldError(DOBYearInput, DobYearError, $"Year must be between 1900 and {DateTime.Now.Year}");
                valid = false;
            }
        }

        if (!valid)
        {
            // Focus first error field
            if (FullNameInput.Tag?.ToString() == "Error") FullNameInput.Focus();
            else if (DOBYearInput.Tag?.ToString() == "Error") DOBYearInput.Focus();
        }

        return valid;
    }

    private void ShowFieldError(TextBox field, TextBlock errorLabel, string message)
    {
        field.Tag = "Error";
        errorLabel.Text = message;
        errorLabel.Visibility = Visibility.Visible;
    }

    private void ClearValidationErrors()
    {
        FullNameInput.Tag = null;
        FullNameError.Visibility = Visibility.Collapsed;
        SexError.Visibility = Visibility.Collapsed;
        DOBYearInput.Tag = null;
        DobYearError.Visibility = Visibility.Collapsed;
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
            CaptureStatusLabel.Foreground = StatusBrushes.Error;
            return;
        }

        _isCapturing = true;
        CaptureButton.IsEnabled = false;
        CaptureStatusLabel.Text = "Capturing face... Please hold still.";
        CaptureStatusLabel.Foreground = StatusBrushes.Info;

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null)
            {
                CaptureStatusLabel.Text = "Failed to capture frame. Try again.";
                CaptureStatusLabel.Foreground = StatusBrushes.Error;
                return;
            }

            // Capture embedding + thumbnail without persisting to database
            var result = await Task.Run(() => _pipeline.CaptureFromFrame(frame));

            if (result.Success && result.Embedding != null)
            {
                _capturedEmbedding = result.Embedding;
                _capturedThumbnail = result.Thumbnail;

                CaptureStatusLabel.Text = _mode == EnrollmentMode.Edit
                    ? "Face captured successfully! Click Save Changes to complete."
                    : "Face captured successfully! Click Register to complete.";
                CaptureStatusLabel.Foreground = StatusBrushes.Success;
            }
            else
            {
                CaptureStatusLabel.Text = result.Error ?? "No face detected. Try again.";
                CaptureStatusLabel.Foreground = StatusBrushes.Error;
            }
        }
        catch (Exception ex)
        {
            CaptureStatusLabel.Text = $"Error: {ex.Message}";
            CaptureStatusLabel.Foreground = StatusBrushes.Error;
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
                var merged = _fingerprint.MergeTemplates(
                    _fpRegTemplates[0], _fpRegTemplates[1], _fpRegTemplates[2]);

                if (merged != null)
                {
                    _capturedFingerprintTemplate = merged;
                    FpStatusLabel.Text = _mode == EnrollmentMode.Edit
                        ? "Fingerprint captured successfully! Click Save Changes to complete."
                        : "Fingerprint captured successfully! Click Register to complete.";
                    FpCaptureProgress.Text = "3 of 3 captures \u2014 Complete";
                    _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;
                }
                else
                {
                    FpStatusLabel.Text = "Merge failed. Starting over \u2014 place finger again...";
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
    //  SAVE / REGISTER
    // ══════════════════════════════════════════════

    private async Task SavePatientAsync()
    {
        if (_mode == EnrollmentMode.Edit)
            await UpdateExistingPatient();
        else
            await RegisterNewPatient();
    }

    private async Task UpdateExistingPatient()
    {
        NextButton.IsEnabled = false;
        NextButton.Content = "Saving...";

        try
        {
            var patient = _editingPatient!;
            PopulatePatientFromForm(patient);

            // UpdatePatientAsync sets LastModified, ModifiedBy, ModifiedOn
            await _repository.UpdatePatientAsync(patient);

            // Add new face sample if captured
            if (_capturedEmbedding != null)
            {
                await _repository.AddFaceSampleAsync(
                    patient.IDCard, _capturedEmbedding, _capturedThumbnail, "front");
            }

            await SaveFingerprintIfCapturedAsync(patient.IDCard, consent: true);

            _fingerprint.FingerprintCaptured -= OnFingerprintForEnrollment;

            MessageBox.Show(
                $"Patient updated successfully!\n\nPID: {patient.IDCard}\nName: {patient.FullName}",
                "Update Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "Save Changes";
        }
    }

    private async Task RegisterNewPatient()
    {
        if (_capturedEmbedding == null)
        {
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
            var pid = await _pidService.GenerateNextPidAsync();

            var patient = new Patient
            {
                IDCard = pid,
                Site = _pidService.SiteCode,
                AdmissionDate = DateTime.UtcNow,
                CreatedBy = Environment.UserName,
                CreatedOn = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            PopulatePatientFromForm(patient);

            Patient savedPatient;

            if (_capturedEmbedding != null)
            {
                savedPatient = await _repository.RegisterPatientAsync(patient, _capturedEmbedding, _capturedThumbnail);
            }
            else
            {
                // Register without face (remark selected)
                var remarkText = FaceRemarkCombo.SelectedItem?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(remarkText))
                {
                    patient.Note = string.IsNullOrEmpty(patient.Note)
                        ? $"[Face] {remarkText}"
                        : $"{patient.Note}\n[Face] {remarkText}";
                }

                savedPatient = await _repository.RegisterPatientAsync(patient);
            }

            bool consent = ConsentCheckBox.IsChecked == true;
            await SaveFingerprintIfCapturedAsync(savedPatient.IDCard, consent);

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

    /// <summary>
    /// Populate a Patient entity from the current form field values.
    /// Used by both Create and Edit paths.
    /// </summary>
    private void PopulatePatientFromForm(Patient patient)
    {
        patient.FullName = FullNameInput.Text.Trim();
        patient.Sex = SexMale.IsChecked == true ? (byte)1 : (byte)2;

        // Names
        patient.BurmeseName = NullIfEmpty(BurmeseNameInput.Text);
        patient.KarenName = NullIfEmpty(KarenNameInput.Text);

        // DOB
        if (short.TryParse(DOBYearInput.Text.Trim(), out short year))
            patient.DOB_year = year;
        else
            patient.DOB_year = null;

        patient.DOB_month = DOBMonthCombo.SelectedIndex == 0 ? (short)-1 : (short)DOBMonthCombo.SelectedIndex;
        patient.DOB_day = DOBDayCombo.SelectedIndex == 0 ? (short)-1 : (short)DOBDayCombo.SelectedIndex;

        // Calculate age (y, m, d) at current time
        if (patient.DOB_year.HasValue && patient.DOB_year > 0)
        {
            var now = DateTime.UtcNow;
            short dobMonth = patient.DOB_month > 0 ? patient.DOB_month.Value : (short)1;
            short dobDay = patient.DOB_day > 0 ? patient.DOB_day.Value : (short)1;

            var parts = CalculateAgeParts(patient.DOB_year.Value, dobMonth, dobDay, now);
            if (parts != null)
            {
                patient.Age = (byte)Math.Clamp(parts.Value.Years, 0, 255);
                patient.Month = (byte)Math.Clamp(parts.Value.Months, 0, 255);
                patient.Day = (byte)Math.Clamp(parts.Value.Days, 0, 255);
            }
            else
            {
                patient.Age = (byte)Math.Clamp(now.Year - patient.DOB_year.Value, 0, 255);
                patient.Month = 0;
                patient.Day = 0;
            }
        }

        // Family
        patient.MotherPID = NullIfEmpty(MotherPIDInput.Text);
        patient.MotherName = NullIfEmpty(MotherNameInput.Text);
        patient.FatherName = NullIfEmpty(FatherNameInput.Text);
        patient.SpouseName = NullIfEmpty(SpouseNameInput.Text);

        // Contact & Address
        patient.PhoneNumber = NullIfEmpty(PhoneNumberInput.Text);
        patient.AddressCode = NullIfEmpty(AddressCodeInput.Text);
        patient.AddressOther = NullIfEmpty(AddressOtherInput.Text);
        patient.Note = NullIfEmpty(NotesInput.Text);
    }

    /// <summary>
    /// Save fingerprint template or remark if captured. Shared by Create and Edit paths.
    /// </summary>
    private async Task SaveFingerprintIfCapturedAsync(string pid, bool consent)
    {
        if (_capturedFingerprintTemplate != null)
        {
            await _repository.AddFingerprintTemplateAsync(
                pid, _selectedFingerType, _capturedFingerprintTemplate, consent);
        }
        else if (FingerprintRemarkCombo.SelectedIndex > 0)
        {
            var fpRemarkText = FingerprintRemarkCombo.SelectedItem?.ToString() ?? "";
            await _repository.AddFingerprintTemplateAsync(
                pid, _selectedFingerType, template: null, consent, remark: fpRemarkText);
        }
    }

    private static string? NullIfEmpty(string text)
    {
        var trimmed = text.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
