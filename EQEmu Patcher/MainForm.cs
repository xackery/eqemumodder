﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.WindowsAPICodePack.Taskbar;
using System.Diagnostics;
using System.Threading;

namespace EQEmu_Patcher
{

    public partial class MainForm : Form
    {

        public static string serverName;
        public static string filelistUrl; //filelist url
        public static string patcherUrl; //patcher url e.g. eqemupatcher-hash.txt
        public static string version; //version of file
        string fileName; //base name of executable
        bool isPatching = false;
        bool isPatchCancelled = false;
        bool isPendingPatch = false; // This is used to indicate that someone pressed "Patch" before we did some background update checks
        string myHash; //my MD5 generated hash
        bool isNeedingSelfUpdate;
        bool isLoading;
        bool isAutoPatch = false;
        bool isAutoPlay = false;
        CancellationTokenSource cts;
        System.Diagnostics.Process process;

        //Note that for supported versions, the 3 letter suffix is needed on the filelist_###.yml file.
        public static List<VersionTypes> supportedClients = new List<VersionTypes> { //Supported clients for patcher
            //VersionTypes.Unknown, //unk
            //VersionTypes.Titanium, //tit
            //VersionTypes.Underfoot, //und
            //VersionTypes.Secrets_Of_Feydwer, //sof
            //VersionTypes.Seeds_Of_Destruction, //sod
            VersionTypes.Rain_Of_Fear, //rof
            VersionTypes.Rain_Of_Fear_2 //rof
            //VersionTypes.Broken_Mirror, //bro
        };

        private Dictionary<VersionTypes, ClientVersion> clientVersions = new Dictionary<VersionTypes, ClientVersion>();

        VersionTypes currentVersion;

       // TaskbarItemInfo tii = new TaskbarItemInfo();
        public MainForm()
        {
            InitializeComponent();
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            isLoading = true;
            version = Assembly.GetEntryAssembly().GetName().Version.ToString();
            Console.WriteLine($"Initializing {version}");
            Console.WriteLine($"Current Directory: {Directory.GetCurrentDirectory()}");
            cts = new CancellationTokenSource();


            serverName = "Xack's EQEmu Modder";

            fileName = "eqemumodder";

            filelistUrl = "https://github.com/xackery/eqemupatcher/releases/latest/download";
            if (!filelistUrl.EndsWith("/")) filelistUrl += "/";

            patcherUrl = $"https://github.com/xackery/eqemupatcher/releases/latest/download/";
            if (!patcherUrl.EndsWith("/")) patcherUrl += "/";

            txtList.Visible = false;
            splashLogo.Visible = true;
            if (this.Width < 432) {
                this.Width = 432;
            }
            if (this.Height < 550)
            {
                this.Height = 550;
            }
            buildClientVersions();
            IniLibrary.Load();
            detectClientVersion();
            isAutoPlay = (IniLibrary.instance.AutoPlay.ToLower() == "true");
            isAutoPatch = (IniLibrary.instance.AutoPatch.ToLower() == "true");
            chkAutoPlay.Checked = isAutoPlay;
            chkAutoPatch.Checked = isAutoPatch;
            try
            {
                if (File.Exists(Application.ExecutablePath + ".old"))
                {
                    File.Delete(Application.ExecutablePath + ".old");
                }

            } catch (Exception exDelete)
            {
                Console.WriteLine($"Failed to delete .old file: {exDelete.Message}");
            }

            if (IniLibrary.instance.ClientVersion == VersionTypes.Unknown)
            {
                detectClientVersion();
                if (currentVersion == VersionTypes.Unknown)
                {
                    this.Close();
                }
                IniLibrary.instance.ClientVersion = currentVersion;
                IniLibrary.Save();
            }

            this.Text = serverName + " (Client: " + currentVersion.ToString().Replace("_", " ") + ")";
            progressBar.Minimum = 0;
            progressBar.Maximum = 10000;
            progressBar.Value = 0;
            StatusLibrary.SubscribeProgress(new StatusLibrary.ProgressHandler((int value) => {
                Invoke((MethodInvoker)delegate {
                    progressBar.Value = value;
                    if (Environment.OSVersion.Version.Major < 6) {
                        return;
                    }
                    var taskbar = TaskbarManager.Instance;
                    taskbar.SetProgressValue(value, 10000);
                    taskbar.SetProgressState((value == 10000) ? TaskbarProgressBarState.NoProgress : TaskbarProgressBarState.Normal);
                });
            }));

            StatusLibrary.SubscribeLogAdd(new StatusLibrary.LogAddHandler((string message) => {
                Invoke((MethodInvoker)delegate {
                    if (!txtList.Visible)
                    {
                        txtList.Visible = true;
                        splashLogo.Visible = false;
                    }
                    txtList.AppendText(message + "\r\n");
                });
            }));

            StatusLibrary.SubscribePatchState(new StatusLibrary.PatchStateHandler((bool isPatchGoing) => {
                Invoke((MethodInvoker)delegate {

                    btnCheck.BackColor = SystemColors.Control;
                    if (isPatchGoing)
                    {
                        btnCheck.Text = "Cancel";
                        return;
                    }

                    btnCheck.Text = "Patch";
                });
            }));

            string webUrl = $"{filelistUrl}rof/filelist_rof.yml";

            string response = await DownloadFile(cts, webUrl, "filelist.yml");
            if (response != "")
            {
                webUrl = $"{filelistUrl}/filelist_rof.yml";
                response = await DownloadFile(cts, webUrl, "filelist.yml");
                if (response != "")
                {
                    MessageBox.Show("Failed to fetch filelist from " + webUrl + ": " + response);
                    this.Close();
                    return;
                }
            }

            string url = $"{patcherUrl}{fileName}-hash.txt";
            try
            {
                var data = await Download(cts, url);
                response = System.Text.Encoding.Default.GetString(data).ToUpper();
            } catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch patch from {url}: {ex.Message}");
            }

