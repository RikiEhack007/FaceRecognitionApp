using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FaceRecApp.WPF.Controls;

/// <summary>
/// Horizontal step indicator for wizards.
/// Displays circles + labels for each step, connected by lines.
/// States: Active (blue), Complete (green + checkmark), Inactive (gray).
/// </summary>
public partial class StepIndicator : UserControl
{
    private string[] _stepNames = [];
    private int _currentStep = 1;
    private HashSet<int> _completedSteps = new();
    private HashSet<int> _skippedSteps = new();
    private bool _allowDirectNavigation;

    private readonly List<Border> _circles = new();
    private readonly List<TextBlock> _labels = new();
    private readonly List<Border> _connectors = new();
    private readonly List<TextBlock> _numberTexts = new();

    // Resolved from App.xaml resources (StepActive, StepComplete, StepInactive, TextPrimary, TextMuted)
    private SolidColorBrush ActiveBrush = null!;
    private SolidColorBrush CompleteBrush = null!;
    private SolidColorBrush InactiveBrush = null!;
    private SolidColorBrush TextDark = null!;
    private SolidColorBrush TextMutedBrush = null!;
    private bool _brushesResolved;

    public event Action<int>? StepNavigated;

    public StepIndicator()
    {
        InitializeComponent();
    }

    public string[] StepNames
    {
        get => _stepNames;
        set { _stepNames = value; Rebuild(); }
    }

    public int CurrentStep
    {
        get => _currentStep;
        set { _currentStep = value; UpdateVisuals(); }
    }

    public HashSet<int> CompletedSteps
    {
        get => _completedSteps;
        set { _completedSteps = value; UpdateVisuals(); }
    }

    public HashSet<int> SkippedSteps
    {
        get => _skippedSteps;
        set { _skippedSteps = value; UpdateVisuals(); }
    }

    public bool AllowDirectNavigation
    {
        get => _allowDirectNavigation;
        set { _allowDirectNavigation = value; UpdateVisuals(); }
    }

    private void ResolveBrushes()
    {
        if (_brushesResolved) return;
        var app = Application.Current;
        ActiveBrush = (SolidColorBrush)app.FindResource("StepActive");
        CompleteBrush = (SolidColorBrush)app.FindResource("StepComplete");
        InactiveBrush = (SolidColorBrush)app.FindResource("StepInactive");
        TextDark = (SolidColorBrush)app.FindResource("TextPrimary");
        TextMutedBrush = (SolidColorBrush)app.FindResource("TextMuted");
        _brushesResolved = true;
    }

    private void Rebuild()
    {
        ResolveBrushes();
        RootGrid.Children.Clear();
        RootGrid.ColumnDefinitions.Clear();
        _circles.Clear();
        _labels.Clear();
        _connectors.Clear();
        _numberTexts.Clear();

        if (_stepNames.Length == 0) return;

        for (int i = 0; i < _stepNames.Length; i++)
        {
            // Step column
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            // Circle
            var numberText = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var circle = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = InactiveBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = numberText,
            };

            int stepIndex = i + 1;
            circle.MouseLeftButtonDown += (_, _) =>
            {
                if (_allowDirectNavigation)
                    StepNavigated?.Invoke(stepIndex);
            };

            stack.Children.Add(circle);

            // Label
            var label = new TextBlock
            {
                Text = _stepNames[i],
                FontSize = 11,
                Foreground = TextMutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };
            stack.Children.Add(label);

            Grid.SetColumn(stack, RootGrid.ColumnDefinitions.Count - 1);
            RootGrid.Children.Add(stack);

            _circles.Add(circle);
            _labels.Add(label);
            _numberTexts.Add(numberText);

            // Connector (except after last step)
            if (i < _stepNames.Length - 1)
            {
                RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var connector = new Border
                {
                    Height = 2,
                    Background = InactiveBrush,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4, 14, 4, 0),
                };

                Grid.SetColumn(connector, RootGrid.ColumnDefinitions.Count - 1);
                RootGrid.Children.Add(connector);

                _connectors.Add(connector);
            }
        }

        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (!_brushesResolved || _circles.Count == 0) return;

        for (int i = 0; i < _circles.Count; i++)
        {
            int step = i + 1;
            bool isActive = step == _currentStep;
            bool isCompleted = _completedSteps.Contains(step);

            if (isActive)
            {
                _circles[i].Background = ActiveBrush;
                _numberTexts[i].Text = step.ToString();
                _numberTexts[i].Foreground = Brushes.White;
                _labels[i].Foreground = TextDark;
                _labels[i].FontWeight = FontWeights.SemiBold;
            }
            else if (isCompleted)
            {
                _circles[i].Background = CompleteBrush;
                _numberTexts[i].Text = "\u2713";
                _numberTexts[i].Foreground = Brushes.White;
                _labels[i].Foreground = CompleteBrush;
                _labels[i].FontWeight = FontWeights.Normal;
            }
            else
            {
                _circles[i].Background = InactiveBrush;
                _numberTexts[i].Text = step.ToString();
                _numberTexts[i].Foreground = Brushes.White;
                _labels[i].Foreground = TextMutedBrush;
                _labels[i].FontWeight = FontWeights.Normal;
            }

            _circles[i].Cursor = _allowDirectNavigation ? Cursors.Hand : Cursors.Arrow;
        }

        // Connector colors: colored if next step is reached
        for (int i = 0; i < _connectors.Count; i++)
        {
            int nextStep = i + 2;
            bool nextReached = _completedSteps.Contains(nextStep) || nextStep <= _currentStep;
            _connectors[i].Background = nextReached ? ActiveBrush : InactiveBrush;
        }
    }
}
