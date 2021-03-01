﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.IO;
using System.Globalization;
using RazzTools;

namespace ValheimServerWarden
{
    public class ValheimServer : IEditableObject, IComparable
    {
        public enum ServerStatus
        {
            Stopped,
            Running,
            Starting,
            Stopping
        }
        public enum ServerInstallMethod
        {
            Manual,
            Steam,
            SteamCMD
        }
        struct ServerData
        {
            internal string name;
            internal int port;
            internal string world;
            internal string password;
            internal string savedir;
            internal bool pub;
            internal bool autostart;
            internal bool log;
            internal int restartHours;
            internal string discordWebhook;
        }
        public event EventHandler<UpdatedEventArgs> Updated;
        public event EventHandler<FailedPasswordEventArgs> FailedPassword;
        public event EventHandler<PlayerEventArgs> PlayerConnected;
        public event EventHandler<PlayerEventArgs> PlayerDisconnected;
        public event EventHandler<PlayerEventArgs> PlayerDied;
        public event EventHandler<RandomServerEventArgs> RandomServerEvent;
        public event EventHandler<EventArgs> Starting;
        public event EventHandler<EventArgs> Started;
        public event EventHandler<ServerErrorEventArgs> StartFailed;
        public event EventHandler<ServerErrorEventArgs> StopFailed;
        public event EventHandler<EventArgs> ScheduledRestart;
        public event EventHandler<ServerExitedEventArgs> Exited;
        public event EventHandler<ServerExitedEventArgs> ExitedUnexpectedly;
        public event EventHandler<ServerErrorEventArgs> ErrorOccurred;
        public event DataReceivedEventHandler OutputDataReceived
        {
            add
            {
                this.process.OutputDataReceived += value;
            }
            remove
            {
                this.process.OutputDataReceived -= value;
            }
        }
        public event DataReceivedEventHandler ErrorDataReceived
        {
            add
            {
                this.process.ErrorDataReceived += value;
            }
            remove
            {
                this.process.ErrorDataReceived -= value;
            }
        }
        public event EventHandler<LoggedMessageEventArgs> LoggedMessage;
        public static readonly string DefaultSaveDir = $@"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\AppData\LocalLow\IronGate\Valheim";
        private string serverExe = "valheim_server.exe";
        private bool testMode = false;
        private ServerData data;
        private Dictionary<string, string> _discordWebhookMesssages;
        private ServerData backupData;
        private Process process;
        private PlayerList players;
        private ServerStatus status;
        private DateTime startTime;
        private bool intentionalExit;
        private List<string> connectingSteamIds;
        private bool needsRestart;
        private bool scheduledRestart;
        private System.Timers.Timer restartTimer;
        private List<LogEntry> logEntries;
        private bool inTxn = false;

        private static Dictionary<string, string> _discordWebhookDefaultMessages = new Dictionary<string, string> {
            {"OnStarted", "Server {Server.Name} has started." },
            {"OnStartFailed", "Server {Server.Name} failed to start." },
            {"OnServerExited", "Server {Server.Name} has stopped." },
            {"OnFailedPassword", "User with SteamID {Player.SteamID} tried to join with an invalid password." },
            {"OnPlayerConnected", "{Player.Name} has entered the fray!" },
            {"OnPlayerDisconnected", "{Player.Name} has departed." },
            {"OnPlayerDied", "{Player.Name} met an untimely demise." },
            {"OnRandomServerEvent", "An {EventName} attack is underway!" }
        };

        public static string SteamID { get { return "896660"; } }

