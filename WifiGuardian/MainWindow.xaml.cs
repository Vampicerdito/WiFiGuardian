using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Serialization;
using Windows.ApplicationModel;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;
using Windows.Networking.NetworkOperators;
using Windows.Security.Credentials;

namespace WifiGuardian
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient InternetClient = CreateHttpClient();
        private readonly string _settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WifiGuardian");
        private readonly string _settingsPath;
        private readonly string _statisticsPath;
        private AppSettings _settings;
        private readonly List<StatsSample> _statistics = new List<StatsSample>();
        private string _themeMode = "system";
        private long? _lastLatencyMs;
        private CancellationTokenSource _monitorCancellation;
        private Task _monitorTask;
        private bool _initializingControls;
        private bool _isMonitoring;
        private bool _useRegistryStartup;
        private bool _isDarkTheme;
        private bool _allowClose;
        private bool _hotspotManuallyDisabled;
        private bool _hotspotWasOn;
        private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "WiFiGuardian";

        public MainWindow()
        {
            InitializeComponent();
            _settingsPath = Path.Combine(_settingsFolder, "settings.xml");
            _statisticsPath = Path.Combine(_settingsFolder, "statistics.xml");
            ApplyTheme(IsSystemDarkTheme());
        }

        private static bool IsSystemDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            _themeMode = _isDarkTheme ? "light" : "dark";
            ApplyTheme(_themeMode == "dark");
            SaveThemePreference();
        }

        private void ApplyTheme(bool dark)
        {
            _isDarkTheme = dark;
            SetBrushColor("AppBackgroundBrush", dark ? "#171C1A" : "#F3F6F5");
            SetBrushColor("SidebarBackgroundBrush", dark ? "#202724" : "#FBFCFB");
            SetBrushColor("CardBackgroundBrush", dark ? "#242B28" : "#FFFFFF");
            SetBrushColor("PrimaryTextBrush", dark ? "#EDF4F0" : "#172B25");
            SetBrushColor("SecondaryTextBrush", dark ? "#B2C0B9" : "#687871");
            SetBrushColor("InputBackgroundBrush", dark ? "#303936" : "#F7F9F8");
            SetBrushColor("InputForegroundBrush", dark ? "#F1F5F3" : "#20312B");
            SetBrushColor("LogBackgroundBrush", dark ? "#1C2220" : "#F7F9F8");
            SetBrushColor("LogBorderBrush", dark ? "#414B46" : "#DDE6E1");
            SetBrushColor("AccentBrush", dark ? "#36866A" : "#147D5A");
            SetBrushColor("AccentSoftBrush", dark ? "#294A3C" : "#E4F4EC");
            SetBrushColor("DangerBrush", dark ? "#FF8A8A" : "#B93838");
            SetBrushColor("ControlBorderBrush", dark ? "#46534D" : "#DDE6E1");
            if (ThemeButton != null)
                ThemeButton.Content = dark ? "☀ Tema claro" : "☾ Tema oscuro";
            UpdateNavigationVisuals();
            DrawStatisticsChart();
        }

        private void ThemeSystemButton_Click(object sender, RoutedEventArgs e)
        {
            _themeMode = "system";
            ApplyTheme(IsSystemDarkTheme());
            SaveThemePreference();
        }

        private void ThemeLightButton_Click(object sender, RoutedEventArgs e)
        {
            _themeMode = "light";
            ApplyTheme(false);
            SaveThemePreference();
        }

        private void ThemeDarkButton_Click(object sender, RoutedEventArgs e)
        {
            _themeMode = "dark";
            ApplyTheme(true);
            SaveThemePreference();
        }

        private void SaveThemePreference()
        {
            try
            {
                if (_settings == null)
                    _settings = new AppSettings { DiagnosticIp = "1.1.1.1", IntervalSeconds = 15 };
                _settings.ThemeMode = _themeMode;
                Directory.CreateDirectory(_settingsFolder);
                using (var stream = File.Create(_settingsPath))
                    new XmlSerializer(typeof(AppSettings)).Serialize(stream, _settings);
            }
            catch (Exception ex) { Log("No se pudo guardar la preferencia de tema: " + ex.Message); }
        }

        private void NavConnectionButton_Click(object sender, RoutedEventArgs e) { ShowPage("connection"); }
        private void NavStatsButton_Click(object sender, RoutedEventArgs e) { ShowPage("statistics"); }
        private void NavSettingsButton_Click(object sender, RoutedEventArgs e) { ShowPage("settings"); }

        private void ShowPage(string page)
        {
            PageConnection.Visibility = page == "connection" ? Visibility.Visible : Visibility.Collapsed;
            PageStatistics.Visibility = page == "statistics" ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            PageTitleText.Text = page == "connection" ? "Conexión" : page == "statistics" ? "Estadísticas" : "Configuración";
            PageSubtitleText.Text = page == "connection" ? "Supervisión y recuperación de la conexión" :
                page == "statistics" ? "Historial de disponibilidad y latencia" : "Preferencias, diagnóstico y mantenimiento";
            UpdateNavigationVisuals();
            if (page == "statistics") RefreshStatisticsView();
        }

        private void UpdateNavigationVisuals()
        {
            if (NavConnectionButton == null) return;
            SetNavigationButton(NavConnectionButton, PageConnection.Visibility == Visibility.Visible);
            SetNavigationButton(NavStatsButton, PageStatistics.Visibility == Visibility.Visible);
            SetNavigationButton(NavSettingsButton, PageSettings.Visibility == Visibility.Visible);
        }

        private void SetNavigationButton(Button button, bool selected)
        {
            button.Background = selected ? (System.Windows.Media.Brush)FindResource("AccentSoftBrush") : System.Windows.Media.Brushes.Transparent;
            button.Foreground = selected ? (System.Windows.Media.Brush)FindResource("AccentBrush") :
                (System.Windows.Media.Brush)FindResource("PrimaryTextBrush");
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettings config;
            if (!TryReadSettings(out config)) return;
            try
            {
                config.ThemeMode = _themeMode;
                Directory.CreateDirectory(_settingsFolder);
                using (var stream = File.Create(_settingsPath))
                    new XmlSerializer(typeof(AppSettings)).Serialize(stream, config);
                _settings = config;
                Log("Parámetros guardados correctamente.");
                MessageBox.Show("Los parámetros se guardaron correctamente.", "WiFi Guardian",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudieron guardar los parámetros.\n\n" + ex.Message,
                    "WiFi Guardian", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadStatistics()
        {
            try
            {
                if (!File.Exists(_statisticsPath)) return;
                using (var stream = File.OpenRead(_statisticsPath))
                {
                    var loaded = (List<StatsSample>)new XmlSerializer(typeof(List<StatsSample>)).Deserialize(stream);
                    _statistics.Clear();
                    if (loaded != null) _statistics.AddRange(loaded.OrderBy(s => s.Timestamp).ToList().TakeLastCompatible(2000));
                }
            }
            catch (Exception ex) { Log("No se pudo cargar el historial estadístico: " + ex.Message); }
            RefreshStatisticsView();
        }

        private void SaveStatistics()
        {
            try
            {
                Directory.CreateDirectory(_settingsFolder);
                var samples = _statistics.Count > 2000 ? _statistics.Skip(_statistics.Count - 2000).ToList() : _statistics.ToList();
                using (var stream = File.Create(_statisticsPath))
                    new XmlSerializer(typeof(List<StatsSample>)).Serialize(stream, samples);
            }
            catch (Exception ex) { Log("No se pudo guardar el historial estadístico: " + ex.Message); }
        }

        private void AddStatisticsSample(bool internetOk, long? latencyMs)
        {
            _statistics.Add(new StatsSample { Timestamp = DateTime.Now, InternetOk = internetOk, LatencyMs = latencyMs });
            if (_statistics.Count > 2000) _statistics.RemoveRange(0, _statistics.Count - 2000);
            SaveStatistics();
            RefreshStatisticsView();
        }

        private void RefreshStatisticsView()
        {
            if (StatsAvailabilityText == null) return;
            var samples = _statistics.ToList();
            if (samples.Count == 0)
            {
                StatsAvailabilityText.Text = "Sin datos";
                StatsLatencyText.Text = "—";
                StatsSamplesText.Text = "0";
                StatsHistoryText.Text = "Todavía no hay mediciones. Inicia la vigilancia para comenzar a recopilar datos.";
                DrawStatisticsChart();
                return;
            }
            StatsAvailabilityText.Text = (100.0 * samples.Count(s => s.InternetOk) / samples.Count).ToString("0.0") + "%";
            var recentLatency = samples.Where(s => s.LatencyMs.HasValue).ToList().TakeLastCompatible(20).Select(s => s.LatencyMs.Value).ToList();
            StatsLatencyText.Text = recentLatency.Count == 0 ? "—" : recentLatency.Average().ToString("0") + " ms";
            StatsSamplesText.Text = samples.Count.ToString();
            var historyRows = samples.AsEnumerable().Reverse().Take(12).ToList();
            var historyHeader = string.Format("{0,-19}  {1,-20}  {2,10}", "HORA", "INTERNET", "LATENCIA");
            var historyRule = new string('─', 54);
            StatsHistoryText.Text = historyHeader + Environment.NewLine + historyRule + Environment.NewLine +
                string.Join(Environment.NewLine, historyRows.Select(s => string.Format("{0,-19}  {1,-20}  {2,10}",
                    s.Timestamp.ToString("MM-dd HH:mm:ss"),
                    s.InternetOk ? "Disponible" : "Sin respuesta",
                    s.LatencyMs.HasValue ? s.LatencyMs.Value + " ms" : "—")));
            DrawStatisticsChart();
        }

        private void DrawStatisticsChart()
        {
            if (StatsChartCanvas == null) return;
            StatsChartCanvas.Children.Clear();
            var values = _statistics.Where(s => s.LatencyMs.HasValue).ToList().TakeLastCompatible(40);
            var width = StatsChartCanvas.ActualWidth;
            var height = StatsChartCanvas.ActualHeight;
            if (width < 80 || height < 60 || values.Count == 0)
            {
                var message = new TextBlock
                {
                    Text = values.Count == 0 ? "Las mediciones de latencia aparecerán aquí cuando comience la vigilancia." : "",
                    Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Width = Math.Max(80, width - 24)
                };
                Canvas.SetLeft(message, 12);
                Canvas.SetTop(message, Math.Max(12, height / 2 - 18));
                StatsChartCanvas.Children.Add(message);
                return;
            }
            var max = Math.Max(10, values.Max(v => (double)v.LatencyMs.Value) * 1.2);
            const double left = 12, top = 12;
            var plotWidth = Math.Max(1, width - 24);
            var plotHeight = Math.Max(1, height - 28);
            for (var i = 0; i < 4; i++)
            {
                var y = top + plotHeight * i / 3.0;
                StatsChartCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y,
                    Stroke = (System.Windows.Media.Brush)FindResource("ControlBorderBrush"),
                    StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 3 }
                });
            }
            var line = new System.Windows.Shapes.Polyline
            {
                Stroke = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                StrokeThickness = 2.5,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
            };
            for (var i = 0; i < values.Count; i++)
            {
                var x = left + (values.Count == 1 ? plotWidth / 2 : plotWidth * i / (values.Count - 1));
                var y = top + plotHeight - (values[i].LatencyMs.Value / max * plotHeight);
                line.Points.Add(new System.Windows.Point(x, y));
            }
            StatsChartCanvas.Children.Add(line);
        }

        private void StatsChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { DrawStatisticsChart(); }

        private void ClearStatisticsButton_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show("Se eliminará el historial de estadísticas guardado en este equipo. Esta acción no se puede deshacer. ¿Continuar?",
                "Borrar estadísticas", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _statistics.Clear();
            SaveStatistics();
            RefreshStatisticsView();
            Log("Historial de estadísticas borrado.");
        }

        private void SetBrushColor(string key, string color)
        {
            // WPF may freeze brushes stored in resources. Replace the resource instead of
            // mutating its Color, which can throw InvalidOperationException and close the app.
            var parsedColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
            Resources[key] = new System.Windows.Media.SolidColorBrush(parsedColor);
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                "Se borrarán la configuración guardada, la contraseña cifrada y la preferencia de inicio con Windows. La aplicación volverá a abrirse como nueva. ¿Continuar?",
                "Restablecer WiFi Guardian",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            ResetButton.IsEnabled = false;
            try
            {
                await StopMonitoringAsync(false);

                // Remove app settings, including the DPAPI-protected Wi-Fi password.
                if (Directory.Exists(_settingsFolder))
                    Directory.Delete(_settingsFolder, true);

                // Remove the desktop startup entry if this build uses the registry fallback.
                using (var key = Registry.CurrentUser.OpenSubKey(StartupRunKey, true))
                    key?.DeleteValue(StartupValueName, false);

                // For MSIX builds, also disable the registered startup task when available.
                try
                {
                    var startupTask = await StartupTask.GetAsync("WifiGuardianStartup");
                    startupTask.Disable();
                }
                catch
                {
                    // Expected for unpackaged/desktop runs.
                }

                _initializingControls = true;
                _settings = null;
                _statistics.Clear();
                if (File.Exists(_statisticsPath)) File.Delete(_statisticsPath);
                RefreshStatisticsView();
                _themeMode = "system";
                ApplyTheme(IsSystemDarkTheme());
                IpBox.Text = "1.1.1.1";
                SsidBox.Clear();
                PasswordBox.Clear();
                IntervalBox.Text = "15";
                StartupCheckBox.IsChecked = false;
                _initializingControls = false;
                WifiStatusText.Text = "Esperando configuración";
                IpStatusText.Text = "Sin comprobar";
                InternetStatusText.Text = "Sin comprobar";
                HotspotStatusText.Text = "Sin comprobar";
                LogBox.Clear();
                RunStatusText.Text = "DETENIDO";
                RunStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
                RunStatusDot.Fill = System.Windows.Media.Brushes.Orange;
                ConnectionHeroText.Text = "Esperando configuración";
                StartButton.Content = "Guardar y comenzar";
                Log("Restablecimiento completado. No quedan datos de configuración guardados por la aplicación.");
                MessageBox.Show("La configuración local se ha borrado. WiFi Guardian está listo para configurarse desde cero.",
                    "Restablecimiento completado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _initializingControls = false;
                Log("No se pudieron borrar todos los datos: " + ex.Message);
                MessageBox.Show("No se pudieron borrar todos los datos de la aplicación.\\n\\n" + ex.Message,
                    "Error al restablecer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ResetButton.IsEnabled = true;
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return client;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _initializingControls = true;
            LoadSettingsIntoForm();
            _themeMode = _settings == null || string.IsNullOrWhiteSpace(_settings.ThemeMode) ? "system" : _settings.ThemeMode;
            ApplyTheme(_themeMode == "dark" || (_themeMode == "system" && IsSystemDarkTheme()));
            LoadStatistics();
            await LoadStartupStatusAsync();
            _initializingControls = false;

            if (_settings != null && !string.IsNullOrWhiteSpace(_settings.Ssid))
            {
                await StartMonitoringAsync();
            }
            else
            {
                Log("Introduce el SSID, la contraseña y la IP de diagnóstico; después pulsa Guardar y comenzar.");
            }
        }

        private void LoadSettingsIntoForm()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    _settings = null;
                    return;
                }

                using (var stream = File.OpenRead(_settingsPath))
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    _settings = (AppSettings)serializer.Deserialize(stream);
                }

                IpBox.Text = string.IsNullOrWhiteSpace(_settings.DiagnosticIp) ? "1.1.1.1" : _settings.DiagnosticIp;
                SsidBox.Text = _settings.Ssid ?? string.Empty;
                IntervalBox.Text = Math.Max(5, _settings.IntervalSeconds).ToString();
                _themeMode = string.IsNullOrWhiteSpace(_settings.ThemeMode) ? "system" : _settings.ThemeMode;

                if (!string.IsNullOrWhiteSpace(_settings.ProtectedPassword))
                {
                    try
                    {
                        var encrypted = Convert.FromBase64String(_settings.ProtectedPassword);
                        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                        PasswordBox.Password = Encoding.UTF8.GetString(plain);
                    }
                    catch (Exception ex)
                    {
                        Log("No se pudo descifrar la contraseña guardada. Vuelve a escribirla. " + ex.Message);
                        PasswordBox.Password = string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                _settings = null;
                Log("No se pudo leer la configuración guardada: " + ex.Message);
            }
        }

        private async Task LoadStartupStatusAsync()
        {
            try
            {
                var startupTask = await StartupTask.GetAsync("WifiGuardianStartup");
                _useRegistryStartup = false;
                StartupCheckBox.IsChecked = startupTask.State == StartupTaskState.Enabled ||
                                            startupTask.State == StartupTaskState.EnabledByPolicy;
            }
            catch
            {
                // Al ejecutar sin empaquetado MSIX, la API StartupTask no está disponible.
                // En ese caso se usa la clave Run del usuario actual.
                _useRegistryStartup = true;
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(StartupRunKey, false))
                    {
                        var value = key == null ? null : key.GetValue(StartupValueName) as string;
                        StartupCheckBox.IsChecked = !string.IsNullOrWhiteSpace(value);
                    }
                }
                catch (Exception ex)
                {
                    StartupCheckBox.IsChecked = false;
                    StartupCheckBox.IsEnabled = false;
                    Log("No se pudo leer la configuración de inicio de Windows: " + ex.Message);
                }
            }
        }

        private async void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializingControls || !StartupCheckBox.IsEnabled)
                return;

            try
            {
                if (!_useRegistryStartup)
                {
                    var startupTask = await StartupTask.GetAsync("WifiGuardianStartup");
                    if (StartupCheckBox.IsChecked == true)
                    {
                        var state = await startupTask.RequestEnableAsync();
                        if (state == StartupTaskState.Enabled || state == StartupTaskState.EnabledByPolicy)
                            Log("Inicio con Windows activado.");
                        else
                        {
                            _initializingControls = true;
                            StartupCheckBox.IsChecked = false;
                            _initializingControls = false;
                            Log("Windows no permitió activar el inicio automático. Comprueba Configuración > Aplicaciones > Inicio.");
                        }
                    }
                    else
                    {
                        startupTask.Disable();
                        Log("Inicio con Windows desactivado.");
                    }
                    return;
                }

                if (StartupCheckBox.IsChecked == true)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    using (var key = Registry.CurrentUser.CreateSubKey(StartupRunKey))
                    {
                        if (key == null)
                            throw new InvalidOperationException("Windows no permitió crear la clave de inicio.");
                        key.SetValue(StartupValueName, "\"" + exePath + "\"");
                    }
                    Log("Inicio con Windows activado para el usuario actual.");
                }
                else
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(StartupRunKey, true))
                        key?.DeleteValue(StartupValueName, false);
                    Log("Inicio con Windows desactivado.");
                }
            }
            catch (Exception ex)
            {
                _initializingControls = true;
                StartupCheckBox.IsChecked = !StartupCheckBox.IsChecked;
                _initializingControls = false;
                Log("No se pudo cambiar el inicio automático: " + ex.Message);
                MessageBox.Show("No se pudo cambiar el inicio con Windows.\n\n" + ex.Message,
                    "WiFi Guardian", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettings config;
            if (!TryReadSettings(out config))
                return;

            try
            {
                Directory.CreateDirectory(_settingsFolder);
                var serializer = new XmlSerializer(typeof(AppSettings));
                using (var stream = File.Create(_settingsPath))
                    serializer.Serialize(stream, config);

                _settings = config;
                Log("Configuración guardada. Comenzando la vigilancia.");
                await StartMonitoringAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo guardar la configuración.\n\n" + ex.Message,
                    "WiFi Guardian", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool TryReadSettings(out AppSettings config)
        {
            config = null;
            var ipText = IpBox.Text.Trim();
            IPAddress parsedIp;
            if (!IPAddress.TryParse(ipText, out parsedIp))
            {
                MessageBox.Show("Escribe una dirección IP válida, por ejemplo 1.1.1.1.",
                    "IP no válida", MessageBoxButton.OK, MessageBoxImage.Warning);
                IpBox.Focus();
                return false;
            }

            var ssid = SsidBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ssid))
            {
                MessageBox.Show("Escribe el nombre SSID de la Wi-Fi.",
                    "Falta el SSID", MessageBoxButton.OK, MessageBoxImage.Warning);
                SsidBox.Focus();
                return false;
            }

            int interval;
            if (!int.TryParse(IntervalBox.Text.Trim(), out interval) || interval < 5 || interval > 3600)
            {
                MessageBox.Show("El intervalo debe ser un número entre 5 y 3600 segundos.",
                    "Intervalo no válido", MessageBoxButton.OK, MessageBoxImage.Warning);
                IntervalBox.Focus();
                return false;
            }

            try
            {
                var passwordBytes = Encoding.UTF8.GetBytes(PasswordBox.Password ?? string.Empty);
                var encrypted = ProtectedData.Protect(passwordBytes, null, DataProtectionScope.CurrentUser);
                config = new AppSettings
                {
                    DiagnosticIp = ipText,
                    Ssid = ssid,
                    ProtectedPassword = Convert.ToBase64String(encrypted),
                    IntervalSeconds = interval,
                    ThemeMode = _themeMode
                };
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo proteger la contraseña: " + ex.Message,
                    "Error de seguridad", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task StartMonitoringAsync()
        {
            if (_settings == null || string.IsNullOrWhiteSpace(_settings.Ssid))
                return;

            // Wait for any in-flight Windows tethering call to finish before starting another monitor.
            await StopMonitoringAsync(false);
            _hotspotManuallyDisabled = false;
            _hotspotWasOn = false;
            _monitorCancellation = new CancellationTokenSource();
            _isMonitoring = true;
            StartButton.IsEnabled = true;
            StartButton.Content = "Guardar y reiniciar vigilancia";
            StopButton.IsEnabled = true;
            RunStatusText.Text = "EN EJECUCIÓN";
            RunStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            RunStatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
            ConnectionHeroText.Text = "Vigilancia activa · comprobaciones periódicas";
            Log("Vigilancia iniciada. Intervalo: " + Math.Max(5, _settings.IntervalSeconds) + " s.");
            _monitorTask = MonitorLoopAsync(_monitorCancellation.Token);
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await StopMonitoringAsync(true);
        }

        private async Task StopMonitoringAsync(bool addLog)
        {
            var cancellation = _monitorCancellation;
            var runningTask = _monitorTask;

            if (cancellation != null)
                cancellation.Cancel();

            // Do not dispose the token source or start a new monitor while a Windows API call is still pending.
            if (runningTask != null)
            {
                try
                {
                    await runningTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log("La vigilancia terminó con un error durante la detención: " + ex.Message);
                }
            }

            if (ReferenceEquals(_monitorCancellation, cancellation))
            {
                _monitorCancellation = null;
                _monitorTask = null;
            }

            cancellation?.Dispose();
            StopButton.IsEnabled = false;
            if (_isMonitoring)
            {
                _isMonitoring = false;
                RunStatusText.Text = "DETENIDO";
                RunStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
                RunStatusDot.Fill = System.Windows.Media.Brushes.Orange;
                ConnectionHeroText.Text = "Vigilancia detenida";
            }

            if (addLog)
                Log("Vigilancia detenida. Se esperó a que terminara cualquier operación en curso; el hotspot no se apaga automáticamente.");
        }

        private async Task MonitorLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Log("Iniciando ciclo de comprobación.");
                    await RunCheckCycleAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log("Error inesperado en la revisión: " + ex.Message);
                }

                try
                {
                    Log("Ciclo finalizado. Próxima revisión en " + Math.Max(5, _settings.IntervalSeconds) + " s.");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _settings.IntervalSeconds)), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunCheckCycleAsync(CancellationToken token)
        {
            SetStatus(WifiStatusText, "Comprobando conexión…");
            var wifiOk = await EnsureWifiConnectionAsync(token);
            if (!wifiOk)
            {
                SetStatus(InternetStatusText, "No comprobado: falta la Wi-Fi configurada");
                SetStatus(HotspotStatusText, "No se activa sin una conexión válida");
                AddStatisticsSample(false, null);
                SetStatus(ConnectionHeroText, "Wi-Fi no disponible · esperando recuperación");
                return;
            }

            SetStatus(WifiStatusText, "Conectado a " + _settings.Ssid);
            _lastLatencyMs = await CheckDiagnosticIpAsync(token);

            var internetOk = await HasInternetAsync(token);
            SetStatus(InternetStatusText, internetOk ? "Disponible" : "Sin acceso confirmado");
            AddStatisticsSample(internetOk, _lastLatencyMs);
            SetStatus(ConnectionHeroText, internetOk ? "Internet disponible · comprobación correcta" : "Wi-Fi conectada · Internet no confirmado");

            if (!internetOk)
                Log("La Wi-Fi está conectada, pero la prueba HTTP de Internet no ha respondido correctamente.");

            // Siempre consultar el estado del hotspot. Solo se permite encenderlo cuando Internet se confirma.
            await EnsureHotspotOnAsync(token, internetOk);
        }

        private async Task<bool> EnsureWifiConnectionAsync(CancellationToken token)
        {
            var adapters = (await WiFiAdapter.FindAllAdaptersAsync()).ToList();
            if (adapters.Count == 0)
            {
                SetStatus(WifiStatusText, "No se detecta un adaptador Wi-Fi compatible");
                Log("No hay adaptadores disponibles para WiFiControl.");
                return false;
            }

            WiFiAdapter selected = null;
            WiFiAvailableNetwork network = null;

            // WiFiAdapter no expone GetConnectedProfileAsync en el contrato WinRT usado aquí.
            // Consulta los perfiles de conexión de Windows para evitar reconectar si el SSID ya es correcto.
            token.ThrowIfCancellationRequested();
            var connectedProfile = WinRtNetworkInformation.GetConnectionProfiles().FirstOrDefault(p =>
                p.IsWlanConnectionProfile &&
                string.Equals(p.ProfileName, _settings.Ssid, StringComparison.Ordinal));

            if (connectedProfile != null)
                return true;

            // Si no está conectado al SSID deseado, busca la red en todos los adaptadores disponibles.
            foreach (var adapter in adapters)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    SetStatus(WifiStatusText, "Buscando " + _settings.Ssid + "…");
                    await adapter.ScanAsync();
                    var candidate = adapter.NetworkReport.AvailableNetworks.FirstOrDefault(n =>
                        string.Equals(n.Ssid, _settings.Ssid, StringComparison.Ordinal));
                    if (candidate != null)
                    {
                        selected = adapter;
                        network = candidate;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log("No se pudo explorar un adaptador Wi-Fi: " + ex.Message);
                }
            }

            if (selected == null || network == null)
            {
                SetStatus(WifiStatusText, "SSID no encontrado: " + _settings.Ssid);
                Log("No se encontró la Wi-Fi configurada. Se volverá a intentar en el próximo ciclo.");
                return false;
            }

            SetStatus(WifiStatusText, "Conectando a " + _settings.Ssid + "…");
            WiFiConnectionResult result;
            var plainPassword = GetPlainPassword();
            if (string.IsNullOrEmpty(plainPassword))
            {
                result = await selected.ConnectAsync(network, WiFiReconnectionKind.Automatic);
            }
            else
            {
                var credential = new PasswordCredential { Password = plainPassword };
                result = await selected.ConnectAsync(network, WiFiReconnectionKind.Automatic, credential);
            }

            if (result.ConnectionStatus != WiFiConnectionStatus.Success)
            {
                SetStatus(WifiStatusText, "No se pudo conectar: " + result.ConnectionStatus);
                Log("Falló la conexión a la Wi-Fi: " + result.ConnectionStatus);
                return false;
            }

            Log("Conexión Wi-Fi solicitada correctamente.");
            await Task.Delay(2500, token);
            return true;
        }

        private string GetPlainPassword()
        {
            if (_settings == null || string.IsNullOrWhiteSpace(_settings.ProtectedPassword))
                return string.Empty;

            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(_settings.ProtectedPassword), null,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<long?> CheckDiagnosticIpAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(_settings.DiagnosticIp, 1200);
                    if (reply.Status == IPStatus.Success)
                    {
                        SetStatus(IpStatusText, "Responde (" + reply.RoundtripTime + " ms)");
                        return reply.RoundtripTime;
                    }
                    SetStatus(IpStatusText, "Sin respuesta ICMP (" + reply.Status + ")");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SetStatus(IpStatusText, "No se pudo comprobar: " + ex.Message); }
            return null;
        }

        private async Task<bool> HasInternetAsync(CancellationToken token)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get,
                    "http://www.msftconnecttest.com/connecttest.txt"))
                {
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true,
                        NoStore = true
                    };
                    using (var response = await InternetClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, token))
                    {
                        if (!response.IsSuccessStatusCode)
                            return false;

                        var content = (await response.Content.ReadAsStringAsync()).Trim();
                        return string.Equals(content, "Microsoft Connect Test", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private async Task EnsureHotspotOnAsync(CancellationToken token, bool allowStart)
        {
            token.ThrowIfCancellationRequested();
            if (_hotspotManuallyDisabled)
            {
                SetStatus(HotspotStatusText, "Apagado manualmente. No se reactivará hasta reiniciar la vigilancia.");
                return;
            }

            try
            {
                var profiles = WinRtNetworkInformation.GetConnectionProfiles();
                var sourceProfile = profiles.FirstOrDefault(p => p.IsWlanConnectionProfile &&
                    string.Equals(p.ProfileName, _settings.Ssid, StringComparison.Ordinal));

                if (sourceProfile == null)
                    sourceProfile = WinRtNetworkInformation.GetInternetConnectionProfile();

                if (sourceProfile == null)
                {
                    SetStatus(HotspotStatusText, "No se detecta un perfil de conexión para compartir");
                    Log("No se detecta un perfil de conexión para consultar el hotspot.");
                    return;
                }

                var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(sourceProfile);
                var state = manager.TetheringOperationalState;

                // Si Windows o el usuario están apagando/encendiendo el hotspot, no iniciar otra operación en paralelo.
                if (state == TetheringOperationalState.InTransition)
                {
                    SetStatus(HotspotStatusText, "Windows está terminando el cambio del hotspot…");
                    Log("El hotspot está en transición. Esperando a que Windows termine antes de enviar otra orden.");
                    for (var attempt = 0; attempt < 10 && state == TetheringOperationalState.InTransition; attempt++)
                    {
                        await Task.Delay(1000, token);
                        token.ThrowIfCancellationRequested();
                        state = manager.TetheringOperationalState;
                    }

                    if (state == TetheringOperationalState.InTransition)
                    {
                        SetStatus(HotspotStatusText, "Windows aún no termina el cambio del hotspot");
                        Log("El hotspot sigue en transición después de 10 segundos. No se forzará otra operación; se comprobará en el siguiente ciclo.");
                        return;
                    }

                    if (state == TetheringOperationalState.Off)
                    {
                        _hotspotManuallyDisabled = true;
                        SetStatus(HotspotStatusText, "Apagado manualmente");
                        Log("El hotspot terminó de apagarse durante una transición. Se respetará la decisión y no se volverá a activar automáticamente en esta sesión de vigilancia.");
                        return;
                    }
                }

                if (state == TetheringOperationalState.On)
                {
                    _hotspotWasOn = true;
                    SetStatus(HotspotStatusText, "Activo");
                    return;
                }

                // If the hotspot was active and is now off, respect the change instead of fighting Windows.
                if (state == TetheringOperationalState.Off && _hotspotWasOn)
                {
                    _hotspotManuallyDisabled = true;
                    SetStatus(HotspotStatusText, "Apagado manualmente");
                    Log("El hotspot pasó de activo a apagado. No se volverá a activar automáticamente durante esta sesión; reinicia la vigilancia para permitirlo de nuevo.");
                    return;
                }

                if (!allowStart)
                {
                    SetStatus(HotspotStatusText, "Apagado (no se activa sin Internet confirmado)");
                    return;
                }

                token.ThrowIfCancellationRequested();
                SetStatus(HotspotStatusText, "Activando punto de acceso…");
                Log("Internet disponible y hotspot apagado. Solicitando activación a Windows.");
                var result = await manager.StartTetheringAsync();
                token.ThrowIfCancellationRequested();

                if (result.Status == TetheringOperationStatus.Success)
                {
                    SetStatus(HotspotStatusText, "Activado correctamente");
                    Log("Punto de acceso móvil activado.");
                }
                else
                {
                    var detail = string.IsNullOrWhiteSpace(result.AdditionalErrorMessage)
                        ? result.Status.ToString()
                        : result.Status + " · " + result.AdditionalErrorMessage;
                    SetStatus(HotspotStatusText, "Windows no pudo activarlo: " + detail);
                    Log("No se pudo activar el hotspot: " + detail);
                }
            }
            catch (OperationCanceledException)
            {
                // Detener la vigilancia no debe lanzar otra operación sobre el hotspot.
                throw;
            }
            catch (Exception ex)
            {
                SetStatus(HotspotStatusText, "Error al consultar/activar: " + ex.Message);
                Log("Error al usar la API del punto de acceso móvil: " + ex.Message);
            }
        }

        private void SetStatus(TextBlock target, string text)
        {
            if (Dispatcher.CheckAccess())
                target.Text = text;
            else
                Dispatcher.BeginInvoke(new Action(() => target.Text = text), DispatcherPriority.Background);
        }

        private void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            if (Dispatcher.CheckAccess())
                AppendLogLine(line);
            else
                Dispatcher.BeginInvoke(new Action(() => AppendLogLine(line)), DispatcherPriority.Background);
        }

        private void AppendLogLine(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.CaretIndex = LogBox.Text.Length;
            LogBox.ScrollToEnd();
            var lines = LogBox.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            if (lines.Length > 180)
                LogBox.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - 160));
            LogBox.CaretIndex = LogBox.Text.Length;
            LogBox.ScrollToEnd();
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_allowClose)
                return;

            e.Cancel = true;
            await StopMonitoringAsync(false);
            _allowClose = true;
            Close();
        }

    }

    public class AppSettings
    {
        public string DiagnosticIp { get; set; }
        public string Ssid { get; set; }
        public string ProtectedPassword { get; set; }
        public int IntervalSeconds { get; set; }
        public string ThemeMode { get; set; }
    }

    public class StatsSample
    {
        public DateTime Timestamp { get; set; }
        public bool InternetOk { get; set; }
        public long? LatencyMs { get; set; }
    }

    internal static class EnumerableCompatibility
    {
        public static List<T> TakeLastCompatible<T>(this IEnumerable<T> source, int count)
        {
            var list = source as IList<T> ?? source.ToList();
            return list.Skip(Math.Max(0, list.Count - count)).ToList();
        }
    }
}
