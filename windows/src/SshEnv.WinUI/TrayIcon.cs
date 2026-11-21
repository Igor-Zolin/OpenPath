using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace SshEnv.WinUI;

internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint NotifyAdd = 0;
    private const uint NotifyModify = 1;
    private const uint NotifyDelete = 2;
    private const uint NotifySetFocus = 3;
    private const uint NotifySetVersion = 4;
    private const uint CallbackVersion = 4;
    private const uint NotifyMessage = 0x01;
    private const uint NotifyIcon = 0x02;
    private const uint NotifyTip = 0x04;
    private const uint NotifyShowTip = 0x80;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmGetIcon = 0x007F;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint MessageFilterAllow = 1;
    private const uint MenuDisabled = 0x03;
    private const uint MenuSeparator = 0x0800;
    private const uint PopupRightButton = 0x02;
    private const uint PopupNoNotify = 0x80;
    private const uint PopupReturnCommand = 0x0100;
    private const uint OpenCommand = 1;
    private const uint ToggleCommand = 2;
    private const uint ExitCommand = 3;
    private const uint StatusCommand = 4;

    private readonly nint hwnd;
    private readonly Action openAction;
    private readonly Action toggleAction;
    private readonly Action exitAction;
    private readonly DispatcherQueue dispatcher;
    private readonly WindowSubclass subclass;
    private readonly uint callbackMessage;
    private readonly uint taskbarCreatedMessage;
    private readonly bool attached;
    private string openText = "Open OpenPath";
    private string statusText = "Disconnected";
    private string toggleText = "Connect";
    private string exitText = "Exit";
    private bool toggleEnabled = true;
    private bool disposed;

    public TrayIcon(nint hwnd, Action open, Action toggleConnection, Action exit)
    {
        this.hwnd = hwnd;
        openAction = open;
        toggleAction = toggleConnection;
        exitAction = exit;
        dispatcher = DispatcherQueue.GetForCurrentThread();
        subclass = OnWindowMessage;
        callbackMessage = RegisterWindowMessage("SshEnv.WinUI.TrayIcon");
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        attached = callbackMessage != 0 && SetWindowSubclass(hwnd, subclass, IconId, 0);
        if (attached)
        {
            // Explorer runs without elevation, even when the tunnel app is elevated.
            ChangeWindowMessageFilterEx(hwnd, callbackMessage, MessageFilterAllow, 0);
            if (taskbarCreatedMessage != 0)
            {
                ChangeWindowMessageFilterEx(hwnd, taskbarCreatedMessage, MessageFilterAllow, 0);
            }

            RegisterIcon();
        }
    }

    public bool IsAvailable { get; private set; }

    public void Update(string open, string status, string toggle, string exit, bool toggleEnabled)
    {
        openText = open;
        statusText = status;
        toggleText = toggle;
        exitText = exit;
        this.toggleEnabled = toggleEnabled;

        if (!attached || disposed)
        {
            return;
        }

        var data = CreateIconData();
        data.Flags = NotifyTip | NotifyShowTip;
        if (!IsAvailable || !ShellNotifyIcon(NotifyModify, ref data))
        {
            RegisterIcon();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (attached)
        {
            var data = CreateIconData();
            ShellNotifyIcon(NotifyDelete, ref data);
            RemoveWindowSubclass(hwnd, subclass, IconId);
        }

        IsAvailable = false;
    }

    private void RegisterIcon()
    {
        var data = CreateIconData();
        data.Flags = NotifyMessage | NotifyIcon | NotifyTip | NotifyShowTip;
        data.CallbackMessage = callbackMessage;
        data.Icon = SendMessage(hwnd, WmGetIcon, 2, 0);
        if (data.Icon == 0)
        {
            data.Icon = LoadIcon(0, 32512); // IDI_APPLICATION
        }

        IsAvailable = ShellNotifyIcon(NotifyAdd, ref data) || ShellNotifyIcon(NotifyModify, ref data);
        if (IsAvailable)
        {
            data.Version = CallbackVersion;
            IsAvailable = ShellNotifyIcon(NotifySetVersion, ref data);
            if (!IsAvailable)
            {
                ShellNotifyIcon(NotifyDelete, ref data);
            }
        }
    }

    private NotifyIconData CreateIconData()
    {
        var tooltip = $"OpenPath — {statusText}";
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = hwnd,
            Id = IconId,
            Tip = tooltip.Length < 128 ? tooltip : tooltip[..127],
            Info = string.Empty,
            InfoTitle = string.Empty
        };
    }

    private nint OnWindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmNcDestroy)
        {
            Dispose();
        }
        else if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage)
        {
            RegisterIcon();
        }
        else if (message == callbackMessage && ((uint)lParam >> 16) == IconId)
        {
            switch ((uint)lParam & 0xFFFF)
            {
                case NinSelect:
                case NinKeySelect:
                    Dispatch(openAction);
                    break;
                case WmContextMenu:
                    ShowMenu();
                    break;
            }

            return 0;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        uint command;
        try
        {
            AppendMenu(menu, 0, OpenCommand, EscapeMenuText(openText));
            AppendMenu(menu, MenuDisabled, StatusCommand, EscapeMenuText(statusText));
            AppendMenu(menu, toggleEnabled ? 0u : MenuDisabled, ToggleCommand, EscapeMenuText(toggleText));
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, 0, ExitCommand, EscapeMenuText(exitText));

            var position = GetMenuPosition();
            SetForegroundWindow(hwnd);
            command = TrackPopupMenu(menu, PopupRightButton | PopupNoNotify | PopupReturnCommand,
                position.X, position.Y, 0, hwnd, 0);
            PostMessage(hwnd, 0, 0, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }

        switch (command)
        {
            case OpenCommand:
                Dispatch(openAction);
                break;
            case ToggleCommand when toggleEnabled:
                Dispatch(toggleAction);
                break;
            case ExitCommand:
                Dispatch(exitAction);
                break;
            default:
                var data = CreateIconData();
                ShellNotifyIcon(NotifySetFocus, ref data);
                break;
        }
    }

    private Point GetMenuPosition()
    {
        var icon = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = hwnd,
            Id = IconId
        };

        if (ShellNotifyIconGetRect(ref icon, out var bounds) == 0)
        {
            return new Point { X = bounds.Left + (bounds.Right - bounds.Left) / 2, Y = bounds.Bottom };
        }

        GetCursorPos(out var position);
        return position;
    }

    private void Dispatch(Action action)
    {
        dispatcher.TryEnqueue(() =>
        {
            if (!disposed)
            {
                action();
            }
        });
    }

    private static string EscapeMenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(nint hwnd, WindowSubclass subclass, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(nint hwnd, WindowSubclass subclass, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect", ExactSpelling = true)]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rectangle bounds);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeInfo);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", ExactSpelling = true)]
    private static extern nint LoadIcon(nint module, nint icon);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint command, string? text);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rectangle);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}
