using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Skippy.Core;
using Windows.UI.Notifications;

namespace Skippy.UI;

/// <summary>
/// Real Windows toast notifications for an unpackaged app: registers the "Skippy" AppUserModelID under
/// HKCU (name + icon), no installer or Start-menu shortcut needed. Toasts never take focus, so they are
/// safe while a fullscreen game runs (Windows queues them in the notification center during games).
/// Falls back to a tray balloon if toasts are unavailable.
/// </summary>
public static class Toast
{
    const string Aumid = "Skippy.InstantReplay";
    static bool _ready;
    static NotifyIcon? _fallback;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void Init(NotifyIcon fallback)
    {
        _fallback = fallback;
        try
        {
            string png = Path.Combine(Log.Dir, "skippy.png");
            AppIcon.WritePng(png, 128);
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}"))
            {
                k.SetValue("DisplayName", "Skippy");
                k.SetValue("IconUri", png);
                k.SetValue("IconBackgroundColor", "FF6D4AFF");
            }
            SetCurrentProcessExplicitAppUserModelID(Aumid);
            _ready = true;
        }
        catch (Exception ex) { Log.Warn($"Toast registration failed, using tray balloons: {ex.Message}"); }
    }

    public static void Show(string text, bool isError)
    {
        Log.Info($"Toast: {text}");
        if (_ready)
        {
            try
            {
                string title = isError ? "Skippy – problem" : "Skippy";
                var xml = $"""
                    <toast>
                      <visual><binding template="ToastGeneric"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text></binding></visual>
                      <audio silent="true"/>
                    </toast>
                    """;
                var doc = new Windows.Data.Xml.Dom.XmlDocument();
                doc.LoadXml(xml);
                ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(doc) { ExpirationTime = DateTimeOffset.Now.AddMinutes(isError ? 60 : 2) });
                return;
            }
            catch (Exception ex) { Log.Warn($"Toast failed: {ex.Message}"); }
        }
        _fallback?.ShowBalloonTip(4000, "Skippy", text, isError ? ToolTipIcon.Error : ToolTipIcon.Info);
    }
}
