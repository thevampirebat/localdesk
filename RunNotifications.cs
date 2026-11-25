using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LocalDesk;

sealed class WindowsRunNotifications : IDisposable
{
    const uint Callback = 0x8000 + 78;
    readonly Window window;
    readonly IntPtr handle;
    readonly IntPtr icon;
    readonly Dictionary<uint, Action> activations = [];
    uint nextId;

    public WindowsRunNotifications(Window window)
    {
        this.window = window;
        if (!OperatingSystem.IsWindows()) return;
        handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "LocalDesk.ico"), 1, 0, 0, 0x10 | 0x40);
        Win32Properties.AddWndProcHookCallback(window, HandleMessage);
    }

    public void Show(string title, string detail, Action activated)
    {
        if (handle == IntPtr.Zero) return;
        if (activations.Count >= 6) Remove(activations.Keys.Min());
        var id = ++nextId;
        var data = new NotifyData
        {
            Size = (uint)Marshal.SizeOf<NotifyData>(), Handle = handle, Id = id,
            Flags = 1 | 2 | 4, Callback = Callback, Icon = icon, Tip = "LocalDesk",
            Info = "", Title = ""
        };
        if (!Shell_NotifyIcon(0, ref data)) return;
        activations.Add(id, activated);
        data.Flags = 0x10;
        data.Info = detail.Length > 255 ? detail[..255] : detail;
        data.Title = title.Length > 63 ? title[..63] : title;
        data.InfoFlags = 1;
        if (!Shell_NotifyIcon(1, ref data)) Remove(id);
    }

    IntPtr HandleMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Callback && (lParam.ToInt64() is 0x405 or 0x202))
        {
            var id = (uint)wParam.ToInt64();
            if (activations.TryGetValue(id, out var action))
            {
                Remove(id);
                Dispatcher.UIThread.Post(action);
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        Win32Properties.RemoveWndProcHookCallback(window, HandleMessage);
        foreach (var id in activations.Keys.ToArray()) Remove(id);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    void Remove(uint id)
    {
        activations.Remove(id);
        var data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Handle = handle, Id = id, Tip = "", Info = "", Title = "" };
        Shell_NotifyIcon(2, ref data);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyData
    {
        public uint Size;
        public IntPtr Handle;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}

public sealed partial class MainWindow
{
    WindowsRunNotifications? notifications;
    readonly DispatcherTimer quietRunTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    long lastRunProgress;
    bool quietAlertSent;
    Window? approvalWindow;
    (Conversation Chat, ChatMessage? Reply)? notificationDestination;

    void InitializeNotifications()
    {
        notifications = new WindowsRunNotifications(this);
        quietRunTimer.Tick += (_, _) =>
        {
            if (!requestStreaming || quietAlertSent || approvalWindow != null || IsActive ||
                Environment.TickCount64 - lastRunProgress < TimeSpan.FromMinutes(3).TotalMilliseconds) return;
            quietAlertSent = true;
            NotifyRun(current, activeMessage, "LocalDesk run is still working", "No progress reported for three minutes. Open the run to check Activity.");
        };
        quietRunTimer.Start();
    }

    void RecordRunProgress()
    {
        lastRunProgress = Environment.TickCount64;
        quietAlertSent = false;
    }

    Task<bool> ShowRunApproval(Window dialog) => ShowRunApproval<bool>(dialog);

    async Task<T> ShowRunApproval<T>(Window dialog)
    {
        approvalWindow = dialog;
        UpdateRunState();
        try
        {
            NotifyRun(current, activeMessage, "LocalDesk needs your approval", "Open LocalDesk to review the proposed action.");
            return await dialog.ShowDialog<T>(this);
        }
        finally { approvalWindow = null; UpdateRunState(); RecordRunProgress(); }
    }

    void NotifyRun(Conversation chat, ChatMessage? reply, string title, string detail)
    {
        if (!preferences.RunNotifications || closingWorkspace || (IsActive && WindowState != WindowState.Minimized)) return;
        notifications?.Show(title, detail, () => FocusRun(chat, reply));
    }

    void FocusRun(Conversation chat,ChatMessage? reply)
    {
        if(closingWorkspace)return;
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Activate();
        if(approvalWindow!=null&&current==chat&&activeMessage==reply)
        {approvalWindow.Activate();return;}
        notificationDestination=(chat,reply);
        OpenNotificationDestination();
    }

    void OpenNotificationDestination()
    {
        if (notificationDestination is not { } destination) return;
        if (closingWorkspace || ((busy || sendingRequest) && current != destination.Chat)) return;
        notificationDestination = null;
        if (!store.Chats.Contains(destination.Chat)) return;
        if(miniResult is { } receipt&&receipt.Chat==destination.Chat&&receipt.Reply==destination.Reply)miniResult=null;
        if (current != destination.Chat) { Show(destination.Chat); RefreshHistory(); }
        if (destination.Reply?.Summary != null && messageCards.TryGetValue(destination.Reply.Marker, out var card))
            Dispatcher.UIThread.Post(() => card.BringIntoView(), DispatcherPriority.Background);
        else
        {
            if (!ActivityPanel.IsVisible) ToggleActivity(true);
            Dispatcher.UIThread.Post(() => activityScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }
}
