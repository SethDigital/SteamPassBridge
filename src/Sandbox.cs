using System;

namespace SteamPassBridge
{
    // Set SPB_SANDBOX=<folder> to redirect everything this app changes (Steam userdata, install folder,
    // Start menu entry, uninstall registration) into that folder, and never close/start Steam.
    // Used to test install/uninstall/shortcut migration without touching a real setup.
    static class Sandbox
    {
        public static readonly string Root = Environment.GetEnvironmentVariable("SPB_SANDBOX");
        public static bool On { get { return !string.IsNullOrEmpty(Root); } }
    }
}
