using System;
using System.IO;
using System.Text.Json;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class ConfigurationService : IConfigurationService
{
    private const string ConfigFileName = "appsettings.json";
    private readonly string _configFilePath;

    private AppSettings _appSettings;

    public AppSettings Settings => _appSettings;

    public ConfigurationService()
    {
        // 将配置文件保存到当前用户的 ApplicationData 目录下
        string appDataFolder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(appDataFolder); // 如果目录不存在则创建

        _configFilePath = Path.Combine(appDataFolder, ConfigFileName);
        _appSettings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                string json = File.ReadAllText(_configFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                var migrated = ApplyPersonalDefaults(loaded);
                if (migrated)
                    Save(loaded); // 把补全后的默认值写回磁盘，避免每次启动都判定为「需迁移」
                return loaded;
            }
        }
        catch
        { /* 配置文件损坏时用默认值覆盖 */
        }

        // 文件不存在或解析失败，创建默认配置并保存
        var defaultSettings = new AppSettings();
        Save(defaultSettings);
        return defaultSettings;
    }

    /// <summary>
    /// 补全个人自用所需的默认开关。
    ///
    /// 背景：这台机器上 <c>appsettings.json</c> 是老版本生成的，四个布尔字段都没有值，
    /// JSON 反序列化会把它们解析成 <c>false</c> —— 于是「启动即自动录制」「启动最小化到托盘」
    /// 全部失效。构造函数里的属性初始值只在「新建配置对象」时生效，管不到已有文件。
    /// 所以这里显式做一次补全：字段缺值（false）时按个人自用期望置为 true。
    ///
    /// 保留 <see cref="IsLogExpanded"/> 原状：它是界面偏好，不属于「静默运行」范畴。
    /// 若日后想关掉某项，只需在界面里取消勾选并保存，下次启动就不会被改回。
    /// </summary>
    /// <returns>是否有字段被补全（决定是否需要写回磁盘）。</returns>
    private static bool ApplyPersonalDefaults(AppSettings settings)
    {
        bool changed = false;

        if (!settings.Recording.AutoStartRecording)
        {
            settings.Recording.AutoStartRecording = true;
            changed = true;
        }
        if (!settings.Recording.AutoStartKeyRecording)
        {
            settings.Recording.AutoStartKeyRecording = true;
            changed = true;
        }
        if (!settings.General.StartMinimized)
        {
            settings.General.StartMinimized = true;
            changed = true;
        }

        return changed;
    }

    public void Save() => Save(_appSettings);

    private void Save(AppSettings settings)
    {
        var options = new JsonSerializerOptions { WriteIndented = true }; // 格式化，方便阅读
        string json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(_configFilePath, json);
    }

    // 便捷方法：更新录制自动启动状态
    public void SetRecordingAutoStart(bool enabled)
    {
        _appSettings.Recording.AutoStartRecording = enabled;
        Save();
    }

    public bool GetRecordingAutoStart() => _appSettings.Recording.AutoStartRecording;

    public void SetKeyRecordingAutoStart(bool enabled)
    {
        _appSettings.Recording.AutoStartKeyRecording = enabled;
        Save();
    }

    public bool GetKeyRecordingAutoStart() => _appSettings.Recording.AutoStartKeyRecording;

    public double GetWindowWidth()
    {
        var w = _appSettings.General.WindowWidth;
        // 旧版本在最小化时把窗口尺寸写坏过（实测 160x28），读回时纠正为可用值
        if (w < 400)
        {
            _appSettings.General.WindowWidth = 1280;
            return 1280;
        }
        return w;
    }

    public double GetWindowHeight()
    {
        var h = _appSettings.General.WindowHeight;
        if (h < 300)
        {
            _appSettings.General.WindowHeight = 720;
            return 720;
        }
        return h;
    }

    public bool GetIsLogExpanded() => _appSettings.General.IsLogExpanded;

    public bool GetStartMinimized() => _appSettings.General.StartMinimized;

    public void SetStartMinimized(bool minimized)
    {
        _appSettings.General.StartMinimized = minimized;
        Save();
    }

    public void SetWindowWidth(double width)
    {
        _appSettings.General.WindowWidth = width;
        Save();
    }

    public void SetWindowHeight(double height)
    {
        _appSettings.General.WindowHeight = height;
        Save();
    }

    public void SetIsLogExpanded(bool expanded)
    {
        _appSettings.General.IsLogExpanded = expanded;
        Save();
    }
}
