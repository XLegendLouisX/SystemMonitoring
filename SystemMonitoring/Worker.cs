namespace SystemMonitoring
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Management;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using TaskScheduler = Microsoft.Win32.TaskScheduler;
    using static SystemMonitorJson;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.ServiceProcess;

    public class Worker : BackgroundService
    {
        private readonly HttpClient _httpClient;

        private readonly ILogger<Worker> _logger;
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _ramCounter;

        private int _sharedHost = 0;
        private int _taskHost = 0;
        private int _urlHost = 0;
        private int _serveHost = 0;

        private bool _useRunInterval = false;
        private int _notifyInterval = 0;
        private int _runInterval = 5000;
        private int _storageDays = 60;
        private int _sharedJsonTotal = 0;
        private int _requestTimeout = 60;
        private string _localPath;
        private string _sharedPath;
        private string _taskPath;
        private string _ePayUrl1;
        private string _ePayUrl2;
        private string[] url_list = { };

        // 分時段（UseRunInterval=0 時）
        private TimeSpan _diskEvery;
        private TimeSpan _urlEvery;
        private TimeSpan _urlPostEvery;
        private TimeSpan _taskEvery;
        private TimeSpan _serveEvery;

        private DateTimeOffset _nextDisk;
        private DateTimeOffset _nextUrl;
        private DateTimeOffset _nextUrlPost;
        private DateTimeOffset _nextTask;
        private DateTimeOffset _nextServe;

        private string[] _serveList = Array.Empty<string>();

        private TimeZoneInfo _tz; // 台北時區

        // 快取
        private float _lastCpuUsage;
        private double _lastRamUsage;
        private Dictionary<string, double> _lastDiskUsage = new();
        private Dictionary<string, (int, string)> _lastUrlStatus = new();
        private Dictionary<string, (int, string)> _lastUrlPostStatus = new();
        private Dictionary<string, (string, string)> _lastTaskLogs = new();
        private Dictionary<string, (string, string)> _lastServeStatus = new();

        // 傳輸模式：Folder / Api / Both
        private readonly string _transferMode;

        // API
        private readonly bool _useCentralApi;
        private readonly string _centralApiBaseUrl;
        private readonly string _centralApiToken;

        private bool UseFolderMode =>
            string.Equals(_transferMode, "Folder", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_transferMode, "Both", StringComparison.OrdinalIgnoreCase);

        private bool UseApiMode =>
            string.Equals(_transferMode, "Api", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_transferMode, "Both", StringComparison.OrdinalIgnoreCase);

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;

            // 初始化性能計數器（Windows）
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");

            _sharedHost = configuration.GetValue<int>("WorkerSettings:SharedHost");
            _taskHost = configuration.GetValue<int>("WorkerSettings:TaskHost");
            _urlHost = configuration.GetValue<int>("WorkerSettings:UrlHost");
            _serveHost = configuration.GetValue<int>("WorkerSettings:ServeHost", 0);

            _useRunInterval = configuration.GetValue<int>("WorkerSettings:UseRunInterval", 0) == 1;
            _runInterval = configuration.GetValue<int>("WorkerSettings:RunInterval", 30000);

            _notifyInterval = configuration.GetValue<int>("WorkerSettings:NotifyInterval");
            _storageDays = configuration.GetValue<int>("WorkerSettings:StorageDays");
            _sharedJsonTotal = configuration.GetValue<int>("WorkerSettings:SharedJsonTotal");
            _requestTimeout = configuration.GetValue<int>("WorkerSettings:RequestTimeout");
            _localPath = configuration.GetValue<string>("WorkerSettings:LocalPath");
            _sharedPath = configuration.GetValue<string>("WorkerSettings:SharedPath");
            _taskPath = configuration.GetValue<string>("WorkerSettings:TaskPath");
            _ePayUrl1 = configuration.GetValue<string>("WorkerSettings:EPayUrl1");
            _ePayUrl2 = configuration.GetValue<string>("WorkerSettings:EPayUrl2");
            url_list = configuration.GetSection("WorkerSettings:HttpUrls").Get<string[]>() ?? Array.Empty<string>();
            _serveList = configuration.GetSection("WorkerSettings:Serve").Get<string[]>() ?? Array.Empty<string>();

            _transferMode = configuration.GetValue<string>("WorkerSettings:TransferMode") ?? "Folder";
            _transferMode = _transferMode.Trim();
            if (string.IsNullOrEmpty(_transferMode))
                _transferMode = "Folder";

            _useCentralApi = configuration.GetValue<int>("WorkerSettings:UseCentralApi", 0) == 1;
            _centralApiBaseUrl = configuration.GetValue<string>("WorkerSettings:CentralApiBaseUrl") ?? string.Empty;
            _centralApiToken = configuration.GetValue<string>("WorkerSettings:CentralApiToken") ?? string.Empty;

            if (_notifyInterval < 0) _notifyInterval = 0;
            if (_runInterval <= 1000) _runInterval = 1000;
            if (_storageDays < 1) _storageDays = 1;
            if (_sharedJsonTotal < 0) _sharedJsonTotal = 0;
            if (_requestTimeout < 10) _requestTimeout = 10;

            var handler = new HttpClientHandler
            {
                // 開發 / 自簽憑證用，正式環境建議改回正常驗證
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_requestTimeout)
            };

            // 分時段設定（只在 _useRunInterval=false 時用）
            int diskMin = Math.Max(1, configuration.GetValue<int>("WorkerSettings:DiskEveryMinutes", 30));
            int urlMin = Math.Max(1, configuration.GetValue<int>("WorkerSettings:UrlEveryMinutes", 10));
            int taskMin = Math.Max(1, configuration.GetValue<int>("WorkerSettings:TaskEveryMinutes", 60));
            int serveMin = Math.Max(1, configuration.GetValue<int>("WorkerSettings:ServeEveryMinutes", 60));

            _diskEvery = TimeSpan.FromMinutes(diskMin);
            _urlEvery = TimeSpan.FromMinutes(urlMin);
            _urlPostEvery = TimeSpan.FromMinutes(urlMin);
            _taskEvery = TimeSpan.FromMinutes(taskMin);
            _serveEvery = TimeSpan.FromMinutes(serveMin);

            _tz = TimeFunction.GetTaipeiTimeZone();

            var now = DateTimeOffset.UtcNow;
            _nextDisk = TimeFunction.GetNextByInterval(now, diskMin, _tz);
            _nextUrl = TimeFunction.GetNextByInterval(now, urlMin, _tz);
            _nextUrlPost = TimeFunction.GetNextByInterval(now, urlMin, _tz);
            _nextTask = TimeFunction.GetNextByInterval(now, taskMin, _tz);
            _nextServe = TimeFunction.GetNextByInterval(now, serveMin, _tz);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 預熱 CPU 計數器
            _cpuCounter.NextValue();
            await Task.Delay(1000, stoppingToken);

            string hostIp = GetLocalIp();

            if (_useRunInterval)
            {
                // ===== 模式A：RunInterval 固定間隔 =====
                _logger.LogInformation($"[RunInterval] 啟用，間隔：{_runInterval} ms");
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(_runInterval), stoppingToken);

                        _logger.LogInformation("[RunInterval] 觸發輪巡一次。");

                        var tasks = new List<Task>();
                        Task tSys = UpdateSystemMetricsAsync();
                        tasks.Add(tSys);

                        Task<Dictionary<string, (int, string)>> tUrl = GetUrlStatus();
                        Task<Dictionary<string, (int, string)>> tUrlPost = PostUrlStatus();
                        Task<Dictionary<string, (string, string)>> tTask = GetTaskSchedulerLogsAsync();
                        Task<Dictionary<string, (string, string)>> tSvc = GetServiceStatusAsync();

                        tasks.AddRange(new Task[] { tUrl, tUrlPost, tTask, tSvc });

                        try { await Task.WhenAll(tasks); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "RunInterval 模式並行檢測時發生例外");
                        }

                        if (tUrl.Status == TaskStatus.RanToCompletion) _lastUrlStatus = tUrl.Result;
                        if (tUrlPost.Status == TaskStatus.RanToCompletion) _lastUrlPostStatus = tUrlPost.Result;
                        if (tTask.Status == TaskStatus.RanToCompletion) _lastTaskLogs = tTask.Result;
                        if (tSvc.Status == TaskStatus.RanToCompletion) _lastServeStatus = tSvc.Result;

                        await WriteSnapshotAsync(hostIp);
                    }
                    catch (TaskCanceledException) { /* 正常結束 */ }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "RunInterval 模式執行失敗");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
                return;
            }

            // ===== 模式B：分時段排程 =====
            _logger.LogInformation("[Schedule] 分時段排程模式啟用。");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    DateTimeOffset next = TimeFunction.Min(_nextDisk, _nextUrl, _nextUrlPost, _nextTask, _nextServe);

                    var delay = next - now;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, stoppingToken);

                    now = DateTimeOffset.UtcNow;
                    bool dueDisk = now >= _nextDisk;
                    bool dueUrl = now >= _nextUrl;
                    bool dueUrlPost = now >= _nextUrlPost;
                    bool dueTask = now >= _nextTask;
                    bool dueServe = now >= _nextServe;

                    if (!(dueDisk || dueUrl || dueUrlPost || dueTask || dueServe))
                        continue;

                    var tasks = new List<Task>();
                    Task diskTask = Task.CompletedTask;
                    Task<Dictionary<string, (int, string)>> urlTask = null;
                    Task<Dictionary<string, (int, string)>> urlPostTask = null;
                    Task<Dictionary<string, (string, string)>> schedTask = null;
                    Task<Dictionary<string, (string, string)>> serveTask = null;

                    if (dueDisk)
                    {
                        _logger.LogInformation("[Schedule] 到期：SystemMetrics");
                        diskTask = UpdateSystemMetricsAsync();
                        tasks.Add(diskTask);
                        _nextDisk = _nextDisk.Add(_diskEvery);
                    }
                    if (dueUrl)
                    {
                        _logger.LogInformation("[Schedule] 到期：URL");
                        urlTask = GetUrlStatus();
                        tasks.Add(urlTask);
                        _nextUrl = _nextUrl.Add(_urlEvery);
                    }
                    if (dueUrlPost)
                    {
                        _logger.LogInformation("[Schedule] 到期：URL Post");
                        urlPostTask = PostUrlStatus();
                        tasks.Add(urlPostTask);
                        _nextUrlPost = _nextUrlPost.Add(_urlPostEvery);
                    }
                    if (dueTask)
                    {
                        _logger.LogInformation("[Schedule] 到期：Task");
                        schedTask = GetTaskSchedulerLogsAsync();
                        tasks.Add(schedTask);
                        _nextTask = _nextTask.Add(_taskEvery);
                    }
                    if (dueServe)
                    {
                        _logger.LogInformation("[Schedule] 到期：Service");
                        serveTask = GetServiceStatusAsync();
                        tasks.Add(serveTask);
                        _nextServe = _nextServe.Add(_serveEvery);
                    }

                    try { await Task.WhenAll(tasks); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "分時段排程並行檢測時發生例外");
                    }

                    if (dueUrl && urlTask is not null && urlTask.Status == TaskStatus.RanToCompletion) _lastUrlStatus = urlTask.Result;
                    if (dueUrlPost && urlPostTask is not null && urlPostTask.Status == TaskStatus.RanToCompletion) _lastUrlPostStatus = urlPostTask.Result;
                    if (dueTask && schedTask is not null && schedTask.Status == TaskStatus.RanToCompletion) _lastTaskLogs = schedTask.Result;
                    if (dueServe && serveTask is not null && serveTask.Status == TaskStatus.RanToCompletion) _lastServeStatus = serveTask.Result;

                    await WriteSnapshotAsync(hostIp);
                }
                catch (TaskCanceledException) { /* 正常結束 */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "分時段排程執行失敗");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        // ======= 寫檔 + 傳輸 =======
        private async Task WriteSnapshotAsync(string hostIp)
        {
            try
            {
                string localPath = CreateFile(_localPath);

                var timestamp = DateTime.Now;
                var logFileName = $"SM_{hostIp}_{timestamp:yyyyMMdd}.json";
                var localFilePath = Path.Combine(localPath, logFileName);

                string sharedPath = null;
                string sharedFilePath = null;

                if (UseFolderMode)
                {
                    sharedPath = CreateFile(_sharedPath);
                    sharedFilePath = Path.Combine(sharedPath, Path.GetFileName(localFilePath));
                }

                // 加入Post URL資料
                foreach (var kvp in _lastUrlPostStatus)
                {
                    _lastUrlStatus.TryAdd(kvp.Key, kvp.Value);
                }

                var systemMonitorJson = new SystemMonitorJson()
                {
                    HostIp = hostIp,
                    Timestamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    CpuUsage = $"{_lastCpuUsage:0.00}%",
                    MemoryUsage = $"{_lastRamUsage:0.00}%",
                    DiskUsage = _lastDiskUsage.Select(i => new DiskUsageEntry { Drive = i.Key, Usage = $"{i.Value:0.00}%" }).ToList(),
                    UrlStatus = _lastUrlStatus.Select(i => new UrlStatusEntry { Url = i.Key, Status = i.Value.Item1, Message = i.Value.Item2 }).ToList(),
                    TaskLogs = _lastTaskLogs.Select(i => new TaskLogEntry { Name = i.Key, Status = i.Value.Item1, Status_Text = i.Value.Item2 }).ToList(),
                    ServeLogs = _lastServeStatus.Select(i => new ServeLogEntry { Name = i.Key, Status = i.Value.Item1, Status_Text = i.Value.Item2 }).ToList(),
                };

                _logger.LogInformation(
                    "寫入快照：URLs={UrlCount}, Tasks={TaskCount}, Services={SvcCount}",
                    systemMonitorJson.UrlStatus.Count,
                    systemMonitorJson.TaskLogs.Count,
                    systemMonitorJson.ServeLogs.Count
                );

                if (!Directory.Exists(localPath))
                    return;

                // === 1. 先讀 Shared（Folder 模式）拿 NotifyRecord ===
                SystemMonitor systemMonitorShared = null;
                if (UseFolderMode && !string.IsNullOrEmpty(sharedFilePath))
                {
                    systemMonitorShared = await ReadJsonFileAsync(sharedFilePath);
                }

                // === 2. 讀取本機 JSON，加入這次紀錄 ===
                SystemMonitor systemMonitor = await ReadJsonFileAsync(localFilePath);
                systemMonitor.JsonList ??= new List<SystemMonitorJson>();
                systemMonitor.JsonList.Add(systemMonitorJson);
                systemMonitor.NotifyInterval = _notifyInterval;

                if (systemMonitorShared != null &&
                    systemMonitorShared.NotifyRecord != null &&
                    systemMonitorShared.NotifyRecord.Count > 0)
                {
                    systemMonitor.NotifyRecord = systemMonitorShared.NotifyRecord;
                }

                await WriteJsonFileAsync(localFilePath, systemMonitor);

                // === 3. Folder 模式：同步到 SharedPath 並做數量裁剪 ===
                if (UseFolderMode &&
                    !string.IsNullOrEmpty(sharedPath) &&
                    Directory.Exists(sharedPath) &&
                    !string.IsNullOrEmpty(sharedFilePath))
                {
                    SystemMonitor forShared = await ReadJsonFileAsync(localFilePath);
                    SystemMonitor newData = GetDataByQuantity(forShared);
                    await WriteJsonFileAsync(sharedFilePath, newData);
                }

                // === 4. 刪除舊資料夾 ===
                string result_delete = DeleteOldFolders();
                if (!string.IsNullOrEmpty(result_delete))
                    _logger.LogInformation(result_delete);

                // === 5. API 模式：傳到中央 API ===
                if (UseApiMode)
                {
                    await SendToCentralApiAsync(hostIp, timestamp.Date, systemMonitor);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WriteSnapshotAsync 寫入或傳送時發生例外");
            }
        }

        private async Task SendToCentralApiAsync(string hostIp, DateTime date, SystemMonitor data)
        {
            if (!_useCentralApi)
            {
                _logger.LogDebug("UseCentralApi=0，略過傳送到中央 API。");
                return;
            }

            if (string.IsNullOrWhiteSpace(_centralApiBaseUrl))
            {
                _logger.LogWarning("CentralApiBaseUrl 未設定，無法傳送到中央 API。");
                return;
            }

            try
            {
                string baseUrl = _centralApiBaseUrl.TrimEnd('/');

                // /monitor/upload?hostIp=...&date=...
                string url = $"{baseUrl}?hostIp={WebUtility.UrlEncode(hostIp)}&date={date:yyyyMMdd}";

                var options = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                string json = JsonSerializer.Serialize(data, options);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = content
                };

                if (!string.IsNullOrEmpty(_centralApiToken))
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", _centralApiToken);
                }

                _logger.LogInformation("準備將監控資料 POST 至中央 API：{Url}", url);

                HttpResponseMessage resp = await _httpClient.SendAsync(request);

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("成功將監控資料傳送到中央 API。StatusCode={StatusCode}", resp.StatusCode);
                }
                else
                {
                    string respText = await resp.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "傳送到中央 API 失敗。StatusCode={StatusCode}, Response={Response}",
                        resp.StatusCode, respText
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "傳送監控資料到中央 API 發生例外");
            }
        }

        // ======= 路徑/JSON I-O =======
        private string CreateFile(string path)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            string dateFolderPath = Path.Combine(path, DateTime.Now.ToString("yyyyMMdd"));
            if (!Directory.Exists(dateFolderPath)) Directory.CreateDirectory(dateFolderPath);
            return dateFolderPath;
        }

        private async Task<SystemMonitor> ReadJsonFileAsync(string filePath)
        {
            if (!File.Exists(filePath)) return new SystemMonitor();
            try
            {
                using var fs = File.OpenRead(filePath);
                var obj = await JsonSerializer.DeserializeAsync<SystemMonitor>(fs);
                return obj ?? new SystemMonitor();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"讀取 JSON 檔案失敗: {filePath}, 錯誤: {ex.Message}");
                return new SystemMonitor();
            }
        }

        private async Task WriteJsonFileAsync(string filePath, SystemMonitor data)
        {
            using var fs = File.Create(filePath);
            await JsonSerializer.SerializeAsync(fs, data, new JsonSerializerOptions { WriteIndented = true });
        }

        // ======= 硬體（已含 Log） =======
        private async Task UpdateSystemMetricsAsync()
        {
            float cpuUsage = _cpuCounter.NextValue();
            float availableRam = _ramCounter.NextValue();
            double totalMemory = GetTotalMemory();
            double ramUsage = (totalMemory > 0) ? ((totalMemory - availableRam) / totalMemory) * 100 : 0;

            _lastCpuUsage = cpuUsage;
            _lastRamUsage = ramUsage;

            _lastDiskUsage = GetDiskUsage();

            _logger.LogInformation($"CPU 使用率: {cpuUsage:0.00}%");
            _logger.LogInformation($"記憶體使用率: {ramUsage:0.00}%");
            foreach (var d in _lastDiskUsage)
                _logger.LogInformation($"磁碟 {d.Key} 使用率 {d.Value:0.00}%");

            await Task.CompletedTask;
        }

        private static double GetTotalMemory()
        {
            try
            {
                var query = new ObjectQuery("SELECT * FROM Win32_ComputerSystem");
                using var searcher = new ManagementObjectSearcher(query);
                foreach (var obj in searcher.Get())
                    return Convert.ToDouble(obj["TotalPhysicalMemory"]) / (1024 * 1024);
                return 0;
            }
            catch { return 0; }
        }

        private static Dictionary<string, double> GetDiskUsage()
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .ToDictionary(
                        d => d.Name,
                        d => (1 - (double)d.AvailableFreeSpace / d.TotalSize) * 100
                    );
            }
            catch { return new Dictionary<string, double>(); }
        }

        // ======= URL 監測（加 Log） =======
        public async Task<Dictionary<string, (int, string)>> GetUrlStatus()
        {
            var result = new Dictionary<string, (int, string)>();

            if (_urlHost == 0)
            {
                _logger.LogInformation("[URL] UrlHost=0，略過 URL 檢測。");
                return await Task.FromResult(result);
            }
            if (url_list.Length == 0)
            {
                _logger.LogInformation("[URL] 清單為空，無需檢測。");
                return await Task.FromResult(result);
            }

            _logger.LogInformation($"[URL] 開始檢測 {url_list.Length} 個 URL。");
            foreach (var url in url_list)
            {
                try
                {
                    HttpResponseMessage response = await _httpClient.GetAsync(url);
                    result[url] = ((int)response.StatusCode, "Success");
                    _logger.LogInformation($"[URL] {url} → {(int)response.StatusCode}");
                }
                catch (Exception ex)
                {
                    result[url] = (0, ex.Message);
                    _logger.LogInformation($"[URL] {url} → 失敗：{ex.Message}");
                }
            }
            _logger.LogInformation($"[URL] 完成，成功 {result.Count(kv => kv.Value.Item1 != 0)} / {result.Count}。");
            return result;
        }

        public async Task<Dictionary<string, (int, string)>> PostUrlStatus()
        {
            var result = new Dictionary<string, (int, string)>();

            if (_urlHost == 0)
            {
                _logger.LogInformation("[URL] UrlHost=0，略過 URL 檢測。");
                return await Task.FromResult(result);
            }

            Dictionary<string, string> url_post_list = new Dictionary<string, string>();
            url_post_list.Add(_ePayUrl1, "{ SiteNo = \"63\", UserNo = \"41637536\", CheckNo = \"K\", SiteId = 0, NodeId = 0, IsTest = \"string\" }");
            url_post_list.Add(_ePayUrl2, "{ SiteNo = \"63\", UserNo = \"41637536\", CheckNo = \"K\", SiteId = 0, NodeId = 0, IsTest = \"string\" }");

            foreach (string url in url_post_list.Keys)
            {
                try
                {
                    HttpResponseMessage response;
                    var content = new StringContent(JsonSerializer.Serialize(url_post_list[url].ToString()), Encoding.UTF8, "application/json");
                    response = await _httpClient.PostAsync(url, content);
                    result[url] = ((int)response.StatusCode, "Success");
                    _logger.LogInformation($"[URL] {url} → {(int)response.StatusCode}");
                }
                catch (Exception ex)
                {
                    result[url] = (0, ex.Message);
                    _logger.LogInformation($"[URL] {url} → 失敗：{ex.Message}");
                }
            }
            _logger.LogInformation($"[URL] 完成，成功 {result.Count(kv => kv.Value.Item1 != 0)} / {result.Count}。");
            return result;
        }

        // ======= Windows 工作排程（加 Log） =======
        public async Task<Dictionary<string, (string, string)>> GetTaskSchedulerLogsAsync()
        {
            var logs = new Dictionary<string, (string, string)>();
            if (_taskHost == 0)
            {
                _logger.LogInformation("[Task] TaskHost=0，略過工作排程檢測。");
                return await Task.FromResult(logs);
            }

            try
            {
                using var ts = new TaskScheduler.TaskService();
                TaskScheduler.TaskFolder folder = ts.GetFolder(_taskPath);
                if (folder == null)
                {
                    _logger.LogInformation($"[Task] 找不到資料夾：{_taskPath}");
                    return await Task.FromResult(logs);
                }

                _logger.LogInformation($"[Task] 開始檢測資料夾 {_taskPath} 下的 {folder.Tasks.Count} 個任務。");
                foreach (var task in folder.Tasks)
                {
                    string status = GetTaskStateDescription(task.State) + "[" + GetErrorMessage(task.LastTaskResult) + "]";
                    string status_text = task.State.ToString();
                    logs[task.Name] = (status, status_text);

                    _logger.LogInformation($"[Task] {task.Name} → {status_text} | {status}");
                }
                _logger.LogInformation($"[Task] 完成，總數 {logs.Count}。");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Task] 讀取工作排程失敗: {ex.Message}");
            }

            return await Task.FromResult(logs);
        }

        private string GetTaskStateDescription(TaskScheduler.TaskState state) => state switch
        {
            TaskScheduler.TaskState.Unknown => "未知",
            TaskScheduler.TaskState.Disabled => "已停用",
            TaskScheduler.TaskState.Queued => "排隊中",
            TaskScheduler.TaskState.Ready => "就緒",
            TaskScheduler.TaskState.Running => "正在運行",
            _ => "未定義"
        };

        private string GetErrorMessage(int errorCode)
        {
            try { return new Win32Exception(errorCode).Message; }
            catch { return $"未知錯誤（錯誤碼: {errorCode}）"; }
        }

        // ======= 服務監測（加 Log + ServeHost 開關） =======
        public async Task<Dictionary<string, (string, string)>> GetServiceStatusAsync()
        {
            var result = new Dictionary<string, (string, string)>();

            if (_serveHost == 0)
            {
                _logger.LogInformation("[Service] ServeHost=0，略過服務檢測。");
                return await Task.FromResult(result);
            }
            if (_serveList == null || _serveList.Length == 0)
            {
                _logger.LogInformation("[Service] 清單為空，無需檢測。");
                return await Task.FromResult(result);
            }

            ServiceController[] all = Array.Empty<ServiceController>();
            try
            {
                all = ServiceController.GetServices();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Service] 讀取服務列表失敗: {ex.Message}");
                return await Task.FromResult(result);
            }

            _logger.LogInformation($"[Service] 開始檢測 {_serveList.Length} 個服務。");
            foreach (var nameRaw in _serveList)
            {
                var name = (nameRaw ?? "").Trim();
                if (string.IsNullOrEmpty(name)) continue;

                try
                {
                    var svc = all.FirstOrDefault(s =>
                        string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));

                    if (svc == null)
                    {
                        result[name] = ("未找到服務", "NotFound");
                        _logger.LogInformation($"[Service] {name} → NotFound");
                        continue;
                    }

                    string statusText = svc.Status.ToString();
                    string zh = svc.Status switch
                    {
                        ServiceControllerStatus.Running => "運行中",
                        ServiceControllerStatus.Stopped => "已停止",
                        ServiceControllerStatus.Paused => "已暫停",
                        ServiceControllerStatus.StartPending => "啟動中",
                        ServiceControllerStatus.StopPending => "停止中",
                        ServiceControllerStatus.ContinuePending => "繼續中",
                        ServiceControllerStatus.PausePending => "暫停中",
                        _ => "未知"
                    };

                    result[svc.ServiceName] = (zh, statusText);
                    _logger.LogInformation($"[Service] {svc.ServiceName} → {statusText} | {zh}");
                }
                catch (Exception ex)
                {
                    result[name] = ($"讀取狀態失敗: {ex.Message}", "Error");
                    _logger.LogInformation($"[Service] {name} → Error: {ex.Message}");
                }
            }
            _logger.LogInformation($"[Service] 完成，總數 {result.Count}。");
            return await Task.FromResult(result);
        }

        // ======= 其他工具 =======
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

        private string GetLocalIp()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());

                // 優先非 127.0.0.1 的 IPv4
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        ip.ToString() != "127.0.0.1")
                    {
                        return ip.ToString();
                    }
                }

                // 找不到就退回任何一個 IPv4
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.ToString();
                }

                return "UnknownIP";
            }
            catch
            {
                return "UnableToRetrieveIP";
            }
        }

        private string DeleteOldFolders()
        {
            string result = "";

            string[] pathList;
            if (UseFolderMode && _sharedHost != 0)
                pathList = new[] { _localPath, _sharedPath };
            else
                pathList = new[] { _localPath };

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
                        if (DateTime.TryParseExact(
                                folderName,
                                "yyyyMMdd",
                                null,
                                System.Globalization.DateTimeStyles.None,
                                out var folderDate))
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
                result = $"Error deleting folders: {ex.Message}";
            }

            return result;
        }
    }
}