        public string Name
        {
            get
            {
                return this.data.name;
            }
            set
            {
                this.data.name = value;
            }
        }
        public int Port
        {
            get
            {
                return this.data.port;
            }
            set
            {
                this.data.port = value;
            }
        }
        public string World
        {
            get
            {
                return this.data.world;
            }
            set
            {
                this.data.world = value;
            }
        }
        public string Password
        {
            get
            {
                return this.data.password;
            }
            set
            {
                this.data.password = value;
            }
        }
        public string SaveDir
        {
            get
            {
                return this.data.savedir;
            }
            set
            {
                this.data.savedir = value;
            }
        }
        public bool Public
        {
            get
            {
                return this.data.pub;
            }
            set
            {
                this.data.pub = value;
            }
        }
        public bool Autostart
        {
            get
            {
                return this.data.autostart;
            }
            set
            {
                this.data.autostart = value;
            }
        }
        public bool Log
        {
            get
            {
                return this.data.log;
            }
            set
            {
                this.data.log = value;
            }
        }
        public int RestartHours
        {
            get
            {
                return this.data.restartHours;
            }
            set
            {
                this.data.restartHours = value;
                if (value > 0 && (this.Status == ServerStatus.Running || this.status == ServerStatus.Starting))
                {
                    restartTimer.Interval = this.GetMilisecondsUntilRestart();
                    restartTimer.Enabled = true;
                    restartTimer.Start();
                }
                else
                {
                    restartTimer.Enabled = false;
                }
            }
        }
        public string DiscordWebhook
        {
            get
            {
                return this.data.discordWebhook;
            }
            set
            {
                this.data.discordWebhook = value;
            }
        }
        public Dictionary<string,string> DiscordWebhookMessages
        {
            get
            {
                return this._discordWebhookMesssages;
            }
            set
            {
                this._discordWebhookMesssages = value;
            }
        }
        public string InstallPath { get; set; }
        public ServerInstallMethod InstallMethod { get; set; }
        [JsonIgnore]
        public bool Running
        {
            get
            {
                return (this.Status == ServerStatus.Running || this.Status == ServerStatus.Starting || this.Status == ServerStatus.Stopping);
            }
        }
        [JsonIgnore]
        public ServerStatus Status
        {
            get
            {
                return this.status;
            }
        }
        [JsonIgnore]
        public int PlayerCount
        {
            get
            {
                //return this.playerCount;
                return this.Players.Count;
            }
        }
        [JsonIgnore]
        public PlayerList Players
        {
            get
            {
                return this.players;
            }
        }
        [JsonIgnore]
        public string PlayerList
        {
            get
            {
                return this.players.ToString();
            }
        }
        [JsonIgnore]
        public DateTime StartTime
        {
            get { return this.startTime; }
        }
        [JsonIgnore]
        public Dictionary<string,string> DefaultWebhookMessages
        {
            get
            {
                return _discordWebhookDefaultMessages;
            }
        }
        [JsonIgnore]
        public List<LogEntry> LogEntries
        {
            get
            {
                return logEntries;
            }
        }
        public ValheimServer(string name, int port, string world, string password, bool pubserver, bool autostart, bool log, int restarthours, string discordwebhook, Dictionary<string,string> discordmessages, ServerInstallMethod install, string instpath)
        {
            this.data.name = name;
            this.data.port = 2456;
            this.data.world = world;
            this.data.password = password;
            this.data.savedir = "";
            this.data.pub = pubserver;
            this.data.autostart = autostart;
            this.data.log = log;
            this.data.restartHours = restarthours;
            this.data.discordWebhook = discordwebhook;
            this._discordWebhookMesssages = discordmessages;
            InstallMethod = install;
            InstallPath = instpath;

            this.process = new Process();
            this.process.StartInfo.EnvironmentVariables["SteamAppId"] = "892970";
            this.process.StartInfo.UseShellExecute = false;
            this.process.StartInfo.CreateNoWindow = true;
            this.process.StartInfo.RedirectStandardOutput = true;
            this.process.StartInfo.RedirectStandardError = true;
            this.process.StartInfo.RedirectStandardInput = true;
            this.process.EnableRaisingEvents = true;
            this.process.OutputDataReceived += Process_OutputDataReceived;
            this.process.Exited += Process_Exited;

            restartTimer = new System.Timers.Timer();
            restartTimer.AutoReset = false;
            restartTimer.Elapsed += RestartTimer_Elapsed;

            this.players = new PlayerList();
            this.status = ServerStatus.Stopped;
            this.scheduledRestart = false;
            this.needsRestart = false;
            this.logEntries = new List<LogEntry>();

            connectingSteamIds = new List<string>();
        }

