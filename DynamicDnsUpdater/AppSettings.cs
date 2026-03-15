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

namespace DynamicDnsUpdater
{
    static class AppSettings
    {
        public enum logSettingEnum { noLog = 0, logTemp, logHere };

        //Settings from file
        public static bool firstRun = true;//if file doesn't exist
        public static string user = "";
        public static string password = "";
        public static string hostname = "";
        public static string updateLink = "";
        public static string modifier = "";
        public static uint updateInterval = 1;
        public static logSettingEnum logSetting = logSettingEnum.noLog;
        public static bool checkAlsoLocalIpChange = true;
        public static bool debugMode = false;

        //Settings from cmdLine
        public static bool overrideLogOption = false; //if true, we have log options from cmdline, so we ignore the one from file
        public static logSettingEnum originalLogSetting = logSettingEnum.noLog;//we need to keep a copy of the original setting in case someone save new settings with override log enabled

        //settings from regedit / SchTasks
        public static bool autorunUserEnabled = false;
        public static bool autorunAdminEnabled = false;

        //Settings internal
        public static bool exitUpdateLoop = false;
        public static utilityFunctions.UpdateStatus lastUpdateStatus = utilityFunctions.UpdateStatus.NotConnected;
        public static bool lastUpdateStatusChanged = false;
        public static string logFileName = "ddu.log";
        public static string settingsFileName = "dduSettings.txt";
        public static string lastWebRequestInfo = ""; //stores datetime + url + result for UI display
        public static bool webRequestInfoChanged = false;
        public static string currentWanIp = ""; //stores current WAN IP for UI display
        public static bool wanIpChanged = false;

    }
}
