using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Tesseract;

namespace TextToSpeechStudio
{
    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool LockWindowUpdate(IntPtr hWndLock);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        public static int ActiveAhkPid = -1;

        public static bool KillActiveAhk(string ahkPath)
        {
            bool killed = false;

            if (ActiveAhkPid != -1)
            {
                try
                {
                    Process p = Process.GetProcessById(ActiveAhkPid);
                    if (!p.HasExited)
                    {
                        p.Kill(true); 
                        p.WaitForExit(500);
                        killed = true;
                    }
                }
                catch { }
                ActiveAhkPid = -1;
            }

            try
            {
                string scriptName = Path.GetFileName(ahkPath);
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name like 'AutoHotkey%'\\\" | Where-Object {{$_.CommandLine -match '{scriptName}'}} | Invoke-CimMethod -MethodName Terminate\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (Process ps = Process.Start(psi))
                {
                    ps.WaitForExit(1500);
                    if (ps.ExitCode == 0) killed = true;
                }
            }
            catch { }

            return killed;
        }

        private readonly object spoolerLock = new object();
        private readonly object ocrEngineLock = new object();
        private List<string> ocrSpoolerQueue = new List<string>();
        private bool isSpoolerRunning = false;

        private SplitContainer splitMain;
        private SplitContainer splitText;
        private TabControl tabLeft;
        private TabPage tabFiles;
        private TabPage tabVoices;
        
        private Panel panelListControls;
        private Button btnCheckAll;
        private Button btnCheckDuplicates;
        private Button btnUncheckAll;
        private ComboBox cbSessions;
        private Button btnNewSession;
        private Button btnDeleteSession;
        private Label lblBatchProgress;
        private ListView lvFiles;
        private int sortColumn = -1;
        
        private DataGridView dgvVoices;
        
        private RichTextBox rtbText;
        private RichTextBox rtbLog;
        private Panel panelControls;
        
        private Button btnPlay, btnPause, btnResume, btnSkip, btnStop, btnExportMp3, btnPaste, btnAhk;
        private CheckBox chkHighlight;
        private CheckBox chkLog; 
        private CheckBox chkClipboard;
        private TrackBar tbVolume;
        private Label lblVolume;
        private NumericUpDown nudSpeed; 
        private Label lblSpeed;   
        private Label lblSimilarity;
        private NumericUpDown nudSimilarity;
        private Label lblMemory; 
        private NumericUpDown nudMemory; 
        
        private MenuStrip menuStrip;
        
        private SpeechSynthesizer synthesizer;
        private List<string> installedVoices;
        
        private const string ConfigFile = "config.json";
        private const string LogFile = "log.json";
        private AppConfig config = new AppConfig();

        private List<SpeechSegment> playbackQueue = new List<SpeechSegment>();
        private int currentSegmentIndex = 0;
        private bool isLivePlaying = false;
        private bool isPaused = false;
        private bool isSkipping = false; 
        private bool isAudioCommandRunning = false;
        private string lastClipboardText = "";

        private List<string> livePredictiveMemory = new List<string>();
        private Dictionary<string, string> liveVoiceMap = new Dictionary<string, string>();
        private string liveDefaultVoice = null;

        private Dictionary<string, string> currentSessionHashCache = new Dictionary<string, string>();
        private Dictionary<string, bool> isFileDuplicateCache = new Dictionary<string, bool>();

        public MainForm()
        {
            InitializeSpeech();
            InitializeComponents();
            LoadConfig();
            this.FormClosing += MainForm_FormClosing;
            LogEvent("System", "Studio gestartet. Saubere Datei-Trennung und Abkürzungs-Korrekturen aktiv!");
        }

        public static string GetFileHash(string filePath)
        {
            try 
            {
                using (var md5 = MD5.Create())
                {
                    using (var stream = File.OpenRead(filePath))
                    {
                        var hash = md5.ComputeHash(stream);
                        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    }
                }
            } 
            catch { return "ERROR"; }
        }

        private string GetCachedHash(string filePath)
        {
            if (currentSessionHashCache.ContainsKey(filePath)) return currentSessionHashCache[filePath];
            string hash = GetFileHash(filePath);
            if (hash != "ERROR") currentSessionHashCache[filePath] = hash;
            return hash;
        }

