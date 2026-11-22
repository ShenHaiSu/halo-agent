using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Bridge;

/// <summary>
/// 外观配置 Bridge：真相源为 config/appearance.json。
/// dev(http://localhost:5173) 与 release(app://local) 的 localStorage 按 origin 隔离，
/// 两者不共享配色/宽度配置；统一落到后端数据根后即可跨模式共享。
/// </summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<AppearanceState>> GetAppearance()
    {
        try
        {
            // 必须区分「后端从未写过」与「后端写了默认值」：前者要把 localStorage 快照迁移上来，
            // 后者以后端为准。统一回默认对象会让首次升级丢掉用户已有配置。
            if (!File.Exists(_storageManager.AppearanceFilePath))
                return Task.FromResult(
                    BridgeResponse<AppearanceState>.Fail("APPEARANCE_NOT_FOUND", "外观配置尚未初始化")
                );
            return Task.FromResult(
                BridgeResponse<AppearanceState>.Success(_storageManager.LoadAppearance())
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetAppearance failed", ex);
            return Task.FromResult(
                BridgeResponse<AppearanceState>.Fail("APPEARANCE_ERROR", ex.Message)
            );
        }
    }

    public async Task<BridgeResponse<bool>> SetAppearance(string settingsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
                return BridgeResponse<bool>.Fail("APPEARANCE_VALIDATION", "外观配置 JSON 不能为空");

            AppearanceState? incoming;
            try
            {
                incoming = JsonSerializer.Deserialize<AppearanceState>(
                    settingsJson,
                    HaloJsonOptions.IndentedCamelCase
                );
            }
            catch (Exception)
            {
                return BridgeResponse<bool>.Fail("APPEARANCE_VALIDATION", "外观配置 JSON 解析失败");
            }
            if (incoming == null)
                return BridgeResponse<bool>.Fail("APPEARANCE_VALIDATION", "外观配置 JSON 解析为空");

            // 后端兜底校验：与前端 appearanceStore.mergeWithDefaults 同语义，防止坏数据落盘。
            Normalize(incoming);
            await _storageManager.SaveAppearanceAsync(incoming).ConfigureAwait(false);
            return BridgeResponse<bool>.Success(true);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SetAppearance failed", ex);
            return BridgeResponse<bool>.Fail("APPEARANCE_ERROR", ex.Message);
        }
    }

    private static void Normalize(AppearanceState s)
    {
        if (s.ThemeMode != "dark" && s.ThemeMode != "light")
            s.ThemeMode = "dark";
        if (s.ChatWidthMode != "centered" && s.ChatWidthMode != "full")
            s.ChatWidthMode = "centered";
        if (s.SendKeyMode != "enter" && s.SendKeyMode != "ctrl-enter")
            s.SendKeyMode = "enter";
        if (s.FontSize < 12 || s.FontSize > 16)
            s.FontSize = 13;
        if (!IsHexColor(s.ThemeColor))
            s.ThemeColor = "#3b82f6";
        if (string.IsNullOrWhiteSpace(s.ChineseFont))
            s.ChineseFont = "Microsoft YaHei UI";
        if (string.IsNullOrWhiteSpace(s.EnglishFont))
            s.EnglishFont = "Segoe UI";
        if (string.IsNullOrWhiteSpace(s.MonoFont))
            s.MonoFont = "Consolas";
    }

    private static bool IsHexColor(string? value)
    {
        if (value is null)
            return false;
        ReadOnlySpan<char> hex =
            value.Length > 0 && value[0] == '#' ? value.AsSpan(1) : value.AsSpan();
        if (hex.Length != 3 && hex.Length != 6)
            return false;
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }
}
