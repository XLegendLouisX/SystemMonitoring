using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public class SystemMonitorJson
{
    public string HostIp { get; set; }
    public string Timestamp { get; set; }
    public string CpuUsage { get; set; }
    public string MemoryUsage { get; set; }
    public List<DiskUsageEntry> DiskUsage { get; set; }
    public List<UrlStatusEntry> UrlStatus { get; set; }
    public List<TaskLogEntry> TaskLogs { get; set; }
    public int NotifyInterval { get; set; }
    public List<string> NotifyRecord { get; set; }

    // 無參數建構子
    public SystemMonitorJson() { }

    // 將物件序列化為 JSON
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    // **取得 NotifyRecord 清單**
    public List<string> GetNotifyRecord()
    {
        return NotifyRecord;
    }

    // **將新的通知記錄添加到 NotifyRecord**
    public void AddNotifyRecord(string newRecord)
    {
        NotifyRecord.Add(newRecord);
    }

    // **刪除特定通知記錄**
    public void RemoveNotifyRecord(string record)
    {
        NotifyRecord.Remove(record);
    }

    // **完全覆蓋 NotifyRecord**
    public void UpdateNotifyRecord(List<string> newRecords)
    {
        NotifyRecord = newRecords;
    }

    // 子類別 - 磁碟使用率
    public class DiskUsageEntry
    {
        public string Drive { get; set; }
        public string Usage { get; set; }
    }

    // 子類別 - URL 狀態
    public class UrlStatusEntry
    {
        public string Url { get; set; }
        public int Status { get; set; }
    }

    // 子類別 - 排程任務紀錄
    public class TaskLogEntry
    {
        public string Name { get; set; }
        public string Status { get; set; }
    }
}