        public static double CalculateSimilarity(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0;
            if (str1 == str2) return 1;
            int len1 = str1.Length - 1;
            int len2 = str2.Length - 1;
            if (len1 < 1 || len2 < 1) return 0;

            var bigrams = new Dictionary<string, int>();
            for (int i = 0; i < len1; i++)
            {
                string bigram = str1.Substring(i, 2);
                if (bigrams.ContainsKey(bigram)) bigrams[bigram]++;
                else bigrams[bigram] = 1;
            }

            int intersections = 0;
            for (int i = 0; i < len2; i++)
            {
                string bigram = str2.Substring(i, 2);
                if (bigrams.ContainsKey(bigram) && bigrams[bigram] > 0)
                {
                    bigrams[bigram]--;
                    intersections++;
                }
            }
            return Math.Round((2.0 * intersections) / (len1 + len2), 2);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AddClipboardFormatListener(this.Handle);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            RemoveClipboardFormatListener(this.Handle);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                if (chkClipboard != null && chkClipboard.Checked)
                {
                    HandleClipboardUpdateAsync();
                }
            }
        }

        private async void HandleClipboardUpdateAsync()
        {
            await Task.Delay(150); 
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text) && text != lastClipboardText)
                    {
                        lastClipboardText = text;
                        PasteFromClipboard(text);
                    }
                }
            }
            catch { }
        }

        private void InitializeSpeech()
        {
            synthesizer = new SpeechSynthesizer();
            synthesizer.SetOutputToDefaultAudioDevice();
            installedVoices = synthesizer.GetInstalledVoices().Select(v => v.VoiceInfo.Name).ToList();

            synthesizer.SpeakCompleted += (s, e) => 
            {
                if (!isLivePlaying) return; 

                if (e.Cancelled || e.Error != null)
                {
                    if (isSkipping)
                    {
                        isSkipping = false;
                        currentSegmentIndex++;
                        if (!isPaused) PlayNextSegmentInQueue();
                    }
                    else
                    {
                        isLivePlaying = false;
                        if (rtbText.InvokeRequired) rtbText.BeginInvoke(new Action(ResetAllHighlights));
                        else ResetAllHighlights();
                    }
                    return;
                }

                currentSegmentIndex++;
                if (!isPaused) PlayNextSegmentInQueue();
            };
        }

        private void InitializeComponents()
        {
            this.Text = "TTS Studio - Meister Edition";
            this.Size = new Size(1600, 850); 
            this.AllowDrop = true;
            this.DragEnter += MainForm_DragEnter;
            this.DragDrop += MainForm_DragDrop;

            menuStrip = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("Datei");
            var openItem = new ToolStripMenuItem("Dateien hinzufügen...");
            openItem.Click += OpenItem_Click;
            fileMenu.DropDownItems.Add(openItem);
            
            var openProjectsItem = new ToolStripMenuItem("Projekte-Ordner öffnen...");
            openProjectsItem.Click += (s, e) => {
                string projDir = Path.Combine(Application.StartupPath, "Projekte");
                if (!Directory.Exists(projDir)) Directory.CreateDirectory(projDir);
                Process.Start("explorer.exe", projDir);
            };
            fileMenu.DropDownItems.Add(openProjectsItem);
            
            var settingsMenu = new ToolStripMenuItem("Einstellungen");
            var winVoicesItem = new ToolStripMenuItem("Windows Stimmen verwalten (Win 11)...");
            winVoicesItem.Click += (s, e) => {
                try { Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true }); }
                catch { LogEvent("Fehler", "Konnte Windows-Einstellungen nicht öffnen."); }
            };
            settingsMenu.DropDownItems.Add(winVoicesItem);

            var clearCacheItem = new ToolStripMenuItem("MD5-Gedächtnis komplett zurücksetzen...");
            clearCacheItem.Click += (s, e) => {
                var result = MessageBox.Show("Achtung! Soll das komplette MD5-Gedächtnis gelöscht werden? Das Programm vergisst alle jemals gesehenen Dateien (Duplikat-Warnungen werden resettet). Physische Dateien bleiben erhalten.", "Amnesie", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result == DialogResult.Yes) {
                    config.KnownHashes.Clear();
                    isFileDuplicateCache.Clear();
                    
                    foreach (ListViewItem item in lvFiles.Items) {
                        if (item.Tag != null) {
                            string path = item.Tag.ToString();
                            isFileDuplicateCache[path] = false;
                        }
                    }
                    if (config.CurrentSession != "-- ALLE SESSIONS --") SaveCurrentSessionFiles();
                    RefreshAllListViewItems();
                    LogEvent("System", "Das MD5-Gedächtnis wurde restlos gelöscht. Alle Duplikat-Warnungen resettet.");
                }
            };
            settingsMenu.DropDownItems.Add(clearCacheItem);
            
            menuStrip.Items.Add(fileMenu);
            menuStrip.Items.Add(settingsMenu);
            this.MainMenuStrip = menuStrip;
            this.Controls.Add(menuStrip);

            splitMain = new SplitContainer { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Vertical };
            tabLeft = new TabControl { Dock = DockStyle.Fill };
            tabFiles = new TabPage("Dateien");
            
            panelListControls = new Panel { Height = 40, Dock = DockStyle.Top };
            
            btnCheckAll = new Button { Text = "☑ Alle", Location = new Point(5, 5), Size = new Size(60, 30) };
            btnCheckAll.Click += (s, e) => { 
                lvFiles.BeginUpdate();
                foreach (ListViewItem item in lvFiles.Items) { item.Checked = true; item.Selected = true; } 
                lvFiles.EndUpdate();
                lvFiles.Focus();
            };

            btnCheckDuplicates = new Button { Text = "☑ Doppelte", Location = new Point(70, 5), Size = new Size(85, 30), ForeColor = Color.DarkRed };
            btnCheckDuplicates.Click += (s, e) => { 
                lvFiles.BeginUpdate();
                foreach (ListViewItem item in lvFiles.Items) {
                    if (item.Tag != null) {
                        string path = item.Tag.ToString();
                        bool isDup = isFileDuplicateCache.ContainsKey(path) && isFileDuplicateCache[path];
                        item.Checked = isDup;
                        item.Selected = isDup; 
                    }
                }
                lvFiles.EndUpdate();
                lvFiles.Focus();
            };
            
            btnUncheckAll = new Button { Text = "☐ Keine", Location = new Point(160, 5), Size = new Size(70, 30) };
            btnUncheckAll.Click += (s, e) => { 
                lvFiles.BeginUpdate();
                foreach (ListViewItem item in lvFiles.Items) { item.Checked = false; item.Selected = false; } 
                lvFiles.EndUpdate();
            };
            
            cbSessions = new ComboBox { Location = new Point(240, 9), Size = new Size(150, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            
            btnNewSession = new Button { Text = "Neu", Location = new Point(395, 8), Size = new Size(50, 26) };
            btnNewSession.Click += BtnNewSession_Click;

            btnDeleteSession = new Button { Text = "🗑", Location = new Point(450, 8), Size = new Size(30, 26), ForeColor = Color.Red };
            btnDeleteSession.Click += BtnDeleteSession_Click;

            lblBatchProgress = new Label { 
                Text = "⏳ Bereit", 
                Location = new Point(490, 10),
                AutoSize = true, 
                Font = new Font("Segoe UI", 11f, FontStyle.Bold), 
                ForeColor = Color.DodgerBlue, 
                Visible = false 
            };

            panelListControls.Controls.AddRange(new Control[] { btnCheckAll, btnCheckDuplicates, btnUncheckAll, cbSessions, btnNewSession, btnDeleteSession, lblBatchProgress });

            lvFiles = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true, CheckBoxes = true, AllowDrop = true, ShowItemToolTips = true, HideSelection = false }; 
            lvFiles.Columns.Add("Dateiname", 180);
            lvFiles.Columns.Add("Größe", 70);
            lvFiles.Columns.Add("Geändert am", 110);
            lvFiles.Columns.Add("OCR", 50); 
            lvFiles.Columns.Add("PDF", 50); 
            lvFiles.Columns.Add("MP3 Status", 80); 
            lvFiles.Columns.Add("MP3 Größe", 70); 
            lvFiles.Columns.Add("Hash (MD5)", 80); 
            lvFiles.Columns.Add("Session", 130);

            ContextMenuStrip cmsListView = new ContextMenuStrip();
            var miDelete = new ToolStripMenuItem("Markierte/Gecheckte Einträge restlos entfernen (Entf)");
            miDelete.Click += (s, e) => RemoveSelectedFiles();
            cmsListView.Items.Add(miDelete);
            lvFiles.ContextMenuStrip = cmsListView;

            lvFiles.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelectedFiles(); };

            lvFiles.ColumnClick += LvFiles_ColumnClick; 
            lvFiles.SelectedIndexChanged += LvFiles_SelectedIndexChanged;
            lvFiles.DoubleClick += LvFiles_DoubleClick; 
            lvFiles.DragEnter += MainForm_DragEnter;
            lvFiles.DragDrop += MainForm_DragDrop;
            
            tabFiles.Controls.Add(lvFiles);
            tabFiles.Controls.Add(panelListControls);

            tabVoices = new TabPage("Stimmen-Zuweisung");
            dgvVoices = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window, RowHeadersVisible = false };
            dgvVoices.DataError += (s, e) => { e.Cancel = true; };

            DataGridViewTextBoxColumn colMarker = new DataGridViewTextBoxColumn { Name = "Marker", HeaderText = "Kürzel", FillWeight = 40 };
            DataGridViewComboBoxColumn colVoice = new DataGridViewComboBoxColumn { Name = "Voice", HeaderText = "Windows-Stimme", FillWeight = 60 };
            if (installedVoices != null && installedVoices.Count > 0) colVoice.Items.AddRange(installedVoices.ToArray());

            dgvVoices.Columns.Add(colMarker);
            dgvVoices.Columns.Add(colVoice);
            dgvVoices.CellClick += DgvVoices_CellClick; 
            
            tabVoices.Controls.Add(dgvVoices);
            
            tabLeft.TabPages.Add(tabFiles);
            tabLeft.TabPages.Add(tabVoices);
            splitMain.Panel1.Controls.Add(tabLeft);

            splitText = new SplitContainer { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
            rtbText = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11f), HideSelection = false };
            rtbText.KeyDown += RtbText_KeyDown; 
            splitText.Panel1.Controls.Add(rtbText);

            rtbLog = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), ReadOnly = true, BackColor = Color.Black, ForeColor = Color.Lime };
            splitText.Panel2.Controls.Add(rtbLog);
            splitMain.Panel2.Controls.Add(splitText);

            panelControls = new Panel { Height = 60, Dock = DockStyle.Bottom };

            btnPlay = new Button { Text = "▶ Start", Location = new Point(10, 15), Size = new Size(70, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnPlay.Click += BtnPlay_Click;
            
            btnPause = new Button { Text = "⏸ Pause", Location = new Point(85, 15), Size = new Size(70, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnPause.Click += async (s, e) => await HandleAudioCommand("Pause");

            btnResume = new Button { Text = "⏯ Weiter", Location = new Point(160, 15), Size = new Size(70, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnResume.Click += async (s, e) => await HandleAudioCommand("Resume");

            btnSkip = new Button { Text = "⏭ Skip", Location = new Point(235, 15), Size = new Size(70, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnSkip.Click += async (s, e) => await HandleAudioCommand("Skip");

            btnStop = new Button { Text = "⏹ Stopp", Location = new Point(310, 15), Size = new Size(70, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnStop.Click += async (s, e) => await HandleAudioCommand("Stop");

            btnExportMp3 = new Button { Text = "💾 Batch MP3", Location = new Point(385, 15), Size = new Size(95, 35), BackColor = Color.LightGreen, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnExportMp3.Click += BtnExportMp3_Click;

            btnPaste = new Button { Text = "📋 Einfügen", Location = new Point(485, 15), Size = new Size(90, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnPaste.Click += (s, e) => { if (Clipboard.ContainsText()) PasteFromClipboard(Clipboard.GetText()); };

            chkClipboard = new CheckBox { Text = "Auto-Clipboard", Location = new Point(585, 24), AutoSize = true, Checked = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            lblSpeed = new Label { Text = "⏱ Speed:", Location = new Point(695, 25), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            nudSpeed = new NumericUpDown { Minimum = -10, Maximum = 10, Value = 0, Location = new Point(755, 22), Width = 45, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            nudSpeed.ValueChanged += (s, e) => {
                config.Speed = (int)nudSpeed.Value; 
                if (synthesizer != null) synthesizer.Rate = config.Speed;
            };

            lblVolume = new Label { Text = "🔊 Vol:", Location = new Point(810, 25), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            tbVolume = new TrackBar { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10, Location = new Point(855, 15), Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            tbVolume.Scroll += (s, e) => {
                config.Volume = tbVolume.Value; 
                if (synthesizer != null) synthesizer.Volume = tbVolume.Value;
            };

            lblSimilarity = new Label { Text = "🔍 Filter (%):", Location = new Point(945, 25), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            nudSimilarity = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 85, Location = new Point(1020, 22), Width = 45, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            nudSimilarity.ValueChanged += (s, e) => { config.SimilarityThreshold = (int)nudSimilarity.Value; };

            lblMemory = new Label { Text = "🧠 Mem:", Location = new Point(1075, 25), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            nudMemory = new NumericUpDown { Minimum = 1, Maximum = 50, Value = 5, Location = new Point(1130, 22), Width = 45, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            nudMemory.ValueChanged += (s, e) => { config.PredictiveMemorySize = (int)nudMemory.Value; };

            chkHighlight = new CheckBox { Text = "Live-Highlighting", Location = new Point(1190, 24), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            chkLog = new CheckBox { Text = "Log Matrix", Location = new Point(1315, 24), AutoSize = true, Checked = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            chkLog.CheckedChanged += (s, e) => {
                splitText.Panel2Collapsed = !chkLog.Checked;
            };

            btnAhk = new Button { Text = "AHK Macro", Location = new Point(1415, 15), Size = new Size(100, 35), BackColor = Color.LightGray, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnAhk.Click += (s, e) => new AhkForm().ShowDialog();

            panelControls.Controls.AddRange(new Control[] { btnPlay, btnPause, btnResume, btnSkip, btnStop, btnExportMp3, btnPaste, chkClipboard, lblSpeed, nudSpeed, lblVolume, tbVolume, lblSimilarity, nudSimilarity, lblMemory, nudMemory, chkHighlight, chkLog, btnAhk });

            this.Controls.Add(splitMain);
            this.Controls.Add(panelControls);
            this.Controls.Add(menuStrip);
        }

        private void UpdateStatus(string msg)
        {
            if (lblBatchProgress.InvokeRequired)
            {
                lblBatchProgress.Invoke(new Action(() => {
                    lblBatchProgress.Visible = true;
                    lblBatchProgress.Text = msg;
                    lblBatchProgress.Refresh();
                }));
            }
            else
            {
                lblBatchProgress.Visible = true;
                lblBatchProgress.Text = msg;
                lblBatchProgress.Refresh();
            }
        }

        private void CheckAndHideStatus()
        {
            Task.Run(async () => {
                await Task.Delay(400); 
                if (lblBatchProgress.InvokeRequired)
                {
                    lblBatchProgress.Invoke(new Action(() => { 
                        lock(spoolerLock) { if (!isSpoolerRunning) lblBatchProgress.Visible = false; }
                    }));
                }
                else
                {
                    lock(spoolerLock) { if (!isSpoolerRunning) lblBatchProgress.Visible = false; }
                }
            });
        }

        private void StartOcrSpooler()
        {
            lock (spoolerLock)
            {
                if (isSpoolerRunning) return;
                if (ocrSpoolerQueue.Count == 0) return;
                isSpoolerRunning = true;
            }

            Task.Run(() =>
            {
                while (true)
                {
                    string nextFile = null;
                    int remaining = 0;

                    lock (spoolerLock)
                    {
                        if (ocrSpoolerQueue.Count > 0)
                        {
                            nextFile = ocrSpoolerQueue[0];
                            ocrSpoolerQueue.RemoveAt(0);
                            remaining = ocrSpoolerQueue.Count;
                        }
                        else
                        {
                            isSpoolerRunning = false;
                            break;
                        }
                    }

                    if (File.Exists(nextFile + ".txt")) continue; 

                    UpdateStatus($"⏳ Spooler OCR: {Path.GetFileName(nextFile)} ({remaining} verbleibend)...");

                    LoadFileContent(nextFile);
                    
                    if (lvFiles.InvokeRequired)
                    {
                        lvFiles.BeginInvoke(new Action(() => RefreshAllListViewItems()));
                    }
                    else
                    {
                        RefreshAllListViewItems();
                    }
                }

                CheckAndHideStatus();
                LogEvent("System", "OCR-Spooler hat alle Dateien in der Warteschlange abgearbeitet!");
            });
        }

        private void HardDeleteFileAndHash(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string hash = GetCachedHash(filePath);
                    if (hash != "ERROR" && config.KnownHashes.ContainsKey(hash))
                    {
                        config.KnownHashes.Remove(hash);
                    }
                }

                string projectDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(projectDir) && Directory.Exists(projectDir) && projectDir.Contains("Projekte"))
                {
                    Directory.Delete(projectDir, true);
                }
            }
            catch (Exception ex)
            {
                LogEvent("Fehler", $"Hard-Delete fehlgeschlagen für {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        private void RemoveSelectedFiles()
        {
            var itemsToDelete = lvFiles.Items.Cast<ListViewItem>().Where(i => i.Selected || i.Checked).ToList();

            if (itemsToDelete.Count > 0)
            {
                int count = itemsToDelete.Count;
                foreach (ListViewItem item in itemsToDelete)
                {
                    if (item.Tag != null)
                    {
                        string path = item.Tag.ToString();
                        
                        lock(spoolerLock) {
                            if (ocrSpoolerQueue.Contains(path)) ocrSpoolerQueue.Remove(path);
                        }

                        HardDeleteFileAndHash(path);
                        
                        foreach (var session in config.Sessions.Values) {
                            session.RemoveAll(f => f.FilePath.ToLower() == path.ToLower());
                        }
                    }
                    lvFiles.Items.Remove(item);
                }
                
                if (config.CurrentSession != "-- ALLE SESSIONS --") {
                    SaveCurrentSessionFiles();
                }
                LogEvent("System", $"{count} Datei(en) restlos von der Festplatte und aus dem Gedächtnis gelöscht.");
            }
        }

        private void BtnDeleteSession_Click(object sender, EventArgs e)
        {
            if (cbSessions.SelectedItem == null) return;
            string sessionToDelete = cbSessions.SelectedItem.ToString();
            
            if (sessionToDelete == "-- ALLE SESSIONS --") {
                MessageBox.Show("Du kannst nicht alle Sessions auf einmal über diesen Knopf löschen. Wähle eine spezifische Session aus.", "Abgelehnt", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            var result = MessageBox.Show($"Willst du die Session '{sessionToDelete}' und ALLE zugehörigen Dateien physisch restlos löschen?", "Session & Dateien Hard-Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                if (config.Sessions.ContainsKey(sessionToDelete))
                {
                    var filesToDelete = config.Sessions[sessionToDelete];
                    foreach (var state in filesToDelete)
                    {
                        lock(spoolerLock) {
                            if (ocrSpoolerQueue.Contains(state.FilePath)) ocrSpoolerQueue.Remove(state.FilePath);
                        }
                        HardDeleteFileAndHash(state.FilePath);
                    }
                    
                    try {
                        string sessionDir = Path.Combine(Application.StartupPath, "Projekte", sessionToDelete);
                        if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, true);
                    } catch { }
                }

                config.Sessions.Remove(sessionToDelete);
                
                if (config.Sessions.Count == 0)
                {
                    string newName = "Session_" + DateTime.Now.ToString("ddMM_HHmm");
                    config.Sessions[newName] = new List<FileItemState>();
                    config.CurrentSession = newName;
                }
                else
                {
                    config.CurrentSession = config.Sessions.Keys.Last();
                }
                
                cbSessions.SelectedIndexChanged -= CbSessions_SelectedIndexChanged;
                cbSessions.Items.Clear();
                cbSessions.Items.Add("-- ALLE SESSIONS --");
                cbSessions.Items.AddRange(config.Sessions.Keys.ToArray());
                cbSessions.SelectedItem = config.CurrentSession;
                cbSessions.SelectedIndexChanged += CbSessions_SelectedIndexChanged;
                
                LoadCurrentSessionFiles();
                LogEvent("System", $"Session '{sessionToDelete}' und deren Dateien wurden vernichtet.");
            }
        }

        private void LvFiles_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column != sortColumn)
            {
                sortColumn = e.Column;
                lvFiles.Sorting = SortOrder.Ascending;
            }
            else
            {
                if (lvFiles.Sorting == SortOrder.Ascending) lvFiles.Sorting = SortOrder.Descending;
                else lvFiles.Sorting = SortOrder.Ascending;
            }
            lvFiles.Sort();
            lvFiles.ListViewItemSorter = new ListViewItemComparer(e.Column, lvFiles.Sorting);
        }

        private void CbSessions_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbSessions.SelectedItem == null) return;
            string newSession = cbSessions.SelectedItem.ToString();
            if (newSession == config.CurrentSession) return;
            
            if (config.CurrentSession != "-- ALLE SESSIONS --" && !string.IsNullOrEmpty(config.CurrentSession)) {
                SaveCurrentSessionFiles();
            }
            
            config.CurrentSession = newSession;
            LoadCurrentSessionFiles();
            LogEvent("System", $"Ansicht gewechselt auf: {config.CurrentSession}");
        }

        private void BtnNewSession_Click(object sender, EventArgs e)
        {
            string baseName = "Session_" + DateTime.Now.ToString("ddMM_HHmm");
            string newName = baseName;
            int counter = 1;

            while (config.Sessions.ContainsKey(newName))
            {
                newName = $"{baseName}_{counter}";
                counter++;
            }

            if (config.CurrentSession != "-- ALLE SESSIONS --" && !string.IsNullOrEmpty(config.CurrentSession)) {
                SaveCurrentSessionFiles();
            }

            config.Sessions[newName] = new List<FileItemState>();
            
            cbSessions.SelectedIndexChanged -= CbSessions_SelectedIndexChanged;
            cbSessions.Items.Add(newName);
            cbSessions.SelectedItem = newName;
            config.CurrentSession = newName;
            cbSessions.SelectedIndexChanged += CbSessions_SelectedIndexChanged;
            
            LoadCurrentSessionFiles();
            LogEvent("System", $"Neue Session erstellt: {newName}");
        }

        private void SaveCurrentSessionFiles()
        {
            if (config.CurrentSession == "-- ALLE SESSIONS --") return;

            var list = new List<FileItemState>();
            foreach (ListViewItem item in lvFiles.Items)
            {
                if (item.Tag != null) 
                {
                    string path = item.Tag.ToString();
                    bool isDup = isFileDuplicateCache.ContainsKey(path) ? isFileDuplicateCache[path] : false;
                    list.Add(new FileItemState { FilePath = path, IsChecked = item.Checked, IsDuplicate = isDup });
                }
            }
            if (!string.IsNullOrEmpty(config.CurrentSession))
            {
                config.Sessions[config.CurrentSession] = list;
            }
        }

        private void LoadCurrentSessionFiles()
        {
            lvFiles.Items.Clear();
            isFileDuplicateCache.Clear();

            if (config.CurrentSession == "-- ALLE SESSIONS --")
            {
                foreach (var session in config.Sessions)
                {
                    foreach (var state in session.Value)
                    {
                        if (File.Exists(state.FilePath))
                        {
                            string hash = GetCachedHash(state.FilePath);
                            if (hash != "ERROR" && !config.KnownHashes.ContainsKey(hash)) {
                                config.KnownHashes[hash] = File.GetCreationTime(state.FilePath);
                            }
                            isFileDuplicateCache[state.FilePath] = state.IsDuplicate;
                            AddFileToListView(state.FilePath, false, false, state.IsDuplicate); 
                        }
                    }
                }
            }
            else if (config.Sessions.ContainsKey(config.CurrentSession))
            {
                foreach (var state in config.Sessions[config.CurrentSession])
                {
                    if (File.Exists(state.FilePath)) 
                    {
                        string hash = GetCachedHash(state.FilePath);
                        if (hash != "ERROR" && !config.KnownHashes.ContainsKey(hash)) {
                            config.KnownHashes[hash] = File.GetCreationTime(state.FilePath);
                        }
                        isFileDuplicateCache[state.FilePath] = state.IsDuplicate;
                        AddFileToListView(state.FilePath, state.IsChecked, false, state.IsDuplicate);
                    }
                }
            }

            lock (spoolerLock)
            {
                foreach (ListViewItem item in lvFiles.Items)
                {
                    if (item.Tag != null)
                    {
                        string fp = item.Tag.ToString();
                        string ext = Path.GetExtension(fp).ToLower();
                        bool isMediaFile = ext == ".pdf" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tif";

                        if (isMediaFile && !File.Exists(fp + ".txt"))
                        {
                            if (!ocrSpoolerQueue.Contains(fp))
                            {
                                ocrSpoolerQueue.Add(fp);
                            }
                        }
                    }
                }
            }

            if (ocrSpoolerQueue.Count > 0)
            {
                LogEvent("System", $"Spooler-Resume: {ocrSpoolerQueue.Count} unbearbeitete Datei(en) in die Warteschlange geladen.");
                StartOcrSpooler();
            }
        }

        private string CleanSpecialChars(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            // ANTI-DRIFT: Normiert Zeilenumbrüche exakt, belässt aber ALLE Sonderzeichen im Editor!
            input = input.Replace("\r\n", "\n").Replace("\r", "\n");
            
            return input;
        }

        // NEU: Audio-Übersetzer. Radikalisiert Sonderzeichen & Abkürzungen NUR für den Vorleser, nicht für das Editor-Bild!
        private string PrepareTextForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            string t = text.Replace("\r", " ").Replace("\n", " ");
            
            // Sonderzeichen entfernen, die den Hörgenuss zerstören
            t = t.Replace("*", "").Replace("#", "").Replace("\\", "").Replace("\"", "");
            
            // Abkürzungs-Korrekturen (verhindert das Stottern bei Punkten)
            t = Regex.Replace(t, @"\bz\.B\.", "zum Beispiel", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bz\. B\.", "zum Beispiel", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\busw\.", "und so weiter", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bbzw\.", "beziehungsweise", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bd\.h\.", "das heißt", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bd\. h\.", "das heißt", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bevtl\.", "eventuell", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bggf\.", "gegebenenfalls", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\binkl\.", "inklusive", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bzzgl\.", "zuzüglich", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bca\.", "circa", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bu\.a\.", "unter anderem", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bu\. a\.", "unter anderem", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bJr\.", "Junior", RegexOptions.IgnoreCase); 
            t = Regex.Replace(t, @"\bDr\.", "Doktor", RegexOptions.IgnoreCase); 
            t = Regex.Replace(t, @"\bProf\.", "Professor", RegexOptions.IgnoreCase); 
            t = Regex.Replace(t, @"\bSt\.", "Sankt", RegexOptions.IgnoreCase); 
            
            return t;
        }

        private async void PasteFromClipboard(string text)
        {
            if (config.CurrentSession == "-- ALLE SESSIONS --") {
                BtnNewSession_Click(null, null); 
            }

            UpdateStatus("⏳ Verarbeite Zwischenablage...");
            string clipboardText = CleanSpecialChars(text);
            string prefix = $"Zwischenablage_{DateTime.Now:yyyyMMdd_HHmmss}";
            string projDir = Path.Combine(Application.StartupPath, "Projekte", config.CurrentSession, prefix);
            if (!Directory.Exists(projDir)) Directory.CreateDirectory(projDir);
            
            string filepath = Path.Combine(projDir, $"{prefix}.txt");
            File.WriteAllText(filepath, clipboardText);
            
            bool autoSelect = !isLivePlaying;
            if (!isLivePlaying) {
                rtbText.Text = clipboardText;
                rtbText.Tag = filepath;
            }

            string hash = GetCachedHash(filepath);
            bool isDup = false;
            
            if (hash != "ERROR") {
                if (config.KnownHashes.ContainsKey(hash)) {
                    isDup = true;
                    LogEvent("WARNUNG", $"DUPLIKAT ERKANNT! Dieser Text wurde bereits am {config.KnownHashes[hash]:dd.MM.yy HH:mm} eingefügt.");
                } else {
                    config.KnownHashes[hash] = DateTime.Now;
                }
            }
            
            AddFileToListView(filepath, true, autoSelect, isDup); 
            LogEvent("System", $"Clipboard eingefügt und neues Projekt gespeichert: {prefix}");
            
            if (isLivePlaying)
            {
                EnqueueFileForPlayback(filepath, clipboardText);
                LogEvent("Audio", "Neue Datei aus Zwischenablage nahtlos zur Wiedergabeliste hinzugefügt.");
            }

            CheckAndHideStatus();
        }

        private void RtbText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                e.SuppressKeyPress = true;
                if (Clipboard.ContainsText()) PasteFromClipboard(Clipboard.GetText());
            }
        }

        private string LoadFileContent(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            
            if (ext == ".json" && !filePath.EndsWith("config.json") && !filePath.EndsWith("log.json"))
            {
                return ParseGeminiJson(filePath);
            }

            bool isMediaFile = ext == ".pdf" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tif";
            string cachePath = filePath + ".txt";

            string result = "";

            lock (ocrEngineLock)
            {
                if (isMediaFile && File.Exists(cachePath))
                {
                    LogEvent("Cache", $"[CACHE-HIT] Lade bereits extrahierten Text für: {Path.GetFileName(filePath)}");
                    return CleanSpecialChars(File.ReadAllText(cachePath));
                }

                if (ext == ".pdf")
                {
                    result = ParsePdf(filePath);
                }
                else if (isMediaFile)
                {
                    LogEvent("OCR", $"Starte Bilderkennung für {Path.GetFileName(filePath)}...");
                    result = RunOcrOnImage(filePath);
                }
                else
                {
                    result = CleanSpecialChars(File.ReadAllText(filePath));
                }

                if (isMediaFile && !string.IsNullOrWhiteSpace(result))
                {
                    try 
                    { 
                        File.WriteAllText(cachePath, result); 
                        LogEvent("Cache", $"Extrahierter Text (oder Fehlercode) wurde für zukünftige Aufrufe gespeichert."); 
                    } 
                    catch { }
                }
            }

            return result;
        }

        private string ParsePdf(string filePath)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                Stopwatch sw = Stopwatch.StartNew();
                LogEvent("PdfPig", $"[START] Öffne Dokument: {Path.GetFileName(filePath)}...");

                using (PdfDocument document = PdfDocument.Open(filePath))
                {
                    LogEvent("PdfPig", $"Dokument erfolgreich gelesen. Version {document.Version}. {document.NumberOfPages} Seiten gefunden.");
                    int pageCount = 1;
                    foreach (UglyToad.PdfPig.Content.Page page in document.GetPages())
                    {
                        LogEvent("PdfPig", $"[Seite {pageCount}/{document.NumberOfPages}] (Format: {Math.Round(page.Width,0)}x{Math.Round(page.Height,0)} pt) wird analysiert...");
                        string text = page.Text;
                        int imgCount = page.GetImages().Count();
                        
                        LogEvent("PdfPig", $"[Seite {pageCount}/{document.NumberOfPages}] Inhalt: {page.Letters.Count} Zeichen, {imgCount} eingebettete Bilder.");
                        
                        if (string.IsNullOrWhiteSpace(text) || text.Length < 50)
                        {
                            LogEvent("PdfPig", $"[Seite {pageCount}/{document.NumberOfPages}] Zu wenig nativer Text. Dokument scheint ein Scan zu sein.");
                            if (imgCount > 0)
                            {
                                LogEvent("PdfPig", $"[Seite {pageCount}/{document.NumberOfPages}] Triggere Tesseract OCR für Bilder...");
                                int imgIndex = 1;
                                foreach (var image in page.GetImages())
                                {
                                    LogEvent("PdfPig", $"[Seite {pageCount}/{document.NumberOfPages}] Extrahiere Bild {imgIndex}/{imgCount} in RAM...");
                                    if (image.TryGetPng(out byte[] pngBytes))
                                    {
                                        string ocrText = RunOcrOnBytes(pngBytes);
                                        if (!string.IsNullOrWhiteSpace(ocrText)) sb.AppendLine(ocrText);
                                    }
                                    imgIndex++;
                                }
                            }
                        }
                        else
                        {
                            sb.AppendLine(text);
                        }
                        pageCount++;
                    }
                }
                sw.Stop();
                string result = CleanSpecialChars(sb.ToString());
                LogEvent("PdfPig", $"[ENDE] PDF Parsing abgeschlossen in {sw.ElapsedMilliseconds} ms. ({result.Length} Zeichen extrahiert)");
                return result;
            }
            catch (Exception ex)
            {
                string err = $"PDF-Fehler: {ex.Message}";
                LogEvent("Fehler", err);
                return err;
            }
        }

        private string GetAbsoluteTesseractPath()
        {
            return Path.IsPathRooted(config.TessDataPath) 
                ? config.TessDataPath 
                : Path.Combine(Application.StartupPath, config.TessDataPath);
        }

        private string RunOcrOnImage(string filePath)
        {
            string absTessPath = GetAbsoluteTesseractPath();
            if (!Directory.Exists(absTessPath))
            {
                LogEvent("Fehler", $"OCR-Ordner fehlt: {absTessPath}. Bitte 'tessdata' Ordner zur .exe kopieren!");
                return "OCR-Fehler: tessdata Ordner fehlt.";
            }

            try
            {
                Stopwatch swTotal = Stopwatch.StartNew();
                LogEvent("Tesseract", $"[START] Initialisiere Engine (Modus: Default, Sprache: deu)...");
                using (var engine = new TesseractEngine(absTessPath, "deu", EngineMode.Default))
                {
                    LogEvent("Tesseract", $"Engine bereit. Lade physische Datei über C# in RAM...");
                    byte[] imageBytes = File.ReadAllBytes(filePath);
                    
                    using (var img = Pix.LoadFromMemory(imageBytes))
                    {
                        LogEvent("Tesseract", $"Bildstruktur geladen: {img.Width}x{img.Height} Pixel, Farbtiefe: {img.Depth} bpp.");
                        LogEvent("Tesseract", $"Starte Mustererkennung (dies kann je nach CPU dauern)...");
                        Stopwatch swOcr = Stopwatch.StartNew();
                        
                        using (var page = engine.Process(img))
                        {
                            swOcr.Stop();
                            LogEvent("Tesseract", $"Mustererkennung abgeschlossen in {swOcr.ElapsedMilliseconds} ms.");
                            
                            using (var iter = page.GetIterator())
                            {
                                iter.Begin();
                                int blockCount = 0;
                                do {
                                    if (iter.IsAtBeginningOf(PageIteratorLevel.Block)) blockCount++;
                                } while (iter.Next(PageIteratorLevel.Block));
                                LogEvent("Tesseract", $"Layout-Analyse: {blockCount} Textblöcke identifiziert.");
                            }

                            float conf = page.GetMeanConfidence();
                            LogEvent("Tesseract", $"Genauigkeits-Prüfung (Confidence): {Math.Round(conf * 100, 2)}%");

                            string result = CleanSpecialChars(page.GetText());
                            swTotal.Stop();
                            LogEvent("Tesseract", $"[ENDE] OCR komplett in {swTotal.ElapsedMilliseconds} ms. ({result.Length} Zeichen extrahiert)");
                            return result;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                string err = $"OCR-Fehler: {ex.Message}";
                LogEvent("Fehler", err);
                return err;
            }
        }

        private string RunOcrOnBytes(byte[] imageBytes)
        {
            string absTessPath = GetAbsoluteTesseractPath();
            if (!Directory.Exists(absTessPath)) return "OCR-Fehler: tessdata Ordner fehlt.";

            try
            {
                Stopwatch swTotal = Stopwatch.StartNew();
                LogEvent("Tesseract", $"[Speicher-OCR] Initialisiere Engine...");
                using (var engine = new TesseractEngine(absTessPath, "deu", EngineMode.Default))
                {
                    using (var img = Pix.LoadFromMemory(imageBytes))
                    {
                        LogEvent("Tesseract", $"Bilddaten ({img.Width}x{img.Height}) geladen. Starte Extraktion...");
                        using (var page = engine.Process(img))
                        {
                            float conf = page.GetMeanConfidence();
                            LogEvent("Tesseract", $"Block-OCR abgeschlossen. Confidence: {Math.Round(conf * 100, 2)}% in {swTotal.ElapsedMilliseconds} ms.");
                            return page.GetText();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent("Fehler", $"Interner OCR Fehler: {ex.Message}");
                return $"OCR-Fehler: {ex.Message}"; 
            }
        }

        private string ParseGeminiJson(string filePath)
        {
            try
            {
                string jsonString = File.ReadAllText(filePath);
                using (JsonDocument doc = JsonDocument.Parse(jsonString))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        foreach (JsonElement element in root.EnumerateArray())
                        {
                            string role = element.TryGetProperty("role", out JsonElement roleEl) ? roleEl.GetString() : "unknown";
                            string marker = (role == "user") ? "@1" : "@2";

                            if (element.TryGetProperty("contents", out JsonElement contentsEl) && contentsEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement contentItem in contentsEl.EnumerateArray())
                                {
                                    if (contentItem.TryGetProperty("type", out JsonElement typeEl) && typeEl.GetString() == "text")
                                    {
                                        if (contentItem.TryGetProperty("content", out JsonElement textEl))
                                        {
                                            string text = textEl.GetString();
                                            text = Regex.Replace(text, @"```[\s\S]*?```", "");
                                            text = text.Trim();
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                sb.AppendLine($"{marker}: {text}");
                                                sb.AppendLine();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        return CleanSpecialChars(sb.ToString().Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent("Fehler", $"JSON-Parsing für {Path.GetFileName(filePath)} fehlgeschlagen: {ex.Message}");
            }
            return CleanSpecialChars(File.ReadAllText(filePath)); 
        }

        private List<Tuple<string, int>> ChunkMonologueWithIndices(string text, int maxWords = 80)
        {
            List<Tuple<string, int>> result = new List<Tuple<string, int>>();
            MatchCollection sentenceMatches = Regex.Matches(text, @"[^.!?]+[.!?]*");
            
            foreach (Match sm in sentenceMatches)
            {
                string sentence = sm.Value;
                if (string.IsNullOrWhiteSpace(sentence)) continue;
                
                string[] words = sentence.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (words.Length > maxWords)
                {
                    int currentWordCount = 0;
                    int currentChunkStartOffset = 0;
                    int searchOffset = 0;
                    string currentChunkStr = "";
                    
                    foreach(string w in words)
                    {
                        int wordPos = sentence.IndexOf(w, searchOffset);
                        if (currentChunkStr == "") currentChunkStartOffset = wordPos;
                        
                        string partToAdd = sentence.Substring(searchOffset, (wordPos + w.Length) - searchOffset);
                        currentChunkStr += partToAdd;
                        searchOffset = wordPos + w.Length;
                        
                        currentWordCount++;
                        bool isAtComma = w.EndsWith(",") || w.EndsWith(";") || w.EndsWith(":");
                        
                        if (currentWordCount >= maxWords || (currentWordCount >= maxWords - 15 && isAtComma))
                        {
                            string finalChunk = currentChunkStr.Trim();
                            if (!string.IsNullOrWhiteSpace(finalChunk))
                            {
                                int startDelta = currentChunkStr.IndexOf(finalChunk[0]);
                                result.Add(new Tuple<string, int>(finalChunk, sm.Index + currentChunkStartOffset + startDelta));
                            }
                            currentChunkStr = "";
                            currentWordCount = 0;
                        }
                    }
                    
                    if (!string.IsNullOrWhiteSpace(currentChunkStr))
                    {
                        string finalChunk = currentChunkStr.Trim();
                        int startDelta = currentChunkStr.IndexOf(finalChunk[0]);
                        result.Add(new Tuple<string, int>(finalChunk, sm.Index + currentChunkStartOffset + startDelta));
                    }
                }
                else
                {
                    string finalChunk = sentence.Trim();
                    int firstCharOffset = sentence.IndexOf(finalChunk[0]);
                    result.Add(new Tuple<string, int>(finalChunk, sm.Index + firstCharOffset));
                }
            }
            return result;
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            ImportFilesToProjects(files);
        }

        private void OpenItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog() { Multiselect = true, Filter = "Unterstützte Dateien|*.json;*.txt;*.pdf;*.png;*.jpg;*.jpeg;*.bmp|Alle Dateien (*.*)|*.*" })
            {
                if (ofd.ShowDialog() == DialogResult.OK) ImportFilesToProjects(ofd.FileNames);
            }
        }

        private async void ImportFilesToProjects(string[] files)
        {
            if (config.CurrentSession == "-- ALLE SESSIONS --")
            {
                BtnNewSession_Click(null, null); 
            }

            string baseDir = Path.Combine(Application.StartupPath, "Projekte");
            List<string> newlyAddedFiles = new List<string>();
            
            foreach (var file in files)
            {
                if (File.Exists(file))
                {
                    try
                    {
                        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                        string fileName = Path.GetFileName(file);
                        string projectDir = Path.Combine(baseDir, config.CurrentSession, fileNameWithoutExt);
                        
                        if (!Directory.Exists(projectDir)) Directory.CreateDirectory(projectDir);
                        
                        string targetPath = Path.Combine(projectDir, fileName);
                        
                        if (file.ToLower() != targetPath.ToLower()) File.Copy(file, targetPath, true);
                        
                        string hash = GetCachedHash(targetPath);
                        bool isDup = false;
                        if (hash != "ERROR") {
                            if (config.KnownHashes.ContainsKey(hash)) {
                                isDup = true;
                                LogEvent("WARNUNG", $"DUPLIKAT ERKANNT! '{fileName}' existiert seit {config.KnownHashes[hash]:dd.MM.yy HH:mm}.");
                            } else {
                                config.KnownHashes[hash] = DateTime.Now;
                            }
                        }

                        bool autoSelect = !isLivePlaying;
                        AddFileToListView(targetPath, false, autoSelect, isDup);
                        newlyAddedFiles.Add(targetPath);
                    }
                    catch (Exception ex) { LogEvent("Fehler", $"Konnte Projekt nicht anlegen für {file}: {ex.Message}"); }
                }
            }
            
            if (newlyAddedFiles.Count > 0)
            {
                string firstFile = newlyAddedFiles[0];

                lock (spoolerLock)
                {
                    for (int i = 0; i < newlyAddedFiles.Count; i++)
                    {
                        if (!ocrSpoolerQueue.Contains(newlyAddedFiles[i]) && !File.Exists(newlyAddedFiles[i] + ".txt"))
                        {
                            ocrSpoolerQueue.Add(newlyAddedFiles[i]);
                        }
                    }
                }
                StartOcrSpooler();

                UpdateStatus($"⏳ Bereite {newlyAddedFiles.Count} Datei(en) vor...");
                
                if (!isLivePlaying)
                {
                    lvFiles.SelectedIndexChanged -= LvFiles_SelectedIndexChanged;
                    foreach (ListViewItem item in lvFiles.Items)
                    {
                        if (item.Tag != null && item.Tag.ToString().ToLower() == firstFile.ToLower())
                        {
                            item.Selected = true;
                            item.EnsureVisible();
                            break;
                        }
                    }
                    lvFiles.SelectedIndexChanged += LvFiles_SelectedIndexChanged;

                    bool isCached = File.Exists(firstFile + ".txt");
                    rtbText.Text = isCached ? "Lade Datei aus dem Speicher... Bitte warten..." : "Führe Bilderkennung (OCR) aus... Bitte warten...";
                    rtbText.Tag = firstFile;

                    string firstFileText = await Task.Run(() => LoadFileContent(firstFile));

                    if (rtbText.Tag?.ToString() == firstFile)
                    {
                        rtbText.Text = firstFileText;
                    }
                }
                
                RefreshAllListViewItems();
                CheckAndHideStatus();
                LogEvent("System", $"Datei-Import abgeschlossen ({newlyAddedFiles.Count} Datei(en)).");
            }
        }

        private void AddFileToListView(string targetPath, bool isChecked = false, bool autoSelect = false, bool isDuplicate = false)
        {
            foreach (ListViewItem existingItem in lvFiles.Items)
            {
                if (existingItem.Tag != null && existingItem.Tag.ToString().ToLower() == targetPath.ToLower()) 
                {
                    if (autoSelect) { existingItem.Selected = true; existingItem.EnsureVisible(); }
                    LogEvent("Hinweis", $"Datei '{Path.GetFileName(targetPath)}' ist in der aktuellen Ansicht bereits gelistet.");
                    return; 
                }
            }

            var item = new ListViewItem();
            item.Tag = targetPath;
            item.Checked = isChecked;
            
            if (isDuplicate) isFileDuplicateCache[targetPath] = true;
            else if (!isFileDuplicateCache.ContainsKey(targetPath)) isFileDuplicateCache[targetPath] = false;

            lvFiles.Items.Add(item);
            RefreshAllListViewItems(); 
            
            if (autoSelect) { item.Selected = true; item.EnsureVisible(); }
        }

        private void RefreshAllListViewItems()
        {
            if (lvFiles.InvokeRequired)
            {
                lvFiles.BeginInvoke(new Action(RefreshAllListViewItems));
                return;
            }

            foreach (ListViewItem item in lvFiles.Items)
            {
                if (item.Tag != null)
                {
                    string txtPath = item.Tag.ToString();
                    if (File.Exists(txtPath))
                    {
                        FileInfo fiTxt = new FileInfo(txtPath);
                        string dir = Path.GetDirectoryName(txtPath);
                        string baseName = Path.GetFileNameWithoutExtension(txtPath);
                        var mp3Files = Directory.GetFiles(dir, $"{baseName}*.mp3");
                        
                        item.SubItems.Clear();
                        item.Text = fiTxt.Name;
                        item.SubItems.Add($"{Math.Ceiling(fiTxt.Length / 1024.0)} KB");
                        item.SubItems.Add(fiTxt.LastWriteTime.ToString("dd.MM.yy HH:mm"));

                        bool isPdf = txtPath.ToLower().EndsWith(".pdf");
                        bool isImage = txtPath.ToLower().EndsWith(".jpg") || txtPath.ToLower().EndsWith(".png") || txtPath.ToLower().EndsWith(".jpeg") || txtPath.ToLower().EndsWith(".bmp") || txtPath.ToLower().EndsWith(".tif");
                        string ocrStatus = "-";
                        string pdfStatus = "-";
                        string cacheFile = txtPath + ".txt";

                        if (isPdf)
                        {
                            if (File.Exists(cacheFile))
                            {
                                try {
                                    string firstLine = File.ReadLines(cacheFile).FirstOrDefault() ?? "";
                                    if (firstLine.StartsWith("PDF-Fehler")) pdfStatus = "❌";
                                    else pdfStatus = "✓";
                                } catch { pdfStatus = "✓"; }
                            }
                            else pdfStatus = "⏳";
                        }
                        else if (isImage)
                        {
                            if (File.Exists(cacheFile))
                            {
                                try {
                                    string firstLine = File.ReadLines(cacheFile).FirstOrDefault() ?? "";
                                    if (firstLine.StartsWith("OCR-Fehler")) ocrStatus = "❌";
                                    else ocrStatus = "✓";
                                } catch { ocrStatus = "✓"; }
                            }
                            else ocrStatus = "⏳";
                        }

                        item.SubItems.Add(ocrStatus);
                        item.SubItems.Add(pdfStatus);

                        if (mp3Files.Length > 0)
                        {
                            FileInfo fiMp3 = new FileInfo(mp3Files[mp3Files.Length - 1]);
                            item.SubItems.Add("✓");
                            item.SubItems.Add($"{Math.Ceiling(fiMp3.Length / 1024.0)} KB");
                        }
                        else
                        {
                            item.SubItems.Add("-");
                            item.SubItems.Add("-");
                        }
                        
                        string hash = GetCachedHash(txtPath);
                        if (hash.Length > 8) item.SubItems.Add(hash.Substring(0, 8).ToUpper());
                        else item.SubItems.Add(hash.ToUpper());

                        string sessionName = "Unbekannt";
                        if (config.CurrentSession == "-- ALLE SESSIONS --")
                        {
                            var foundSession = config.Sessions.FirstOrDefault(s => s.Value.Any(f => f.FilePath.ToLower() == txtPath.ToLower()));
                            if (foundSession.Key != null) sessionName = foundSession.Key;
                        }
                        else
                        {
                            sessionName = config.CurrentSession;
                        }
                        item.SubItems.Add(sessionName);

                        if (isFileDuplicateCache.ContainsKey(txtPath) && isFileDuplicateCache[txtPath])
                        {
                            item.UseItemStyleForSubItems = true;
                            item.BackColor = Color.LightCoral;
                            item.ForeColor = Color.Black;
                            
                            DateTime knownDate = config.KnownHashes.ContainsKey(hash) ? config.KnownHashes[hash] : DateTime.Now;
                            item.ToolTipText = $"⚠️ DUPLIKAT: Datei wurde bereits am {knownDate:dd.MM.yyyy HH:mm} eingepflegt!";
                        }
                        else
                        {
                            item.UseItemStyleForSubItems = true;
                            item.BackColor = SystemColors.Window;
                            item.ForeColor = SystemColors.WindowText;
                            item.ToolTipText = "";
                        }
                    }
                }
            }
        }

        private async void BtnPlay_Click(object sender, EventArgs e)
        {
            var checkedItems = lvFiles.CheckedItems.Cast<ListViewItem>().ToList();
            if (checkedItems.Count == 0)
            {
                LogEvent("System", "Bitte mindestens eine Datei anhaken, um sie abzuspielen.");
                return;
            }

            UpdateStatus("⏳ Analysiere Text für Sprachausgabe...");

            synthesizer.SpeakAsyncCancelAll();
            ResetAllHighlights();
            SyncConfigFromGrid();

            playbackQueue.Clear();
            currentSegmentIndex = 0;
            isLivePlaying = true;
            isPaused = false;
            isSkipping = false;

            liveVoiceMap = new Dictionary<string, string>(config.VoiceMappings);
            liveDefaultVoice = null;
            if (liveVoiceMap.ContainsKey("@1") && installedVoices != null && installedVoices.Contains(liveVoiceMap["@1"])) liveDefaultVoice = liveVoiceMap["@1"];
            else if (installedVoices != null && installedVoices.Count > 0) liveDefaultVoice = installedVoices[0];

            livePredictiveMemory.Clear(); 

            foreach (var item in checkedItems)
            {
                string filePath = item.Tag.ToString();
                string fullText = "";
                try {
                    bool isCurrentlyOpen = (rtbText.Tag?.ToString() == filePath);
                    if (isCurrentlyOpen) fullText = rtbText.Text;
                    else fullText = await Task.Run(() => LoadFileContent(filePath));
                } catch { continue; }

                EnqueueFileForPlayback(filePath, fullText);
            }

            CheckAndHideStatus();

            LogEvent("System", $"Warteschlange mit {playbackQueue.Count} Segmenten aus {checkedItems.Count} Datei(en) geladen. Start!");
            PlayNextSegmentInQueue();
        }

        private void EnqueueFileForPlayback(string filePath, string fullText)
        {
            string currentVoice = liveDefaultVoice;
            string[] rawSegments = Regex.Split(fullText, @"(@\d+)");
            int currentIndexInText = 0;

            foreach (var part in rawSegments)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    currentIndexInText += part.Length;
                    continue;
                }

                if (Regex.IsMatch(part, @"^@\d+$"))
                {
                    if (liveVoiceMap.TryGetValue(part, out string mappedVoice) && installedVoices != null && installedVoices.Contains(mappedVoice)) currentVoice = mappedVoice;
                    currentIndexInText += part.Length;
                    continue;
                }

                MatchCollection paragraphMatches = Regex.Matches(part, @"[^\r\n]+(?:\r\n|\n|\r)?");
                foreach (Match pm in paragraphMatches)
                {
                    string paragraph = pm.Value;
                    string cleanPara = paragraph.Trim();
                    
                    if (string.IsNullOrWhiteSpace(cleanPara))
                    {
                        currentIndexInText += paragraph.Length;
                        continue;
                    }

                    if (config.SimilarityThreshold < 100)
                    {
                        bool isRedundant = false;
                        foreach (string pastText in livePredictiveMemory)
                        {
                            double sim = CalculateSimilarity(pastText, cleanPara);
                            if (sim >= (config.SimilarityThreshold / 100.0))
                            {
                                string previewSkip = cleanPara.Length > 25 ? cleanPara.Substring(0, 25).Replace("\n", " ") + "..." : cleanPara.Replace("\n", " ");
                                LogEvent("Filter", $"Überspringe (Mem-Hit {Math.Round(sim * 100, 0)}%): '{previewSkip}'");
                                isRedundant = true;
                                break;
                            }
                        }
                        if (isRedundant) 
                        {
                            currentIndexInText += paragraph.Length;
                            continue; 
                        }
                    }

                    livePredictiveMemory.Add(cleanPara);
                    if (livePredictiveMemory.Count > config.PredictiveMemorySize) livePredictiveMemory.RemoveAt(0);

                    var chunks = ChunkMonologueWithIndices(paragraph, 80);
                    foreach (var chunk in chunks)
                    {
                        playbackQueue.Add(new SpeechSegment
                        {
                            VoiceName = currentVoice,
                            Text = chunk.Item1,
                            StartIndex = currentIndexInText + chunk.Item2,
                            Length = chunk.Item1.Length,
                            SourceFilePath = filePath 
                        });
                    }
                    currentIndexInText += paragraph.Length;
                }
            }
        }

        private void PlayNextSegmentInQueue()
        {
            if (!isLivePlaying) return;

            if (currentSegmentIndex >= playbackQueue.Count)
            {
                isLivePlaying = false;
                if (rtbText.InvokeRequired) rtbText.BeginInvoke(new Action(ResetAllHighlights));
                else ResetAllHighlights();
                LogEvent("Audio", "Wiedergabe der gesamten Liste abgeschlossen.");
                return;
            }

            SpeechSegment segment = playbackQueue[currentSegmentIndex];

            if (rtbText.InvokeRequired) rtbText.BeginInvoke(new Action(() => LoadTextIfNeededAndPlay(segment)));
            else LoadTextIfNeededAndPlay(segment);
        }

        private async void LoadTextIfNeededAndPlay(SpeechSegment segment)
        {
            if (rtbText.Tag?.ToString() != segment.SourceFilePath)
            {
                try
                {
                    bool isCached = File.Exists(segment.SourceFilePath + ".txt");
                    rtbText.Text = isCached ? "Lade Datei aus dem Speicher... Bitte warten..." : "Führe Bilderkennung (OCR) aus... Bitte warten...";
                    
                    rtbText.Text = await Task.Run(() => LoadFileContent(segment.SourceFilePath));
                    rtbText.Tag = segment.SourceFilePath;
                    
                    lvFiles.SelectedIndexChanged -= LvFiles_SelectedIndexChanged;
                    foreach (ListViewItem lvi in lvFiles.Items)
                    {
                        lvi.Selected = (lvi.Tag.ToString() == segment.SourceFilePath);
                        if (lvi.Selected) lvi.EnsureVisible();
                    }
                    lvFiles.SelectedIndexChanged += LvFiles_SelectedIndexChanged;
                }
                catch { }
            }

            string preview = segment.Text.Length > 35 ? segment.Text.Substring(0, 35).Replace("\n", " ") + "..." : segment.Text.Replace("\n", " ");
            string voiceLabel = !string.IsNullOrEmpty(segment.VoiceName) ? segment.VoiceName : "Standardstimme";
            LogEvent("Lese", $"[{currentSegmentIndex + 1}/{playbackQueue.Count}] {voiceLabel}: \"{preview}\"");

            if (chkHighlight.Checked) HighlightSegmentBlock(segment);

            if (!string.IsNullOrEmpty(segment.VoiceName)) { try { synthesizer.SelectVoice(segment.VoiceName); } catch { } }
            
            synthesizer.Rate = config.Speed;
            synthesizer.Volume = config.Volume;
            
            string textToSpeak = PrepareTextForSpeech(segment.Text);
            synthesizer.SpeakAsync(textToSpeak);
        }

        private async Task HandleAudioCommand(string command)
        {
            if (isAudioCommandRunning) return;
            isAudioCommandRunning = true;
            
            btnPause.Enabled = false;
            btnResume.Enabled = false;
            btnStop.Enabled = false;
            btnSkip.Enabled = false;

            try
            {
                if (command == "Pause") 
                {
                    isPaused = true;
                    if (synthesizer.State == SynthesizerState.Speaking) synthesizer.Pause();
                    LogEvent("Audio", "Pausiert.");
                }
                else if (command == "Resume") 
                {
                    isPaused = false;
                    if (synthesizer.State == SynthesizerState.Paused) synthesizer.Resume();
                    else if (isLivePlaying) PlayNextSegmentInQueue(); 
                    LogEvent("Audio", "Fortgesetzt.");
                }
                else if (command == "Skip") 
                {
                    if (isLivePlaying && (synthesizer.State == SynthesizerState.Speaking || synthesizer.State == SynthesizerState.Paused))
                    {
                        isSkipping = true;
                        synthesizer.SpeakAsyncCancelAll();
                        LogEvent("Audio", "Segment übersprungen.");
                    }
                }
                else if (command == "Stop") 
                {
                    isLivePlaying = false;
                    isSkipping = false;
                    synthesizer.SpeakAsyncCancelAll();
                    ResetAllHighlights();
                    LogEvent("Audio", "Stopp-Signal verarbeitet.");
                }

                await Task.Delay(250);
            }
            catch (Exception ex)
            {
                LogEvent("Fehler", $"Audio-Befehlsfehler: {ex.Message}");
            }
            finally
            {
                btnPause.Enabled = true;
                btnResume.Enabled = true;
                btnStop.Enabled = true;
                btnSkip.Enabled = true;
                isAudioCommandRunning = false;
            }
        }

        private void HighlightSegmentBlock(SpeechSegment segment)
        {
            LockWindowUpdate(rtbText.Handle);
            try
            {
                int savedStart = rtbText.SelectionStart;
                int savedLength = rtbText.SelectionLength;
                rtbText.SelectAll();
                rtbText.SelectionBackColor = rtbText.BackColor;
                
                int actualStart = -1;
                string boxText = rtbText.Text;
                
                int searchStart = Math.Max(0, segment.StartIndex - 150);
                actualStart = boxText.IndexOf(segment.Text, searchStart, StringComparison.OrdinalIgnoreCase);
                
                if (actualStart == -1) actualStart = boxText.IndexOf(segment.Text, StringComparison.OrdinalIgnoreCase);
                
                if (actualStart == -1) actualStart = segment.StartIndex;

                rtbText.Select(actualStart, segment.Length);
                rtbText.SelectionBackColor = Color.LightSkyBlue;

                Point p = rtbText.GetPositionFromCharIndex(actualStart);
                if (p.Y > rtbText.Height - 80 || p.Y < 0) rtbText.ScrollToCaret();
            }
            catch { }
            finally { LockWindowUpdate(IntPtr.Zero); }
        }

        private void ResetAllHighlights()
        {
            try
            {
                LockWindowUpdate(rtbText.Handle);
                int savedStart = rtbText.SelectionStart;
                int savedLength = rtbText.SelectionLength;

                rtbText.SelectAll();
                rtbText.SelectionBackColor = rtbText.BackColor;
                
                rtbText.Select(savedStart, savedLength);
            }
            catch { }
            finally { LockWindowUpdate(IntPtr.Zero); }
        }

        private void LogEvent(string action, string details)
        {
            var entry = new LogEntry { Timestamp = DateTime.Now, Action = action, Details = details };
            string jsonLine = JsonSerializer.Serialize(entry);
            
            if (chkLog != null && chkLog.Checked && !splitText.Panel2Collapsed) 
            {
                if (rtbLog.InvokeRequired) 
                {
                    rtbLog.BeginInvoke(new Action(() => { 
                        rtbLog.AppendText($"[{entry.Timestamp:HH:mm:ss}] {action}: {details}\n"); 
                        rtbLog.ScrollToCaret(); 
                    }));
                }
                else 
                { 
                    rtbLog.AppendText($"[{entry.Timestamp:HH:mm:ss}] {action}: {details}\n"); 
                    rtbLog.ScrollToCaret(); 
                }
            }

            try { File.AppendAllText(LogFile, jsonLine + Environment.NewLine); } catch { }
        }

        private async void LvFiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count > 0)
            {
                string filePath = lvFiles.SelectedItems[0].Tag.ToString();
                try
                {
                    if (isLivePlaying && rtbText.Tag?.ToString() == filePath) return;

                    isLivePlaying = false;
                    synthesizer.SpeakAsyncCancelAll();
                    ResetAllHighlights();
                    
                    lock(spoolerLock) {
                        if (ocrSpoolerQueue.Contains(filePath)) {
                            ocrSpoolerQueue.Remove(filePath);
                            LogEvent("Spooler", $"Datei {Path.GetFileName(filePath)} vorgezogen!");
                        }
                    }
                    
                    bool isCached = File.Exists(filePath + ".txt");
                    UpdateStatus(isCached ? "⏳ Lade Text aus Cache..." : "⏳ Analysiere Dokument (OCR)...");
                    
                    rtbText.Text = isCached ? "Lade Datei aus dem Speicher... Bitte warten..." : "Führe Bilderkennung (OCR) aus... Bitte warten...";
                    
                    string loadedText = await Task.Run(() => LoadFileContent(filePath));
                    
                    rtbText.Text = loadedText;
                    rtbText.Tag = filePath;
                    RefreshAllListViewItems();
                    CheckAndHideStatus();
                }
                catch (Exception ex) { LogEvent("Fehler", $"Datei laden fehlgeschlagen: {ex.Message}"); }
            }
        }

        private void LvFiles_DoubleClick(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count > 0)
            {
                string filePath = lvFiles.SelectedItems[0].Tag.ToString();
                string projectDir = Path.GetDirectoryName(filePath);
                
                if (Directory.Exists(projectDir))
                {
                    try { Process.Start("explorer.exe", projectDir); LogEvent("System", $"Projektordner geöffnet."); }
                    catch (Exception ex) { LogEvent("Fehler", $"Explorer konnte nicht gestartet werden: {ex.Message}"); }
                }
            }
        }

        private void DgvVoices_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex == 0 && e.RowIndex >= 0)
            {
                var voiceCell = dgvVoices.Rows[e.RowIndex].Cells["Voice"].Value;
                if (voiceCell != null)
                {
                    string selectedVoice = voiceCell.ToString();
                    LogEvent("Audio", $"Starte Sprachtest für: {selectedVoice}");
                    
                    Task.Run(() => 
                    {
                        try
                        {
                            synthesizer.SpeakAsyncCancelAll();
                            synthesizer.Rate = config.Speed;
                            synthesizer.Volume = config.Volume;
                            
                            PromptBuilder pb = new PromptBuilder();
                            pb.StartVoice(selectedVoice);
                            pb.AppendText($"Hallo Meister. Dies ist ein kurzer Test.");
                            pb.EndVoice();
                            synthesizer.SpeakAsync(pb);
                        }
                        catch (Exception ex) { LogEvent("Fehler", $"Testfehler: {ex.Message}"); }
                    });
                }
            }
        }

        private void SyncConfigFromGrid()
        {
            config.VoiceMappings.Clear();
            foreach (DataGridViewRow row in dgvVoices.Rows)
            {
                if (!row.IsNewRow && row.Cells["Marker"].Value != null && row.Cells["Voice"].Value != null)
                {
                    string marker = row.Cells["Marker"].Value.ToString().Trim();
                    string voice = row.Cells["Voice"].Value.ToString();
                    if (!string.IsNullOrEmpty(marker) && !config.VoiceMappings.ContainsKey(marker))
                    {
                        config.VoiceMappings.Add(marker, voice);
                    }
                }
            }
        }

        private async Task ExportMp3Async(string textToExport, string exportPath)
        {
            LogEvent("Export", "Schritt 1/3: Analysiere Segmente für MP3-Export...");

            SyncConfigFromGrid();
            var voiceMap = new Dictionary<string, string>(config.VoiceMappings);
            var installed = new List<string>();
            if (installedVoices != null) installed = new List<string>(installedVoices);
            
            int currentSpeed = config.Speed;
            int currentVol = config.Volume;

            try
            {
                synthesizer.SpeakAsyncCancelAll();

                PromptBuilder prompt = await Task.Run(() => 
                {
                    PromptBuilder pb = new PromptBuilder();
                    string[] segments = Regex.Split(textToExport, @"(@\d+)");
                    
                    string currentVoice = installed.Count > 0 ? installed[0] : null;
                    if (voiceMap.ContainsKey("@1") && installed.Contains(voiceMap["@1"]))
                    {
                        currentVoice = voiceMap["@1"];
                    }

                    List<string> exportPredictiveMemory = new List<string>();

                    foreach (var part in segments)
                    {
                        if (string.IsNullOrWhiteSpace(part)) continue;

                        if (Regex.IsMatch(part, @"^@\d+$"))
                        {
                            if (voiceMap.TryGetValue(part, out string mappedVoice))
                            {
                                if (installed.Contains(mappedVoice)) currentVoice = mappedVoice;
                            }
                            continue;
                        }

                        if (currentVoice != null) pb.StartVoice(currentVoice);
                        
                        MatchCollection paragraphMatches = Regex.Matches(part, @"[^\r\n]+(?:\r\n|\n|\r)?");
                        foreach (Match pm in paragraphMatches)
                        {
                            string paragraph = pm.Value;
                            string cleanPara = paragraph.Trim();
                            
                            if (string.IsNullOrWhiteSpace(cleanPara)) continue;

                            if (config.SimilarityThreshold < 100)
                            {
                                bool isRedundant = false;
                                foreach (string pastText in exportPredictiveMemory)
                                {
                                    double sim = CalculateSimilarity(pastText, cleanPara);
                                    if (sim >= (config.SimilarityThreshold / 100.0))
                                    {
                                        isRedundant = true;
                                        break;
                                    }
                                }
                                if (isRedundant) continue;
                            }

                            exportPredictiveMemory.Add(cleanPara);
                            if (exportPredictiveMemory.Count > config.PredictiveMemorySize) exportPredictiveMemory.RemoveAt(0);

                            var chunks = ChunkMonologueWithIndices(paragraph, 80);
                            foreach (var chunk in chunks)
                            {
                                string textForAudio = PrepareTextForSpeech(chunk.Item1);
                                pb.AppendText(textForAudio);
                            }
                        }
                        
                        if (currentVoice != null) pb.EndVoice();
                    }
                    return pb;
                });

                LogEvent("Export", $"Schritt 2/3: WAV Rendern...");
                string tempWav = "temp_export.wav";
                
                await Task.Run(() => 
                {
                    synthesizer.Rate = currentSpeed;
                    synthesizer.Volume = currentVol;
                    synthesizer.SetOutputToWaveFile(tempWav);
                    synthesizer.Speak(prompt); 
                    synthesizer.SetOutputToDefaultAudioDevice();
                });

                long wavSizeMb = new FileInfo(tempWav).Length / 1024 / 1024;
                LogEvent("Export", $"Schritt 3/3: FFmpeg Komprimierung ({wavSizeMb} MB)...");
                await Task.Run(() => { ConvertWavToMp3(tempWav, exportPath); });
                
                RefreshAllListViewItems();
            }
            catch (Exception ex) { LogEvent("Fehler", $"MP3-Engine Fehler: {ex.Message}"); }
        }

        private async void BtnExportMp3_Click(object sender, EventArgs e)
        {
            if (lvFiles.CheckedItems.Count == 0)
            {
                LogEvent("System", "Bitte wähle mindestens eine Datei aus für den Batch-Export.");
                return;
            }

            btnExportMp3.Enabled = false;
            var itemsToProcess = lvFiles.CheckedItems.Cast<ListViewItem>().ToList();
            LogEvent("Batch", $"Starte Verarbeitung von {itemsToProcess.Count} Datei(en)...");
            
            UpdateStatus($"⏳ Batch MP3 Export für {itemsToProcess.Count} Datei(en)...");

            foreach (var item in itemsToProcess)
            {
                string filePath = item.Tag.ToString();
                string projectDir = Path.GetDirectoryName(filePath);
                string baseName = Path.GetFileNameWithoutExtension(filePath);
                string mp3Path = Path.Combine(projectDir, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp3");

                try
                {
                    string textData = "";
                    bool isCurrentlyOpen = (rtbText.Tag?.ToString() == filePath);
                    
                    if (isCurrentlyOpen)
                    {
                        textData = rtbText.Text;
                        LogEvent("System", $"Verwende manuell editierten Text aus der Anzeige für: {baseName}");
                    }
                    else
                    {
                        textData = await Task.Run(() => LoadFileContent(filePath)); 
                    }
                    
                    await ExportMp3Async(textData, mp3Path);
                }
                catch (Exception ex) { LogEvent("Fehler", $"Batch-Fehler bei {baseName}: {ex.Message}"); }
            }
            
            CheckAndHideStatus();
            LogEvent("Batch", "Alle MP3-Exporte abgeschlossen!");
            btnExportMp3.Enabled = true;
        }

        private void ConvertWavToMp3(string wavFile, string mp3File)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg.exe",
                    Arguments = $"-y -i \"{wavFile}\" -b:a 192k \"{mp3File}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true 
                };

                using (Process p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) LogEvent("FFmpeg", ev.Data); };
                    p.BeginErrorReadLine();
                    p.WaitForExit();

                    if (p.ExitCode == 0) LogEvent("FFmpeg", $"MP3 erfolgreich im Projektordner gespeichert.");
                    else LogEvent("Fehler", $"FFmpeg Fehler-Code {p.ExitCode}.");
                }
            }
            catch (Exception ex) { LogEvent("Fehler", $"FFmpeg Fehler: {ex.Message}"); }
            finally { if (File.Exists(wavFile)) File.Delete(wavFile); }
        }

        private void LoadConfig()
        {
            if (File.Exists(ConfigFile))
            {
                try
                {
                    string json = File.ReadAllText(ConfigFile);
                    config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

                    bool isVisible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(config.WindowBounds));
                    if (isVisible && config.WindowBounds.Width > 0)
                    {
                        this.StartPosition = FormStartPosition.Manual;
                        this.Bounds = config.WindowBounds;
                    }
                    else this.StartPosition = FormStartPosition.CenterScreen;

                    this.WindowState = config.WindowState == FormWindowState.Minimized ? FormWindowState.Normal : config.WindowState;
                    
                    if (config.SplitterMainDistance > 0) splitMain.SplitterDistance = config.SplitterMainDistance;
                    if (config.SplitterTextDistance > 0) splitText.SplitterDistance = config.SplitterTextDistance;
                }
                catch { this.StartPosition = FormStartPosition.CenterScreen; }
            }
            else this.StartPosition = FormStartPosition.CenterScreen;

            if (chkHighlight != null) chkHighlight.Checked = config.EnableHighlighting;
            
            if (chkLog != null) {
                chkLog.Checked = config.EnableLogMatrix;
                splitText.Panel2Collapsed = !chkLog.Checked;
            }
            
            if (tbVolume != null) { tbVolume.Value = config.Volume; if (synthesizer != null) synthesizer.Volume = config.Volume; }
            if (nudSpeed != null) { nudSpeed.Value = config.Speed; if (synthesizer != null) synthesizer.Rate = config.Speed; }
            if (nudSimilarity != null) { nudSimilarity.Value = config.SimilarityThreshold; }
            if (nudMemory != null) { nudMemory.Value = config.PredictiveMemorySize; }

            if (config.Sessions == null) config.Sessions = new Dictionary<string, List<FileItemState>>();
            if (config.KnownHashes == null) config.KnownHashes = new Dictionary<string, DateTime>();
            
            var keysToRemove = config.Sessions.Where(kvp => kvp.Value == null || kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList();
            foreach (var key in keysToRemove) config.Sessions.Remove(key);

            if (!config.Sessions.ContainsKey(config.CurrentSession ?? ""))
            {
                if (config.Sessions.Count > 0) 
                {
                    config.CurrentSession = config.Sessions.Keys.Last();
                } 
                else 
                {
                    string newSession = "Session_" + DateTime.Now.ToString("ddMM_HHmm");
                    config.Sessions[newSession] = new List<FileItemState>();
                    config.CurrentSession = newSession;
                }
            }
            
            cbSessions.Items.Clear();
            cbSessions.Items.Add("-- ALLE SESSIONS --");
            cbSessions.Items.AddRange(config.Sessions.Keys.ToArray());
            
            if (cbSessions.Items.Contains(config.CurrentSession)) 
                cbSessions.SelectedItem = config.CurrentSession;
            else 
                cbSessions.SelectedIndex = 1; 

            LoadCurrentSessionFiles();

            cbSessions.SelectedIndexChanged += CbSessions_SelectedIndexChanged;

            dgvVoices.Rows.Clear();
            string[] preferred = { "hedda", "karsten", "katja", "michael", "stefan" };
            List<string> usedVoices = new List<string>();

            for (int i = 0; i < 5; i++)
            {
                string marker = $"@{i + 1}";
                string matchName = null;

                if (config.VoiceMappings.ContainsKey(marker) && installedVoices != null && installedVoices.Contains(config.VoiceMappings[marker]))
                {
                    matchName = config.VoiceMappings[marker];
                }
                else if (installedVoices != null)
                {
                    matchName = installedVoices.FirstOrDefault(v => v.ToLower().Contains(preferred[i]));
                }

                dgvVoices.Rows.Add(marker, matchName);
                if (matchName != null) usedVoices.Add(matchName);
            }

            if (installedVoices != null)
            {
                var remaining = installedVoices.Where(v => !usedVoices.Contains(v)).OrderBy(v => v).ToList();
                int rIdx = 6;
                foreach (var v in remaining)
                {
                    string marker = $"@{rIdx}";
                    string mappedVoice = v;
                    if (config.VoiceMappings.ContainsKey(marker) && installedVoices.Contains(config.VoiceMappings[marker]))
                    {
                        mappedVoice = config.VoiceMappings[marker];
                    }
                    dgvVoices.Rows.Add(marker, mappedVoice);
                    rIdx++;
                }
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            config.WindowBounds = this.WindowState == FormWindowState.Minimized ? this.RestoreBounds : this.Bounds;
            config.WindowState = this.WindowState;
            config.SplitterMainDistance = splitMain.SplitterDistance;
            config.SplitterTextDistance = splitText.SplitterDistance;
            config.EnableHighlighting = chkHighlight != null && chkHighlight.Checked;
            config.EnableLogMatrix = chkLog != null && chkLog.Checked; 
            config.Volume = tbVolume != null ? tbVolume.Value : 100;
            config.Speed = nudSpeed != null ? (int)nudSpeed.Value : 0;
            config.SimilarityThreshold = nudSimilarity != null ? (int)nudSimilarity.Value : 85;
            config.PredictiveMemorySize = nudMemory != null ? (int)nudMemory.Value : 5;
            SyncConfigFromGrid();

            if (config.CurrentSession != "-- ALLE SESSIONS --") {
                SaveCurrentSessionFiles();
            }
            
            var emptyKeys = config.Sessions.Where(kvp => kvp.Value == null || kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList();
            foreach (var key in emptyKeys) config.Sessions.Remove(key);
            
            if (config.CurrentSession == "-- ALLE SESSIONS --" || !config.Sessions.ContainsKey(config.CurrentSession ?? "")) {
                if (config.Sessions.Count > 0) config.CurrentSession = config.Sessions.Keys.Last();
            }

            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFile, json);
            }
            catch { }

            try
            {
                if (ActiveAhkPid != -1) {
                    Process p = Process.GetProcessById(ActiveAhkPid);
                    if (!p.HasExited) p.Kill(true);
                }
            }
            catch { }

            if (synthesizer != null) { try { synthesizer.Dispose(); } catch { } }
        }
    }
}