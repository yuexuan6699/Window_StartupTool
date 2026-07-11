using System.Data;
using System.ServiceProcess;
using StartupCommon;

namespace StartupTool;

public partial class MainForm : Form
{
    private readonly ConfigManager _configManager;
    private readonly Logger _logger;
    private readonly ScriptRunner _scriptRunner;
    private readonly System.Windows.Forms.Timer _statusTimer = new();

    private NotifyIcon _notifyIcon = null!;
    private DataGridView _dgvScripts = null!;
    private Button _btnAdd = null!, _btnRemove = null!, _btnStartAll = null!, _btnStopAll = null!;
    private Button _btnStartSelected = null!, _btnStopSelected = null!, _btnShowProcesses = null!;
    private TextBox _txtName = null!, _txtPath = null!, _txtArguments = null!;
    private NumericUpDown _nudOrder = null!;
    private Button _btnToggleService = null!, _btnRefreshService = null!;
    private GroupBox _grpAdd = null!, _grpScripts = null!, _grpService = null!;
    private Label _lblServiceStatus = null!;
    private bool _isClosing;
    private bool _isRefreshing;
    private readonly string _startupAction = string.Empty;

    public MainForm(string startupAction = "")
    {
        var baseDir = AppHelper.GetBaseDirectory();
        _configManager = new ConfigManager(baseDir);
        _logger = new Logger(baseDir);
        _scriptRunner = new ScriptRunner(_logger, baseDir);
        _startupAction = startupAction;

        InitializeUI();
        SetupNotifyIcon();
        SetupStatusTimer();
        RefreshAll();

        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;

        Load += async (s, e) =>
        {
            _scriptRunner.StartResourceMonitor(_configManager.GetScripts());

            if (!string.IsNullOrEmpty(_startupAction) && AppHelper.IsRunningAsAdmin())
            {
                await Task.Delay(500);
                await ExecuteStartupActionAsync();
                ShowWindow();
            }
            else
            {
                CheckServicePathChanged();
            }
        };
    }