        private void RestartTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (this.PlayerCount == 0)
            {
                scheduledRestart = true;
                OnScheduledRestart(new EventArgs());
                this.Stop();
            } else
            {
                this.needsRestart = true;
            }
        }

        public ValheimServer() : this("My Server", 2456, "Dedicated", "Secret", false, false, false, 0, null, new Dictionary<string,string>(), ServerInstallMethod.Manual, Properties.Settings.Default.ServerFilePath)
        {
        }

        public ValheimServer(string name) : this(name, 2456, "Dedicated", "Secret", false, false, false, 0, null, new Dictionary<string,string>(), ServerInstallMethod.Manual, Properties.Settings.Default.ServerFilePath)
        {

        }
        public string GetLogName()
        {
            string logname = this.Name.Replace(" ", "_");
            logname = Regex.Replace(logname, @"[<]", "[");
            logname = Regex.Replace(logname, @"[>]", "]");
            foreach (var c in Path.GetInvalidFileNameChars()) { logname = logname.Replace(c, '-'); }
            //Regex rgx = new Regex("[^a-zA-Z0-9_-]");
            //logname = rgx.Replace(logname, "");
            //return $"{logname}-{this.startTime.ToString("yyyy-MM-dd_HH-mm-ss")}.log";
            return $"{logname}-{this.Port}-{this.World}.log";
        }

        public double GetMilisecondsUntilRestart()
        {
            DateTime restartTime = this.startTime.AddHours(this.RestartHours);
            //DateTime restartTime = this.startTime.AddMinutes(1);
            TimeSpan ts = restartTime - this.startTime;
            return ts.TotalMilliseconds;
        }

        public string GetWebhookMessage(string EventName)
        {
            if (this.DiscordWebhookMessages.ContainsKey(EventName))
            {
                return this.DiscordWebhookMessages[EventName];
            }
            else if (_discordWebhookDefaultMessages.ContainsKey(EventName))
            {
                return _discordWebhookDefaultMessages[EventName];
            }
            return null;
        }

