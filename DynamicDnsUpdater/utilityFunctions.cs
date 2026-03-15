/*
 * Based on Dynamic DNS Updater from Nesos
 * https://github.com/Nesos-ita/DynamicDnsUpdater
 * 
 * Modifications:
 * - Added modifier field to support additional URL parameters (e.g., &myipv4=preserve)
 * - Extended logging for network interfaces and DNS update requests
 * - Added debug mode that logs requests without executing them
 * - Enhanced network interface change detection logging
 */

using System;
using System.Text;
using System.Windows.Forms;

using TaskScheduler;
using System.IO;
using System.Net;
using Microsoft.Win32;
using System.Security.Principal;
using System.Net.NetworkInformation;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;

namespace DynamicDnsUpdater
{
    class utilityFunctions
    {
        static StreamWriter streamLog = null;
        static StreamWriter streamLogLocal = null;
        struct timerWait
        {
            public DateTime myTime;
            public DateTime oldTime;
        };
        const string taskName = "DynamicDnsUpdater"; //both regedit and schtasks
        //const string updateLinkIPv6= "https://update6.dedyn.io"; //NOT USED NOW
        const string checkIpLink = "https://checkip.dedyn.io";
        List<string> oldNetCardIdAndIp = null; //used by IpIdChangedSinceLastCall
        IPAddress oldWanIp = null; //used by WanIpChangedSinceLastCall
        int ipBasedUpdaterRateLimiter = 3;//change also max "lives" in the wait timer

        /// <summary>
        /// Check if user is administrator
        /// </summary>
        /// <returns></returns>
        public bool IsUserAdmin()
        {
            bool isAdmin;
            try
            {
                WindowsIdentity user = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(user);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                isAdmin = false;
            }
            return isAdmin;
        }

        /// <summary>
        /// Returns program path (and exe name if needed), path ends with '\' but doesn't contain ""
        /// </summary>
        /// <returns></returns>
        public string GetExePath(bool includeProgramName = false)
        {
            string exePath = Application.ExecutablePath;//damn .net framework that doesnt sanitize the path!
            exePath = exePath.Replace('/', '\\');
            if (includeProgramName == false)
                exePath = exePath.Substring(0, exePath.LastIndexOf('\\') + 1);
            return exePath;
        }

        /// <summary>
        /// Returns program path (and exe name if needed), path ends with '\' and include "" when needed (if space is present)
        /// </summary>
        /// <param name="includeProgramName"></param>
        /// <returns></returns>
        public string GetSanitizedExePath(bool includeProgramName = false)
        {
            string exePath = GetExePath(includeProgramName);
            if (exePath.Contains(" ") == true)
            {
                exePath = exePath.Insert(0, "\"");
                exePath = exePath.Insert(exePath.Length, "\""); //sanitize path; otherwise we might read/write wrong file
            }
            return exePath;
        }

