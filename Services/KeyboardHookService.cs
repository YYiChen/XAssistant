using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyboardHookService : IKeyboardHookService, IDisposable
{
    public event Action<string>? KeyPressed;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly ILogger<KeyboardHookService> _logger;
    private LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // 记录当前正处于按下状态的键（名称）
    private readonly HashSet<string> _pressedKeys = new(StringComparer.Ordinal);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    public KeyboardHookService(ILogger<KeyboardHookService> logger)
    {
        _logger = logger;
        _proc = HookCallback;
    }

    /// <summary>
    /// 安装低级键盘钩子。
    ///
    /// 与 <see cref="MouseClickHookService.Start"/> 对齐（上游此处两版实现不一致）：
    /// 1. 防重复：已安装时直接返回。否则重复调用会覆盖 <c>_hookId</c>，
    ///    旧句柄永久泄漏——它仍挂在钩子链上持续被调用，且 Stop() 只能卸载最新那个，
    ///    表现为「点了停止记录却仍在记录」。
    /// 2. 失败检测：SetWindowsHookEx 失败时抛异常。
    ///    上游实现静默忽略返回值，钩子没装上而界面仍显示「记录中」，
    ///    用户会以为在记录、实际零数据。
    /// </summary>
    public void Start()
    {
        if (_hookId != IntPtr.Zero)
            return; // 已有一个钩子在运行

        string? moduleName;
        try
        {
            using var curProcess = Process.GetCurrentProcess();
            moduleName = curProcess.MainModule?.ModuleName;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法获取主模块名称，键盘钩子安装失败。", ex);
        }

        if (string.IsNullOrEmpty(moduleName))
            throw new InvalidOperationException("无法获取主模块名称，键盘钩子安装失败。");

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(moduleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.LogError("键盘钩子安装失败，Win32 错误码 {Error}", error);
            throw new InvalidOperationException($"SetWindowsHookEx 失败，错误代码：{error}");
        }

        _logger.LogInformation("键盘钩子已安装");
        // 钩子重建后清空按下状态，避免残留状态导致漏记（例如停止期间某个键未抬起）
        _pressedKeys.Clear();
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                string keyName = GetKeyNameFromScanCode(kb.vkCode, kb.scanCode, kb.flags);

                if (isKeyDown)
                {
                    // 如果该键已经处于按下状态，则为长按重复，直接忽略
                    if (_pressedKeys.Contains(keyName))
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);

                    _pressedKeys.Add(keyName);
                    KeyPressed?.Invoke(keyName);
                }
                else // isKeyUp
                {
                    _pressedKeys.Remove(keyName);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // 左右修饰键固定映射：GetKeyNameText 对左/右 Shift、Ctrl、Alt、Win 的命名依赖键盘布局
    // （部分布局自带 “Left/Right” 方位词，多数不区分），统一走这里保证左右稳定可区分。
    // Win 用 “Left Windows/Right Windows” 与正式版历史数据保持一致
    private static readonly Dictionary<int, string> ModifierKeyNames = new()
    {
        [0x5B] = "Left Windows", // VK_LWIN
        [0x5C] = "Right Windows", // VK_RWIN
        [0xA0] = "Shift", // VK_LSHIFT
        [0xA1] = "Right Shift", // VK_RSHIFT
        [0xA2] = "Ctrl", // VK_LCONTROL
        [0xA3] = "Right Ctrl", // VK_RCONTROL
        [0xA4] = "Alt", // VK_LMENU
        [0xA5] = "Right Alt", // VK_RMENU
    };

    // 把扫描码 + 扩展标志合成 GetKeyNameText 所需的参数
    private string GetKeyNameFromScanCode(int vkCode, int scanCode, int flags)
    {
        // 左右修饰键优先走固定映射，避免布局差异
        if (ModifierKeyNames.TryGetValue(vkCode, out var modifierName))
            return modifierName;

        // 如果设置了扩展位（flags & 1），需要给扫描码加上 0x100
        bool isExtended = (flags & 1) != 0;
        int lParamValue = (scanCode << 16) | (isExtended ? 0x1000000 : 0);

        var sb = new StringBuilder(256);
        int result = GetKeyNameText(lParamValue, sb, sb.Capacity);
        if (result > 0)
            return sb.ToString();

        // GetKeyNameText 无法命名（常见于笔记本 Fn 键等非标准键）。
        // 扫描码 0x63 是多数笔记本 Fn 的扫描码；其余保留原始码值以便后续识别
        if (scanCode == 0x63)
            return "Fn";
        _logger.LogWarning("按键无法识别，扫描码 0x{Scan:X2}，虚拟键码 0x{Vk:X2}，标志 0x{Flags:X2}", scanCode, vkCode, flags);
        return $"Unknown (scan 0x{scanCode:X2}, vk 0x{vkCode:X2})";
    }

    // 结构体定义
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    // P/Invoke
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose() => Stop();
}