    private async Task ExecuteStartupActionAsync()
    {
        try
        {
            using var sc = new ServiceController("StartupTool");

            switch (_startupAction)
            {
                case "--install-service":
                    InstallService();
                    break;
                case "--start-service":
                    sc.Start();
                    await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)));
                    MessageBox.Show(LocalizationManager.GetString("ServiceStartMsg"), LocalizationManager.GetString("Hint"));
                    break;
                case "--stop-service":
                    sc.Stop();
                    await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)));
                    MessageBox.Show(LocalizationManager.GetString("ServiceStoppedMsg"), LocalizationManager.GetString("Hint"));
                    break;
                case "--update-service-path":
                    UpdateServicePath();
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            if (_startupAction == "--install-service" || _startupAction == "--start-service")
            {
                InstallService();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(LocalizationManager.GetString("OperationFailed", ex.Message),
                LocalizationManager.GetString("Error"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshServiceStatus();
        }
    }

    private void CheckServicePathChanged()
    {
        try
        {
            var serviceBinPath = AppHelper.GetServicePath("StartupTool");
            if (string.IsNullOrEmpty(serviceBinPath))
                return;

            var serviceExePath = AppHelper.ExtractExePathFromBinPath(serviceBinPath);
            var currentExePath = AppHelper.GetSelfExePath();

            if (string.IsNullOrEmpty(serviceExePath) || string.IsNullOrEmpty(currentExePath))
                return;

            if (!string.Equals(serviceExePath, currentExePath, StringComparison.OrdinalIgnoreCase))
            {
                ShowWindow();
                var result = MessageBox.Show(
                    LocalizationManager.GetString("ServicePathChanged", serviceExePath, currentExePath),
                    LocalizationManager.GetString("UpdateServicePathTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    if (AppHelper.IsRunningAsAdmin())
                    {
                        UpdateServicePath();
                    }
                    else
                    {
                        if (AppHelper.RestartAsAdmin("--update-service-path"))
                        {
                            _isClosing = true;
                            Application.Exit();
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private void UpdateServicePath()
    {
        try
        {
            var currentExePath = AppHelper.GetSelfExePath();
            var newBinPath = $"{currentExePath} --service";

            if (AppHelper.UpdateServicePath("StartupTool", newBinPath))
            {
                MessageBox.Show(LocalizationManager.GetString("ServicePathUpdated"),
                    LocalizationManager.GetString("Success"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(LocalizationManager.GetString("ServicePathUpdateFailed", "unknown error"),
                    LocalizationManager.GetString("Error"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(LocalizationManager.GetString("ServicePathUpdateFailed", ex.Message),
                LocalizationManager.GetString("Error"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void InitializeUI()
    {
        Text = LocalizationManager.GetString("AppTitle");
        Size = new Size(950, 700);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));

        var langPanel = CreateLanguagePanel();
        _grpService = CreateServiceGroup();
        _grpScripts = CreateScriptsGroup();
        _grpAdd = CreateAddGroup();

        mainPanel.Controls.Add(langPanel, 0, 0);
        mainPanel.Controls.Add(_grpService, 0, 1);
        mainPanel.Controls.Add(_grpScripts, 0, 2);
        mainPanel.Controls.Add(_grpAdd, 0, 3);

        Controls.Add(mainPanel);
        FormClosing += MainForm_FormClosing;
        Resize += MainForm_Resize;
        LocalizationManager.LanguageChanged += RefreshAll;
    }

    private Panel CreateLanguagePanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        Button btnChinese = null!;
        Button btnEnglish = null!;

        btnChinese = new Button
        {
            Text = LocalizationManager.GetString("Chinese"),
            Size = new Size(75, 25),
            Location = new Point(8, 5),
            BackColor = LocalizationManager.CurrentLanguage == Language.Chinese ? Color.FromArgb(33, 150, 243) : Color.Empty,
            ForeColor = LocalizationManager.CurrentLanguage == Language.Chinese ? Color.White : SystemColors.ControlText,
            FlatStyle = LocalizationManager.CurrentLanguage == Language.Chinese ? FlatStyle.Flat : FlatStyle.Standard
        };
        if (LocalizationManager.CurrentLanguage == Language.Chinese)
            btnChinese.FlatAppearance.BorderSize = 0;
        btnChinese.Click += (s, e) =>
        {
            LocalizationManager.CurrentLanguage = Language.Chinese;
            UpdateLanguageButtons(btnChinese, btnEnglish);
        };

        btnEnglish = new Button
        {
            Text = LocalizationManager.GetString("English"),
            Size = new Size(75, 25),
            Location = new Point(90, 5),
            BackColor = LocalizationManager.CurrentLanguage == Language.English ? Color.FromArgb(33, 150, 243) : Color.Empty,
            ForeColor = LocalizationManager.CurrentLanguage == Language.English ? Color.White : SystemColors.ControlText,
            FlatStyle = LocalizationManager.CurrentLanguage == Language.English ? FlatStyle.Flat : FlatStyle.Standard
        };
        if (LocalizationManager.CurrentLanguage == Language.English)
            btnEnglish.FlatAppearance.BorderSize = 0;
        btnEnglish.Click += (s, e) =>
        {
            LocalizationManager.CurrentLanguage = Language.English;
            UpdateLanguageButtons(btnChinese, btnEnglish);
        };

        panel.Controls.Add(btnChinese);
        panel.Controls.Add(btnEnglish);

        return panel;
    }

    private void UpdateLanguageButtons(Button btnChinese, Button btnEnglish)
    {
        bool isChinese = LocalizationManager.CurrentLanguage == Language.Chinese;
        btnChinese.BackColor = isChinese ? Color.FromArgb(33, 150, 243) : Color.Empty;
        btnChinese.ForeColor = isChinese ? Color.White : SystemColors.ControlText;
        btnChinese.FlatStyle = isChinese ? FlatStyle.Flat : FlatStyle.Standard;
        btnChinese.FlatAppearance.BorderSize = isChinese ? 0 : 1;

        btnEnglish.BackColor = !isChinese ? Color.FromArgb(33, 150, 243) : Color.Empty;
        btnEnglish.ForeColor = !isChinese ? Color.White : SystemColors.ControlText;
        btnEnglish.FlatStyle = !isChinese ? FlatStyle.Flat : FlatStyle.Standard;
        btnEnglish.FlatAppearance.BorderSize = !isChinese ? 0 : 1;
    }

    private GroupBox CreateServiceGroup()
    {
        var grp = new GroupBox { Text = LocalizationManager.GetString("ServiceStatus"), Dock = DockStyle.Fill };

        _btnToggleService = new Button
        {
            Text = LocalizationManager.GetString("EnableAutoStart"),
            Size = new Size(120, 35),
            Location = new Point(15, 30),
            BackColor = Color.FromArgb(76, 175, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnToggleService.FlatAppearance.BorderSize = 0;
        _btnToggleService.Click += BtnToggleService_Click;

        _btnRefreshService = new Button
        {
            Text = LocalizationManager.GetString("RefreshStatus"),
            Size = new Size(90, 35),
            Location = new Point(145, 30)
        };
        _btnRefreshService.Click += (s, e) => RefreshServiceStatus();

        _lblServiceStatus = new Label
        {
            Text = LocalizationManager.GetString("ServiceUnknown"),
            AutoSize = true,
            Location = new Point(250, 38),
            ForeColor = Color.Gray
        };

        grp.Controls.Add(_btnToggleService);
        grp.Controls.Add(_btnRefreshService);
        grp.Controls.Add(_lblServiceStatus);

        return grp;
    }

    private GroupBox CreateScriptsGroup()
    {
        var grp = new GroupBox { Text = LocalizationManager.GetString("ScriptList"), Dock = DockStyle.Fill };

        _dgvScripts = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            ContextMenuStrip = CreateScriptContextMenu()
        };
        _dgvScripts.CellValueChanged += DgvScripts_CellValueChanged;
        _dgvScripts.CurrentCellDirtyStateChanged += DgvScripts_CurrentCellDirtyStateChanged;

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(0, 5, 0, 0),
            FlowDirection = FlowDirection.LeftToRight
        };

        _btnShowProcesses = CreateButton(LocalizationManager.GetString("RunningProcesses"), Color.FromArgb(76, 175, 80), (s, e) => ShowProcessWindow());
        _btnStartSelected = CreateButton(LocalizationManager.GetString("StartSelected"), Color.FromArgb(33, 150, 243), BtnStartSelected_Click);
        _btnStopSelected = CreateButton(LocalizationManager.GetString("StopSelected"), Color.FromArgb(255, 152, 0), BtnStopSelected_Click);
        _btnStartAll = CreateButton(LocalizationManager.GetString("BatchStart"), Color.FromArgb(33, 150, 243), BtnStartAll_Click);
        _btnStopAll = CreateButton(LocalizationManager.GetString("StopAll"), Color.Empty, BtnStopAll_Click);
        _btnRemove = CreateButton(LocalizationManager.GetString("RemoveSelected"), Color.FromArgb(244, 67, 54), BtnRemove_Click);

        btnPanel.Controls.Add(_btnShowProcesses);
        btnPanel.Controls.Add(_btnStartSelected);
        btnPanel.Controls.Add(_btnStopSelected);
        btnPanel.Controls.Add(_btnStartAll);
        btnPanel.Controls.Add(_btnStopAll);
        btnPanel.Controls.Add(_btnRemove);

        grp.Controls.Add(_dgvScripts);
        grp.Controls.Add(btnPanel);

        return grp;
    }

    private GroupBox CreateAddGroup()
    {
        var grp = new GroupBox { Text = LocalizationManager.GetString("AddScript"), Dock = DockStyle.Fill };

        var pnl = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(5, 8, 5, 5),
            FlowDirection = FlowDirection.LeftToRight
        };

        pnl.Controls.Add(new Label { Text = LocalizationManager.GetString("ScriptName") + ":", AutoSize = true, Padding = new Padding(0, 8, 5, 0) });
        _txtName = new TextBox { Width = 120 };
        pnl.Controls.Add(_txtName);

        pnl.Controls.Add(new Label { Text = LocalizationManager.GetString("ScriptPath") + ":", AutoSize = true, Padding = new Padding(20, 8, 5, 0) });
        _txtPath = new TextBox { Width = 260 };
        pnl.Controls.Add(_txtPath);

        var btnBrowse = new Button { Text = LocalizationManager.GetString("Browse"), Size = new Size(60, 24), Margin = new Padding(5, 8, 0, 0) };
        btnBrowse.Click += (s, e) => BrowseScriptPath();
        pnl.Controls.Add(btnBrowse);

        pnl.Controls.Add(new Label { Text = LocalizationManager.GetString("ScriptArguments") + ":", AutoSize = true, Padding = new Padding(15, 8, 5, 0) });
        _txtArguments = new TextBox { Width = 150 };
        pnl.Controls.Add(_txtArguments);

        pnl.Controls.Add(new Label { Text = LocalizationManager.GetString("ScriptOrder") + ":", AutoSize = true, Padding = new Padding(15, 8, 5, 0) });
        _nudOrder = new NumericUpDown { Width = 60, Minimum = 0, Maximum = 999, Value = 0 };
        pnl.Controls.Add(_nudOrder);

        _btnAdd = CreateButton(LocalizationManager.GetString("AddScript"), Color.FromArgb(76, 175, 80), BtnAdd_Click);
        _btnAdd.Margin = new Padding(20, 2, 0, 0);
        pnl.Controls.Add(_btnAdd);

        grp.Controls.Add(pnl);
        return grp;
    }

    private Button CreateButton(string text, Color backColor, EventHandler clickHandler)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(60, 30),
            Padding = new Padding(10, 0, 10, 0),
            ForeColor = backColor != Color.Empty ? Color.White : SystemColors.ControlText,
            FlatStyle = backColor != Color.Empty ? FlatStyle.Flat : FlatStyle.Standard
        };
        if (backColor != Color.Empty)
        {
            btn.BackColor = backColor;
            btn.FlatAppearance.BorderSize = 0;
        }
        btn.Click += clickHandler;
        return btn;
    }

    private ContextMenuStrip CreateScriptContextMenu()
    {
        var ctxMenu = new ContextMenuStrip();
        ctxMenu.Items.Add(LocalizationManager.GetString("Start")).Click += MenuStartScript_Click;
        ctxMenu.Items.Add(LocalizationManager.GetString("Stop")).Click += MenuStopScript_Click;
        ctxMenu.Items.Add(new ToolStripSeparator());
        ctxMenu.Items.Add(LocalizationManager.GetString("Enable")).Click += MenuToggleEnabled_Click;
        ctxMenu.Items.Add(LocalizationManager.GetString("Disable")).Click += MenuToggleEnabled_Click;
        return ctxMenu;
    }

    private void BrowseScriptPath()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "脚本文件 (*.bat;*.cmd;*.ps1)|*.bat;*.cmd;*.ps1|所有文件 (*.*)|*.*",
            Title = LocalizationManager.GetString("SelectScriptFile")
        };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            _txtPath.Text = ofd.FileName;
        }
    }

    private void SetupNotifyIcon()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceName = "StartupTool.Resources.icon_32.png";
        Icon notifyIcon;
        using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream != null)
            {
                using var img = Image.FromStream(stream);
                notifyIcon = Icon.FromHandle(((Bitmap)img).GetHicon());
            }
            else
            {
                notifyIcon = SystemIcons.Application;
            }
        }

        Icon = notifyIcon;

        _notifyIcon = new NotifyIcon
        {
            Icon = notifyIcon,
            Text = LocalizationManager.GetString("AppTitle"),
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.Add(LocalizationManager.GetString("ShowWindow")).Click += (s, e) => ShowWindow();
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(LocalizationManager.GetString("StartAllScripts")).Click += (s, e) => StartAllScripts();
        _notifyIcon.ContextMenuStrip.Items.Add(LocalizationManager.GetString("StopAllScripts")).Click += (s, e) => _scriptRunner.StopAllScripts();
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(LocalizationManager.GetString("Exit")).Click += (s, e) =>
        {
            _isClosing = true;
            Application.Exit();
        };

        _notifyIcon.DoubleClick += (s, e) => ShowWindow();
    }

    private void SetupStatusTimer()
    {
        _statusTimer.Interval = 3000;
        _statusTimer.Tick += async (s, e) => await RefreshScriptListAsync();
        _statusTimer.Start();
    }

    private void ShowWindow()
    {
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Show();
        Activate();
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            ShowInTaskbar = false;
            Hide();
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
            return;
        }

        _statusTimer.Stop();
        _scriptRunner.StopAllScripts();
        _configManager.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void RefreshServiceStatus()
    {
        try
        {
            using var sc = new ServiceController("StartupTool");
            bool isRunning = sc.Status == ServiceControllerStatus.Running;

            UpdateServiceUI(isRunning, true);
        }
        catch (InvalidOperationException)
        {
            UpdateServiceUI(false, false);
        }
        catch (Exception ex)
        {
            _logger.Error($"查询服务状态失败: {ex.Message}");
            _lblServiceStatus.Text = LocalizationManager.GetString("ServiceQueryFailed");
            _lblServiceStatus.ForeColor = Color.Red;
        }
    }

    private void UpdateServiceUI(bool isRunning, bool isInstalled)
    {
        if (!isInstalled)
        {
            _btnToggleService.Text = LocalizationManager.GetString("InstallService");
            _btnToggleService.BackColor = Color.FromArgb(76, 175, 80);
            _lblServiceStatus.Text = LocalizationManager.GetString("ServiceNotInstalled");
            _lblServiceStatus.ForeColor = Color.Orange;
            return;
        }

        _btnToggleService.Text = isRunning ? LocalizationManager.GetString("DisableAutoStart") : LocalizationManager.GetString("EnableAutoStart");
        _btnToggleService.BackColor = isRunning ? Color.FromArgb(244, 67, 54) : Color.FromArgb(76, 175, 80);
        _lblServiceStatus.Text = isRunning ? LocalizationManager.GetString("ServiceRunning") : LocalizationManager.GetString("ServiceStopped");
        _lblServiceStatus.ForeColor = isRunning ? Color.Green : Color.Red;
    }

    private async void BtnToggleService_Click(object? sender, EventArgs e)
    {
        _btnToggleService.Enabled = false;

        try
        {
            using var sc = new ServiceController("StartupTool");

            if (sc.Status == ServiceControllerStatus.Running)
            {
                if (!AppHelper.IsRunningAsAdmin())
                {
                    if (MessageBox.Show(LocalizationManager.GetString("NeedAdminForServiceOp"),
                            LocalizationManager.GetString("Hint"),
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        if (AppHelper.RestartAsAdmin("--stop-service"))
                        {
                            _isClosing = true;
                            Application.Exit();
                        }
                    }
                    return;
                }

                sc.Stop();
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)));
                MessageBox.Show(LocalizationManager.GetString("ServiceStoppedMsg"), LocalizationManager.GetString("Hint"));
            }
            else
            {
                if (!AppHelper.IsRunningAsAdmin())
                {
                    if (MessageBox.Show(LocalizationManager.GetString("NeedAdminForServiceOp"),
                            LocalizationManager.GetString("Hint"),
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        if (AppHelper.RestartAsAdmin("--start-service"))
                        {
                            _isClosing = true;
                            Application.Exit();
                        }
                    }
                    return;
                }

                sc.Start();
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)));
                MessageBox.Show(LocalizationManager.GetString("ServiceStartMsg"), LocalizationManager.GetString("Hint"));
            }
            RefreshServiceStatus();
        }
        catch (InvalidOperationException)
        {
            InstallService();
            RefreshServiceStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(LocalizationManager.GetString("OperationFailed", ex.Message), LocalizationManager.GetString("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnToggleService.Enabled = true;
        }
    }

    private void InstallService()
    {
        if (!AppHelper.IsRunningAsAdmin())
        {
            var result = MessageBox.Show(
                LocalizationManager.GetString("NeedAdminInstall"),
                LocalizationManager.GetString("InstallServiceTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            if (AppHelper.RestartAsAdmin("--install-service"))
            {
                _isClosing = true;
                Application.Exit();
            }
            else
            {
                MessageBox.Show(LocalizationManager.GetString("ServiceInstallFailed"), LocalizationManager.GetString("Error"));
            }
            return;
        }

        try
        {
            var serviceExe = AppHelper.GetSelfExePath();
            if (!File.Exists(serviceExe))
            {
                MessageBox.Show(LocalizationManager.GetString("FileNotFound", serviceExe), LocalizationManager.GetString("Error"));
                return;
            }

            using var sc = new ServiceController("StartupTool");
            try
            {
                var _ = sc.Status;
            }
            catch (InvalidOperationException)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"create StartupTool binPath= \"{serviceExe} --service\" start= auto",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit();

                if (p?.ExitCode != 0)
                {
                    var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
                    var error = p?.StandardError.ReadToEnd() ?? string.Empty;
                    var details = string.IsNullOrEmpty(output) ? error : output;
                    if (string.IsNullOrEmpty(details))
                    {
                        details = $"ExitCode: {p?.ExitCode}";
                    }
                    MessageBox.Show($"{LocalizationManager.GetString("ServiceInstallFailed")}\n\n{details.Trim()}",
                        LocalizationManager.GetString("Error"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var descPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "description StartupTool \"StartupTool - auto start scripts service\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(descPsi)?.WaitForExit();
            }

            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }

            MessageBox.Show(LocalizationManager.GetString("ServiceInstalledStarted"), LocalizationManager.GetString("Success"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(LocalizationManager.GetString("OperationFailed", ex.Message), LocalizationManager.GetString("Error"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnAdd_Click(object? sender, EventArgs e)
    {
        var name = _txtName.Text.Trim();
        var path = _txtPath.Text.Trim();
        var arguments = _txtArguments.Text.Trim();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
        {
            MessageBox.Show(LocalizationManager.GetString("PleaseFillNameAndPath"), LocalizationManager.GetString("Hint"));
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show(LocalizationManager.GetString("FileNotFound", path), LocalizationManager.GetString("Error"));
            return;
        }

        var script = new ScriptItem
        {
            Name = name,
            Path = path,
            Order = (int)_nudOrder.Value,
            Enabled = true,
            Arguments = arguments
        };

        if (_configManager.AddScript(script))
        {
            _logger.Success($"添加脚本: {name}");
            ClearAddForm();
            RefreshScriptList();
        }
        else
        {
            MessageBox.Show(LocalizationManager.GetString("ScriptExists", name), LocalizationManager.GetString("Hint"));
        }
    }

    private void ClearAddForm()
    {
        _txtName.Clear();
        _txtPath.Clear();
        _txtArguments.Clear();
        _nudOrder.Value = 0;
    }

    private void BtnRemove_Click(object? sender, EventArgs e)
    {
        var name = GetSelectedScriptName();
        if (string.IsNullOrEmpty(name)) return;

        if (MessageBox.Show(LocalizationManager.GetString("ConfirmRemove", name), LocalizationManager.GetString("Confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;

        if (_configManager.RemoveScript(name))
        {
            _scriptRunner.StopScript(name);
            _logger.Success($"移除脚本: {name}");
            RefreshScriptList();
        }
    }

    private async void BtnStartAll_Click(object? sender, EventArgs e)
    {
        await StartAllScriptsAsync();
    }

    private async Task StartAllScriptsAsync()
    {
        var scripts = _configManager.GetScripts().Where(s => s.Enabled).ToList();
        if (scripts.Count == 0)
        {
            MessageBox.Show(LocalizationManager.GetString("NoScriptsEnabled"), LocalizationManager.GetString("Hint"));
            return;
        }

        _btnStartAll.Enabled = false;
        try
        {
            await _scriptRunner.StartAllScriptsAsync(scripts);
            RefreshScriptList();
        }
        finally
        {
            _btnStartAll.Enabled = true;
        }
    }

    private void StartAllScripts()
    {
        var scripts = _configManager.GetScripts().Where(s => s.Enabled).ToList();
        if (scripts.Count == 0)
        {
            MessageBox.Show(LocalizationManager.GetString("NoScriptsEnabled"), LocalizationManager.GetString("Hint"));
            return;
        }
        _scriptRunner.StartAllScripts(scripts);
        RefreshScriptList();
    }

    private void BtnStopAll_Click(object? sender, EventArgs e)
    {
        _scriptRunner.StopAllScripts();
        foreach (var script in _configManager.GetScripts())
        {
            if (!_scriptRunner.IsRunning(script.Name))
            {
                _scriptRunner.StopScriptFromSystem(script.Name, script.Path);
            }
        }
        RefreshScriptList();
    }

    private void BtnStartSelected_Click(object? sender, EventArgs e)
    {
        var script = GetSelectedScript();
        if (script == null)
        {
            MessageBox.Show(LocalizationManager.GetString("PleaseSelectScript"), LocalizationManager.GetString("Hint"));
            return;
        }
        if (!script.Enabled)
        {
            MessageBox.Show(LocalizationManager.GetString("StatusDisabled"), LocalizationManager.GetString("Hint"));
            return;
        }
        if (_scriptRunner.IsRunning(script.Name))
        {
            MessageBox.Show(LocalizationManager.GetString("ScriptAlreadyRunning", script.Name), LocalizationManager.GetString("Hint"));
            return;
        }
        _scriptRunner.StartScript(script);
        RefreshScriptList();
    }

    private void BtnStopSelected_Click(object? sender, EventArgs e)
    {
        var script = GetSelectedScript();
        if (script == null)
        {
            MessageBox.Show(LocalizationManager.GetString("PleaseSelectScript"), LocalizationManager.GetString("Hint"));
            return;
        }

        if (!_scriptRunner.IsRunning(script.Name))
        {
            _scriptRunner.StopScriptFromSystem(script.Name, script.Path);
        }
        else
        {
            _scriptRunner.StopScript(script.Name);
        }
        RefreshScriptList();
    }

    private void MenuStartScript_Click(object? sender, EventArgs e)
    {
        var script = GetSelectedScript();
        if (script != null && script.Enabled)
        {
            _scriptRunner.StartScript(script);
            RefreshScriptList();
        }
        else if (script != null && !script.Enabled)
        {
            MessageBox.Show(LocalizationManager.GetString("StatusDisabled"), LocalizationManager.GetString("Hint"));
        }
    }

    private void MenuStopScript_Click(object? sender, EventArgs e)
    {
        var script = GetSelectedScript();
        if (script == null) return;

        if (!_scriptRunner.IsRunning(script.Name))
        {
            _scriptRunner.StopScriptFromSystem(script.Name, script.Path);
        }
        else
        {
            _scriptRunner.StopScript(script.Name);
        }
        RefreshScriptList();
    }

    private void MenuToggleEnabled_Click(object? sender, EventArgs e)
    {
        var script = GetSelectedScript();
        if (script == null) return;

        var toolStripItem = sender as ToolStripMenuItem;
        bool enable = toolStripItem?.Text == LocalizationManager.GetString("Enable");

        script.Enabled = enable;
        _configManager.UpdateScript(script.Name, script);
        
        _logger.Info($"{(enable ? "启用" : "禁用")}脚本: {script.Name}");
        RefreshScriptList();
    }

    private string? GetSelectedScriptName()
    {
        if (_dgvScripts.SelectedRows.Count == 0) return null;
        return _dgvScripts.SelectedRows[0].Cells["名称"].Value?.ToString();
    }

    private ScriptItem? GetSelectedScript()
    {
        var name = GetSelectedScriptName();
        return string.IsNullOrEmpty(name) ? null : _configManager.GetScript(name);
    }

    private async Task RefreshScriptListAsync()
    {
        if (_dgvScripts.IsDisposed || _isRefreshing) return;

        _isRefreshing = true;
        try
        {
            var scripts = _configManager.GetScripts();
            var runningStatus = await Task.Run(() => _scriptRunner.GetRunningStatusWithSystemScan(scripts));

            var dt = new DataTable();
            var colName = dt.Columns.Add("名称", typeof(string));
            colName.Caption = LocalizationManager.GetString("ScriptName");
            var colPath = dt.Columns.Add("路径", typeof(string));
            colPath.Caption = LocalizationManager.GetString("ScriptPath");
            var colArgs = dt.Columns.Add("参数", typeof(string));
            colArgs.Caption = LocalizationManager.GetString("ScriptArguments");
            var colEnabled = dt.Columns.Add("启用", typeof(bool));
            colEnabled.Caption = LocalizationManager.GetString("ScriptEnabled");
            var colOrder = dt.Columns.Add("顺序", typeof(int));
            colOrder.Caption = LocalizationManager.GetString("ScriptOrder");
            var colStatus = dt.Columns.Add("状态", typeof(string));
            colStatus.Caption = LocalizationManager.GetString("ScriptStatus");

            foreach (var script in scripts)
            {
                var row = dt.NewRow();
                row["名称"] = script.Name;
                row["路径"] = script.Path;
                row["参数"] = script.Arguments;
                row["启用"] = script.Enabled;
                row["顺序"] = script.Order;

                bool exists = File.Exists(script.Path);
                bool running = runningStatus.TryGetValue(script.Name, out var r) && r;

                if (!exists)
                    row["状态"] = LocalizationManager.GetString("StatusFileNotFound");
                else if (!script.Enabled)
                    row["状态"] = LocalizationManager.GetString("StatusDisabled");
                else if (running)
                    row["状态"] = LocalizationManager.GetString("StatusRunning");
                else
                    row["状态"] = LocalizationManager.GetString("StatusStopped");

                dt.Rows.Add(row);
            }

            _dgvScripts.DataSource = dt;

            foreach (DataGridViewColumn col in _dgvScripts.Columns)
            {
                col.ReadOnly = col.Name != "启用";
                if (col.Name == "启用")
                {
                    col.FillWeight = 8;
                    col.SortMode = DataGridViewColumnSortMode.Automatic;
                }
                else if (col.Name == "名称")
                    col.FillWeight = 15;
                else if (col.Name == "路径")
                    col.FillWeight = 35;
                else if (col.Name == "参数")
                    col.FillWeight = 20;
                else if (col.Name == "顺序")
                    col.FillWeight = 7;
                else if (col.Name == "状态")
                    col.FillWeight = 15;
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshScriptList()
    {
        _ = RefreshScriptListAsync();
    }

    private void DgvScripts_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_dgvScripts.IsCurrentCellDirty)
        {
            _dgvScripts.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void DgvScripts_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (_isRefreshing) return;
            
            if (e.ColumnIndex >= 0 && e.RowIndex >= 0)
            {
                var col = _dgvScripts.Columns[e.ColumnIndex];
                
                if (col.Name == "启用")
            {
                var name = _dgvScripts.Rows[e.RowIndex].Cells["名称"].Value?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    var script = _configManager.GetScript(name);
                    if (script != null)
                    {
                        var newValue = (bool)(_dgvScripts.Rows[e.RowIndex].Cells["启用"].Value ?? false);
                            if (script.Enabled != newValue)
                            {
                                script.Enabled = newValue;
                                _configManager.UpdateScript(name, script);
                                _logger.Info($"{(newValue ? "启用" : "禁用")}脚本: {name}");
                            }
                        }
                    }
                }
            }
        }

    private async void RefreshAll()
    {
        await RefreshScriptListAsync();
        RefreshServiceStatus();
        Text = LocalizationManager.GetString("AppTitle");
    }

    private void ShowProcessWindow()
    {
        var processForm = new Form
        {
            Text = LocalizationManager.GetString("RunningProcesses"),
            Size = new Size(900, 550),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false
        };

        var splitPanel = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 180,
            FixedPanel = FixedPanel.Panel1,
            BorderStyle = BorderStyle.None
        };

        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };
        var scriptList = new ListBox
        {
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.One,
            Font = new Font("Microsoft YaHei", 10)
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Padding = new Padding(5),
            FlowDirection = FlowDirection.RightToLeft
        };

        var btnTerminate = new Button
        {
            Text = LocalizationManager.GetString("Terminate"),
            Size = new Size(70, 28),
            BackColor = Color.FromArgb(244, 67, 54),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnTerminate.FlatAppearance.BorderSize = 0;

        var btnRefresh = new Button
        {
            Text = LocalizationManager.GetString("Refresh"),
            Size = new Size(70, 28),
            Margin = new Padding(5, 0, 0, 0)
        };

        var btnClearLogs = new Button
        {
            Text = LocalizationManager.GetString("ClearLogs"),
            Size = new Size(80, 28),
            Margin = new Padding(5, 0, 0, 0)
        };

        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };
        
        var resourcePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = Color.FromArgb(25, 25, 25),
            BorderStyle = BorderStyle.FixedSingle
        };

        var cpuLabel = new Label { Text = "CPU: --%", ForeColor = Color.LightBlue, Location = new Point(10, 10), Font = new Font("Microsoft YaHei", 10) };
        var memoryLabel = new Label { Text = "内存: --MB", ForeColor = Color.LightGreen, Location = new Point(120, 10), Font = new Font("Microsoft YaHei", 10) };
        var gpuLabel = new Label { Text = "GPU: --%", ForeColor = Color.LightPink, Location = new Point(230, 10), Font = new Font("Microsoft YaHei", 10) };
        var gpuMemLabel = new Label { Text = "GPU显存: --MB", ForeColor = Color.Orange, Location = new Point(340, 10), Font = new Font("Microsoft YaHei", 10) };
        var pidLabel = new Label { Text = "PID: --", ForeColor = Color.White, Location = new Point(480, 10), Font = new Font("Microsoft YaHei", 10) };

        resourcePanel.Controls.Add(cpuLabel);
        resourcePanel.Controls.Add(memoryLabel);
        resourcePanel.Controls.Add(gpuLabel);
        resourcePanel.Controls.Add(gpuMemLabel);
        resourcePanel.Controls.Add(pidLabel);

        var logTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 10),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap = false
        };

        string selectedScriptName = "";

        void LoadProcesses()
        {
            var runningProcesses = _scriptRunner.GetRunningProcessesWithSystemScan(_configManager.GetScripts());
            var previousSelection = selectedScriptName;

            scriptList.Items.Clear();
            foreach (var (name, process) in runningProcesses)
            {
                try
                {
                    scriptList.Items.Add($"{name} (PID: {process.Id})");
                }
                catch
                {
                }
            }

            if (scriptList.Items.Count == 0)
            {
                scriptList.Items.Add(LocalizationManager.GetString("NoRunningProcesses"));
                logTextBox.Clear();
                logTextBox.AppendText(LocalizationManager.GetString("NoRunningProcesses"));
                selectedScriptName = "";
            }
            else if (!string.IsNullOrEmpty(previousSelection))
            {
                var idx = scriptList.FindString(previousSelection);
                if (idx >= 0)
                {
                    scriptList.SelectedIndex = idx;
                }
            }
            else
            {
                scriptList.SelectedIndex = 0;
            }
        }

        void LoadLogs()
        {
            if (string.IsNullOrEmpty(selectedScriptName))
            {
                logTextBox.Clear();
                cpuLabel.Text = "CPU: --%";
                memoryLabel.Text = "内存: --MB";
                gpuLabel.Text = "GPU: --%";
                gpuMemLabel.Text = "GPU显存: --MB";
                pidLabel.Text = "PID: --";
                return;
            }

            var logs = _scriptRunner.GetScriptLogs(selectedScriptName);
            logTextBox.Clear();
            foreach (var log in logs)
            {
                if (log.Contains("[STDERR]"))
                {
                    logTextBox.SelectionColor = Color.Red;
                }
                else
                {
                    logTextBox.SelectionColor = Color.White;
                }
                logTextBox.AppendText(log + "\n");
            }
            logTextBox.ScrollToCaret();

            _ = Task.Run(() =>
            {
                try
                {
                    var resourceInfo = _scriptRunner.GetProcessResourceInfo(_configManager.GetScripts());
                    if (resourceInfo.TryGetValue(selectedScriptName, out var info) && info.Running)
                    {
                        cpuLabel.Text = $"CPU: {info.CpuPercent:F1}%";
                        memoryLabel.Text = $"内存: {info.MemoryMb}MB";
                        gpuLabel.Text = $"GPU: {info.GpuPercent:F1}%";
                        gpuMemLabel.Text = $"GPU显存: {info.GpuMemoryMb}MB";
                        pidLabel.Text = $"PID: {info.Pid}";
                    }
                    else
                    {
                        cpuLabel.Text = "CPU: --%";
                        memoryLabel.Text = "内存: --MB";
                        gpuLabel.Text = "GPU: --%";
                        gpuMemLabel.Text = "GPU显存: --MB";
                        pidLabel.Text = "PID: --";
                    }
                }
                catch { }
            });
        }

        scriptList.SelectedIndexChanged += (s, e) =>
        {
            if (scriptList.SelectedItem == null) return;
            var text = scriptList.SelectedItem.ToString() ?? "";
            var idx = text.IndexOf(" (PID:");
            selectedScriptName = idx > 0 ? text.Substring(0, idx) : text;
            if (selectedScriptName == LocalizationManager.GetString("NoRunningProcesses"))
            {
                selectedScriptName = "";
            }
            LoadLogs();
        };

        btnRefresh.Click += (s, e) =>
        {
            LoadProcesses();
            LoadLogs();
        };

        btnTerminate.Click += (s, e) =>
        {
            if (string.IsNullOrEmpty(selectedScriptName)) return;

            var script = _configManager.GetScript(selectedScriptName);
            if (script == null) return;

            if (!_scriptRunner.IsRunning(selectedScriptName))
            {
                _scriptRunner.StopScriptFromSystem(selectedScriptName, script.Path);
            }
            else
            {
                _scriptRunner.StopScript(selectedScriptName);
            }
            LoadProcesses();
            RefreshScriptList();
        };

        btnClearLogs.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(selectedScriptName))
            {
                _scriptRunner.ClearScriptLogs(selectedScriptName);
                logTextBox.Clear();
            }
        };

        btnPanel.Controls.Add(btnTerminate);
        btnPanel.Controls.Add(btnClearLogs);
        btnPanel.Controls.Add(btnRefresh);

        leftPanel.Controls.Add(scriptList);
        leftPanel.Controls.Add(btnPanel);

        rightPanel.Controls.Add(resourcePanel);
        rightPanel.Controls.Add(logTextBox);

        splitPanel.Panel1.Controls.Add(leftPanel);
        splitPanel.Panel2.Controls.Add(rightPanel);

        processForm.Controls.Add(splitPanel);

        LoadProcesses();

        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (s, e) =>
        {
            if (!processForm.IsDisposed && !logTextBox.IsDisposed)
            {
                LoadLogs();
            }
            else
            {
                timer.Stop();
                timer.Dispose();
            }
        };
        timer.Start();

        processForm.FormClosing += (s, e) =>
        {
            timer.Stop();
            timer.Dispose();
        };

        processForm.ShowDialog();
    }
}