        /// <summary>
        /// Opens/create the log file
        /// </summary>
        void InitLog()
        {
            try
            {
                string tmppath = Path.GetTempPath();
                streamLog = new StreamWriter(tmppath + AppSettings.logFileName, true);
                streamLog.AutoFlush = true;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Opens/create the log file
        /// </summary>
        void InitLogLocal()
        {
            try
            {
                streamLogLocal = new StreamWriter(GetExePath() + AppSettings.logFileName, true);
                streamLogLocal.AutoFlush = true;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Adds a line to the log file (if opened)
        /// </summary>
        /// <param name="str">message to be logged</param>
        public void AddLog(string str)
        {
            if (AppSettings.logSetting == AppSettings.logSettingEnum.logHere)
            {
                if (streamLogLocal == null)
                    InitLogLocal();
                if (streamLogLocal != null)
                {
                    try
                    {
                        DateTime t = DateTime.Now;
                        streamLogLocal.WriteLine(t.ToString() + " - " + str);
                    }
                    catch (Exception) { }
                }
            }
            if (AppSettings.logSetting == AppSettings.logSettingEnum.logTemp)
            {
                if (streamLog == null)
                    InitLog();
                if (streamLog != null)
                {
                    try
                    {
                        DateTime t = DateTime.Now;
                        //string ora = t.Day.ToString().PadLeft(2, '0') + "-" + t.Month.ToString().PadLeft(2, '0') + "-" + t.Year.ToString().PadLeft(4, '0') + "," + t.Hour.ToString().PadLeft(2, '0') + "-" + t.Minute.ToString().PadLeft(2, '0') + "-" + t.Second.ToString().PadLeft(2, '0') + " - ";
                        streamLog.WriteLine(t.ToString() + " - " + str);
                    }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// PC Clock based wait
        /// </summary>
        /// <param name="t">structure that hold current and old time</param>
        /// <param name="seconds">seconds to wait</param>
        /// <returns></returns>
        bool Wait(ref timerWait t, uint seconds)
        {
            if (t.oldTime.ToBinary() == 0)
                t.oldTime = DateTime.Now; //init
            t.myTime = DateTime.Now;
            TimeSpan diff = t.myTime.Subtract(t.oldTime);
            if ((uint)diff.TotalSeconds < seconds)
                return false; //not yet
            t.oldTime = t.myTime; //reset
            return true; //done
        }

        /// <summary>
        /// Return IP address (2 sec timeout)
        /// </summary>
        /// <returns></returns>
        public IPAddress GetIP()
        {
            HttpWebRequest webRequest = (HttpWebRequest)HttpWebRequest.Create(new Uri(checkIpLink));
            webRequest.Timeout = 2000;
            webRequest.Method = "GET";
            webRequest.ContentType = "text/plain";
            webRequest.ContentLength = 0;
            try
            {
                HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse();
                if (webResponse.StatusCode != HttpStatusCode.OK)
                    return null;
                StreamReader reader = new StreamReader(webResponse.GetResponseStream());
                string result = reader.ReadToEnd();
                reader.Close();
                if (result.Length > 15)
                    return null;
                IPAddress ip;
                if (IPAddress.TryParse(result, out ip) == false)
                    return null;
                return ip;
            }
            catch (Exception) { }
            return null;
        }

        public enum UpdateStatus { OK, NotConnected, Firewalled, UserNotFound, Unauthorized, UpdateFailed, InvalidUpdateLink, UnknownError };
        /// <summary>
        /// Updates the DDNS
        /// </summary>
        /// <param name="user"></param>
        /// <param name="password"></param>
        /// <param name="hostname"></param>
        /// <param name="updateLink"></param>
        /// <param name="modifier"></param>
        /// <returns></returns>
        public UpdateStatus UpdateDns(string user, string password, string hostname, string updateLink, string modifier = "")
        {
            string requestDateTime = DateTime.Now.ToString();
            try
            {
                HttpWebRequest webRequest = null;
                try
                {
                    string modifierParam = "";
                    if (!string.IsNullOrEmpty(modifier))
                    {
                        if (!modifier.StartsWith("&"))
                            modifierParam = "&" + modifier;
                        else
                            modifierParam = modifier;
                    }
                    string requestUrl = "https://" + updateLink + "?hostname=" + hostname + modifierParam;
                    AddLog("Requesting URL: " + requestUrl);

                    if (AppSettings.debugMode)
                    {
                        AddLog("DEBUG MODE: Web request NOT executed (simulated success)");
                        AppSettings.lastWebRequestInfo = requestDateTime + "\r\n" + requestUrl + " -> DEBUG MODE (simulated OK)";
                        AppSettings.webRequestInfoChanged = true;
                        return UpdateStatus.OK;
                    }

                    webRequest = (HttpWebRequest)WebRequest.Create(new Uri(requestUrl));
                }
                catch (UriFormatException ex)
                {
                    AddLog("Update link error: " + ex.Message);
                    AppSettings.lastWebRequestInfo = requestDateTime + "\r\n" + "Invalid URL format -> " + ex.Message;
                    AppSettings.webRequestInfoChanged = true;
                    return UpdateStatus.InvalidUpdateLink;
                }
                webRequest.Timeout = 10000; //timeout for the operation, wait no more than 10 seconds for an answer
                webRequest.Method = "GET";
                webRequest.ContentType = "text/plain";
                webRequest.ContentLength = 0;
                webRequest.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password))); //HTTP Basic Authentication
                HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse();
                if (webResponse.StatusCode != HttpStatusCode.OK)
                {
                    AddLog("Error, WebResponse returned: " + webResponse.StatusCode.ToString()); //probably never ends here since it throw exceptions
                    string requestUrl = webRequest.RequestUri.ToString();
                    AppSettings.lastWebRequestInfo = requestDateTime + "\r\n" + requestUrl + " -> ERROR: " + webResponse.StatusCode.ToString();
                    AppSettings.webRequestInfoChanged = true;
                    return UpdateStatus.UnknownError;
                }
                StreamReader reader = new StreamReader(webResponse.GetResponseStream());
                long slen = webResponse.ContentLength; //long bad answers could "fill the ram" (probably no need to fix)
                string result = reader.ReadToEnd();
                reader.Close();
                if (result == null || result == "")
                {
                    AddLog("Error, server null or empty response");
                    string requestUrl = webRequest.RequestUri.ToString();
                    AppSettings.lastWebRequestInfo = requestDateTime + "\r\n" + requestUrl + " -> ERROR: null or empty response";
                    AppSettings.webRequestInfoChanged = true;
                    return UpdateStatus.UpdateFailed;
                }
                if (result.StartsWith("good", StringComparison.OrdinalIgnoreCase) == true || (result.StartsWith("nochg", StringComparison.OrdinalIgnoreCase) == true)) //compare + ignore case (start with? good || nochg)
                {
                    string requestUrl = webRequest.RequestUri.ToString();
                    AppSettings.lastWebRequestInfo = requestDateTime + "\r\n" + requestUrl + " -> " + result;
                    AppSettings.webRequestInfoChanged = true;
                    return UpdateStatus.OK;
                }
                else
                {
                    int len = result.Length;
                    if (len > 255)
                        len = 255; //avoid abusing error log, short messages can't fill the hdd
                    AddLog("Error, server says: " + result.Substring(0, len));
                    string requestUrl = webRequest.RequestUri.ToString();
                    AppSettings.lastWebRequestInfo = requestDateTime + "\r\n" + requestUrl + " -> ERROR: " + result.Substring(0, len);
                    AppSettings.webRequestInfoChanged = true;
                    return UpdateStatus.UpdateFailed;
                }
            }
            catch (WebException ex)
            {
                switch (ex.Status)
                {
                    case WebExceptionStatus.ConnectFailure:
                        AddLog("Error, ConnectFailure: " + ex.Message);
                        return UpdateStatus.Firewalled;
                    //break; //break is not needed since we immediatly return but we keep it here in case new lines will be added
                    case WebExceptionStatus.NameResolutionFailure:
                        AddLog("Error, NameResolutionFailure: " + ex.Message);
                        return UpdateStatus.NotConnected;
                    //break;
                    case WebExceptionStatus.ProtocolError:
                        HttpWebResponse r = (HttpWebResponse)ex.Response;
                        if (r == null)
                        {
                            AddLog("Protocol error, response is null");
                            return UpdateStatus.UnknownError;
                        }
                        switch (r.StatusCode)
                        {
                            case HttpStatusCode.Unauthorized:
                                return UpdateStatus.Unauthorized;
                            //break;
                            case HttpStatusCode.NotFound:
                                return UpdateStatus.UserNotFound;
                            //break;
                            default: //unhandled exception
                                AddLog("Error, WebResponse returned: " + r.StatusCode.ToString());
                                AddLog("UPDATE Exception: " + ex.Message);
                                if (ex.InnerException != null)
                                    AddLog("UPDATE Exception inner: " + ex.InnerException.Message);
                                return UpdateStatus.UnknownError;
                        }
                    //break; //end of protocol error case
                    case WebExceptionStatus.Timeout:
                        AddLog("Error, ConnectFailure, timeout: " + ex.Message);
                        return UpdateStatus.Firewalled;
                    //break;
                    default: //unhandled exception
                        AddLog("UPDATE Exception, unknown status: " + ex.Message);
                        if (ex.InnerException != null)
                            AddLog("UPDATE Exception inner, unknown status: " + ex.InnerException.Message);
                        return UpdateStatus.UnknownError;
                }
            }
            catch (Exception ex)
            {
                AddLog("UPDATE Exception: " + ex.Message);
                if (ex.InnerException != null)
                    AddLog("UPDATE Exception inner: " + ex.InnerException.Message);
            }
            return UpdateStatus.UnknownError;
        }

        /// <summary>
        /// Adds an autorun entry in Regedit or Tasks Scheduler
        /// </summary>
        /// <param name="userMode"></param>
        /// <param name="program"></param>
        /// <param name="arguments"></param>
        public bool AddTask(bool userMode, string program, string arguments = null)
        {
            if (userMode == true)
            {
                try
                {
                    RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run\", true);
                    if (arguments == null)
                        k.SetValue(taskName, program, RegistryValueKind.String);
                    else
                        k.SetValue(taskName, program + " " + arguments, RegistryValueKind.String);
                    k.Close();
                    return true;
                }
                catch (Exception ex)
                {
                    AddLog("Add user task error: " + ex.Message);
                }
            }
            else
            {
                try
                {
                    //SCHTASKS.exe /Create /SC DAILY /TN report /TR notepad.exe
                    TaskScheduler.TaskScheduler scheduler = new TaskScheduler.TaskScheduler();
                    scheduler.Connect(null, null, null, null);

                    ITaskDefinition task = scheduler.NewTask(0);
                    task.RegistrationInfo.Author = "Dynamic Dns Updater";
                    task.RegistrationInfo.Description = "Keeps the Dynamic DNS updated";
                    task.Settings.AllowDemandStart = true;
                    task.Settings.Hidden = false;
                    task.Settings.RestartInterval = "PT1M"; //on error retry every minute 10 times
                    task.Settings.RestartCount = 10;
                    task.Settings.StartWhenAvailable = true;
                    task.Settings.DisallowStartIfOnBatteries = false; // start the task also if pc is on battery.
                    task.Settings.StopIfGoingOnBatteries = false;
                    task.Settings.ExecutionTimeLimit = "PT0S"; // infinite.
                    task.Settings.WakeToRun = false;
                    task.Principal.RunLevel = _TASK_RUNLEVEL.TASK_RUNLEVEL_HIGHEST;
                    task.Triggers.Create(_TASK_TRIGGER_TYPE2.TASK_TRIGGER_BOOT);

                    IExecAction action = (IExecAction)task.Actions.Create(_TASK_ACTION_TYPE.TASK_ACTION_EXEC); // the type of action to run .exe, there are others to send mail and show msg, both already depracated by Microsoft.
                    action.Path = program;
                    if (arguments != null)
                        action.Arguments = arguments;
                    //action.WorkingDirectory = "C:\\windows\\system32";

                    ITaskFolder root = scheduler.GetFolder("\\");
                    IRegisteredTask regTask = root.RegisterTaskDefinition(taskName, task, (int)_TASK_CREATION.TASK_CREATE_OR_UPDATE, "NT AUTHORITY\\SYSTEM", null, _TASK_LOGON_TYPE.TASK_LOGON_NONE, "");
                    //IRunningTask runTask = regTask.Run(null); // this will run just created task (run on demand).
                    return true;
                }
                catch (Exception ex)
                {
                    AddLog("Add admin task error: " + ex.Message);
                }
            }
            return false;
        }

        /// <summary>
        /// Deletes the task
        /// </summary>
        /// <param name="userMode"></param>
        /// <returns></returns>
        public bool DeleteTask(bool userMode)
        {
            if (userMode == true)
            {
                try
                {
                    RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run\", true);
                    k.DeleteValue(taskName);
                    k.Close();
                    return true;
                }
                catch (Exception ex)
                {
                    AddLog("Delete user task error: " + ex.Message);
                }
            }
            else
            {
                try
                {
                    TaskScheduler.TaskScheduler scheduler = new TaskScheduler.TaskScheduler();
                    scheduler.Connect(null, null, null, null);
                    ITaskFolder root = scheduler.GetFolder("\\");
                    root.DeleteTask(taskName, 0);
                    return true;
                }
                catch (Exception ex)
                {
                    AddLog("Delete admin task error: " + ex.Message);
                }
            }
            return false;
        }

        /// <summary>
        /// Check if a task exists
        /// </summary>
        /// <param name="userMode"></param>
        /// <returns></returns>
        public bool TaskExist(bool userMode)
        {
            try
            {
                if (userMode == true)
                {
                    RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run\", false);
                    string val = (string)k.GetValue(taskName, null, RegistryValueOptions.None);
                    k.Close();
                    if (val != null)
                        return true;
                }
                else
                {
                    TaskScheduler.TaskScheduler scheduler = new TaskScheduler.TaskScheduler();
                    scheduler.Connect(null, null, null, null);
                    ITaskFolder root = scheduler.GetFolder("\\");
                    IRegisteredTask task = root.GetTask(taskName);
                    if (task != null)
                        return true;
                }
            }
            catch (Exception)
            {

            }
            return false;
        }

        /// <summary>
        /// returns interfaces that are up and has type: ethernet or wifi
        /// </summary>
        /// <returns></returns>
        public List<NetworkInterface> ListActiveInterfaces()
        {
            List<NetworkInterface> validInterfaces = new List<NetworkInterface>();
            try
            {
                NetworkInterface[] interfcaces = NetworkInterface.GetAllNetworkInterfaces();
                AddLog("Total network interfaces found: " + interfcaces.Length);
                for (int i = 0; i < interfcaces.Length; i++)
                {
                    //less restrictive filter might be != NetworkInterfaceType.Loopback, now is strict compared to GetIsNetworkAvailable()
                    if (interfcaces[i].OperationalStatus == OperationalStatus.Up && (interfcaces[i].NetworkInterfaceType == NetworkInterfaceType.Ethernet || interfcaces[i].NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                    {
                        validInterfaces.Add(interfcaces[i]);
                        AddLog("Active interface: " + interfcaces[i].Name + " (" + interfcaces[i].NetworkInterfaceType.ToString() + ") - ID: " + interfcaces[i].Id);
                    }
                }
                AddLog("Valid active interfaces: " + validInterfaces.Count);
            }
            catch (Exception ex)
            {
                AddLog("Error listing network interfaces: " + ex.Message);
                return new List<NetworkInterface>();
            }
            return validInterfaces;
        }

        /// <summary>
        /// returns ipv4 of the wan interface (ipv6 does not work, you cannot portmap ipv6 on proximus)
        /// using api of ipify.org https://www.ipify.org/
        /// </summary>
        /// <returns></returns>
        public async Task<IPAddress> QueryWanIPAsync()
        {
            IPAddress wanIp = null;
            try
            {
                var httpClient = new HttpClient();
                var ip = await httpClient.GetStringAsync("https://api.ipify.org");
                if (ip == null)
                    AddLog("Wan interface has no IPv4 address");
                else
                {
                    wanIp = IPAddress.Parse(ip);
                    AddLog("Wan IP: " + wanIp.ToString());
                }
            }
            catch (Exception ex)
            {
                AddLog("Error querying https://www.ipify.org for Wan IP" + ": " + ex.Message);
                return null;
            }
            return wanIp;
        }

        /// <summary>
        /// returns ipv4 of the interface (the first internet ipv4)
        /// </summary>
        /// <param name="netInterface"></param>
        /// <returns></returns>
        public IPAddress QueryInterfaceIP(NetworkInterface netInterface)
        {
            IPAddress ip = null;
            try
            {
                IPInterfaceProperties ipInfo = netInterface.GetIPProperties();
                for (int i = 0; i < ipInfo.UnicastAddresses.Count; i++)
                {
                    if (ipInfo.UnicastAddresses[i].Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        ip = ipInfo.UnicastAddresses[i].Address;
                        AddLog("Interface " + netInterface.Name + " has IPv4: " + ip.ToString());
                        break;
                    }
                }
                if (ip == null)
                    AddLog("Interface " + netInterface.Name + " has no IPv4 address");
            }
            catch (Exception ex)
            {
                AddLog("Error querying interface IP for " + netInterface.Name + ": " + ex.Message);
                return null;
            }
            return ip;
        }

        /// <summary>
        /// returns a unique identifier string composed by cardid+ip
        /// </summary>
        /// <returns></returns>
        public List<string> GetInterfaceIdIpList()
        {
            List<NetworkInterface> li = ListActiveInterfaces();
            if (li.Count == 0)
                return new List<string>();
            List<string> idip = new List<string>();
            for (int i = 0; i < li.Count; i++)
            {
                IPAddress ip = QueryInterfaceIP(li[i]);
                string ipStr = "";
                if (ip != null)
                    ipStr = ip.ToString();
                idip.Add(li[i].Id.ToString() + ipStr);
            }
            return idip;
        }

        /// <summary>
        /// returns true if the network status (id+ip) is not equal to the old one and there is a connection, false if status is not changed, there are 0 connections, first call
        /// </summary>
        /// <returns></returns>
        public bool IpIdChangedSinceLastCall()
        {
            bool ipOrNetworkCardChanged = false;
            List<string> currentNetCardIdAndIp = GetInterfaceIdIpList();
            if (oldNetCardIdAndIp == null)
            {
                oldNetCardIdAndIp = currentNetCardIdAndIp;
                AddLog("First network status check - Current interfaces: " + currentNetCardIdAndIp.Count);
                return false; //first start
            }
            if (oldNetCardIdAndIp.Count == currentNetCardIdAndIp.Count)
            {
                for (int i = 0; i < oldNetCardIdAndIp.Count; i++)
                {
                    if (oldNetCardIdAndIp.Contains(currentNetCardIdAndIp[i]) == false)//check if ip or network card is changed
                    {
                        ipOrNetworkCardChanged = true;
                        AddLog("Network change detected - Interface/IP changed: " + currentNetCardIdAndIp[i]);
                    }
                }
            }
            else
            {
                ipOrNetworkCardChanged = true; //number of active network cards changed, update ddns
                AddLog("Network change detected - Interface count changed from " + oldNetCardIdAndIp.Count + " to " + currentNetCardIdAndIp.Count);
            }
            if (currentNetCardIdAndIp.Count == 0)
            {
                ipOrNetworkCardChanged = false;//connected to disconnected is a change, but a useless one
                AddLog("No active network interfaces detected");
            }
            oldNetCardIdAndIp = currentNetCardIdAndIp;
            return ipOrNetworkCardChanged;
        }

        /// <summary>
        /// returns true if the WAN IP is not equal to the old one, false if IP is not changed or first call
        /// </summary>
        /// <returns></returns>
        public bool WanIpChangedSinceLastCall()
        {
            bool wanIpChanged = false;
            IPAddress currentWanIp = null;

            try
            {
                // Call async method synchronously (not ideal but works for .NET Framework 4.8)
                var task = QueryWanIPAsync();
                task.Wait();
                currentWanIp = task.Result;
            }
            catch (Exception ex)
            {
                AddLog("Error getting WAN IP: " + ex.Message);
                return false;
            }

            if (oldWanIp == null)
            {
                oldWanIp = currentWanIp;
                if (currentWanIp != null)
                {
                    AddLog("First WAN IP check - Current WAN IP: " + currentWanIp.ToString());
                    AppSettings.currentWanIp = currentWanIp.ToString();
                    AppSettings.wanIpChanged = true;
                }
                return false; //first start
            }

            if (currentWanIp == null)
            {
                AddLog("Unable to retrieve WAN IP");
                AppSettings.currentWanIp = "Unable to retrieve";
                AppSettings.wanIpChanged = true;
                return false;
            }

            if (!oldWanIp.Equals(currentWanIp))
            {
                wanIpChanged = true;
                AddLog("WAN IP change detected - Old: " + oldWanIp.ToString() + " -> New: " + currentWanIp.ToString());
                oldWanIp = currentWanIp;
                AppSettings.currentWanIp = currentWanIp.ToString();
                AppSettings.wanIpChanged = true;
            }

            return wanIpChanged;
        }

        /// <summary>
        /// Wait the desidered time and update DNS
        /// </summary>
        public void WaitAndUpdate()
        {
            UpdateStatus ret;
            timerWait tmrTimedUpdate = new timerWait();
            timerWait tmrIpChangeDetect = new timerWait();
            timerWait tmrIpChangeUpdate = new timerWait();
            timerWait tmrWanIpChangeDetect = new timerWait();
            timerWait tmrWanIpChangeUpdate = new timerWait();
            bool ipChanged = false;
            bool wanIpChanged = false;
            timerWait tmrRateLimiter = new timerWait();

            ret = UpdateDns(AppSettings.user, AppSettings.password, AppSettings.hostname, AppSettings.updateLink, AppSettings.modifier);
            AppSettings.lastUpdateStatus = ret;
            AppSettings.lastUpdateStatusChanged = true;
            AddLog("Updating DNS: " + ret.ToString());

            while (AppSettings.exitUpdateLoop == false)
            {
                if (Wait(ref tmrTimedUpdate, AppSettings.updateInterval * 60) == true)//default updateInterval is 60 min
                {
                    ret = UpdateDns(AppSettings.user, AppSettings.password, AppSettings.hostname, AppSettings.updateLink, AppSettings.modifier);
                    AppSettings.lastUpdateStatus = ret;
                    AppSettings.lastUpdateStatusChanged = true;
                    AddLog("Updating DNS: " + ret.ToString());
                    oldNetCardIdAndIp = null;//prevent next ip change check from trigger: if you boot from standby after long time and every timer is expired, probably you are also reconnecting, no need to update twice
                }
                if (AppSettings.checkAlsoLocalIpChange == true)
                {
                    if (Wait(ref tmrIpChangeDetect, 30) == true)//every 30 second check for local ip change
                    {
                        AddLog("Checking for network interface changes...");
                        ipChanged = IpIdChangedSinceLastCall();
                        if (ipChanged)
                        {
                            AddLog("*** NETWORK INTERFACE CHANGED - Update scheduled in 15 seconds ***");
                        }
                        tmrIpChangeUpdate.oldTime = DateTime.Now;//reset timer
                    }
                    if (Wait(ref tmrIpChangeUpdate, 15) == true && ipChanged == true && ipBasedUpdaterRateLimiter > 0)//after 15 second do the actual update
                    {
                        //if status changed and i have lives, update ddns. 30sec delay to ensure you are connected after network change
                        AddLog("Network interface change confirmed, triggering DNS update (rate limiter: " + ipBasedUpdaterRateLimiter + "/3)");
                        ipChanged = false;//disable timer
                        ipBasedUpdaterRateLimiter--;//use a life
                        ret = UpdateDns(AppSettings.user, AppSettings.password, AppSettings.hostname, AppSettings.updateLink, AppSettings.modifier);
                        AppSettings.lastUpdateStatus = ret;
                        AppSettings.lastUpdateStatusChanged = true;
                        tmrTimedUpdate.oldTime = DateTime.Now;//reset timer, we updated just now because of ip change, so reset timed update
                        if (ipBasedUpdaterRateLimiter > 0)
                            AddLog("Ip changed, updating DNS: " + ret.ToString());
                        else
                            AddLog("Ip changed (rate limited), updating DNS: " + ret.ToString());
                    }

                    // Check for WAN IP changes every 60 seconds
                    if (Wait(ref tmrWanIpChangeDetect, 60) == true)
                    {
                        AddLog("Checking for WAN IP changes...");
                        wanIpChanged = WanIpChangedSinceLastCall();
                        if (wanIpChanged)
                        {
                            AddLog("*** WAN IP CHANGED - Update scheduled in 15 seconds ***");
                        }
                        tmrWanIpChangeUpdate.oldTime = DateTime.Now;//reset timer
                    }
                    if (Wait(ref tmrWanIpChangeUpdate, 15) == true && wanIpChanged == true && ipBasedUpdaterRateLimiter > 0)//after 15 second do the actual update
                    {
                        //if WAN IP changed and i have lives, update ddns. 15sec delay to ensure connection is stable
                        AddLog("WAN IP change confirmed, triggering DNS update (rate limiter: " + ipBasedUpdaterRateLimiter + "/3)");
                        wanIpChanged = false;//disable timer
                        ipBasedUpdaterRateLimiter--;//use a life
                        ret = UpdateDns(AppSettings.user, AppSettings.password, AppSettings.hostname, AppSettings.updateLink, AppSettings.modifier);
                        AppSettings.lastUpdateStatus = ret;
                        AppSettings.lastUpdateStatusChanged = true;
                        tmrTimedUpdate.oldTime = DateTime.Now;//reset timer, we updated just now because of WAN IP change, so reset timed update
                        if (ipBasedUpdaterRateLimiter > 0)
                            AddLog("WAN IP changed, updating DNS: " + ret.ToString());
                        else
                            AddLog("WAN IP changed (rate limited), updating DNS: " + ret.ToString());
                    }

                    if (Wait(ref tmrRateLimiter, 10 * 60) == true)//every 10 minutes add a "life" to rate limiter counter
                    {
                        if (ipBasedUpdaterRateLimiter < 3)
                            ipBasedUpdaterRateLimiter++;//this means that you have 3 fast(30 sec) updates and then you will be rate limited to 1 every 5 minutes, in case of slower update you eventually regain all 3 fast updates
                    }
                }
                System.Threading.Thread.Sleep(1000);//1 sec
            }
        }

        /// <summary>
        /// Reads settings, returns false on any error
        /// </summary>
        /// <returns></returns>
        public bool ReadSettings()
        {
            string path = GetExePath();
            path += AppSettings.settingsFileName;
            if (File.Exists(path) == true)
            {
                try
                {
                    FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read);
                    StreamReader reader = new StreamReader(f, Encoding.UTF8);
                    string user = reader.ReadLine();
                    string password = reader.ReadLine();
                    string hostname = reader.ReadLine();
                    string updateLink = reader.ReadLine();
                    string modifier = "";
                    if (reader.EndOfStream == false)//compatibility with older version (where there wasn't modifier)
                        modifier = reader.ReadLine();
                    uint updateInterval = Convert.ToUInt32(reader.ReadLine());
                    if (updateInterval < 1)
                        updateInterval = 1;
                    if (updateInterval > 1440)
                        updateInterval = 1440; //fix min & max

                    string logOption = "nolog";
                    if (reader.EndOfStream == false)//compatibility with older version (where there wasn't log setting)
                        logOption = reader.ReadLine().ToLower();
                    bool checkIpUpdate = false;
                    if (reader.EndOfStream == false)//compatibility with older version (where there wasn't ip setting)
                        checkIpUpdate = Convert.ToBoolean(reader.ReadLine());
                    bool debugMode = false;
                    if (reader.EndOfStream == false)//compatibility with older version (where there wasn't debug mode)
                        debugMode = Convert.ToBoolean(reader.ReadLine());
                    if (reader.EndOfStream == false)
                        throw new Exception("Incorrect file format, too long");
                    reader.Close(); //if we reach this point settings has been sucesfully read

                    AppSettings.logSettingEnum tempLogStatus = AppSettings.logSettingEnum.noLog;
                    switch (logOption)
                    {
                        case "nolog":
                            //nolog is default
                            break;
                        case "loghere":
                            tempLogStatus = AppSettings.logSettingEnum.logHere;
                            break;
                        case "logtemp":
                        case "log":
                            tempLogStatus = AppSettings.logSettingEnum.logTemp;
                            break;
                        default:
                            throw new Exception("Incorrect file format, unknown log option");
                    }
                    //---change setting only now that we know they are valid---
                    AppSettings.user = user; //apply settings
                    AppSettings.password = password;
                    AppSettings.hostname = hostname;
                    AppSettings.updateLink = updateLink;
                    AppSettings.modifier = modifier;
                    AppSettings.updateInterval = updateInterval;
                    AppSettings.originalLogSetting = tempLogStatus;
                    if (AppSettings.overrideLogOption == false)//log option from cmdline, don't use file settings
                        AppSettings.logSetting = tempLogStatus;
                    AppSettings.checkAlsoLocalIpChange = checkIpUpdate;
                    AppSettings.debugMode = debugMode;
                    AppSettings.firstRun = false;
                    AddLog("Settings read OK");
                    return true;
                }
                catch (Exception ex)
                {
                    AddLog("Error reading settings: " + ex.Message);
                }
            }
            return false;
        }

        /// <summary>
        /// Save settings
        /// </summary>
        public bool SaveSettings()
        {
            string path = GetExePath();
            path += AppSettings.settingsFileName;
            try
            {
                FileStream f = new FileStream(path, FileMode.Create, FileAccess.Write);
                StreamWriter writer = new StreamWriter(f, Encoding.UTF8);
                writer.WriteLine(AppSettings.user);
                writer.WriteLine(AppSettings.password);
                writer.WriteLine(AppSettings.hostname);
                writer.WriteLine(AppSettings.updateLink);
                writer.WriteLine(AppSettings.modifier);
                writer.WriteLine(AppSettings.updateInterval.ToString());
                if (AppSettings.overrideLogOption == false)
                    writer.WriteLine(AppSettings.logSetting.ToString());//devo scrivere l'originale
                else
                    writer.WriteLine(AppSettings.originalLogSetting.ToString());
                writer.WriteLine(AppSettings.checkAlsoLocalIpChange);
                writer.WriteLine(AppSettings.debugMode);
                writer.Close();
                AddLog("Settings saved OK");
                return true;
            }
            catch (Exception ex)
            {
                AddLog("Error saving settings: " + ex.Message);
            }
            return false;
        }
    }
}
