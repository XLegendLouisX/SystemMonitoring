using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static SystemMonitorJson;

public class SystemMonitor
{
    public int NotifyInterval { get; set; }
    public List<NotifyRecord> NotifyRecord { get; set; } = new List<NotifyRecord>();
    public List<SystemMonitorJson> JsonList { get; set; }

    public SystemMonitor()
    {
        JsonList = new List<SystemMonitorJson>();
    }
}

public class SystemMonitorJson
{
    public string HostIp { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string CpuUsage { get; set; } = string.Empty;
    public string MemoryUsage { get; set; } = string.Empty;
    public List<DiskUsageEntry> DiskUsage { get; set; } = new List<DiskUsageEntry>();
    public List<UrlStatusEntry> UrlStatus { get; set; } = new List<UrlStatusEntry>();
    public List<TaskLogEntry> TaskLogs { get; set; } = new List<TaskLogEntry>();

    // 將物件序列化為 JSON
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    // 子類別 - 磁碟使用率
    public class DiskUsageEntry
    {
        public string Drive { get; set; } = string.Empty;
        public string Usage { get; set; } = string.Empty;
    }

    // 子類別 - URL 狀態
    public class UrlStatusEntry
    {
        public string Url { get; set; } = string.Empty;
        public int Status { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    // 子類別 - 排程任務紀錄
    public class TaskLogEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Status_Text { get; set; } = string.Empty;
    }

    public class NotifyRecord
    {
        public string NotifyTime { get; set; } = string.Empty;
        public string NotifyText { get; set; } = string.Empty;
    }
}