        public void SendDiscordWebhook(string EventName, Player player, string serverEventName)
        {
            string message = GetWebhookMessage(EventName);
            if (message == "") return;
            message = message.Replace("{Server.Name}", this.Name);
            message = message.Replace("{Server.PlayerCount}", this.PlayerCount.ToString());
            if (player != null)
            {
                message = message.Replace("{Player.Name}", player.Name);
                message = message.Replace("{Player}", player.Name);
                message = message.Replace("{Player.SteamID}", player.SteamID);
                message = message.Replace("{SteamID}", player.SteamID);
                message = message.Replace("{Player.Deaths}", player.Deaths.ToString());
                message = message.Replace("{Player.JoinTime}", player.JoinTime.ToString());
            }
            if (serverEventName != null)
            {
                message = message.Replace("{EventName}", serverEventName);
            }
            SendDiscordWebhook(message);
        }
        public void SendDiscordWebhook(string EventName, Player player)
        {
            SendDiscordWebhook(EventName, player, null);
        }
        public void SendDiscordWebhook(string message)
        {
            if (this.DiscordWebhook != null && this.DiscordWebhook != "")
            {
                using (DiscordWebhook webhook = new DiscordWebhook())
                {
                    //webhook.ProfilePicture = "https://static.giantbomb.com/uploads/original/4/42381/1196379-gas_mask_respirator.jpg";
                    //webhook.UserName = "Bot";
                    webhook.WebHook = this.DiscordWebhook;
                    webhook.SendMessage(message);
                }
            }
        }

        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            string msg = e.Data;
            if (msg == null) return;
            //Debug.WriteLine(msg);
            if (this.Log)
            {
                try
                {
                    StreamWriter writer = System.IO.File.AppendText(this.GetLogName());
                    writer.WriteLine(msg);
                    writer.Close();
                }
                catch (Exception ex)
                {
                    //not being able to clear the log is not a major problem
                }
            }
            //Monitor for incorrect password attempts
            Regex rx = new Regex(@"Peer (\d+) has wrong password", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Match match = rx.Match(msg);
            if (match.Success)
            {
                if (connectingSteamIds.Contains(match.Groups[1].ToString()))
                {
                    connectingSteamIds.Remove(match.Groups[1].ToString());
                }
                OnFailedPassword(new FailedPasswordEventArgs(match.Groups[1].ToString()));
                return;
            }

            //Monitor for initiation of new connection
            rx = new Regex(@"Got handshake from client (\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = rx.Match(msg);
            if (match.Success)
            {
                connectingSteamIds.Add(match.Groups[1].ToString());
                return;
            }

            //Monitor for new player connected and player deaths
            rx = new Regex(@"Got character ZDOID from (.+) : (-?\d+:-?\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = rx.Match(msg);
            if (match.Success)
            {
                if (match.Groups[2].ToString().Equals("0:0"))
                {
                    //player died
                    foreach (Player player in this.players)
                    {
                        if (player.Name.Equals(match.Groups[1].ToString()))
                        {
                            player.Deaths++;
                            OnPlayerDied(new PlayerEventArgs(player));
                            break;
                        }
                    }
                } 
                else if (connectingSteamIds.Count > 0)
                {
                    //player connected
                    var steamid = connectingSteamIds.First();
                    Player player = new Player(match.Groups[1].ToString(), steamid);
                    this.players.Add(player);
                    connectingSteamIds.Remove(steamid);
                    OnPlayerConnected(new PlayerEventArgs(player));
                }
                return;
            }

            //Monitor for player disconnected
            rx = new Regex(@"Closing socket (\d{2,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = rx.Match(msg);
            if (match.Success)
            {
                string steamid = match.Groups[1].ToString();
                //Player player = new Player(match.Groups[1].ToString(), this.connectingSteamID);
                foreach (Player player in this.players)
                {
                    if (steamid.Equals(player.SteamID))
                    {
                        this.players.Remove(player);
                        OnPlayerDisconnected(new PlayerEventArgs(player));
                        if (this.needsRestart && this.PlayerCount == 0)
                        {
                            scheduledRestart = true;
                            OnScheduledRestart(new EventArgs());
                            this.Stop();
                        }
                        break;
                    }
                }
                return;
            }

            //Monitor for update to number of players connected
            /*rx = new Regex(@"Connections (\d+) ZDOS:(?:\d+)  sent:(?:\d+) recv:(?:\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = rx.Match(msg);
            if (match.Success)
            {
                this.playerCount = Int16.Parse(match.Groups[1].ToString());
                OnPlayerCountUpdated(new ServerEventArgs(this));
            }*/

            //Monitor for server finishes starting
            if (this.Status == ServerStatus.Starting)
            {
                rx = new Regex(@"DungeonDB Start \d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                match = rx.Match(msg);
                if (match.Success)
                {
                    this.status = ServerStatus.Running;
                    OnStarted(new EventArgs());
                    //logMessage($"Server {this.Name}: started", LogType.Success);
                }
                return;
            }

            //Monitor for server fails to start
            // handled more robustly in the process exited event handler
            /*rx = new Regex(@"GameServer.Init\(\) failed", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = rx.Match(msg);
            if (match.Success)
            {
                this.status = ServerStatus.Stopping;
                OnStartFailed(new ServerEventArgs(this));
                logMessage($"Server {this.Name} failed to start. Maybe try a different port", LogType.Error);
                return;
            }*/

            //Monitor for random events
            rx = new Regex(@"Random event set:([a-zA-Z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = rx.Match(msg);
            if (match.Success)
            {
                OnRandomServerEvent(new RandomServerEventArgs(match.Groups[1].ToString()));
                return;
                //army_moder
            }
        }

        public void Start()
        {
            if (this.Running)
            {
                //logMessage($"Server {this.Name} cannot start since it is already running.", LogType.Error);
                //addToLog("Server cannot start; it is already running.", LogEntryType.Error);
                OnStartFailed(new ServerErrorEventArgs("Server cannot start; it is already running."));
                return;
            }
            string saveDir = this.SaveDir;
            if (saveDir == null || saveDir.Length == 0)
            {
                saveDir = DefaultSaveDir;
            }
            string serverpath = InstallPath;//Properties.Settings.Default.ServerFilePath;
            if (!File.Exists(serverpath))
            {
                OnStartFailed(new ServerErrorEventArgs($"Server cannot start because {this.serverExe} was not found at the server executable path ({serverpath}). Please update the server executable path."));
                return;
            }
            string arguments = $"-nographics -batchmode -name \"{this.Name}\" -port {this.Port} -world \"{this.World}\"";
            if (Password != null & Password.Length > 0)
            {
                arguments += $" -password \"{this.Password}\"";
            }
            if (!saveDir.Equals(DefaultSaveDir))
            {
                arguments += $" -savedir \"{this.SaveDir}\"";
            }
            if (Public)
            {
                arguments += $" -public 1";
            }
            if (testMode)
            {
                serverpath = "tracert.exe";
                arguments = "google.com";
            }
            this.intentionalExit = false;
            new Thread(() =>
            {
                if (this.Log)
                {
                    System.IO.File.WriteAllText(this.GetLogName(),"");
                }
                this.startTime = DateTime.Now;
                if (this.RestartHours > 0)
                {
                    restartTimer.Interval = this.GetMilisecondsUntilRestart();
                    restartTimer.Enabled = true;
                    restartTimer.Start();
                } 
                else
                {
                    restartTimer.Enabled = false;
                }
                this.status = ServerStatus.Starting;
                OnStarting(new EventArgs());
                this.process.StartInfo.FileName = serverpath;
                this.process.StartInfo.Arguments = arguments;
                Thread.CurrentThread.IsBackground = true;
                //this.processRunning = true;
                this.process.Refresh();
                this.needsRestart = false;
                this.scheduledRestart = false;
                this.process.Start();
                this.process.BeginOutputReadLine();
                this.process.WaitForExit();
            }).Start();
        }

        public void Stop()
        {
            if (!this.Running)
            {
                //throw new Exception("This server is not running.");
                OnStopFailed(new ServerErrorEventArgs($"Server cannot stop since it is not running."));
                return;
            }
            new Thread(() =>
            {
                if (AttachConsole((uint)this.process.Id))
                {
                    this.status = ServerStatus.Stopping;
                    SetConsoleCtrlHandler(null, true);
                    GenerateConsoleCtrlEvent(ConsoleCtrlEvent.CTRL_C, 0);
                    FreeConsole();
                    this.process.WaitForExit(2000);
                    SetConsoleCtrlHandler(null, false);
                    this.intentionalExit = true;
                    this.restartTimer.Enabled = false;
                }
            }).Start();
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GenerateConsoleCtrlEvent(ConsoleCtrlEvent sigevent, int dwProcessGroupId);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(uint dwProcessId);
        [DllImport("kernel32.dll")]
        static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate HandlerRoutine, bool Add);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        static extern bool FreeConsole();
        delegate Boolean ConsoleCtrlDelegate(uint CtrlType);
        public enum ConsoleCtrlEvent
        {
            CTRL_C = 0,
            CTRL_BREAK = 1,
            CTRL_CLOSE = 2,
            CTRL_LOGOFF = 5,
            CTRL_SHUTDOWN = 6
        }

        private void Process_Exited(object sender, EventArgs e)
        {
            bool failedstart = (this.status == ServerStatus.Starting);
            bool unwantedexit = (this.status != ServerStatus.Stopping);
            this.status = ServerStatus.Stopped;
            this.process.CancelOutputRead();
            //this.processRunning = false;
            this.players.Clear();
            if (failedstart)
            {
                OnStartFailed(new ServerErrorEventArgs($"Server failed to start."));
            }
            else
            {
                OnServerExited(new ServerExitedEventArgs(this.process.ExitCode, this.intentionalExit));
                if (scheduledRestart)
                {
                    this.Start();
                } else if (unwantedexit)
                {
                    OnServerExitedUnexpectedly(new ServerExitedEventArgs(this.process.ExitCode, this.intentionalExit));
                    if (this.Autostart)
                    {
                        this.Start();
                    }
                }
            }
        }
        void IEditableObject.BeginEdit()
        {
            if (!inTxn)
            {
                this.backupData = this.data;
                inTxn = true;
            }
        }

        void IEditableObject.CancelEdit()
        {
            if (inTxn)
            {
                this.data = backupData;
                inTxn = false;
            }
        }

        void IEditableObject.EndEdit()
        {
            if (inTxn)
            {
                var changedValues = new NameValueCollection();
                if (backupData.name != data.name)
                {
                    changedValues["Name"] = backupData.name;
                    //OnUpdated(new UpdatedEventArgs("Name"));
                }
                if (backupData.port!= data.port)
                {
                    changedValues["Port"] = backupData.port.ToString();
                    //OnUpdated(new UpdatedEventArgs("Port"));
                }
                if (backupData.world != data.world)
                {
                    changedValues["World"] = backupData.world;
                    //OnUpdated(new UpdatedEventArgs("World"));
                }
                if (backupData.password != data.password)
                {
                    changedValues["Password"] = backupData.password;
                    //OnUpdated(new UpdatedEventArgs("Password"));
                }
                if (backupData.savedir != data.savedir)
                {
                    changedValues["SaveDir"] = backupData.savedir;
                    //OnUpdated(new UpdatedEventArgs("SaveDir"));
                }
                if (backupData.pub != data.pub)
                {
                    changedValues["Public"] = backupData.pub.ToString();
                    //OnUpdated(new UpdatedEventArgs("Public"));
                }
                if (backupData.autostart != data.autostart)
                {
                    changedValues["Autostart"] = backupData.autostart.ToString();
                    //OnUpdated(new UpdatedEventArgs("Autostart"));
                }
                if (backupData.log != data.log)
                {
                    changedValues["Log"] = backupData.log.ToString();
                    //OnUpdated(new UpdatedEventArgs("Log"));
                }
                if (backupData.restartHours != data.restartHours)
                {
                    changedValues["RestartHours"] = backupData.restartHours.ToString();
                    //OnUpdated(new UpdatedEventArgs("RestartHours"));
                }
                if (changedValues.Count > 0)
                {
                    OnUpdated(new UpdatedEventArgs(changedValues));
                }
                backupData = new ServerData();
                inTxn = false;
            }
        }

        int IComparable.CompareTo(object obj)
        {
            ValheimServer vs = (ValheimServer)obj;
            int nameCompare = String.Compare(this.Name, vs.Name);
            if (nameCompare == 0)
            {
                return String.Compare(this.Port.ToString(), vs.Port.ToString());
            }
            return nameCompare;
        }

        private void OnUpdated(UpdatedEventArgs args)
        {
            EventHandler<UpdatedEventArgs> handler = Updated;
            if (null != handler) handler(this, args);
        }
        private void OnFailedPassword(FailedPasswordEventArgs args)
        {
            try
            {
                SendDiscordWebhook(System.Reflection.MethodBase.GetCurrentMethod().Name, new Player("Unknown", args.SteamID));
            }
            catch (Exception ex)
            {
                OnErrorOccurred(new ServerErrorEventArgs($"Error sending Webhook for failed password attempt: {ex.Message}", ex));
            }
            addToLog($"Failed password attempt for steamid {args.SteamID}.");
            EventHandler<FailedPasswordEventArgs> handler = FailedPassword;
            if (null != handler) handler(this, args);
        }
        private void OnPlayerConnected(PlayerEventArgs args)
        {
            try
            {
                SendDiscordWebhook(System.Reflection.MethodBase.GetCurrentMethod().Name, args.Player);
            }
            catch (Exception ex)
            {
                OnErrorOccurred(new ServerErrorEventArgs($"Error sending Webhook for player connected: {ex.Message}", ex));
            }
            addToLog($"Player {args.Player.Name} ({args.Player.SteamID}) connected");
            EventHandler<PlayerEventArgs> handler = PlayerConnected;
            if (null != handler) handler(this, args);
        }
        private void OnPlayerDisconnected(PlayerEventArgs args)
        {
            try
            {
                SendDiscordWebhook(System.Reflection.MethodBase.GetCurrentMethod().Name, args.Player);
            }
            catch (Exception ex)
            {
                OnErrorOccurred(new ServerErrorEventArgs($"Error sending Webhook for player disconnected: {ex.Message}", ex));
            }
            addToLog($"Player {args.Player.Name} ({args.Player.SteamID}) disconnected");
            EventHandler<PlayerEventArgs> handler = PlayerDisconnected;
            if (null != handler) handler(this, args);
        }
        private void OnPlayerDied(PlayerEventArgs args)
        {
            try
            {
                SendDiscordWebhook(System.Reflection.MethodBase.GetCurrentMethod().Name, args.Player);
            }
            catch (Exception ex)
            {
                OnErrorOccurred(new ServerErrorEventArgs($"Error sending Webhook for player death: {ex.Message}", ex));
            }
            addToLog($"Player {args.Player.Name} ({args.Player.SteamID}) died");
            EventHandler<PlayerEventArgs> handler = PlayerDied;
            if (null != handler) handler(this, args);
        }
        private void OnServerExited(ServerExitedEventArgs args)
        {
            try
            {
                SendDiscordWebhook(System.Reflection.MethodBase.GetCurrentMethod().Name, null, null);
            }
            catch (Exception ex)
            {
                OnErrorOccurred(new ServerErrorEventArgs($"Error sending Webhook for server stop: {ex.Message}", ex));
            }
            if (args.ExitCode == 0 || args.ExitCode == -1073741510)
            {
                addToLog($"Server stopped.");
            }
            else
            {
                addToLog($"Server exited with code {args.ExitCode}.", LogEntryType.Error);
            }
            EventHandler<ServerExitedEventArgs> handler = Exited;
            if (null != handler) handler(this, args);
        }
        private void OnServerExitedUnexpectedly(ServerExitedEventArgs args)
        {
            addToLog($"Server stopped unexpectedly.", LogEntryType.Error);
            EventHandler<ServerExitedEventArgs> handler = ExitedUnexpectedly;
            if (null != handler) handler(this, args);
        }
        private void OnRandomServerEvent(RandomServerEventArgs args)
        {
            try
            {
                SendDiscordWebhook(System.Reflection.MethodBase.GetCurrentMethod().Name, null, args.EventName);
            }
            catch (Exception ex)
            {
                OnErrorOccurred(new ServerErrorEventArgs($"Error sending Webhook for random server eventt: {ex.Message}", ex));
            }
            addToLog($"Random event {args.EventName}.");
            EventHandler<RandomServerEventArgs> handler = RandomServerEvent;
            if (null != handler) handler(this, args);
        }
        private void OnLoggedMessage(LoggedMessageEventArgs args)
        {
            EventHandler<LoggedMessageEventArgs> handler = LoggedMessage;
            if (null != handler) handler(this, args);
        }
        private void OnStarting(EventArgs args)
        {
            addToLog("Server starting...");
            EventHandler<EventArgs> handler = Starting;
            if (null != handler) handler(this, args);
        }
        private void OnStarted(EventArgs args)
        {
            try
            {
                SendDiscordWebhook(System.Reflection.MethodBase.GetCurrentMethod().Name, null, null);
            }
            catch (Exception ex)
            {
                OnErrorOccurred(new ServerErrorEventArgs($"Error sending Webhook for server start: {ex.Message}", ex));
            }
            addToLog($"Server started.", LogEntryType.Success);
            EventHandler<EventArgs> handler = Started;
            if (null != handler) handler(this, args);
        }
        private void OnStartFailed(ServerErrorEventArgs args)
        {
            addToLog(args.Message, LogEntryType.Error);
            EventHandler<ServerErrorEventArgs> handler = StartFailed;
            if (null != handler) handler(this, args);
        }
        private void OnStopFailed(ServerErrorEventArgs args)
        {
            addToLog(args.Message, LogEntryType.Error);
            EventHandler<ServerErrorEventArgs> handler = StopFailed;
            if (null != handler) handler(this, args);
        }
        private void OnScheduledRestart(EventArgs args)
        {
            addToLog($"Initiating scheduled restart...");
            EventHandler<EventArgs> handler = ScheduledRestart;
            if (null != handler) handler(this, args);
        }
        private void OnErrorOccurred(ServerErrorEventArgs args)
        {
            addToLog(args.Message, LogEntryType.Error);
            EventHandler<ServerErrorEventArgs> handler = ErrorOccurred;
            if (null != handler) handler(this, args);
        }
        /*private void OutputReceived(DataReceivedEventArgs args)
        {
            EventHandler<DataReceivedEventArgs> handler = OutputDataReceived;
            if (null != handler) handler(this, args);
        }
        private void ErrorReceived(DataReceivedEventArgs args)
        {
            EventHandler<DataReceivedEventArgs> handler = ErrorDataReceived;
            if (null != handler) handler(this, args);
        }*/
        private void addToLog(string message, LogEntryType t)
        {
            var entry = new LogEntry(message, t);
            LogEntries.Add(entry);
            OnLoggedMessage(new LoggedMessageEventArgs(entry));
        }
        private void addToLog(string message)
        {
            addToLog(message, LogEntryType.Normal);
        }
        public static void TerminateAll(Process[] servers)
        {
            new Thread(() =>
            {
                foreach (Process proc in servers)
                {
                    if (AttachConsole((uint)proc.Id))
                    {
                        SetConsoleCtrlHandler(null, true);
                        GenerateConsoleCtrlEvent(ConsoleCtrlEvent.CTRL_C, 0);
                        FreeConsole();
                        proc.WaitForExit(2000);
                        SetConsoleCtrlHandler(null, false);
                    }
                }
            }).Start();
        }
    }

        public class UpdatedEventArgs : EventArgs
    {
        private readonly NameValueCollection _fieldNames;

        public UpdatedEventArgs(NameValueCollection fieldNames)
        {
            _fieldNames = fieldNames;
        }

        public NameValueCollection FieldName
        {
            get { return _fieldNames; }
        }
    }

    public class FailedPasswordEventArgs : EventArgs
    {
        private readonly string _steamid;

        public FailedPasswordEventArgs(string steamid)
        {
            _steamid = steamid;
        }
        public string SteamID
        {
            get { return _steamid; }
        }
    }
    public class PlayerEventArgs : EventArgs
    {
        private readonly Player _player;

        public PlayerEventArgs(Player player)
        {
            _player = player;
        }
        public Player Player
        {
            get { return _player; }
        }
    }
    public class ServerExitedEventArgs
    {
        private readonly int _exitcode;
        private readonly bool _intentionalexit;

        public ServerExitedEventArgs(int exitcode, bool intentionalexit)
        {
            _exitcode = exitcode;
            _intentionalexit = intentionalexit;
        }
        public int ExitCode
        {
            get { return _exitcode; }
        }
        public bool IntentionalExit
        {
            get { return _intentionalexit; }
        }
    }
    public class RandomServerEventArgs
    {
        private readonly string _eventname;

        public RandomServerEventArgs(string eventname)
        {
            _eventname = eventname;
        }
        public string EventName
        {
            get { return _eventname; }
        }
    }
    public class ServerErrorEventArgs : EventArgs
    {
        private readonly Exception _exception;
        private readonly string _message;
        public ServerErrorEventArgs(Exception ex)
        {
            _exception = ex;
            _message = ex.Message;
        }
        public ServerErrorEventArgs(string message)
        {
            _exception = new Exception(message);
            _message = message;
        }
        public ServerErrorEventArgs(string message, Exception ex)
        {
            _exception = ex;
            _message = message;
        }
        public string Message
        {
            get { return _message; }
        }
        public Exception Exception
        {
            get
            {
                return _exception;
            }
        }
    }
}