            if (response != "")
            {
                myHash = UtilityLibrary.GetMD5(Application.ExecutablePath);
                if (response != myHash)
                {
                    isNeedingSelfUpdate = true;
                    //StatusLibrary.Log($"{myHash} vs {response} selfpatch");
                    if (!isPendingPatch)
                    {
                        btnCheck.BackColor = Color.Red;
                    }
                }
            }

            FileList filelist;

            using (var input = File.OpenText($"{System.IO.Path.GetDirectoryName(Application.ExecutablePath)}\\filelist.yml"))
            {
                var deserializerBuilder = new DeserializerBuilder().WithNamingConvention(new CamelCaseNamingConvention());

                var deserializer = deserializerBuilder.Build();

                filelist = deserializer.Deserialize<FileList>(input);
            }

            if (filelist.version != IniLibrary.instance.LastPatchedVersion)
            {
                if (!isPendingPatch)
                {
                    btnCheck.BackColor = Color.Red;
                }
            } else
            {
                if (isAutoPlay) PlayGame();
            }
            isLoading = false;
            if (File.Exists("eqemupatcher.png"))
            {
                splashLogo.Load("eqemupatcher.png");
            }
            cts.Cancel();
        }

        private void detectClientVersion()
        {
            try
            {

                var hash = UtilityLibrary.GetEverquestExecutableHash(AppDomain.CurrentDomain.BaseDirectory);
                if (hash == "")
                {
                    MessageBox.Show("Please run this patcher in your Everquest directory.");
                    this.Close();
                    return;
                }
                currentVersion = VersionTypes.Rain_Of_Fear;
                splashLogo.Image = Properties.Resources.rof;
                if (currentVersion == VersionTypes.Unknown)
                {
                    if (MessageBox.Show("Unable to recognize the Everquest client in this directory, open a web page to report to devs?", "Visit", MessageBoxButtons.YesNo, MessageBoxIcon.Asterisk) == DialogResult.Yes)
                    {
                        System.Diagnostics.Process.Start("https://github.com/Xackery/eqemupatcher/issues/new?title=A+New+EQClient+Found&body=Hi+I+Found+A+New+Client!+Hash:+" + hash);
                    }
                    StatusLibrary.Log($"Unable to recognize the Everquest client in this directory, send to developers: {hash}");
                }
                else
                {
                    //StatusLibrary.Log($"You seem to have put me in a {clientVersions[currentVersion].FullName} client directory");
                }

                //MessageBox.Show(""+currentVersion);
                //StatusLibrary.Log($"If you wish to help out, press the scan button on the bottom left and wait for it to complete, then copy paste this data as an Issue on github!");
            }
            catch (UnauthorizedAccessException err)
            {
                MessageBox.Show("You need to run this program with Administrative Privileges" + err.Message);
                return;
            }
        }

        //Build out all client version's dictionary
        private void buildClientVersions()
        {
            clientVersions.Clear();
            clientVersions.Add(VersionTypes.Titanium, new ClientVersion("Titanium", "titanium"));
            clientVersions.Add(VersionTypes.Secrets_Of_Feydwer, new ClientVersion("Secrets Of Feydwer", "sof"));
            clientVersions.Add(VersionTypes.Seeds_Of_Destruction, new ClientVersion("Seeds of Destruction", "sod"));
            clientVersions.Add(VersionTypes.Rain_Of_Fear, new ClientVersion("Rain of Fear", "rof"));
            clientVersions.Add(VersionTypes.Rain_Of_Fear_2, new ClientVersion("Rain of Fear 2", "rof2"));
            clientVersions.Add(VersionTypes.Underfoot, new ClientVersion("Underfoot", "underfoot"));
            clientVersions.Add(VersionTypes.Broken_Mirror, new ClientVersion("Broken Mirror", "brokenmirror"));
        }


