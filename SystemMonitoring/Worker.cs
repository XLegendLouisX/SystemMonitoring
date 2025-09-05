namespace SystemMonitoring
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Management;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using TaskScheduler = Microsoft.Win32.TaskScheduler;
    using static SystemMonitorJson;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    public class Worker : BackgroundService
    {
        private readonly HttpClient _httpClient;

        private readonly ILogger<Worker> _logger;
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _ramCounter;

        private int _sharedHost = 0;
        private int _taskHost = 0;
        private int _urlHost = 0;

        private int _notifyInterval = 0;
        private int _storageDays = 60;
        private int _sharedJsonTotal = 0;
        private int _requestTimeout = 60;
        private string _localPath;
        private string _sharedPath;
        private string _taskPath;
        private string[] url_list = { };

        // === 分時段檢測 === //
        private TimeSpan _diskEvery;
        private TimeSpan _urlEvery;
        private TimeSpan _taskEvery;

        private DateTimeOffset _nextDisk;
        private DateTimeOffset _nextUrl;
        private DateTimeOffset _nextTask;

        private TimeZoneInfo _tz; // 目標時區（台北）

        // === 快取：分別由各自排程更新，寫檔時組合 ===
        private float _lastCpuUsage;
        private double _lastRamUsage;
        private Dictionary<string, double> _lastDiskUsage = new();
        private Dictionary<string, (int, string)> _lastUrlStatus = new();
        private Dictionary<string, (string, string)> _lastTaskLogs = new();
        // === 分時段檢測 === //

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            // 初始化性能計數器（Windows）
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");

            _sharedHost = configuration.GetValue<int>("WorkerSettings:SharedHost");
            _taskHost = configuration.GetValue<int>("WorkerSettings:TaskHost");
            _urlHost = configuration.GetValue<int>("WorkerSettings:UrlHost");

            _notifyInterval = configuration.GetValue<int>("WorkerSettings:NotifyInterval");
            _storageDays = configuration.GetValue<int>("WorkerSettings:StorageDays");
            _sharedJsonTotal = configuration.GetValue<int>("WorkerSettings:SharedJsonTotal");
            _requestTimeout = configuration.GetValue<int>("WorkerSettings:RequestTimeout");
            _localPath = configuration.GetValue<string>("WorkerSettings:LocalPath");
            _sharedPath = configuration.GetValue<string>("WorkerSettings:SharedPath");
            _taskPath = configuration.GetValue<string>("WorkerSettings:TaskPath");
            url_list = configuration.GetSection("WorkerSettings:HttpUrls").Get<string[]>() ?? Array.Empty<string>();

            if (_notifyInterval < 0) _notifyInterval = 0;
            if (_storageDays < 1) _storageDays = 1;
            if (_sharedJsonTotal < 0) _sharedJsonTotal = 0;
            if (_requestTimeout < 10) _requestTimeout = 10;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_requestTimeout)
            };

            // === 讀取分流排程（若未設定給預設值） ===
            int diskMin = Math.Max(1, configuration.GetValue<int>("WorkerSettings:DiskEveryMinutes", 1));
            int urlMin = Math.Max(1, configuration.GetValue<int>("WorkerSettings:UrlEveryMinutes", 1));
            int taskMin = Math.Max(1, configuration.GetValue<int>("WorkerSettings:TaskEveryMinutes", 1));

            _diskEvery = TimeSpan.FromMinutes(diskMin);
            _urlEvery = TimeSpan.FromMinutes(urlMin);
            _taskEvery = TimeSpan.FromMinutes(taskMin);

            // 台北時區
            _tz = TimeFunction.GetTaipeiTimeZone();

            // 對齊到「下一個整 N 分鐘」時間點（例如 00/10/20/30...）
            var now = DateTimeOffset.UtcNow;
            _nextDisk = TimeFunction.GetNextByInterval(now, diskMin, _tz);
            _nextUrl = TimeFunction.GetNextByInterval(now, urlMin, _tz);
            _nextTask = TimeFunction.GetNextByInterval(now, taskMin, _tz);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 預熱 CPU 計數器
            _cpuCounter.NextValue();
            await Task.Delay(1000, stoppingToken);

            string hostIp = GetLocalIp();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 找出下一個最近要執行的時間
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    DateTimeOffset next = TimeFunction.Min(_nextDisk, _nextUrl, _nextTask);

                    var delay = next - now;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, stoppingToken);

                    // 到點後判斷哪些到期（可能多個同時到期）
                    now = DateTimeOffset.UtcNow;
                    bool dueDisk = now >= _nextDisk;
                    bool dueUrl = now >= _nextUrl;
                    bool dueTask = now >= _nextTask;

                    if (!(dueDisk || dueUrl || dueTask))
                        continue;

                    // **關鍵：並行執行**
                    var tasks = new List<Task>();
                    Task diskTask = Task.CompletedTask;
                    Task<Dictionary<string, (int, string)>> urlTask = null;
                    Task<Dictionary<string, (string, string)>> schedTask = null;

                    // 建立任務，同步「先排下一次時間」→ 即使某項工作較久也不會漂移
                    if (dueDisk)
                    {
                        diskTask = UpdateSystemMetricsAsync();
                        tasks.Add(diskTask);
                        _nextDisk = _nextDisk.Add(_diskEvery);
                    }
                    if (dueUrl)
                    {
                        urlTask = GetUrlStatus();
                        tasks.Add(urlTask);
                        _nextUrl = _nextUrl.Add(_urlEvery);
                    }
                    if (dueTask)
                    {
                        schedTask = GetTaskSchedulerLogsAsync();
                        tasks.Add(schedTask);
                        _nextTask = _nextTask.Add(_taskEvery);
                    }

                    try
                    {
                        await Task.WhenAll(tasks);
                    }
                    catch (Exception ex)
                    {
                        // 若其中一個失敗，仍嘗試收集其他已完成結果
                        _logger.LogError(ex, "並行檢測時發生例外");
                    }

                    // 收集結果（只在該類任務成功完成時覆寫快取）
                    if (dueUrl && urlTask is not null && urlTask.Status == TaskStatus.RanToCompletion)
                        _lastUrlStatus = urlTask.Result;

                    if (dueTask && schedTask is not null && schedTask.Status == TaskStatus.RanToCompletion)
                        _lastTaskLogs = schedTask.Result;

                    // 任何一項到期都寫一次快照（合併三類最新快取）
                    await WriteSnapshotAsync(GetLocalIp());
                }
                catch (TaskCanceledException)
                {
                    // 正常結束
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "排程執行失敗");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        // ✅ 已改為由各排程更新快取，這裡負責合併與寫檔
        private async Task WriteSnapshotAsync(string hostIp)
        {
            string localPath = CreateFile(_localPath);
            string sharedPath = CreateFile(_sharedPath);

            var timestamp = DateTime.Now;
            var logFileName = $"SM_{hostIp}_{timestamp:yyyyMMdd}.json";
            var localFilePath = Path.Combine(localPath, logFileName);
            var sharedFilePath = Path.Combine(sharedPath, Path.GetFileName(localFilePath));

            // 組合目前快取成一筆記錄
            SystemMonitorJson systemMonitorJson = new SystemMonitorJson()
            {
                HostIp = hostIp,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CpuUsage = $"{_lastCpuUsage:0.00}%",
                MemoryUsage = $"{_lastRamUsage:0.00}%",
                DiskUsage = _lastDiskUsage.Select(i => new DiskUsageEntry { Drive = i.Key, Usage = $"{i.Value:0.00}%" }).ToList(),
                UrlStatus = _lastUrlStatus.Select(i => new UrlStatusEntry { Url = i.Key, Status = i.Value.Item1, Message = i.Value.Item2 }).ToList(),
                TaskLogs = _lastTaskLogs.Select(i => new TaskLogEntry { Name = i.Key, Status = i.Value.Item1, Status_Text = i.Value.Item2 }).ToList(),
            };

            if (Directory.Exists(localPath))
            {
                // ========== 先讀共享（拿到 NotifyRecord 等） ==========
                SystemMonitor systemMonitor_shared = await ReadJsonFileAsync(sharedFilePath);

                // ========== 本機 ==========
                SystemMonitor systemMonitor = await ReadJsonFileAsync(localFilePath);
                systemMonitor.JsonList.Add(systemMonitorJson);
                systemMonitor.NotifyInterval = _notifyInterval;

                if (systemMonitor_shared.NotifyRecord.Count > 0)
                    systemMonitor.NotifyRecord = systemMonitor_shared.NotifyRecord;

                await WriteJsonFileAsync(localFilePath, systemMonitor);

                // ========== 同步到共享 ==========
                if (Directory.Exists(sharedPath))
                {
                    systemMonitor = await ReadJsonFileAsync(localFilePath);
                    SystemMonitor newData = GetDataByQuantity(systemMonitor);
                    await WriteJsonFileAsync(sharedFilePath, newData);

                    string result_delete = DeleteOldFolders();
                    if (!string.IsNullOrEmpty(result_delete))
                        _logger.LogInformation(result_delete);
                }
            }
        }

        /// <summary>
        /// 建立Json路徑（以日期分資料夾）
        /// </summary>
        private string CreateFile(string path)
        {
            string newPath = path;

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            string dateFolder = DateTime.Now.ToString("yyyyMMdd");
            string dateFolderPath = Path.Combine(path, dateFolder);
            if (!Directory.Exists(dateFolderPath))
                Directory.CreateDirectory(dateFolderPath);

            newPath = dateFolderPath;
            return newPath;
        }

        /// <summary>
        /// 讀取Json檔（若不存在或解析失敗回傳空物件）
        /// </summary>
        private async Task<SystemMonitor> ReadJsonFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
                return new SystemMonitor();

            try
            {
                using (FileStream fs = File.OpenRead(filePath))
                {
                    var obj = await JsonSerializer.DeserializeAsync<SystemMonitor>(fs);
                    return obj ?? new SystemMonitor(); // ⚠️ 防 null
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"讀取 JSON 檔案失敗: {filePath}, 錯誤: {ex.Message}");
                return new SystemMonitor();
            }
        }

        /// <summary>
        /// 寫入Json檔
        /// </summary>
        private async Task WriteJsonFileAsync(string filePath, SystemMonitor data)
        {
            using (FileStream fs = File.Create(filePath))
            {
                await JsonSerializer.SerializeAsync(fs, data, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        /// <summary>
        /// 每次到期更新系統資源快取（CPU/RAM/Disk）
        /// </summary>
        private async Task UpdateSystemMetricsAsync()
        {
            // CPU / RAM
            float cpuUsage = _cpuCounter.NextValue();
            float availableRam = _ramCounter.NextValue();
            double totalMemory = GetTotalMemory();
            double ramUsage = (totalMemory > 0) ? ((totalMemory - availableRam) / totalMemory) * 100 : 0;

            _lastCpuUsage = cpuUsage;
            _lastRamUsage = ramUsage;

            // Disk
            _lastDiskUsage = GetDiskUsage();

            // 即時 log（可保留/移除）
            _logger.LogInformation($"CPU 使用率: {cpuUsage:0.00}%");
            _logger.LogInformation($"記憶體使用率: {ramUsage:0.00}%");
            foreach (var d in _lastDiskUsage)
                _logger.LogInformation($"磁碟 {d.Key} 使用率 {d.Value:0.00}%");

            await Task.CompletedTask;
        }

        /// <summary>
        /// 取得記憶體總量（MB）
        /// </summary>
        private static double GetTotalMemory()
        {
            try
            {
                var query = new ObjectQuery("SELECT * FROM Win32_ComputerSystem");
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (var obj in searcher.Get())
                        return Convert.ToDouble(obj["TotalPhysicalMemory"]) / (1024 * 1024);
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 取得硬碟使用率（%）
        /// </summary>
        private static Dictionary<string, double> GetDiskUsage()
        {
            try
            {
                var driveInfo = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .ToDictionary(
                        d => d.Name,
                        d => (1 - (double)d.AvailableFreeSpace / d.TotalSize) * 100
                    );
                return driveInfo;
            }
            catch
            {
                return new Dictionary<string, double>();
            }
        }

        /// <summary>
        /// 取得網址狀態
        /// </summary>
        public async Task<Dictionary<string, (int, string)>> GetUrlStatus()
        {
            var result = new Dictionary<string, (int, string)>();
            if (_urlHost == 0 || url_list.Length == 0)
                return await Task.FromResult(result);

            foreach (var url in url_list)
            {
                try
                {
                    HttpResponseMessage response = await _httpClient.GetAsync(url);
                    result[url] = ((int)response.StatusCode, "Success");
                }
                catch (Exception ex)
                {
                    result[url] = (0, ex.Message);
                }
            }
            return result;
        }

        /// <summary>
        /// 取得工作排程（Windows 工作排程器）
        /// </summary>
        public async Task<Dictionary<string, (string, string)>> GetTaskSchedulerLogsAsync()
        {
            var logs = new Dictionary<string, (string, string)>();
            if (_taskHost == 0)
                return await Task.FromResult(logs);

            try
            {
                using (TaskScheduler.TaskService ts = new TaskScheduler.TaskService())
                {
                    TaskScheduler.TaskFolder folder = ts.GetFolder(_taskPath);
                    if (folder != null)
                    {
                        foreach (var task in folder.Tasks)
                        {
                            string status = GetTaskStateDescription(task.State) + "[" + GetErrorMessage(task.LastTaskResult) + "]";
                            string status_text = task.State.ToString();
                            logs[task.Name] = (status, status_text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"讀取工作排程失敗: {ex.Message}");
            }

            return await Task.FromResult(logs);
        }

        private string GetTaskStateDescription(TaskScheduler.TaskState state)
        {
            return state switch
            {
                TaskScheduler.TaskState.Unknown => "未知",
                TaskScheduler.TaskState.Disabled => "已停用",
                TaskScheduler.TaskState.Queued => "排隊中",
                TaskScheduler.TaskState.Ready => "就緒",
                TaskScheduler.TaskState.Running => "正在運行",
                _ => "未定義"
            };
        }

        private string GetErrorMessage(int errorCode)
        {
            try { return new Win32Exception(errorCode).Message; }
            catch { return $"未知錯誤（錯誤碼: {errorCode}）"; }
        }

        /// <summary>
        /// 更新共享資料夾Json檔（保留最近 _sharedJsonTotal 筆）
        /// </summary>
        private SystemMonitor GetDataByQuantity(SystemMonitor localSystemMonitor)
        {
            SystemMonitor systemMonitor = localSystemMonitor ?? new SystemMonitor();
            try
            {
                if (_sharedJsonTotal > 0)
                {
                    int count = systemMonitor.JsonList.Count;
                    if (count > _sharedJsonTotal)
                        systemMonitor.JsonList = systemMonitor.JsonList.Skip(count - _sharedJsonTotal).ToList();
                }
                return systemMonitor;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"整理共享 JSON 失敗: {ex.Message}");
                return systemMonitor;
            }
        }

        /// <summary>
        /// 取得本機IP（IPv4）
        /// </summary>
        private string GetLocalIp()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.ToString();
                }
                return "Unknown IP";
            }
            catch
            {
                return "Unable to retrieve local IP address.";
            }
        }

        /// <summary>
        /// 刪除過期資料夾（依日期 yyyyMMdd）
        /// </summary>
        private string DeleteOldFolders()
        {
            string result = "";
            string[] pathList = (_sharedHost == 0) ? new[] { _localPath } : new[] { _localPath, _sharedPath };

            try
            {
                foreach (var path in pathList)
                {
                    if (!Directory.Exists(path))
                    {
                        result += $"folder does not exist: {{{path}}}\n";
                        continue;
                    }

                    var targetDate = DateTime.Today.AddDays(-_storageDays);
                    var folders = Directory.GetDirectories(path);

                    foreach (var folder in folders)
                    {
                        var folderName = Path.GetFileName(folder);
                        if (DateTime.TryParseExact(folderName, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var folderDate))
                        {
                            if (folderDate <= targetDate)
                            {
                                try
                                {
                                    Directory.Delete(folder, true);
                                    result += $"Deleted folder: {{{folder}}}\n";
                                }
                                catch (Exception ex)
                                {
                                    result += $"Failed to delete folder: {{{folder}}},{ex.Message}\n";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result = $"Error transmitting JSON to target: {ex.Message}";
            }

            return result;
        }
    }
}