﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Threading;
using System.Threading.Tasks;

using WiiTUIO.Provider;
using WiiTUIO.Properties;
using System.Windows.Input;
using WiiTUIO.Output;
using Microsoft.Win32;
using System.Diagnostics;
using Newtonsoft.Json;
using MahApps.Metro.Controls;
using System.Windows.Interop;
using System.Net;
using Newtonsoft.Json.Linq;
using WiiTUIO.DeviceUtils;
using WiiCPP;
using WiiTUIO.Output.Handlers.Xinput;
using WiiTUIO.ArcadeHook;
using System.IO.Pipes;

namespace WiiTUIO
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    public partial class MainWindow : MetroWindow, WiiCPP.WiiPairListener
    {
        private bool wiiPairRunning = false;

        private bool minimizedOnce = false;

        private Thread wiiPairThread;

        private bool providerHandlerConnected = false;

        private bool tryingToConnect = false;

        private bool startupPair = false;

        private ArcadeHookMain arcadeHook;

        private Thread arcadeHookThread;

        private Mutex statusStackMutex = new Mutex();

        private SystemProcessMonitor processMonitor;

        private CommandListener commandListener;

        private IntPtr previousForegroundWindow = IntPtr.Zero;

        /// <summary>
        /// A reference to the WiiProvider we want to use to get/forward input.
        /// </summary>
        private IProvider pWiiProvider = null;

        WiiCPP.WiiPair wiiPair = null;

        System.Windows.Threading.Dispatcher overlayDispatcher = null;
        Thread overlayUIThread = null;

        /// <summary>
        /// Boolean to tell if we are connected to the mote and network.
        /// </summary>
        private bool bConnected = false;

        private static MainWindow defaultInstance;

        public static MainWindow Current
        {
            get
            {
                return defaultInstance;
            }
        }

        /// <summary>
        /// Construct a new Window.
        /// </summary>
        public MainWindow()
        {
            //Set highest priority on main process.
            Process currentProcess = Process.GetCurrentProcess();
            currentProcess.PriorityClass = ProcessPriorityClass.High;
            Thread.CurrentThread.Priority = ThreadPriority.Normal;

            if (Settings.Default.minimizeOnStart)
            {
                this.ShowActivated = false;
                this.WindowState = System.Windows.WindowState.Minimized;
            }

            Settings.Default.primaryMonitor = "";

            defaultInstance = this;

            // Load from the XAML.
            InitializeComponent();
        }

        protected override void OnInitialized(EventArgs e)
        {
            KeymapDatabase.Current.CreateDefaultFiles();

            base.OnInitialized(e);

            KeymapConfigWindow.Instance.Visibility = System.Windows.Visibility.Collapsed;

            this.mainPanel.Visibility = Visibility.Visible;
            this.canvasSettings.Visibility = Visibility.Collapsed;
            this.canvasAbout.Visibility = Visibility.Collapsed;
            this.spPairing.Visibility = Visibility.Collapsed;
            this.tbPair2.Visibility = Visibility.Visible;
            this.tbPairDone.Visibility = Visibility.Collapsed;
            this.spErrorMsg.Visibility = Visibility.Collapsed;
            this.spInfoMsg.Visibility = Visibility.Collapsed;
            this.animateExpand(this.mainPanel);

            overlayUIThread = new Thread(() =>
            {
                previousForegroundWindow = UIHelpers.GetForegroundWindow();
                OverlayWindow.Current.Show();
                CalibrationOverlay.Current.Show();

                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(delegate()
                {
                    D3DCursorWindow.Current.Start((new WindowInteropHelper(OverlayWindow.Current)).Handle);
                }));

                if (previousForegroundWindow != IntPtr.Zero && Settings.Default.minimizeOnStart)
                    UIHelpers.SetForegroundWindow(previousForegroundWindow);

                // Grab dispatcher for current thread
                overlayDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                System.Windows.Threading.Dispatcher.Run();
                Console.WriteLine("Overlay UI Thread Ended");
            });
            overlayUIThread.SetApartmentState(ApartmentState.STA);
            overlayUIThread.IsBackground = true;
            overlayUIThread.Priority = ThreadPriority.Highest;
            overlayUIThread.Start();

            Application.Current.Exit += appWillExit;
            Application.Current.SessionEnding += windowsShutdownEvent;

            wiiPair = new WiiCPP.WiiPair();
            wiiPair.addListener(this);

            Settings.Default.PropertyChanged += Settings_PropertyChanged;

            // Create the providers.
            this.createProvider();
            //this.createProviderHandler();

            if (Settings.Default.pairOnStart)
            {
                this.startupPair = true;
                this.runWiiPair();
            }
            else //if (Settings.Default.connectOnStart)
            {
                this.connectProvider();
            }

            AppSettingsUC settingspanel = new AppSettingsUC();
            settingspanel.OnClose += SettingsPanel_OnClose;

            this.canvasSettings.Children.Add(settingspanel);

            AboutUC aboutpanel = new AboutUC();
            aboutpanel.OnClose += AboutPanel_OnClose;

            this.canvasAbout.Children.Add(aboutpanel);

            Loaded += MainWindow_Loaded;

            checkNewVersion();

            if (Settings.Default.disconnectWiimotesOnDolphin)
            {
                this.processMonitor = SystemProcessMonitor.Default;
                this.processMonitor.ProcessChanged += processChanged;
                this.processMonitor.Start();
            }

            StartArcadeHook();

            this.commandListener = CommandListener.Default;

            AudioUtil.IsValid("sound1");
            AudioUtil.IsValid("sound2");
        }

        private void processChanged(ProcessChangedEvent obj)
        {
            if((Settings.Default.dolphin_path == "" && obj.Process.ProcessName == "Dolphin"))
            {
                Console.WriteLine("Dolphin detected. Disconnecting provider. Hiding overlay window.");
                this.disconnectDolphin();
                D3DCursorWindow.Current.RefreshCursors();
            }
            else if(obj.Process.ProcessName == "Dolphin" && (Settings.Default.dolphin_path.IndexOfAny(Path.GetInvalidPathChars()) == -1))
            {
                if (obj.Process.MainModule?.FileName == Path.GetFullPath(Settings.Default.dolphin_path))
                {
                    Console.WriteLine("Dolphin detected. Disconnecting provider. Hiding overlay window.");
                    this.disconnectDolphin();
                    D3DCursorWindow.Current.RefreshCursors();
                }
            }
            else
            {
                this.connectProvider();
            }
        }

        private HttpWebRequest wrGETURL;

        private void checkNewVersion()
        {
            try
            {
                string sURL;
                sURL = "http://www.touchmote.net/api/versionUpdate?version=1.0b15";

                wrGETURL = (HttpWebRequest)HttpWebRequest.Create(sURL);
                wrGETURL.BeginGetResponse(new AsyncCallback(checkNewVersionResponse), null);
            }
            catch(Exception e)
            {

            }

        }

        private void checkNewVersionResponse(IAsyncResult result)
        {
            try
            {
                Stream objStream;
                objStream = wrGETURL.EndGetResponse(result).GetResponseStream();

                StreamReader objReader = new StreamReader(objStream);

                var serializer = new JsonSerializer();
                JObject jObject = (JObject)serializer.Deserialize(objReader, typeof(JObject));

                bool needsUpdate = (bool)jObject.GetValue("needs_update").ToObject(typeof(bool));

                if (needsUpdate)
                {
                    this.ShowMessage("A new version (" + jObject.GetValue("latest_version").ToString() + ") is available at touchmote.net", MessageType.Info);
                }
            }
            catch(Exception e)
            {

            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Settings.Default.minimizeToTray)
            {
                MinimizeToTray.Enable(this, Settings.Default.minimizeOnStart);
            }
            
            KeymapConfigWindow.Instance.Owner = this;
        }

        private void windowsShutdownEvent(object sender, SessionEndingCancelEventArgs e)
        {
            Settings.Default.Save();
        }

        private void AboutPanel_OnClose()
        {
            this.showMain();
        }

        private void SettingsPanel_OnClose()
        {
            Settings.Default.Save();
            this.showMain();
        }

        void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {

            if (e.PropertyName == "minimizeToTray")
            {
                if (Settings.Default.minimizeToTray)
                {
                    MinimizeToTray.Enable(this,false);
                }
                else
                {
                    MinimizeToTray.Disable(this);
                }
            }

        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

        }
        
        private void appWillExit(object sender, ExitEventArgs e)
        {
            if (overlayDispatcher != null)
            {
                overlayDispatcher.InvokeShutdown();
                overlayDispatcher = null;
            }

            overlayUIThread.Join();
            overlayUIThread = null;

            this.stopWiiPair();
            this.disconnectProvider();
            this.StopArcadeHook();

            ViGEmBusClientSingleton.Disconnect();
            //this.disconnectProviderHandler();
        }


        /// <summary>
        /// This is called when the wii remote is connected
        /// </summary>
        /// <param name="obj"></param>
        private void pWiiProvider_OnConnect(int ID, int totalWiimotes)
        {
            // Dispatch it.
            Dispatcher.BeginInvoke(new Action(delegate()
            {
                this.bConnected = true;

                
                if (totalWiimotes == 1)
                {
                    this.connectedCount.Content = "Un Wiimote conectado";
                }
                else
                {
                    this.connectedCount.Content = totalWiimotes+" Wiimotes conectados";
                }
                statusStackMutex.WaitOne();
                WiimoteStatusUC uc = new WiimoteStatusUC(ID);
                uc.Visibility = Visibility.Collapsed;
                this.statusStack.Children.Add(uc);
                this.animateExpand(uc);
                statusStackMutex.ReleaseMutex();

                //connectProviderHandler();

            }), null);


        }

        /// <summary>
        /// This is called when the wii remote is disconnected
        /// </summary>
        /// <param name="obj"></param>
        private void pWiiProvider_OnDisconnect(int ID, int totalWiimotes)
        {
            // Dispatch it.
            Dispatcher.BeginInvoke(new Action(delegate()
            {
                if (totalWiimotes == 1)
                {
                    this.connectedCount.Content = "Un Wiimote conectado";
                }
                else
                {
                    this.connectedCount.Content = totalWiimotes + " Wiimotes conectados";
                }
                statusStackMutex.WaitOne();
                foreach (UIElement child in this.statusStack.Children)
                {
                    WiimoteStatusUC uc = (WiimoteStatusUC)child;
                    if (uc.ID == ID)
                    {
                        this.animateCollapse(uc,true);
                        //this.statusStack.Children.Remove(child);
                        break;
                    }
                }
                statusStackMutex.ReleaseMutex();
                if (totalWiimotes == 0)
                {
                    this.bConnected = false;

                    //disconnectProviderHandler();
                }

            }), null);
        }


        private Mutex pCommunicationMutex = new Mutex();
      
        /// <summary>
        /// This is called when the battery state changes.
        /// </summary>
        /// <param name="obj"></param>
        private void pWiiProvider_OnStatusUpdate(WiimoteStatus status)
        {
            // Dispatch it.
            Dispatcher.BeginInvoke(new Action(delegate()
            {
                statusStackMutex.WaitOne();
                foreach(UIElement child in this.statusStack.Children) {
                    WiimoteStatusUC uc = (WiimoteStatusUC)child;
                    if (uc.ID == status.ID)
                    {
                        uc.updateStatus(status);
                    }
                }
                statusStackMutex.ReleaseMutex();
            }), null);
        }


        #region Messages - Err/Inf

        public enum MessageType { Info, Error };

        public void ShowMessage(string message, MessageType eType)
        {
            Dispatcher.BeginInvoke(new Action(delegate()
            {
            switch (eType)
            {
                case MessageType.Error:
                    this.tbErrorMsg.Text = message;
                    this.animateExpand(this.spErrorMsg);
                    break;
                case MessageType.Info:
                    this.tbInfoMsg.Text = message;
                    this.animateExpand(this.spInfoMsg);
                    break;
            }
            

            // Fade in and out.
            //messageFadeIn(fTimeout, false);
            
            }), null);
        }

        #endregion


        private void animateExpand(FrameworkElement elem)
        {
            UIHelpers.animateExpand(elem);
        }

        private void animateCollapse(FrameworkElement elem, bool remove)
        {
            UIHelpers.animateCollapse(elem,remove);
        }

        private void showConfig()
        {
            if (this.mainPanel.IsVisible)
            {
                animateCollapse(this.mainPanel,false);
            }
            if (this.canvasAbout.IsVisible)
            {
                animateCollapse(this.canvasAbout, false);
            }
            if (!this.canvasSettings.IsVisible)
            {
                animateExpand(this.canvasSettings);
            }
            //this.mainPanel.Visibility = Visibility.Collapsed;
            //this.canvasAbout.Visibility = Visibility.Collapsed;
            //this.canvasSettings.Visibility = Visibility.Visible;
        }

        private void showMain()
        {
            if (this.canvasSettings.IsVisible)
            {
                animateCollapse(this.canvasSettings, false);
            }
            if (this.canvasAbout.IsVisible)
            {
                animateCollapse(this.canvasAbout, false);
            }
            if (!this.mainPanel.IsVisible)
            {
                animateExpand(this.mainPanel);
            }
            //this.canvasSettings.Visibility = Visibility.Collapsed;
            //this.canvasAbout.Visibility = Visibility.Collapsed;
            //this.mainPanel.Visibility = Visibility.Visible;
        }

        private void showAbout()
        {
            if (this.canvasSettings.IsVisible)
            {
                animateCollapse(this.canvasSettings, false);
            }
            if (this.mainPanel.IsVisible)
            {
                animateCollapse(this.mainPanel, false);
            }
            if (!this.canvasAbout.IsVisible)
            {
                animateExpand(this.canvasAbout);
            }
            //this.mainPanel.Visibility = Visibility.Collapsed;
            //this.canvasAbout.Visibility = Visibility.Visible;
            //this.canvasSettings.Visibility = Visibility.Collapsed;
        }
        
        #region WiiProvider
        /// <summary>
        /// Try to create the WiiProvider (this involves connecting to the Wiimote).
        /// </summary>
        private void connectProvider()
        {
            if (!this.tryingToConnect)
            {
                Launcher.Launch("Driver", "devcon", " enable \"BTHENUM*_VID*57e*_PID&0306*\"", new Action(delegate ()
                {
                    Launcher.Launch("Driver", "devcon", " enable \"BTHENUM*_VID*57e*_PID&0330*\"", null);
                }));

                this.startProvider();

            }
        }

        /// <summary>
        /// Try to create the WiiProvider (this involves connecting to the Wiimote).
        /// </summary>
        private bool startProvider()
        {
            try
            {
                this.pWiiProvider.start();
                this.tryingToConnect = true;
                return true;
            }
            catch (Exception pError)
            {
                // Tear down.
                try
                {
                    this.pWiiProvider.stop();
                    this.tryingToConnect = false;
                    if (Settings.Default.completelyDisconnect)
                    {
                        completelyDisconnectAll();
                    }
                }
                catch { }

                // Report the error.
                Console.WriteLine(pError.Message);
                ShowMessage(pError.Message, MessageType.Error);
                //MessageBox.Show(pError.Message, "WiiTUIO", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        
        /// <summary>
        /// Try to create the WiiProvider (this involves connecting to the Wiimote).
        /// </summary>
        private bool createProvider()
        {
            try
            {
                // Connect a Wiimote, hook events then start.
                this.pWiiProvider = new MultiWiiPointerProvider();
                this.pWiiProvider.OnStatusUpdate += new Action<WiimoteStatus>(pWiiProvider_OnStatusUpdate);
                this.pWiiProvider.OnConnect += new Action<int,int>(pWiiProvider_OnConnect);
                this.pWiiProvider.OnDisconnect += new Action<int,int>(pWiiProvider_OnDisconnect);
                return true;
            }
            catch (Exception pError)
            {
                // Tear down.
                try
                {
                    
                }
                catch { }
                Console.WriteLine(pError.Message);
                // Report the error.cr
                ShowMessage(pError.Message, MessageType.Error);
                //MessageBox.Show(pError.Message, "WiiTUIO", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Tear down the provider connections.
        /// </summary>
        private void disconnectProvider()
        {
            this.tryingToConnect = false;
            // Disconnect the Wiimote.
            if (this.pWiiProvider != null)
            {
                this.pWiiProvider.stop();
            }

            //this.pWiiProvider = null;
            if (Settings.Default.completelyDisconnect)
            {
                completelyDisconnectAll();
            }
        }

        private void disconnectDolphin()
        {
            this.tryingToConnect = false;
            // Disconnect the Wiimote.
            if (this.pWiiProvider != null)
            {
                this.pWiiProvider.stop();
            }
        }

        private void completelyDisconnectAll()
        {
            //Disable Wiimote in device manager to disconnect it from the computer (so it doesn't drain battery when not used)
            Launcher.Launch("Driver", "devcon", " disable \"BTHENUM*_VID*57e*_PID&0306*\"", new Action(delegate ()
            {
                Launcher.Launch("Driver", "devcon", " enable \"BTHENUM*_VID*57e*_PID&0306*\"", new Action(delegate ()
                {
                    Launcher.Launch("Driver", "devcon", " disable \"BTHENUM*_VID*57e*_PID&0330*\"", new Action(delegate ()
                    {
                        Launcher.Launch("Driver", "devcon", " enable \"BTHENUM*_VID*57e*_PID&0330*\"", null);
                    }));
                }));
            }));
        }
        #endregion

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }
    
        private void PairWiimotes_Click(object sender, RoutedEventArgs e)
        {
            //this.disableMainControls();
            //this.pairWiimoteOverlay.Visibility = Visibility.Visible;
            //this.pairWiimoteOverlayPairing.Visibility = Visibility.Visible;

            this.runWiiPair();
        }

        private void runWiiPair() {
            if (!this.wiiPairRunning)
            {
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    this.animateExpand(this.spPairing);//.Visibility = Visibility.Visible;
                    this.tbPair2.Visibility = Visibility.Collapsed;
                    this.tbPairDone.Visibility = Visibility.Visible;

                    this.pairWiimoteTRFail.Visibility = Visibility.Hidden;
                    this.pairWiimoteTryAgain.Visibility = Visibility.Hidden;
                    this.pairProgress.Visibility = Visibility.Visible;
                }), null);
                if (this.wiiPairThread != null)
                {
                    this.wiiPairThread.Abort();
                }
                this.wiiPairThread = new Thread(new ThreadStart(wiiPairThreadWorker));
                this.wiiPairThread.Priority = ThreadPriority.Normal;
                this.wiiPairThread.Start();
            }
        }

        private void wiiPairThreadWorker()
        {
            this.wiiPairRunning = true;
            wiiPair.start(true,10);//First remove all connected devices.
        }

        private void stopWiiPair() {
            this.wiiPairRunning = false;
            wiiPair.stop();
        }

        public void onPairingProgress(WiiCPP.WiiPairReport report)
        {
            Console.WriteLine("Pairing progress: number=" + report.numberPaired + " removeMode=" + report.removeMode + " devicelist=" + report.deviceNames);
            if (report.status == WiiCPP.WiiPairReport.Status.RUNNING)
            {
                if (report.numberPaired > 0)
                {
                    Settings.Default.pairedOnce = true;
                }
            }
            else
            {
                if (report.removeMode && report.status != WiiCPP.WiiPairReport.Status.CANCELLED)
                {
                    this.wiiPairRunning = true;

                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        this.connectProvider();
                    }), null);

                    int stopat = 10;
                    if (this.startupPair)
                    {
                        stopat = 1;
                        this.startupPair = false;
                    }
                    wiiPair.start(false, stopat); //Run the actual pairing after removing all previous connected devices.
                }
                else
                {
                    this.wiiPairRunning = false;
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        //this.canvasPairing.Visibility = Visibility.Collapsed;
                        this.animateCollapse(this.spPairing,false);
                        this.tbPair2.Visibility = Visibility.Visible;
                        this.tbPairDone.Visibility = Visibility.Collapsed;

                        this.pairProgress.IsActive = false;
                    }), null);
                }
            }
        }


        private void pairWiimoteTryAgain_Click(object sender, RoutedEventArgs e)
        {
            this.stopWiiPair();
            this.runWiiPair();
        }

        public void onPairingStarted()
        {
            this.disconnectProvider();
            Dispatcher.BeginInvoke(new Action(delegate()
            {

                this.pairProgress.IsActive = true;
            }), null);
        }

        public void pairingConsole(string message)
        {
            Console.Write(message);
        }

        public void pairingMessage(string message, WiiCPP.WiiPairListener.MessageType type)
        {
            Dispatcher.BeginInvoke(new Action(delegate()
            {
                this.pairWiimoteText.Text = message;
                if (message == "Escaneando...")
                {
                    pairWiimotePressSync.Visibility = Visibility.Visible;

                }
                else
                {
                    pairWiimotePressSync.Visibility = Visibility.Hidden;
                }

                if (type == WiiCPP.WiiPairListener.MessageType.ERR)
                {
                    this.ShowMessage(message, MessageType.Error);
                }

            }), null);
        }
        
        private void btnAppSettings_Click(object sender, RoutedEventArgs e)
        {
            this.showConfig();
        }

        private void btnAbout_Click(object sender, RoutedEventArgs e)
        {
            this.showAbout();
        }

        private void PairWiimotesDone_Click(object sender, RoutedEventArgs e)
        {
            if (this.wiiPairRunning)
            {
                this.pairWiimoteText.Text = "Cerrando...";
                this.pairWiimotePressSync.Visibility = Visibility.Hidden;

                this.stopWiiPair();
            }
            else
            {
                //this.pairWiimoteOverlay.Visibility = Visibility.Hidden;
                //this.enableMainControls();
            }
        }

        private void spInfoMsg_MouseUp(object sender, MouseButtonEventArgs e)
        {
            //this.spInfoMsg.Visibility = Visibility.Collapsed;
            this.animateCollapse(spInfoMsg,false);
        }

        private void spErrorMsg_MouseUp(object sender, MouseButtonEventArgs e)
        {
            //this.spErrorMsg.Visibility = Visibility.Collapsed;
            this.animateCollapse(spErrorMsg,false);
        }

        private void StartArcadeHook()
        {
            arcadeHook = ArcadeHookSingleton.Default;

            arcadeHookThread = new Thread(new ThreadStart(arcadeHook.ConnectToServer));
            arcadeHookThread.IsBackground = true;
            arcadeHookThread.Start();
        }

        public void StopArcadeHook()
        {
            if (arcadeHook != null)
            {
                arcadeHook.Stop();
                arcadeHookThread.Join();
                arcadeHook = null;
                arcadeHookThread = null;
            }
        }

    }

    
}
