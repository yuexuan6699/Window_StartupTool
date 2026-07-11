using System.Data;
using System.Drawing.Drawing2D;
using System.ServiceProcess;
using System.Windows.Forms;
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
    private NumericUpDown _nudOrder = null!, _nudMaxCpu = null!, _nudMaxMemory = null!, _nudMaxGpu = null!, _nudRestartDelay = null!;
    private Button _btnBrowse = null!;
    private Label _lblScriptName = null!, _lblScriptPath = null!, _lblScriptArguments = null!, _lblScriptOrder = null!;
    private Label _lblMaxCpu = null!, _lblMaxMemory = null!, _lblMaxGpu = null!, _lblRestartDelay = null!;
    private Button _btnToggleService = null!, _btnRefreshService = null!;
    private Panel _pnlTop = null!, _pnlLeft = null!, _pnlRight = null!, _pnlAddForm = null!;
    private SplitContainer _splitMain = null!;
    private Label _lblScriptsTitle = null!, _lblAddTitle = null!, _lblServiceStatus = null!;
    private Label _lblBasicInfo = null!, _lblResourceLimits = null!;
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
        Size = new Size(1000, 680);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei", 9.5f);

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _pnlTop = CreateTopBar();

        _splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BorderStyle = BorderStyle.None,
            SplitterWidth = 1,
            BackColor = Color.FromArgb(224, 224, 224)
        };

        _pnlLeft = CreateScriptsPanel();
        _pnlRight = CreateAddPanel();
        _splitMain.Panel1.Controls.Add(_pnlLeft);
        _splitMain.Panel2.Controls.Add(_pnlRight);

        mainPanel.Controls.Add(_pnlTop, 0, 0);
        mainPanel.Controls.Add(_splitMain, 0, 1);

        Controls.Add(mainPanel);
        FormClosing += MainForm_FormClosing;
        Resize += MainForm_Resize;
        LocalizationManager.LanguageChanged += RefreshAll;

        // 延迟到窗体首次布局完成后再设置初始分栏位置，用 try-catch 兜底防止任何边界情况崩溃
        // 不设置 Panel1MinSize/Panel2MinSize（用默认值 25px），避免其内部触发 SplitterDistance 校验
        Load += (s, e) => BeginInvoke(new Action(() =>
        {
            try
            {
                int totalWidth = _splitMain.Width;
                if (totalWidth > 100)
                {
                    int initialDist = (int)(totalWidth * 0.62);
                    int maxDist = totalWidth - _splitMain.SplitterWidth - 25;
                    if (initialDist > maxDist) initialDist = maxDist;
                    if (initialDist < 25) initialDist = 25;
                    _splitMain.SplitterDistance = initialDist;
                }
            }
            catch { }
        }));
    }

    private Panel CreateTopBar()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White
        };

        // 底部分隔线
        panel.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(224, 224, 224), 1);
            e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
        };

        // 左侧：服务操作
        _btnToggleService = new Button
        {
            Text = LocalizationManager.GetString("EnableAutoStart"),
            Size = new Size(120, 32),
            Location = new Point(12, 8),
            BackColor = Color.FromArgb(76, 175, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei", 9.5f)
        };
        _btnToggleService.FlatAppearance.BorderSize = 0;
        _btnToggleService.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Color.FromArgb(76, 175, 80), 0.2f);
        _btnToggleService.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(Color.FromArgb(76, 175, 80), 0.1f);
        _btnToggleService.Click += BtnToggleService_Click;

        _btnRefreshService = new Button
        {
            Text = "⟳",
            Size = new Size(32, 32),
            Location = new Point(140, 8),
            BackColor = Color.FromArgb(245, 245, 245),
            ForeColor = Color.FromArgb(80, 80, 80),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei", 13f)
        };
        _btnRefreshService.FlatAppearance.BorderSize = 0;
        _btnRefreshService.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 235, 235);
        _btnRefreshService.FlatAppearance.MouseDownBackColor = Color.FromArgb(225, 225, 225);
        _btnRefreshService.Click += (s, e) => RefreshServiceStatus();

        _lblServiceStatus = new Label
        {
            Text = LocalizationManager.GetString("ServiceUnknown"),
            AutoSize = false,
            Size = new Size(240, 32),
            Location = new Point(182, 8),
            ForeColor = Color.Gray,
            Font = new Font("Microsoft YaHei", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        // 右侧：语言切换
        var btnChinese = new Button
        {
            Text = LocalizationManager.GetString("Chinese"),
            Size = new Size(70, 28),
            BackColor = LocalizationManager.CurrentLanguage == Language.Chinese ? Color.FromArgb(33, 150, 243) : Color.FromArgb(245, 245, 245),
            ForeColor = LocalizationManager.CurrentLanguage == Language.Chinese ? Color.White : Color.FromArgb(80, 80, 80),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei", 9f)
        };
        btnChinese.FlatAppearance.BorderSize = 0;

        var btnEnglish = new Button
        {
            Text = LocalizationManager.GetString("English"),
            Size = new Size(70, 28),
            BackColor = LocalizationManager.CurrentLanguage == Language.English ? Color.FromArgb(33, 150, 243) : Color.FromArgb(245, 245, 245),
            ForeColor = LocalizationManager.CurrentLanguage == Language.English ? Color.White : Color.FromArgb(80, 80, 80),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei", 9f)
        };
        btnEnglish.FlatAppearance.BorderSize = 0;

        void PositionLanguageButtons()
        {
            btnChinese.Location = new Point(panel.Width - 150, 10);
            btnEnglish.Location = new Point(panel.Width - 75, 10);
        }
        PositionLanguageButtons();
        panel.Resize += (s, e) => PositionLanguageButtons();

        btnChinese.Click += (s, e) =>
        {
            LocalizationManager.CurrentLanguage = Language.Chinese;
            UpdateLanguageButtons(btnChinese, btnEnglish);
        };
        btnEnglish.Click += (s, e) =>
        {
            LocalizationManager.CurrentLanguage = Language.English;
            UpdateLanguageButtons(btnChinese, btnEnglish);
        };

        panel.Controls.Add(_btnToggleService);
        panel.Controls.Add(_btnRefreshService);
        panel.Controls.Add(_lblServiceStatus);
        panel.Controls.Add(btnChinese);
        panel.Controls.Add(btnEnglish);

        return panel;
    }

    private void UpdateLanguageButtons(Button btnChinese, Button btnEnglish)
    {
        bool isChinese = LocalizationManager.CurrentLanguage == Language.Chinese;
        var active = Color.FromArgb(33, 150, 243);
        var normal = Color.FromArgb(245, 245, 245);
        var activeFore = Color.White;
        var normalFore = Color.FromArgb(80, 80, 80);

        btnChinese.BackColor = isChinese ? active : normal;
        btnChinese.ForeColor = isChinese ? activeFore : normalFore;
        btnEnglish.BackColor = !isChinese ? active : normal;
        btnEnglish.ForeColor = !isChinese ? activeFore : normalFore;
    }

    private Panel CreateScriptsPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White
        };

        _lblScriptsTitle = new Label
        {
            Text = "  " + LocalizationManager.GetString("ScriptList"),
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(66, 66, 66),
            BackColor = Color.FromArgb(250, 250, 250),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var titleSep = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Color.FromArgb(224, 224, 224)
        };

        _dgvScripts = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(238, 238, 238),
            Font = new Font("Microsoft YaHei", 9.5f),
            RowTemplate = { Height = 34, DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(8, 4, 8, 4) } },
            ContextMenuStrip = CreateScriptContextMenu()
        };
        _dgvScripts.DefaultCellStyle.SelectionBackColor = Color.FromArgb(230, 240, 255);
        _dgvScripts.DefaultCellStyle.SelectionForeColor = Color.FromArgb(0, 0, 0);
        _dgvScripts.DefaultCellStyle.BackColor = Color.White;
        _dgvScripts.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 250);
        _dgvScripts.ColumnHeadersHeight = 34;
        _dgvScripts.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _dgvScripts.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 250);
        _dgvScripts.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(80, 80, 80);
        _dgvScripts.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Bold);
        _dgvScripts.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _dgvScripts.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _dgvScripts.CellValueChanged += DgvScripts_CellValueChanged;
        _dgvScripts.CurrentCellDirtyStateChanged += DgvScripts_CurrentCellDirtyStateChanged;

        var btnPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            BackColor = Color.FromArgb(250, 250, 250)
        };

        var btnSep = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Color.FromArgb(224, 224, 224)
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 8, 8, 8),
            BackColor = Color.FromArgb(250, 250, 250)
        };

        _btnShowProcesses = CreateFlatButton(LocalizationManager.GetString("RunningProcesses"), Color.FromArgb(76, 175, 80));
        _btnShowProcesses.Click += (s, e) => ShowProcessWindow();
        _btnStartSelected = CreateFlatButton(LocalizationManager.GetString("StartSelected"), Color.FromArgb(33, 150, 243));
        _btnStartSelected.Click += BtnStartSelected_Click;
        _btnStopSelected = CreateFlatButton(LocalizationManager.GetString("StopSelected"), Color.FromArgb(255, 152, 0));
        _btnStopSelected.Click += BtnStopSelected_Click;
        _btnStartAll = CreateFlatButton(LocalizationManager.GetString("BatchStart"), Color.FromArgb(33, 150, 243));
        _btnStartAll.Click += BtnStartAll_Click;
        _btnStopAll = CreateFlatButton(LocalizationManager.GetString("StopAll"), Color.FromArgb(120, 120, 120));
        _btnStopAll.Click += BtnStopAll_Click;
        _btnRemove = CreateFlatButton(LocalizationManager.GetString("RemoveSelected"), Color.FromArgb(244, 67, 54));
        _btnRemove.Click += BtnRemove_Click;

        flow.Controls.Add(_btnShowProcesses);
        flow.Controls.Add(_btnStartSelected);
        flow.Controls.Add(_btnStopSelected);
        flow.Controls.Add(_btnStartAll);
        flow.Controls.Add(_btnStopAll);
        flow.Controls.Add(_btnRemove);

        btnPanel.Controls.Add(flow);
        btnPanel.Controls.Add(btnSep);

        panel.Controls.Add(_dgvScripts);
        panel.Controls.Add(btnPanel);
        panel.Controls.Add(titleSep);
        panel.Controls.Add(_lblScriptsTitle);

        return panel;
    }

    private Panel CreateAddPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White
        };

        _lblAddTitle = new Label
        {
            Text = "  " + LocalizationManager.GetString("AddScript"),
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(66, 66, 66),
            BackColor = Color.FromArgb(250, 250, 250),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var titleSep = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Color.FromArgb(224, 224, 224)
        };

        _pnlAddForm = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White
        };

        var formLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 11,
            BackColor = Color.White,
            Padding = new Padding(16, 12, 16, 12)
        };
        formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 行0: 基本信息分组标题
        _lblBasicInfo = CreateGroupLabel(LocalizationManager.GetString("BasicInfo"));
        formLayout.Controls.Add(_lblBasicInfo, 0, 0);
        formLayout.SetColumnSpan(_lblBasicInfo, 2);

        // 行1: 名称
        _lblScriptName = CreateFieldLabel(LocalizationManager.GetString("ScriptName"));
        _txtName = CreateFieldTextBox();
        formLayout.Controls.Add(_lblScriptName, 0, 1);
        formLayout.Controls.Add(_txtName, 1, 1);

        // 行2: 路径 + 浏览
        _lblScriptPath = CreateFieldLabel(LocalizationManager.GetString("ScriptPath"));
        var pathTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White,
            Margin = new Padding(0)
        };
        pathTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _txtPath = CreateFieldTextBox();
        _btnBrowse = new Button
        {
            Text = LocalizationManager.GetString("Browse"),
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 4, 0, 4),
            BackColor = Color.FromArgb(245, 245, 245),
            ForeColor = Color.FromArgb(80, 80, 80),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei", 9f)
        };
        _btnBrowse.FlatAppearance.BorderSize = 0;
        _btnBrowse.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 235, 235);
        _btnBrowse.FlatAppearance.MouseDownBackColor = Color.FromArgb(225, 225, 225);
        _btnBrowse.Click += (s, e) => BrowseScriptPath();
        pathTable.Controls.Add(_txtPath, 0, 0);
        pathTable.Controls.Add(_btnBrowse, 1, 0);
        formLayout.Controls.Add(_lblScriptPath, 0, 2);
        formLayout.Controls.Add(pathTable, 1, 2);

        // 行3: 参数
        _lblScriptArguments = CreateFieldLabel(LocalizationManager.GetString("ScriptArguments"));
        _txtArguments = CreateFieldTextBox();
        formLayout.Controls.Add(_lblScriptArguments, 0, 3);
        formLayout.Controls.Add(_txtArguments, 1, 3);

        // 行4: 顺序
        _lblScriptOrder = CreateFieldLabel(LocalizationManager.GetString("ScriptOrder"));
        _nudOrder = CreateFieldNumeric(0, 999, 0);
        formLayout.Controls.Add(_lblScriptOrder, 0, 4);
        formLayout.Controls.Add(_nudOrder, 1, 4);

        // 行5: 资源限制分组标题
        _lblResourceLimits = CreateGroupLabel(LocalizationManager.GetString("ResourceLimits"));
        formLayout.Controls.Add(_lblResourceLimits, 0, 5);
        formLayout.SetColumnSpan(_lblResourceLimits, 2);

        // 行6: CPU上限
        _lblMaxCpu = CreateFieldLabel(LocalizationManager.GetString("MaxCpuPercent"));
        _nudMaxCpu = CreateFieldNumeric(0, 100, 90);
        formLayout.Controls.Add(_lblMaxCpu, 0, 6);
        formLayout.Controls.Add(_nudMaxCpu, 1, 6);

        // 行7: 内存上限
        _lblMaxMemory = CreateFieldLabel(LocalizationManager.GetString("MaxMemoryMb"));
        _nudMaxMemory = CreateFieldNumeric(0, 65535, 0);
        formLayout.Controls.Add(_lblMaxMemory, 0, 7);
        formLayout.Controls.Add(_nudMaxMemory, 1, 7);

        // 行8: GPU上限
        _lblMaxGpu = CreateFieldLabel(LocalizationManager.GetString("MaxGpuPercent"));
        _nudMaxGpu = CreateFieldNumeric(0, 100, 90);
        formLayout.Controls.Add(_lblMaxGpu, 0, 8);
        formLayout.Controls.Add(_nudMaxGpu, 1, 8);

        // 行9: 重启延迟
        _lblRestartDelay = CreateFieldLabel(LocalizationManager.GetString("RestartDelaySeconds"));
        _nudRestartDelay = CreateFieldNumeric(0, 3600, 10);
        formLayout.Controls.Add(_lblRestartDelay, 0, 9);
        formLayout.Controls.Add(_nudRestartDelay, 1, 9);

        // 行10: 添加脚本按钮（修复原来未绑定 Click 事件的 bug）
        _btnAdd = CreateFlatButton(LocalizationManager.GetString("AddScript"), Color.FromArgb(76, 175, 80));
        _btnAdd.Dock = DockStyle.Fill;
        _btnAdd.Click += BtnAdd_Click;
        formLayout.Controls.Add(_btnAdd, 0, 10);
        formLayout.SetColumnSpan(_btnAdd, 2);

        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        _pnlAddForm.Controls.Add(formLayout);

        panel.Controls.Add(_pnlAddForm);
        panel.Controls.Add(titleSep);
        panel.Controls.Add(_lblAddTitle);

        return panel;
    }

    private Label CreateGroupLabel(string text)
    {
        return new Label
        {
            Text = "— " + text + " —",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(120, 120, 120),
            BackColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 6, 0, 4)
        };
    }

    private Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei", 9.5f),
            ForeColor = Color.FromArgb(80, 80, 80),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 4, 4, 4)
        };
    }

    private TextBox CreateFieldTextBox()
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei", 9.5f)
        };
    }

    private NumericUpDown CreateFieldNumeric(decimal min, decimal max, decimal value)
    {
        return new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = min,
            Maximum = max,
            Value = value,
            Margin = new Padding(0, 4, 0, 4),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei", 9.5f)
        };
    }

    private Button CreateFlatButton(string text, Color backColor)
    {
        var btn = new Button
        {
            Text = text,
            Size = new Size(110, 32),
            BackColor = backColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei", 9.5f)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.2f);
        btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.1f);
        return btn;
    }

    private Button CreateButton(string text, Color backColor, EventHandler clickHandler)
    {
        var btn = CreateFlatButton(text, backColor);
        btn.Click += clickHandler;
        return btn;
    }

    private ContextMenuStrip _scriptContextMenu = null!;
    private ToolStripMenuItem _menuStart = null!;
    private ToolStripMenuItem _menuStop = null!;
    private ToolStripMenuItem _menuEnable = null!;
    private ToolStripMenuItem _menuDisable = null!;

    private ContextMenuStrip CreateScriptContextMenu()
    {
        _scriptContextMenu = new ContextMenuStrip();
        _menuStart = new ToolStripMenuItem(LocalizationManager.GetString("Start"));
        _menuStop = new ToolStripMenuItem(LocalizationManager.GetString("Stop"));
        _menuEnable = new ToolStripMenuItem(LocalizationManager.GetString("Enable"));
        _menuDisable = new ToolStripMenuItem(LocalizationManager.GetString("Disable"));

        _menuStart.Click += MenuStartScript_Click;
        _menuStop.Click += MenuStopScript_Click;
        _scriptContextMenu.Items.Add(new ToolStripSeparator());
        _menuEnable.Click += MenuToggleEnabled_Click;
        _menuDisable.Click += MenuToggleEnabled_Click;

        _scriptContextMenu.Items.AddRange(new ToolStripItem[] { _menuStart, _menuStop, new ToolStripSeparator(), _menuEnable, _menuDisable });
        return _scriptContextMenu;
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
            Arguments = arguments,
            MaxCpuPercent = (int)_nudMaxCpu.Value,
            MaxMemoryMb = (long)_nudMaxMemory.Value,
            MaxGpuPercent = (int)_nudMaxGpu.Value,
            RestartDelaySeconds = (int)_nudRestartDelay.Value
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
        _nudMaxCpu.Value = 90;
        _nudMaxMemory.Value = 0;
        _nudMaxGpu.Value = 90;
        _nudRestartDelay.Value = 10;
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
        var scripts = _configManager.GetScripts();
        foreach (var script in scripts)
        {
            _scriptRunner.StopScript(script.Name, script);
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

        _scriptRunner.StopScript(script.Name, script);
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

        _scriptRunner.StopScript(script.Name, script);
        RefreshScriptList();
    }

    private void MenuToggleEnabled_Click(object? sender, EventArgs e)
    {
        var script = GetSelectedScript();
        if (script == null) return;

        var toolStripItem = sender as ToolStripMenuItem;
        bool enable = toolStripItem == _menuEnable;

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
            var runningStatus = await Task.Run(() => _scriptRunner.GetUnifiedStatus(scripts));

            var selectedName = GetSelectedScriptName();

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

            // 设置列标题本地化
            foreach (DataGridViewColumn col in _dgvScripts.Columns)
            {
                col.HeaderText = col.Name switch
                {
                    "名称" => LocalizationManager.GetString("ScriptName"),
                    "路径" => LocalizationManager.GetString("ScriptPath"),
                    "参数" => LocalizationManager.GetString("ScriptArguments"),
                    "启用" => LocalizationManager.GetString("ScriptEnabled"),
                    "顺序" => LocalizationManager.GetString("ScriptOrder"),
                    "状态" => LocalizationManager.GetString("ScriptStatus"),
                    _ => col.HeaderText
                };

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

            if (!string.IsNullOrEmpty(selectedName))
            {
                foreach (DataGridViewRow row in _dgvScripts.Rows)
                {
                    if (row.Cells["名称"].Value?.ToString() == selectedName)
                    {
                        row.Selected = true;
                        _dgvScripts.CurrentCell = row.Cells["名称"];
                        break;
                    }
                }
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
        Text = LocalizationManager.GetString("AppTitle");
        _lblScriptsTitle.Text = "  " + LocalizationManager.GetString("ScriptList");
        _lblAddTitle.Text = "  " + LocalizationManager.GetString("AddScript");
        _lblBasicInfo.Text = "— " + LocalizationManager.GetString("BasicInfo") + " —";
        _lblResourceLimits.Text = "— " + LocalizationManager.GetString("ResourceLimits") + " —";

        _btnShowProcesses.Text = LocalizationManager.GetString("RunningProcesses");
        _btnStartSelected.Text = LocalizationManager.GetString("StartSelected");
        _btnStopSelected.Text = LocalizationManager.GetString("StopSelected");
        _btnStartAll.Text = LocalizationManager.GetString("BatchStart");
        _btnStopAll.Text = LocalizationManager.GetString("StopAll");
        _btnRemove.Text = LocalizationManager.GetString("RemoveSelected");
        _btnAdd.Text = LocalizationManager.GetString("AddScript");

        _lblScriptName.Text = LocalizationManager.GetString("ScriptName");
        _lblScriptPath.Text = LocalizationManager.GetString("ScriptPath");
        _lblScriptArguments.Text = LocalizationManager.GetString("ScriptArguments");
        _lblScriptOrder.Text = LocalizationManager.GetString("ScriptOrder");
        _lblMaxCpu.Text = LocalizationManager.GetString("MaxCpuPercent");
        _lblMaxMemory.Text = LocalizationManager.GetString("MaxMemoryMb");
        _lblMaxGpu.Text = LocalizationManager.GetString("MaxGpuPercent");
        _lblRestartDelay.Text = LocalizationManager.GetString("RestartDelaySeconds");
        _btnBrowse.Text = LocalizationManager.GetString("Browse");
        
        _notifyIcon.Text = LocalizationManager.GetString("AppTitle");
        _notifyIcon.ContextMenuStrip.Items[0].Text = LocalizationManager.GetString("ShowWindow");
        _notifyIcon.ContextMenuStrip.Items[2].Text = LocalizationManager.GetString("StartAllScripts");
        _notifyIcon.ContextMenuStrip.Items[3].Text = LocalizationManager.GetString("StopAllScripts");
        _notifyIcon.ContextMenuStrip.Items[5].Text = LocalizationManager.GetString("Exit");
        
        if (_scriptContextMenu != null)
        {
            _menuStart.Text = LocalizationManager.GetString("Start");
            _menuStop.Text = LocalizationManager.GetString("Stop");
            _menuEnable.Text = LocalizationManager.GetString("Enable");
            _menuDisable.Text = LocalizationManager.GetString("Disable");
        }
        
        // 异步刷新数据
        _ = RefreshScriptListAsync();
        RefreshServiceStatus();
    }

    #region 运行中的脚本窗口

    private void ShowProcessWindow()
    {
        var processForm = new Form
        {
            Text = LocalizationManager.GetString("RunningProcesses"),
            Size = new Size(1000, 600),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = true,
            MinimumSize = new Size(700, 450),
            Font = new Font("Microsoft YaHei", 9.5f),
            BackColor = Color.White
        };

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Color.White
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var resourcePanel = CreateResourcePanel(out var cpuLabel, out var memoryLabel, out var gpuLabel, out var gpuMemLabel, out var pidLabel, out var statusLabel);

        var splitPanel = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 280,
            FixedPanel = FixedPanel.Panel1,
            BorderStyle = BorderStyle.None
        };

        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White };
        leftPanel.DoubleBuffered(true);
        
        var scriptList = new ListBox
        {
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.One,
            Font = new Font("Microsoft YaHei", 10),
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ItemHeight = 28,
            DrawMode = DrawMode.OwnerDrawFixed
        };
        scriptList.DrawItem += (s, e) =>
        {
            e.DrawBackground();
            var isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var brush = isSelected ? new SolidBrush(Color.FromArgb(230, 240, 255)) : new SolidBrush(Color.White);
            e.Graphics.FillRectangle(brush, e.Bounds);
            var text = scriptList.Items[e.Index].ToString();
            var textBrush = new SolidBrush(isSelected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(60, 60, 60));
            e.Graphics.DrawString(text, e.Font ?? scriptList.Font, textBrush, e.Bounds.X + 8, e.Bounds.Y + 5);
            e.DrawFocusRectangle();
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(5, 6, 5, 5),
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.White
        };

        var btnTerminate = CreateModernButton(LocalizationManager.GetString("Terminate"), Color.FromArgb(244, 67, 54), 80, 30);
        var btnClearLogs = CreateModernButton(LocalizationManager.GetString("ClearLogs"), Color.FromArgb(158, 158, 158), 85, 30);
        var btnRefresh = CreateModernButton(LocalizationManager.GetString("Refresh"), Color.FromArgb(0, 120, 215), 70, 30);

        btnPanel.Controls.Add(btnTerminate);
        btnPanel.Controls.Add(btnClearLogs);
        btnPanel.Controls.Add(btnRefresh);

        leftPanel.Controls.Add(scriptList);
        leftPanel.Controls.Add(btnPanel);

        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.FromArgb(30, 30, 30) };

        var logTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 9.5f),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.None,
            WordWrap = false
        };

        rightPanel.Controls.Add(logTextBox);

        splitPanel.Panel1.Controls.Add(leftPanel);
        splitPanel.Panel2.Controls.Add(rightPanel);

        var scriptListHeader = CreateSectionHeader(LocalizationManager.GetString("ScriptList"));
        var scriptLogsHeader = CreateSectionHeader(LocalizationManager.GetString("ScriptLogs"));
        mainPanel.Controls.Add(resourcePanel, 0, 0);
        mainPanel.Controls.Add(scriptListHeader, 0, 1);
        mainPanel.Controls.Add(splitPanel, 0, 2);
        mainPanel.Controls.Add(scriptLogsHeader, 0, 3);

        processForm.Controls.Add(mainPanel);

        LocalizationManager.LanguageChanged += () => UpdateProcessWindowLanguage(processForm, btnTerminate, btnClearLogs, btnRefresh, scriptListHeader, scriptLogsHeader);

        string selectedScriptName = "";

        void LoadProcesses()
        {
            var runningInfos = _scriptRunner.GetRunningProcessInfos(_configManager.GetScripts());
            var previousSelection = selectedScriptName;

            scriptList.BeginUpdate();
            try
            {
                scriptList.Items.Clear();
                foreach (var (name, pid) in runningInfos)
                {
                    scriptList.Items.Add($"{name}  (PID: {pid})");
                }

                if (scriptList.Items.Count == 0)
                {
                    scriptList.Items.Add(LocalizationManager.GetString("NoRunningProcesses"));
                    logTextBox.Clear();
                    logTextBox.AppendText(LocalizationManager.GetString("NoRunningProcesses"));
                    selectedScriptName = "";
                    statusLabel.Text = LocalizationManager.GetString("NoRunningProcesses");
                    statusLabel.ForeColor = Color.Gray;
                    UpdateResourceLabels(cpuLabel, memoryLabel, gpuLabel, gpuMemLabel, pidLabel, null);
                }
                else if (!string.IsNullOrEmpty(previousSelection))
                {
                    var idx = scriptList.FindString(previousSelection);
                    if (idx >= 0)
                    {
                        scriptList.SelectedIndex = idx;
                    }
                    else
                    {
                        scriptList.SelectedIndex = 0;
                    }
                }
                else
                {
                    scriptList.SelectedIndex = 0;
                }

                if (scriptList.SelectedIndex >= 0 && scriptList.Items[scriptList.SelectedIndex].ToString() != LocalizationManager.GetString("NoRunningProcesses"))
                {
                    statusLabel.Text = LocalizationManager.GetString("StatusRunning");
                    statusLabel.ForeColor = Color.FromArgb(76, 175, 80);
                }
            }
            finally
            {
                scriptList.EndUpdate();
            }
        }

        void LoadLogs()
        {
            if (string.IsNullOrEmpty(selectedScriptName))
            {
                logTextBox.Clear();
                UpdateResourceLabels(cpuLabel, memoryLabel, gpuLabel, gpuMemLabel, pidLabel, null);
                return;
            }

            var logs = _scriptRunner.GetScriptLogs(selectedScriptName);
            logTextBox.SuspendLayout();
            try
            {
                logTextBox.Clear();
                foreach (var log in logs)
                {
                    if (log.Contains("[STDERR]"))
                    {
                        logTextBox.SelectionColor = Color.FromArgb(255, 150, 150);
                    }
                    else if (log.Contains("[ERROR]"))
                    {
                        logTextBox.SelectionColor = Color.FromArgb(255, 180, 100);
                    }
                    else if (log.Contains("[WARN]"))
                    {
                        logTextBox.SelectionColor = Color.FromArgb(255, 220, 100);
                    }
                    else if (log.Contains("[SUCCESS]"))
                    {
                        logTextBox.SelectionColor = Color.FromArgb(100, 220, 150);
                    }
                    else
                    {
                        logTextBox.SelectionColor = Color.FromArgb(180, 180, 180);
                    }
                    logTextBox.AppendText(log + "\n");
                }
                logTextBox.ScrollToCaret();
            }
            finally
            {
                logTextBox.ResumeLayout();
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var resourceInfo = _scriptRunner.GetProcessResourceInfo(_configManager.GetScripts());
                    if (resourceInfo.TryGetValue(selectedScriptName, out var info) && info.Running)
                    {
                        processForm.InvokeIfRequired(() =>
                        {
                            UpdateResourceLabels(cpuLabel, memoryLabel, gpuLabel, gpuMemLabel, pidLabel, info);
                        });
                    }
                    else
                    {
                        processForm.InvokeIfRequired(() =>
                        {
                            UpdateResourceLabels(cpuLabel, memoryLabel, gpuLabel, gpuMemLabel, pidLabel, null);
                        });
                    }
                }
                catch { }
            });
        }

        scriptList.SelectedIndexChanged += (s, e) =>
        {
            if (scriptList.SelectedItem == null) return;
            var text = scriptList.SelectedItem.ToString() ?? "";
            var idx = text.IndexOf("  (PID:");
            selectedScriptName = idx > 0 ? text.Substring(0, idx) : text;
            if (selectedScriptName == LocalizationManager.GetString("NoRunningProcesses"))
            {
                selectedScriptName = "";
                statusLabel.Text = LocalizationManager.GetString("NoRunningProcesses");
                statusLabel.ForeColor = Color.Gray;
            }
            else
            {
                statusLabel.Text = LocalizationManager.GetString("StatusRunning");
                statusLabel.ForeColor = Color.FromArgb(76, 175, 80);
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

            _scriptRunner.StopScript(selectedScriptName, script);
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

    private void UpdateProcessWindowLanguage(Form processForm, Button btnTerminate, Button btnClearLogs, Button btnRefresh, Label scriptListHeader, Label scriptLogsHeader)
    {
        processForm.InvokeIfRequired(() =>
        {
            processForm.Text = LocalizationManager.GetString("RunningProcesses");
            btnTerminate.Text = LocalizationManager.GetString("Terminate");
            btnClearLogs.Text = LocalizationManager.GetString("ClearLogs");
            btnRefresh.Text = LocalizationManager.GetString("Refresh");
            scriptListHeader.Text = "  " + LocalizationManager.GetString("ScriptList");
            scriptLogsHeader.Text = "  " + LocalizationManager.GetString("ScriptLogs");
        });
    }

    private Panel CreateResourcePanel(out Label cpuLabel, out Label memoryLabel, out Label gpuLabel, out Label gpuMemLabel, out Label pidLabel, out Label statusLabel)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 6),
            RowCount = 2,
            ColumnCount = 6
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

        statusLabel = new Label
        {
            Text = LocalizationManager.GetString("StatusRunning"),
            ForeColor = Color.FromArgb(76, 175, 80),
            Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        panel.Controls.Add(statusLabel, 0, 0);
        panel.SetColumnSpan(statusLabel, 2);

        var titleLabel = new Label
        {
            Text = LocalizationManager.GetString("ResourceMonitor"),
            ForeColor = Color.FromArgb(80, 80, 80),
            Font = new Font("Microsoft YaHei", 8.5f),
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill
        };
        panel.Controls.Add(titleLabel, 2, 0);
        panel.SetColumnSpan(titleLabel, 2);

        cpuLabel = CreateResourceInfoLabel("CPU", "--%", Color.FromArgb(33, 150, 243));
        memoryLabel = CreateResourceInfoLabel("内存", "--MB", Color.FromArgb(76, 175, 80));
        gpuLabel = CreateResourceInfoLabel("GPU", "--%", Color.FromArgb(233, 30, 99));
        gpuMemLabel = CreateResourceInfoLabel("显存", "--MB", Color.FromArgb(255, 152, 0));
        pidLabel = CreateResourceInfoLabel("PID", "--", Color.FromArgb(120, 120, 120));

        panel.Controls.Add(cpuLabel, 0, 1);
        panel.Controls.Add(memoryLabel, 1, 1);
        panel.Controls.Add(gpuLabel, 2, 1);
        panel.Controls.Add(gpuMemLabel, 3, 1);
        panel.Controls.Add(pidLabel, 4, 1);

        return panel;
    }

    private Label CreateResourceInfoLabel(string title, string value, Color color)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(4)
        };

        var titleLbl = new Label
        {
            Text = title,
            ForeColor = Color.FromArgb(80, 80, 80),
            Font = new Font("Microsoft YaHei", 8f),
            TextAlign = ContentAlignment.BottomLeft,
            Dock = DockStyle.Fill
        };

        var valueLbl = new Label
        {
            Text = value,
            ForeColor = color,
            Font = new Font("Consolas", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.TopLeft,
            Dock = DockStyle.Fill
        };

        panel.Controls.Add(titleLbl, 0, 0);
        panel.Controls.Add(valueLbl, 0, 1);

        return valueLbl;
    }

    private void UpdateResourceLabels(Label cpuLabel, Label memoryLabel, Label gpuLabel, Label gpuMemLabel, Label pidLabel, ProcessResourceInfo? info)
    {
        if (info == null || !info.Running)
        {
            cpuLabel.Text = "--%";
            memoryLabel.Text = "--MB";
            gpuLabel.Text = "--%";
            gpuMemLabel.Text = "--MB";
            pidLabel.Text = "--";
        }
        else
        {
            cpuLabel.Text = $"{info.CpuPercent:F1}%";
            memoryLabel.Text = $"{info.MemoryMb}MB";
            gpuLabel.Text = $"{info.GpuPercent:F1}%";
            gpuMemLabel.Text = $"{info.GpuMemoryMb}MB";
            pidLabel.Text = info.Pid.ToString();
        }
    }

    private Button CreateModernButton(string text, Color backColor, int width, int height)
    {
        var btn = new Button
        {
            Text = text,
            Size = new Size(width, height),
            BackColor = backColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei", 9f),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.2f);
        btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.1f);
        return btn;
    }

    private Label CreateSectionHeader(string text)
    {
        return new Label
        {
            Text = "  " + text,
            Dock = DockStyle.Fill,
            Height = 28,
            ForeColor = Color.FromArgb(66, 66, 66),
            Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Bold),
            BackColor = Color.FromArgb(250, 250, 250),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    #endregion
}

public static class ControlExtensions
{
    public static void DoubleBuffered(this Control control, bool enabled)
    {
        if (control is not ListBox)
            return;
        
        var prop = control.GetType().GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        prop?.SetValue(control, enabled, null);
    }

    public static void InvokeIfRequired(this Control control, Action action)
    {
        if (control.InvokeRequired)
        {
            control.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
