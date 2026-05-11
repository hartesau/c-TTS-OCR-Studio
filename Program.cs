// --- FILE: Program.cs ---
// Program.cs
// AUTOCONFIG: [4, 5]
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
    public class FileItemState
    {
        public string FilePath { get; set; }
        public bool IsChecked { get; set; }
        public bool IsDuplicate { get; set; } = false; 
    }

    public class AppConfig
    {
        public Rectangle WindowBounds { get; set; }
        public FormWindowState WindowState { get; set; }
        public int SplitterMainDistance { get; set; } = 550; 
        public int SplitterTextDistance { get; set; } = 400;
        public bool EnableHighlighting { get; set; } = false; 
        public bool EnableLogMatrix { get; set; } = true; 
        public int Volume { get; set; } = 100; 
        public int Speed { get; set; } = 0; 
        public int SimilarityThreshold { get; set; } = 85; 
        public int PredictiveMemorySize { get; set; } = 5; 
        public string CurrentSession { get; set; } = ""; 
        public string TessDataPath { get; set; } = "tessdata"; 
        public Dictionary<string, List<FileItemState>> Sessions { get; set; } = new Dictionary<string, List<FileItemState>>(); 
        public List<FileItemState> RecentFiles { get; set; } = new List<FileItemState>(); 
        public Dictionary<string, string> VoiceMappings { get; set; } = new Dictionary<string, string>();
        
        public Dictionary<string, DateTime> KnownHashes { get; set; } = new Dictionary<string, DateTime>();
        public Dictionary<string, string> HashOriginalFiles { get; set; } = new Dictionary<string, string>();
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Action { get; set; }
        public string Details { get; set; }
    }

    public class SpeechSegment
    {
        public string VoiceName { get; set; }
        public string Text { get; set; }
        public int StartIndex { get; set; }
        public int Length { get; set; }
        public string SourceFilePath { get; set; } 
    }

    public class ListViewItemComparer : System.Collections.IComparer
    {
        private int col;
        private SortOrder order;
        public ListViewItemComparer(int column, SortOrder order)
        {
            col = column;
            this.order = order;
        }
        public int Compare(object x, object y)
        {
            int returnVal = -1;
            ListViewItem lviX = (ListViewItem)x;
            ListViewItem lviY = (ListViewItem)y;
            
            if (lviX.SubItems.Count <= col || lviY.SubItems.Count <= col) return 0;
            returnVal = String.Compare(lviX.SubItems[col].Text, lviY.SubItems[col].Text);
            
            if (order == SortOrder.Descending) returnVal *= -1;
            return returnVal;
        }
    }

    public class AhkForm : Form
    {
        private RichTextBox rtbCode;
        public AhkForm()
        {
            this.Text = "AHK Macro Editor - Meister Edition";
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;

            rtbCode = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 11f), AcceptsTab = true };
            Panel p = new Panel { Dock = DockStyle.Bottom, Height = 60 };
            
            Button btnSave = new Button { Text = "💾 Speichern", Location = new Point(20, 15), Size = new Size(120, 30), BackColor = Color.LightBlue };
            Button btnStart = new Button { Text = "▶ Starten", Location = new Point(150, 15), Size = new Size(120, 30), BackColor = Color.LightGreen };
            Button btnKill = new Button { Text = "⏹ Kill AHK", Location = new Point(280, 15), Size = new Size(120, 30), BackColor = Color.LightCoral }; 
            
            p.Controls.Add(btnSave);
            p.Controls.Add(btnStart);
            p.Controls.Add(btnKill);
            this.Controls.Add(rtbCode);
            this.Controls.Add(p);

            string ahkPath = Path.Combine(Application.StartupPath, "hotkey.ahk");
            if (File.Exists(ahkPath)) rtbCode.Text = File.ReadAllText(ahkPath);

            btnSave.Click += (s, e) => {
                string code = rtbCode.Text;
                string header = "If !FileExist(A_ScriptDir \"\\save\\\\\")\n" +
                                "FileCreateDir,% A_ScriptDir \"\\save\\\\\"\n" +
                                "FileCopy, % A_ScriptFullPath, % A_ScriptDir \"\\save\\\\\" A_ScriptName \" save \" A_Now \" .ahk\"\n" +
                                "#SingleInstance force\n" +
                                "#NoEnv\n" +
                                "#Persistent\n" +
                                "FileEncoding, UTF-8\n" +
                                "SetBatchLines, -1\n" +
                                "SetTitleMatchMode, 2\n" +
                                "SetKeyDelay 20\n" +
                                "SetWorkingDir, %A_ScriptDir%\n\n";

                if (!code.Contains("#SingleInstance force"))
                {
                    code = header + code;
                    rtbCode.Text = code;
                }
                
                File.WriteAllText(ahkPath, code);
                MessageBox.Show("AHK Skript inklusive Sicherheits-Header gespeichert!", "Gespeichert", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            btnStart.Click += (s, e) => {
                if (File.Exists(ahkPath)) 
                {
                    MainForm.KillActiveAhk(ahkPath); 

                    try { 
                        Process p_ahk = Process.Start("autohotkey.exe", $"\"{ahkPath}\""); 
                        if (p_ahk != null) MainForm.ActiveAhkPid = p_ahk.Id;
                    }
                    catch { 
                        try { 
                            Process p_ahk = Process.Start(new ProcessStartInfo(ahkPath) { UseShellExecute = true }); 
                            if (p_ahk != null) MainForm.ActiveAhkPid = p_ahk.Id;
                        } 
                        catch { MessageBox.Show("Fehler beim Starten des AHK Scripts."); }
                    }
                }
                else MessageBox.Show("Bitte zuerst speichern.");
            };

            btnKill.Click += (s, e) => {
                if (MainForm.KillActiveAhk(ahkPath)) {
                    MessageBox.Show($"AHK Skript gnadenlos beendet!", "Erfolg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                } else {
                    MessageBox.Show("Kein aktives AHK Skript gefunden.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
        }
    }

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
            LogEvent("System", "Studio gestartet. Abkürzungs-Korrektur und absoluter Live-Sync für Highlights aktiv!");
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
