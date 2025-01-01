#load "key"

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

[Flags]
public enum Modifiers : uint
{
  None = 0,
  Alt = 1,
  Control = 2,
  Ctrl = Control,
  Shift = 4,
  Windows = 8,
  Win = Windows,
  NoRepeat = 0x4000
}

public class HotKeyEventArgs : EventArgs
{
  public int Id { get; }
  public Modifiers Modifiers { get; }
  public Key Key { get; }

  internal HotKeyEventArgs(int id, Modifiers modifiers, Key key)
  {
    Id = id;
    Modifiers = modifiers;
    Key = key;
  }
}

public class HotKey : IDisposable
{
  [StructLayout(LayoutKind.Sequential)]
  internal struct POINT
  {
    public int x;
    public int y;
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct MSG
  {
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
  }
  
  [DllImport("user32.dll", SetLastError=true)]
  private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);
  [DllImport("user32.dll")]
  private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
  [DllImport("user32.dll", SetLastError = true)]
  private static extern uint MsgWaitForMultipleObjects(
    uint nCount, IntPtr[] pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);
  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool PeekMessage(
    out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool TranslateMessage(ref MSG lpMsg);
  [DllImport("user32.dll", SetLastError = true)]
  private static extern IntPtr DispatchMessage(ref MSG lpMsg);

  public event EventHandler<HotKeyEventArgs> KeyPressed;

  private readonly int id_;
  private readonly uint modifiers_;
  private readonly uint vk_;
  private readonly Thread messageThread_;
  private readonly CancellationTokenSource? cts_;
  private readonly CancellationToken token_;
  private readonly AutoResetEvent signal_ = new(false);
  
  private HotKey(int id, uint modifiers, uint vk, CancellationTokenSource cts)
    : this(id, modifiers, vk, cts.Token)
  {
    cts_ = cts;
  }

  private HotKey(int id, uint modifiers, uint vk, CancellationToken token)
  {
    id_ = id;
    modifiers_ = modifiers;
    vk_ = vk;
    token_ = token;
    messageThread_ = new(MessageLoop);
    messageThread_.IsBackground = true;
    messageThread_.Start(token_);
  }

  public int Id => id_;
  public WaitHandle Signal => signal_;

  public bool Enabled { get; set; } = true;

  public void Dispose()
  {
    Enabled = false;
    cts_?.Cancel();
    cts_?.Dispose();
  }

  private void MessageLoop(object? state)
  {
    if (state is null)
    {
      return;
    }

    if (!RegisterHotKey(IntPtr.Zero, id_, modifiers_, vk_))
    {
      throw new Win32Exception();
    }

    bool running = true;
    var token = (CancellationToken)state;
    try
    {
      while (running && !token.IsCancellationRequested)
      {
        uint result = MsgWaitForMultipleObjects(
          0,          // No kernel handles
          Array.Empty<IntPtr>(), // Empty array since no kernel handles
          false,      // Wait for any signal
          100,        // Timeout in milliseconds
          QS_ALLEVENTS // Wait for messages
        );

        switch (result)
        {
        case WAIT_OBJECT_0:  // A message is in the queue
          running &= ProcessMessages();
          break;
        case WAIT_TIMEOUT: // Timeout occurred
          running &= !token.IsCancellationRequested;
          break;
        default:
          Debug.WriteLine($"Unexpected wait result: {result} ({result:x})");
          running = false;
          break;
        }
      }
    }
    finally
    {
      UnregisterHotKey(IntPtr.Zero, id_);
    }
  }

  private bool ProcessMessages()
  {
    bool running = true;
    while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, 1))
    {
      if (msg.message == WM_HOTKEY)
      {
        NotifyHotKey(msg);
      }
      else if (msg.message == WM_QUIT)
      {
        running = false;
      }
      
      TranslateMessage(ref msg);
      DispatchMessage(ref msg);
    }

    return running;
  }

  private void NotifyHotKey(MSG msg)
  {
    if (!Enabled)
    {
      return;
    }

    int id = unchecked((int)msg.wParam);
    long lparam = (long)msg.lParam;
    Modifiers modifiers = (Modifiers)(unchecked((uint)((lparam >> 16) & 0xffff)));
    uint vk = unchecked((uint)(lparam & 0xffff));
    Key key = KeyFromVirtualKey(unchecked((int)vk));
    HotKeyEventArgs args = new(id, modifiers, key);
    signal_.Set();
    KeyPressed?.Invoke(this, args);
  }


  public static HotKey Register(
    int id, uint fsModifiers, uint vk, CancellationToken token = default)
  {
    HotKey? hotKey = null;
    if (token == default)
    {
      CancellationTokenSource cts = new();
      hotKey = new(id, fsModifiers, vk, cts);
    }
    else
    {
      hotKey = new(id, fsModifiers, vk, token);
    }

    return hotKey;
  }
}
