using System;
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
        private AppSettings _settings;
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
            ApplyTheme(!_isDarkTheme);
        }

        private void ApplyTheme(bool dark)
        {
            _isDarkTheme = dark;
            SetBrushColor("AppBackgroundBrush", dark ? "#171C1A" : "#F1F5F3");
            SetBrushColor("CardBackgroundBrush", dark ? "#242B28" : "#FFFFFF");
            SetBrushColor("PrimaryTextBrush", dark ? "#EDF4F0" : "#20312B");
            SetBrushColor("SecondaryTextBrush", dark ? "#B2C0B9" : "#65736F");
            SetBrushColor("InputBackgroundBrush", dark ? "#303936" : "#FFFFFF");
            SetBrushColor("InputForegroundBrush", dark ? "#F1F5F3" : "#20312B");
            SetBrushColor("LogBackgroundBrush", dark ? "#1C2220" : "#F7F9F8");
            SetBrushColor("LogBorderBrush", dark ? "#414B46" : "#E2E8E5");
            SetBrushColor("AccentBrush", dark ? "#36866A" : "#216C54");
            if (ThemeButton != null)
                ThemeButton.Content = dark ? "☀ Modo claro" : "☾ Modo oscuro";
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
                RunStatusText.Text = "● DETENIDO";
                RunStatusText.Foreground = System.Windows.Media.Brushes.Orange;
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
                    IntervalSeconds = interval
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
            RunStatusText.Text = "● EN EJECUCIÓN";
            RunStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
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
                RunStatusText.Text = "● DETENIDO";
                RunStatusText.Foreground = System.Windows.Media.Brushes.Orange;
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
                return;
            }

            SetStatus(WifiStatusText, "Conectado a " + _settings.Ssid);
            await CheckDiagnosticIpAsync(token);

            var internetOk = await HasInternetAsync(token);
            SetStatus(InternetStatusText, internetOk ? "Disponible" : "Sin acceso confirmado");

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

        private async Task CheckDiagnosticIpAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(_settings.DiagnosticIp, 1200);
                    if (reply.Status == IPStatus.Success)
                        SetStatus(IpStatusText, "Responde (" + reply.RoundtripTime + " ms)");
                    else
                        SetStatus(IpStatusText, "Sin respuesta ICMP (" + reply.Status + ")");
                }
            }
            catch (Exception ex)
            {
                SetStatus(IpStatusText, "No se pudo comprobar: " + ex.Message);
            }
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
    }
}
