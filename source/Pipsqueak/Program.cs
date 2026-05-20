using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Pipsqueak
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            try
            {
                Application app = new Application();
                app.Run(new MainWindow());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Application crashed during startup:\n\n{ex.Message}\n\n{ex.InnerException?.Message}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public class Host
    {
        public string IP { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public int Latency { get; set; }
        public string MAC { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class PortResult
    {
        public int Port { get; set; }
        public string Protocol { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Service { get; set; } = string.Empty;
        public int Latency { get; set; }
    }

    public static class Scripts
    {
        public const string IpScanScript = @"
param([string]$IP)
$status = 'Unreachable'
$latency = 0
$hostname = 'Unknown'
$mac = 'Unknown'

$ping = New-Object System.Net.NetworkInformation.Ping
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $reply = $ping.Send($IP, 500) # Fast 500ms timeout
    $sw.Stop()
    if ($reply.Status -eq 'Success') {
        $status = 'Alive'
        $latency = $reply.RoundtripTime
        try { 
            $dns = Resolve-DnsName -Name $IP -ErrorAction SilentlyContinue -QuickTimeout
            if ($dns) { $hostname = $dns.NameHost } 
        } catch {}
        try { 
            $arp = Get-NetNeighbor -IPAddress $IP -ErrorAction SilentlyContinue
            if ($arp) { $mac = $arp.LinkLayerAddress } 
        } catch {}
    }
} catch { }
finally {
    $ping.Dispose()
}

$obj = [PSCustomObject]@{ IP = $IP; Hostname = $hostname; Latency = $latency; MAC = $mac; Status = $status }
$obj | ConvertTo-Json -Compress
";

        public const string PortScanScript = @"
param([string]$IP, [int]$Port)
$status = 'Closed'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $async = $tcp.BeginConnect($IP, $Port, $null, $null)
    $wait = $async.AsyncWaitHandle.WaitOne(1000, $false)
    $sw.Stop()
    if ($wait -and $tcp.Connected) { $status = 'Open' } 
    elseif (-not $wait) { $status = 'Filtered' }
} catch { 
    $status = 'Closed' 
} finally { 
    $tcp.Close() 
}

$svc = switch ($Port) {
    21 {'FTP'} 22 {'SSH'} 23 {'Telnet'} 25 {'SMTP'} 53 {'DNS'} 80 {'HTTP'}
    110 {'POP3'} 139 {'NetBIOS'} 143 {'IMAP'} 443 {'HTTPS'} 445 {'SMB'} 3389 {'RDP'}
    default {'Unknown'}
}

$obj = [PSCustomObject]@{ Port = $Port; Protocol = 'TCP'; Status = $status; Service = $svc; Latency = $sw.ElapsedMilliseconds }
$obj | ConvertTo-Json -Compress
";
    }

    public class PowerShellRunner : IDisposable
    {
        private RunspacePool _pool;

        public PowerShellRunner(int maxThreads = 100)
        {
            _pool = RunspaceFactory.CreateRunspacePool(1, maxThreads);
            _pool.Open();
        }

        public async Task<string?> ExecuteScriptAsync(string script, Dictionary<string, object> parameters)
        {
            return await Task.Run(() =>
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.RunspacePool = _pool;
                    ps.AddScript(script);
                    foreach (var kvp in parameters) { ps.AddParameter(kvp.Key, kvp.Value); }

                    var results = ps.Invoke();
                    if (results != null && results.Count > 0)
                    {
                        return string.Join("", results.Select(r => r.BaseObject.ToString()));
                    }
                    return null;
                }
            });
        }

        public void Dispose()
        {
            _pool?.Dispose();
        }
    }

    public static class NetworkUtils
    {
        public static IEnumerable<string> ParseIPs(string input)
        {
            if (input.Contains('-'))
            {
                var parts = input.Split('-');
                uint start = IPToUInt(parts[0].Trim());
                uint end = IPToUInt(parts[1].Trim());
                for (uint i = start; i <= end; i++) yield return UIntToIP(i);
            }
            else if (input.Contains('/'))
            {
                var parts = input.Split('/');
                uint ip = IPToUInt(parts[0].Trim());
                int mask = int.Parse(parts[1].Trim());
                uint maskValue = ~((1u << (32 - mask)) - 1);
                uint start = ip & maskValue;
                uint end = start | ~maskValue;
                for (uint i = start + 1; i < end; i++) yield return UIntToIP(i);
            }
            else
            {
                yield return input.Trim();
            }
        }

        private static uint IPToUInt(string ip)
        {
            var bytes = IPAddress.Parse(ip).GetAddressBytes();
            Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }

        private static string UIntToIP(uint ip)
        {
            var bytes = BitConverter.GetBytes(ip);
            Array.Reverse(bytes);
            return new IPAddress(bytes).ToString();
        }

        public static IEnumerable<int> ParsePorts(string input)
        {
            foreach (var part in input.Split(','))
            {
                if (part.Contains('-'))
                {
                    var range = part.Split('-');
                    if (int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
                    {
                        for (int i = start; i <= end; i++) yield return i;
                    }
                }
                else if (int.TryParse(part.Trim(), out int port))
                {
                    yield return port;
                }
            }
        }
    }

    public class MainWindow : Window
    {
        private ObservableCollection<Host> _hosts = new ObservableCollection<Host>();
        private ObservableCollection<PortResult> _ports = new ObservableCollection<PortResult>();
        private PowerShellRunner _ipRunner = new PowerShellRunner(200); // Increased thread pool
        private PowerShellRunner _portRunner = new PowerShellRunner(200);
        private CancellationTokenSource? _cts;

        private TextBox _txtTargetStart = null!;
        private TextBox _txtTargetEnd = null!;
        private ComboBox _cmbProfile = null!;
        private TextBox _txtCustomPorts = null!;
        private Button _btnScan = null!;
        private Button _btnStop = null!;
        private Label _lblStatus = null!;
        private DataGrid _gridHosts = null!;
        private DataGrid _gridPorts = null!;

        public MainWindow()
        {
            InitializeUI();
            WireEvents();
        }

        private void InitializeUI()
        {
            this.Title = "Pipsqueak by Joshua Dwight";
            this.Height = 700;
            this.Width = 1000;
            this.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 36)); // #1E1E24
            this.Foreground = System.Windows.Media.Brushes.White;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Injecting WPF layout dynamically inside the single file rooted at Grid
            string xaml = @"
            <Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                  xmlns:x='http://schemas.microsoft.com/schemas/2006/xaml'
                  Margin='10'>
                <Grid.RowDefinitions>
                    <RowDefinition Height='Auto'/>
                    <RowDefinition Height='2*'/>
                    <RowDefinition Height='5'/>
                    <RowDefinition Height='1*'/>
                </Grid.RowDefinitions>

                <Border Grid.Row='0' Background='#2B2B36' CornerRadius='5' Padding='10' Margin='0,0,0,10'>
                    <StackPanel Orientation='Horizontal'>
                        <TextBlock Text='IP Range:' VerticalAlignment='Center' Margin='0,0,5,0' Foreground='#AAAAAA'/>
                        <TextBox Name='txtTargetStart' Width='110' Text='192.168.1.1' Background='#1E1E24' Foreground='White' BorderBrush='#444' Padding='4'/>
                        <TextBlock Text='to' VerticalAlignment='Center' Margin='5,0,5,0' Foreground='#AAAAAA'/>
                        <TextBox Name='txtTargetEnd' Width='110' Text='192.168.1.25' Background='#1E1E24' Foreground='White' BorderBrush='#444' Padding='4'/>
                        
                        <Button Name='btnScan' Content='Scan IPs' Width='80' Margin='15,0,5,0' BorderThickness='0' Cursor='Hand'>
                            <Button.Style>
                                <Style TargetType='Button'>
                                    <Setter Property='Background' Value='#4CAF50'/>
                                    <Setter Property='Foreground' Value='White'/>
                                    <Style.Triggers>
                                        <Trigger Property='IsEnabled' Value='False'>
                                            <Setter Property='Background' Value='#2E5A31'/>
                                            <Setter Property='Foreground' Value='#9E9E9E'/>
                                        </Trigger>
                                    </Style.Triggers>
                                </Style>
                            </Button.Style>
                        </Button>
                        <Button Name='btnStop' Content='Stop' Width='60' Margin='0,0,15,0' IsEnabled='False' BorderThickness='0' Cursor='Hand'>
                            <Button.Style>
                                <Style TargetType='Button'>
                                    <Setter Property='Background' Value='#F44336'/>
                                    <Setter Property='Foreground' Value='White'/>
                                    <Style.Triggers>
                                        <Trigger Property='IsEnabled' Value='False'>
                                            <Setter Property='Background' Value='#6B2824'/>
                                            <Setter Property='Foreground' Value='#9E9E9E'/>
                                        </Trigger>
                                    </Style.Triggers>
                                </Style>
                            </Button.Style>
                        </Button>
                        
                        <TextBlock Text='Port Profile:' VerticalAlignment='Center' Margin='15,0,10,0' Foreground='#AAAAAA'/>
                        <ComboBox Name='cmbProfile' Width='100' SelectedIndex='0' Background='#1E1E24' Foreground='Black'>
                            <ComboBoxItem Content='Quick Scan'/>
                            <ComboBoxItem Content='Full Scan'/>
                            <ComboBoxItem Content='Custom Scan'/>
                        </ComboBox>
                        <TextBox Name='txtCustomPorts' Width='120' Text='80,443,8080' Margin='10,0,0,0' Background='#1E1E24' Foreground='White' BorderBrush='#444' Padding='4'/>
                    </StackPanel>
                </Border>

                <!-- Hosts DataGrid -->
                <Border Grid.Row='1' BorderBrush='#444' BorderThickness='1' CornerRadius='5'>
                    <DataGrid Name='gridHosts' AutoGenerateColumns='False' IsReadOnly='True' Background='#2B2B36' RowBackground='#2B2B36' AlternatingRowBackground='#32323D' Foreground='White' GridLinesVisibility='None' SelectionMode='Single'>
                        <DataGrid.Resources>
                            <Style TargetType='DataGridColumnHeader'>
                                <Setter Property='Background' Value='#1E1E24'/>
                                <Setter Property='Foreground' Value='White'/>
                                <Setter Property='Padding' Value='8'/>
                            </Style>
                        </DataGrid.Resources>
                        <DataGrid.ContextMenu>
                            <ContextMenu Background='#2B2B36' Foreground='White' BorderBrush='#444'>
                                <ContextMenu.Resources>
                                    <Style TargetType='MenuItem'>
                                        <Setter Property='Background' Value='#2B2B36'/>
                                        <Setter Property='Foreground' Value='White'/>
                                        <Setter Property='Template'>
                                            <Setter.Value>
                                                <ControlTemplate TargetType='MenuItem'>
                                                    <Border Background='{TemplateBinding Background}' Padding='10,8'>
                                                        <ContentPresenter ContentSource='Header'/>
                                                    </Border>
                                                    <ControlTemplate.Triggers>
                                                        <Trigger Property='IsHighlighted' Value='True'>
                                                            <Setter Property='Background' Value='#3F3F4E'/>
                                                        </Trigger>
                                                    </ControlTemplate.Triggers>
                                                </ControlTemplate>
                                            </Setter.Value>
                                        </Setter>
                                    </Style>
                                </ContextMenu.Resources>
                                <MenuItem Name='menuPortScan' Header='Run Port Scan on Selected Host' />
                            </ContextMenu>
                        </DataGrid.ContextMenu>
                        <DataGrid.Columns>
                            <DataGridTextColumn Header='IP Address' Binding='{Binding IP}' Width='150'/>
                            <DataGridTextColumn Header='Hostname' Binding='{Binding Hostname}' Width='*'/>
                            <DataGridTextColumn Header='MAC Address' Binding='{Binding MAC}' Width='180'/>
                            <DataGridTextColumn Header='Latency (ms)' Binding='{Binding Latency}' Width='100'/>
                            <DataGridTextColumn Header='Status' Binding='{Binding Status}' Width='120'/>
                        </DataGrid.Columns>
                    </DataGrid>
                </Border>

                <GridSplitter Grid.Row='2' Height='5' HorizontalAlignment='Stretch' Background='#1E1E24'/>

                <!-- Ports DataGrid -->
                <Border Grid.Row='3' BorderBrush='#444' BorderThickness='1' CornerRadius='5' Margin='0,5,0,0'>
                    <Grid>
                        <Grid.RowDefinitions>
                            <RowDefinition Height='Auto'/>
                            <RowDefinition Height='*'/>
                        </Grid.RowDefinitions>
                        <Border Background='#1E1E24' Padding='5'>
                            <Label Name='lblStatus' Content='Ready' Foreground='#00E676' FontWeight='Bold'/>
                        </Border>
                        <DataGrid Name='gridPorts' Grid.Row='1' AutoGenerateColumns='False' IsReadOnly='True' Background='#2B2B36' RowBackground='#2B2B36' AlternatingRowBackground='#32323D' Foreground='White' GridLinesVisibility='None'>
                            <DataGrid.Resources>
                                <Style TargetType='DataGridColumnHeader'>
                                    <Setter Property='Background' Value='#1E1E24'/>
                                    <Setter Property='Foreground' Value='White'/>
                                    <Setter Property='Padding' Value='8'/>
                                </Style>
                            </DataGrid.Resources>
                            <DataGrid.Columns>
                                <DataGridTextColumn Header='Port' Binding='{Binding Port}' Width='100'/>
                                <DataGridTextColumn Header='Protocol' Binding='{Binding Protocol}' Width='100'/>
                                <DataGridTextColumn Header='Service' Binding='{Binding Service}' Width='*'/>
                                <DataGridTextColumn Header='Status' Binding='{Binding Status}' Width='120'/>
                                <DataGridTextColumn Header='Latency (ms)' Binding='{Binding Latency}' Width='120'/>
                            </DataGrid.Columns>
                        </DataGrid>
                    </Grid>
                </Border>
            </Grid>".Replace("'", "\"");

            Grid mainGrid = (Grid)XamlReader.Parse(xaml);
            this.Content = mainGrid;

            // Bind Controls using the Grid's NameScope
            _txtTargetStart = (TextBox)mainGrid.FindName("txtTargetStart");
            _txtTargetEnd = (TextBox)mainGrid.FindName("txtTargetEnd");
            _cmbProfile = (ComboBox)mainGrid.FindName("cmbProfile");
            _txtCustomPorts = (TextBox)mainGrid.FindName("txtCustomPorts");
            _btnScan = (Button)mainGrid.FindName("btnScan");
            _btnStop = (Button)mainGrid.FindName("btnStop");
            _lblStatus = (Label)mainGrid.FindName("lblStatus");
            _gridHosts = (DataGrid)mainGrid.FindName("gridHosts");
            _gridPorts = (DataGrid)mainGrid.FindName("gridPorts");

            _gridHosts.ItemsSource = _hosts;
            _gridPorts.ItemsSource = _ports;
        }

        private void WireEvents()
        {
            _btnScan.Click += BtnScan_Click;
            _btnStop.Click += BtnStop_Click;
            
            var mainGrid = (Grid)this.Content;
            var menuPortScan = (MenuItem)mainGrid.FindName("menuPortScan");
            if (menuPortScan != null)
            {
                menuPortScan.Click += MenuPortScan_Click;
            }

            _cmbProfile.SelectionChanged += (s, e) => {
                if (_txtCustomPorts != null)
                    _txtCustomPorts.IsEnabled = _cmbProfile.SelectedIndex == 2;
            };
        }

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            _hosts.Clear();
            _ports.Clear();
            _cts = new CancellationTokenSource();
            _btnScan.IsEnabled = false;
            _btnStop.IsEnabled = true;

            string target = $"{_txtTargetStart.Text}-{_txtTargetEnd.Text}";
            _lblStatus.Content = $"Discovering hosts in {target}...";
            _lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)); // Amber

            try
            {
                var ips = NetworkUtils.ParseIPs(target).ToList();
                int completed = 0;

                // Offload thread dispatch to prevent UI blocking
                await Task.Run(async () =>
                {
                    var tasks = new List<Task>();
                    using var semaphore = new SemaphoreSlim(250); // Limit concurrent Runspace dispatches

                    foreach (var ip in ips)
                    {
                        if (_cts.IsCancellationRequested) break;
                        await semaphore.WaitAsync();

                        var pms = new Dictionary<string, object> { { "IP", ip } };
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var jsonResult = await _ipRunner.ExecuteScriptAsync(Scripts.IpScanScript, pms);
                                if (jsonResult != null)
                                {
                                    try
                                    {
                                        var host = JsonSerializer.Deserialize<Host>(jsonResult);
                                        if (host != null && host.Status == "Alive")
                                        {
                                            Dispatcher.Invoke(() => _hosts.Add(host));
                                        }
                                    }
                                    catch { /* Ignore parse errors for unreachable hosts */ }
                                }
                                Interlocked.Increment(ref completed);
                                Dispatcher.Invoke(() => _lblStatus.Content = $"Scanning IPs... {completed}/{ips.Count} completed.");
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }));
                    }
                    await Task.WhenAll(tasks);
                });

                _lblStatus.Content = $"IP Scan Complete. Found {_hosts.Count} active hosts.";
                _lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118)); // Green
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during scan: {ex.Message}", "Error");
            }
            finally
            {
                _btnScan.IsEnabled = true;
                _btnStop.IsEnabled = false;
            }
        }

        private async void MenuPortScan_Click(object sender, RoutedEventArgs e)
        {
            if (_gridHosts.SelectedItem is Host selectedHost)
            {
                _ports.Clear();
                _cts = new CancellationTokenSource();
                _btnScan.IsEnabled = false;
                _btnStop.IsEnabled = true;

                IEnumerable<int> targetPorts;
                if (_cmbProfile.SelectedIndex == 0) // Quick
                    targetPorts = new[] { 21, 22, 23, 25, 53, 80, 110, 139, 143, 443, 445, 3389 };
                else if (_cmbProfile.SelectedIndex == 1) // Full
                    targetPorts = Enumerable.Range(1, 65535);
                else // Custom
                    targetPorts = NetworkUtils.ParsePorts(_txtCustomPorts.Text);

                var portsList = targetPorts.ToList();
                _lblStatus.Content = $"Running port scan on {selectedHost.IP} ({portsList.Count} ports)...";
                _lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7));

                int completed = 0;

                // Offload massive task dispatching to background thread so UI doesn't lock up during 'Full Scan' loop
                await Task.Run(async () =>
                {
                    var tasks = new List<Task>();
                    using var semaphore = new SemaphoreSlim(250);

                    foreach (var port in portsList)
                    {
                        if (_cts.IsCancellationRequested) break;
                        await semaphore.WaitAsync();

                        var pms = new Dictionary<string, object> { { "IP", selectedHost.IP }, { "Port", port } };
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var jsonResult = await _portRunner.ExecuteScriptAsync(Scripts.PortScanScript, pms);
                                if (jsonResult != null)
                                {
                                    try
                                    {
                                        var result = JsonSerializer.Deserialize<PortResult>(jsonResult);
                                        if (result != null && result.Status != "Closed") // Only show Open or Filtered
                                        {
                                            Dispatcher.Invoke(() => _ports.Add(result));
                                        }
                                    }
                                    catch { }
                                }
                                Interlocked.Increment(ref completed);
                                
                                // Throttle UI updates to prevent hanging during full scans
                                if (completed % 50 == 0 || completed == portsList.Count)
                                {
                                    Dispatcher.Invoke(() => _lblStatus.Content = $"Scanning Ports on {selectedHost.IP}... {completed}/{portsList.Count} completed.");
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }));
                    }
                    await Task.WhenAll(tasks);
                });

                _lblStatus.Content = $"Port Scan Complete on {selectedHost.IP}. Found {_ports.Count} active/filtered ports.";
                _lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118));
                
                _btnScan.IsEnabled = true;
                _btnStop.IsEnabled = false;
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _lblStatus.Content = "Scan Aborted by User.";
            _lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)); // Red
            _btnScan.IsEnabled = true;
            _btnStop.IsEnabled = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            _ipRunner.Dispose();
            _portRunner.Dispose();
            base.OnClosed(e);
        }
    }
}