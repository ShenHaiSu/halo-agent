using System.Text.Json;
using System.Text.Json.Serialization;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Storage.Config;

namespace HaloAgent.Engine.Storage;

/// <summary>
/// UI 偏好持久化（prefs.json）：只放与 providers 树无关的轻量偏好。
/// 独立于 config.json，避免触碰旧形状嗅探 / 自动备份 / 迁移确认语义。
/// 读写一律容错：缺失、损坏、未知值都回落，不抛异常（调用方决定缺省）。
/// </summary>
public static class PrefsStore
{
    private sealed class PrefsModel
    {
        [JsonPropertyName("permission_mode")]
        public string? PermissionMode { get; set; }
    }

    /// <summary>读取已持久化的权限模式；无文件/损坏/未知值返回 null（调用方回落 Ask）。</summary>
    public static PermissionMode? LoadPermissionMode()
    {
        string path;
        try
        {
            path = Paths.PrefsFile;
        }
        catch
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var model = JsonSerializer.Deserialize<PrefsModel>(text, StorageJson.Options);
            if (model?.PermissionMode is null)
            {
                return null;
            }

            return PermissionModeExtensions.TryParseWire(model.PermissionMode.Trim(), out var mode)
                ? mode
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 持久化权限模式（原子写：临时文件 + 替换）。
    /// 与现有偏好合并写入，不丢其他键。失败返回 false，不抛异常。
    /// </summary>
    public static bool SavePermissionMode(PermissionMode mode)
    {
        string path;
        try
        {
            path = Paths.PrefsFile;
        }
        catch
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Paths.DataDir);
        }
        catch
        {
            return false;
        }

        try
        {
            // 与现有偏好合并写入，不丢其他键（字典原样回写，key 不做命名转换）。
            Dictionary<string, JsonElement> dict;
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    dict = string.IsNullOrWhiteSpace(text)
                        ? new Dictionary<string, JsonElement>()
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text, StorageJson.Options)
                            ?? new Dictionary<string, JsonElement>();
                }
                catch
                {
                    // 损坏文件：重建（只丢偏好，不影响 config.json）。
                    dict = new Dictionary<string, JsonElement>();
                }
            }
            else
            {
                dict = new Dictionary<string, JsonElement>();
            }
            dict["permission_mode"] = JsonSerializer.SerializeToElement(mode.ToWire());
            var json = JsonSerializer.Serialize(dict, StorageJson.Options);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
