using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace XivVault.Core.Platform;

/// <summary>Windows notifications, for scheduled runs that have no window of their own.</summary>
public interface INotifier
{
    void Notify(string title, string message);
}

/// <summary>
/// Shows a Windows notification by calling WinRT directly, so Core stays plain .NET and can build
/// for Linux later. Each interface is used only up to the methods below, in the order Windows'
/// metadata gives them; a published WinRT interface never changes.
/// </summary>
public sealed partial class WindowsNotifier(IAppEnvironment environment, ILogger<WindowsNotifier> logger) : INotifier
{
    // XIV Vault's own app ID, registered per user, so Setup, portable copies and the CLI all
    // notify under the same name and icon without needing a Start menu shortcut.
    private const string AppId = "hazeliscoding.XIVVault";

    private const int RoInitMultithreaded = 1;
    private const int ChangedMode = unchecked((int)0x80010106);

    private static readonly Guid IToastNotificationManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    private static readonly Guid IToastNotificationFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid IXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid IXmlDocumentIO = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");

    public void Notify(string title, string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Register();
            Show(new XElement("toast",
                new XElement("visual",
                    new XElement("binding", new XAttribute("template", "ToastGeneric"),
                        new XElement("text", title),
                        new XElement("text", message)))).ToString(SaveOptions.DisableFormatting));
        }
        catch (Exception ex)
        {
            // A notification is extra: the run's result is already recorded, so it must not fail the run.
            logger.LogWarning(ex, "Could not show a Windows notification");
        }
    }

    [SupportedOSPlatform("windows")]
    private void Register()
    {
        // Windows reads the icon from a file, and a single-file exe has none to point at.
        var icon = Path.Combine(environment.DataDirectory, "notification-icon.png");
        Directory.CreateDirectory(environment.DataDirectory);
        using (var source = typeof(WindowsNotifier).Assembly.GetManifestResourceStream("app-icon.png")!)
        using (var target = File.Create(icon))
        {
            source.CopyTo(target);
        }

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        key.SetValue("DisplayName", "XIV Vault");
        key.SetValue("IconUri", icon);
    }

    private static unsafe void Show(string xml)
    {
        // RPC_E_CHANGED_MODE means the thread is already single-threaded (the desktop app's), which works too.
        var init = RoInitialize(RoInitMultithreaded);
        if (init < 0 && init != ChangedMode)
        {
            throw new COMException("Could not start WinRT", init);
        }

        nint manager = 0, notifier = 0, document = 0, xmlDocument = 0, io = 0, factory = 0, toast = 0;
        try
        {
            using var appId = new HString(AppId);
            using var content = new HString(xml);
            manager = Factory("Windows.UI.Notifications.ToastNotificationManager", IToastNotificationManagerStatics);
            // IToastNotificationManagerStatics.CreateToastNotifier(String)
            Check(((delegate* unmanaged<nint, nint, nint*, int>)Slot(manager, 7))(manager, appId.Handle, &notifier));

            using (var className = new HString("Windows.Data.Xml.Dom.XmlDocument"))
            {
                Check(RoActivateInstance(className.Handle, &document));
            }

            io = Query(document, IXmlDocumentIO);
            // IXmlDocumentIO.LoadXml(String)
            Check(((delegate* unmanaged<nint, nint, int>)Slot(io, 6))(io, content.Handle));

            xmlDocument = Query(document, IXmlDocument);
            factory = Factory("Windows.UI.Notifications.ToastNotification", IToastNotificationFactory);
            // IToastNotificationFactory.CreateToastNotification(XmlDocument)
            Check(((delegate* unmanaged<nint, nint, nint*, int>)Slot(factory, 6))(factory, xmlDocument, &toast));
            // IToastNotifier.Show(ToastNotification)
            Check(((delegate* unmanaged<nint, nint, int>)Slot(notifier, 6))(notifier, toast));
        }
        finally
        {
            foreach (var pointer in (ReadOnlySpan<nint>)[toast, factory, xmlDocument, io, document, notifier, manager])
            {
                if (pointer != 0)
                {
                    Marshal.Release(pointer);
                }
            }

            if (init >= 0)
            {
                RoUninitialize();
            }
        }
    }

    private static unsafe nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static unsafe nint Factory(string className, Guid iid)
    {
        using var name = new HString(className);
        nint factory;
        Check(RoGetActivationFactory(name.Handle, &iid, &factory));
        return factory;
    }

    private static nint Query(nint instance, Guid iid)
    {
        Check(Marshal.QueryInterface(instance, in iid, out var result));
        return result;
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
        {
            throw new COMException("A WinRT call failed", hresult);
        }
    }

    private readonly struct HString : IDisposable
    {
        public HString(string value)
        {
            Check(WindowsCreateString(value, value.Length, out var handle));
            Handle = handle;
        }

        public nint Handle { get; }

        public void Dispose() => WindowsDeleteString(Handle);
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    private static unsafe partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static unsafe partial int RoActivateInstance(nint activatableClassId, nint* instance);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);
}
