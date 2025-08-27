using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace KeyboardBacklightForLenovo
{
    public partial class SettingsWindow : Window
    {
        private readonly bool _nightLightAvailable;
        private readonly WinForms.DateTimePicker _dtStart = new();
        private readonly WinForms.DateTimePicker _dtEnd = new();

        private TraySettings _model;

        public SettingsWindow(bool nightLightAvailable = false)
        {
            InitializeComponent();

            _nightLightAvailable = nightLightAvailable;
            BuildTimePickers();

            _model = TraySettingsStore.LoadOrDefaults();
            ApplyModelToUI();
            UpdateNightSpan();
        }

        private void BuildTimePickers()
        {
            foreach (var dt in new[] { _dtStart, _dtEnd })
            {
                dt.Format = WinForms.DateTimePickerFormat.Custom;
                dt.CustomFormat = "HH:mm";
                dt.ShowUpDown = true;     // spinner, no calendar
                dt.Width = 90;
                dt.ValueChanged += (_, __) => UpdateNightSpan();
            }

            hostStart.Child = _dtStart;
            hostEnd.Child = _dtEnd;
        }

        private void ApplyModelToUI()
        {
            // Levels
            rbDayOff.IsChecked = _model.DayLevel == 0;
            rbDayLow.IsChecked = _model.DayLevel == 1 || _model.DayLevel is < 0 or > 2;
            rbDayHigh.IsChecked = _model.DayLevel == 2;

            rbNightOff.IsChecked = _model.NightLevel == 0;
            rbNightLow.IsChecked = _model.NightLevel == 1;
            rbNightHigh.IsChecked = _model.NightLevel == 2 || _model.NightLevel is < 0 or > 2;

            // Operating mode
            rbModeNightLight.IsEnabled = _nightLightAvailable;
            if (_nightLightAvailable && _model.Mode == OperatingMode.NightLight)
            {
                rbModeNightLight.IsChecked = true;
            }
            else
            {
                rbModeTime.IsChecked = true; // force time-based when Night light not available
            }
            grpTime.IsEnabled = rbModeTime.IsChecked == true;

            // Times (base today, keep only time-of-day)
            var today = DateTime.Today;
            _dtStart.Value = today.Add(_model.DayStart);
            _dtEnd.Value = today.Add(_model.DayEnd);

            // Show/hide time panel depending on mode
            grpTime.Visibility = (rbModeTime.IsChecked == true)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ModeChanged(object sender, RoutedEventArgs e)
        {
            // Hide the time controls entirely when Night light is selected.
            bool timeBased = rbModeTime.IsChecked == true;
            grpTime.Visibility = timeBased ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateNightSpan()
        {
            var s = _dtStart.Value.TimeOfDay;
            var e = _dtEnd.Value.TimeOfDay;
            lblNightSpan.Text = $"Night: {e:hh\\:mm} -> {s:hh\\:mm}";
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            // Collect
            _model.DayLevel = rbDayOff.IsChecked == true ? 0 : rbDayLow.IsChecked == true ? 1 : 2;
            _model.NightLevel = rbNightOff.IsChecked == true ? 0 : rbNightLow.IsChecked == true ? 1 : 2;
            _model.Mode = (rbModeNightLight.IsChecked == true && _nightLightAvailable)
                                ? OperatingMode.NightLight
                                : OperatingMode.TimeBased;

            _model.DayStart = _dtStart.Value.TimeOfDay;
            _model.DayEnd = _dtEnd.Value.TimeOfDay;

            TraySettingsStore.Save(_model);
            DialogResult = true;
            Close();
        }
    }
}