        private void btnStart_Click(object sender, EventArgs e)
        {
            PlayGame();
        }

        private void PlayGame()
        {
            try
            {
                process = UtilityLibrary.StartEverquest();
                if (process != null) this.Close();
                else MessageBox.Show("The process failed to start");
            }
            catch (Exception err)
            {
                MessageBox.Show("An error occured while trying to start everquest: " + err.Message);
            }
        }


        private void btnCheck_Click(object sender, EventArgs e)
        {
            if (isLoading && !isPendingPatch)
            {
                isPendingPatch = true;
                pendingPatchTimer.Enabled = true;
                StatusLibrary.Log("Checking for updates...");
                btnCheck.Text = "Cancel";
                return;
            }

            if (isPatching)
            {
                isPatchCancelled = true;
                cts.Cancel();
            }
            Console.WriteLine("patch button called");
            StartPatch();
        }

        public static async Task<string> DownloadFile(CancellationTokenSource cts, string url, string path)
        {
            path = path.Replace("/", "\\");
            if (path.Contains("\\")) { //Make directory if needed.
                string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath) + "\\" + path.Substring(0, path.LastIndexOf("\\"));
                Directory.CreateDirectory(dir);
            }
            return await UtilityLibrary.DownloadFile(cts, url, path);
        }

        public static async Task<byte[]> Download(CancellationTokenSource cts, string url)
        {
            return await UtilityLibrary.Download(cts, url);
        }

        private void StartPatch()
        {
            if (isPatching)
            {
                Console.WriteLine("premature patch call");
                return;
            }
            cts = new CancellationTokenSource();
            isPatchCancelled = false;
            txtList.Text = "";
            StatusLibrary.SetPatchState(true);
            isPatching = true;
            Task.Run(async () =>
            {
                try
                {
                    await AsyncPatch();
                } catch (Exception e)
                {
                    StatusLibrary.Log($"Exception during patch: {e.Message}");
                }
                StatusLibrary.SetPatchState(false);
                isPatching = false;
                isPatchCancelled = false;
                cts.Cancel();
                if (isAutoPlay) PlayGame();
            });
        }

        private async Task AsyncPatch()
        {
            Stopwatch start = Stopwatch.StartNew();
            StatusLibrary.Log($"Patching with patcher version {version}...");
            StatusLibrary.SetProgress(0);
            FileList filelist;

            using (var input = File.OpenText($"{System.IO.Path.GetDirectoryName(Application.ExecutablePath)}\\filelist.yml"))
            {
                var deserializerBuilder = new DeserializerBuilder().WithNamingConvention(new CamelCaseNamingConvention());

                var deserializer = deserializerBuilder.Build();

                filelist = deserializer.Deserialize<FileList>(input);
            }

            double totalBytes = 0; //total patch size
            double currentBytes = 1; // current patched size
            double patchedBytes = 0; // how many files patched size

            foreach (var entry in filelist.downloads)
            {
                totalBytes += entry.size;
            }
            if (totalBytes == 0) totalBytes = 1;

            if (myHash != "" && isNeedingSelfUpdate)
            {
                StatusLibrary.Log("Self update needed, starting with self patch...");
                string url = $"{patcherUrl}/{fileName}.exe";
                try
                {
                    var data = await Download(cts, url);
                    if (File.Exists(Application.ExecutablePath + ".old")) {
                        File.Delete(Application.ExecutablePath + ".old");
                    }
                    File.Move(Application.ExecutablePath, Application.ExecutablePath + ".old");
                    using (var w = File.Create(Application.ExecutablePath))
                    {
                        await w.WriteAsync(data, 0, data.Length, cts.Token);
                    }
                    StatusLibrary.Log($"Self update of {generateSize(data.Length)} will be used next run");

                } catch (Exception e)
                {
                    StatusLibrary.Log($"Self update failed {url}: {e.Message}");
                }
                isNeedingSelfUpdate = false;

                StatusLibrary.Log("Resuming patching...");
            }
            if (!filelist.downloadprefix.EndsWith("/")) filelist.downloadprefix += "/";
            foreach (var entry in filelist.downloads)
            {
                if (isPatchCancelled)
                {
                    Console.WriteLine("cancelled while downloading");
                    StatusLibrary.Log("Patching cancelled.");
                    return;
                }

                StatusLibrary.SetProgress((int)(currentBytes / totalBytes * 10000));

                var path = entry.name.Replace("/", "\\");
                if (!UtilityLibrary.IsPathChild(path))
                {
                    StatusLibrary.Log("Path " + path + " might be outside of your Everquest directory. Skipping download to this location.");
                    continue;
                }

                // check if file exists and is already patched
                if (File.Exists(path)) {
                    var md5 = UtilityLibrary.GetMD5(path);
                    if (md5.ToUpper() == entry.md5.ToUpper())
                    {
                        currentBytes += entry.size;
                        continue;
                    }
                    Console.WriteLine($"{path} {md5} vs {entry.md5}");
                }


                string url = filelist.downloadprefix + entry.name.Replace("\\", "/");

                string resp = await DownloadFile(cts, url, entry.name);
                if (resp != "")
                {
                    if (resp == "404")
                    {
                        StatusLibrary.Log($"Failed to download {entry.name} ({generateSize(entry.size)}) from {url}, 404 error (website may be down?)");
                        return;
                    }
                    StatusLibrary.Log($"Failed to download {entry.name} ({generateSize(entry.size)}) from {url}: {resp}");
                    return;
                }
                StatusLibrary.Log($"{entry.name} ({generateSize(entry.size)})");

                currentBytes += entry.size;
                patchedBytes += entry.size;
            }

            if (filelist.deletes != null && filelist.deletes.Count > 0)
            {
                foreach (var entry in filelist.deletes)
                {
                    if (isPatchCancelled)
                    {
                        Console.WriteLine("cancellled while deleting");
                        StatusLibrary.Log("Patching cancelled.");
                        return;
                    }
                    if (!UtilityLibrary.IsPathChild(entry.name))
                    {
                        StatusLibrary.Log("Path " + entry.name + " might be outside your Everquest directory. Skipping deletion of this file.");
                        continue;
                    }
                    if (File.Exists(entry.name))
                    {
                        StatusLibrary.Log("Deleting " + entry.name + "...");
                        File.Delete(entry.name);
                    }
                }
            }

            StatusLibrary.SetProgress(10000);
            if (patchedBytes == 0)
            {
                string version = filelist.version;
                if (version.Length >= 8)
                {
                    version = version.Substring(0, 8);
                }

                StatusLibrary.Log($"Up to date with patch {version}.");
                return;
            }

            string elapsed = start.Elapsed.ToString("ss\\.ff");
            StatusLibrary.Log($"Complete! Patched {generateSize(patchedBytes)} in {elapsed} seconds. Press Play to begin.");
            IniLibrary.instance.LastPatchedVersion = filelist.version;
            IniLibrary.Save();
            return;
        }

        private void chkAutoPlay_CheckedChanged(object sender, EventArgs e)
        {
            if (isLoading) return;
            isAutoPlay = chkAutoPlay.Checked;
            IniLibrary.instance.AutoPlay = (isAutoPlay) ? "true" : "false";
            if (isAutoPlay) StatusLibrary.Log("To disable autoplay: edit eqemupatcher.yml or wait until next patch.");

            IniLibrary.Save();
        }

        private void chkAutoPatch_CheckedChanged(object sender, EventArgs e)
        {
            if (isLoading) return;
            isAutoPatch = chkAutoPatch.Checked;
            IniLibrary.instance.AutoPatch = (isAutoPatch) ? "true" : "false";
            IniLibrary.Save();
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (isAutoPatch)
            {
                if (!isLoading)
                {
                    StartPatch();
                    return;
                }
                isPendingPatch = true;
                pendingPatchTimer.Enabled = true;
                StatusLibrary.Log("Checking for updates...");
                btnCheck.Text = "Cancel";
            }
        }

        private string generateSize(double size) {
            if (size < 1024) {
                return $"{Math.Round(size, 2)} bytes";
            }

            size /= 1024;
            if (size < 1024)
            {
                return $"{Math.Round(size, 2)} KB";
            }

            size /= 1024;
            if (size < 1024)
            {
                return $"{Math.Round(size, 2)} MB";
            }

            size /= 1024;
            if (size < 1024)
            {
                return $"{Math.Round(size, 2)} GB";
            }

            return $"{Math.Round(size, 2)} TB";
        }

        private void pendingPatchTimer_Tick(object sender, EventArgs e)
        {
            if (isLoading) return;
            pendingPatchTimer.Enabled = false;
            isPendingPatch = false;
            btnCheck_Click(sender, e);
        }
    }

    public class FileList
    {
        public string version { get; set; }

        public List<FileEntry> deletes { get; set; }
        public string downloadprefix { get; set; }
        public List<FileEntry> downloads { get; set; }
        public List<FileEntry> unpacks { get; set; }

    }

    public class FileEntry
    {
        public string name { get; set;  }
        public string md5 { get; set; }
        public string date { get; set; }
        public string zip { get; set; }
        public int size { get; set; }
    }
}


