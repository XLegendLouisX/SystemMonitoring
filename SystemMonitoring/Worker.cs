namespace SystemMonitoring
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Management;
    using System.Net;
    using System.Net.Http.Headers;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using TaskScheduler = Microsoft.Win32.TaskScheduler;
    using static SystemMonitorJson;

    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _ramCounter;

        private int _sharedHost = 0;
        private int _taskHost = 0;
        private int _urlHost = 0;

        private int _notifyInterval = 0;
        private int _runInterval = 5000;
        private int _storageDays = 60;
        private string _localPath;
        private string _sharedPath;
        private string _taskPath;
        private string[] url_list = { };

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            // 初始化性能計數器
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");

            _sharedHost = configuration.GetValue<int>("WorkerSettings:SharedHost");
            _taskHost = configuration.GetValue<int>("WorkerSettings:TaskHost");
            _urlHost = configuration.GetValue<int>("WorkerSettings:UrlHost");

            _notifyInterval = configuration.GetValue<int>("WorkerSettings:NotifyInterval");
            _runInterval = configuration.GetValue<int>("WorkerSettings:RunInterval");
            _storageDays = configuration.GetValue<int>("WorkerSettings:StorageDays");
            _localPath = configuration.GetValue<string>("WorkerSettings:LocalPath");
            _sharedPath = configuration.GetValue<string>("WorkerSettings:SharedPath");
            _taskPath = configuration.GetValue<string>("WorkerSettings:TaskPath");
            url_list = configuration.GetSection("WorkerSettings:HttpUrls").Get<string[]>();

            if (_notifyInterval < 0)
            {
                _notifyInterval = 0;
            }

            if (_runInterval <= 1000)
            {
                _runInterval = 1000;
            }

            if (_storageDays < 1)
            {
                _storageDays = 1;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // 確保 Log 路徑存在
                if (Directory.Exists(_localPath) == false)
                {
                    Directory.CreateDirectory(_localPath);
                }

                // 生成日期資料夾
                string dateFolder = DateTime.Now.ToString("yyyyMMdd");
                string dateFolderPath = Path.Combine(_localPath, dateFolder);
                if (Directory.Exists(dateFolderPath) == false)
                {
                    Directory.CreateDirectory(dateFolderPath);
                }

                // 取得本機 IP
                string hostIp = GetLocalIp();
                // 取得記憶體空間
                var totalMemory = GetTotalMemory();

                while (!stoppingToken.IsCancellationRequested)
                {
                    // 監測
                    float cpuUsage = _cpuCounter.NextValue();
                    float availableRam = _ramCounter.NextValue();
                    double ramUsage = ((totalMemory - availableRam) / totalMemory) * 100; // 計算記憶體使用率
                    Dictionary<string, double> diskUsage = GetDiskUsage();
                    Dictionary<string, int> urlStatus = GetUrlStatus().Result;
                    Dictionary<string, string> taskLogs = await GetTaskSchedulerLogsAsync();
                    List<string> notifyRecord = new List<string>();
                    // 監測

                    // 產生檔案前再次檢查資料夾是否存在
                    // URL請求比較花時間，若這段時間手段刪除資料夾會有問題
                    if (Directory.Exists(dateFolderPath) == false)
                    {
                        Directory.CreateDirectory(dateFolderPath);
                    }

                    // 動態生成 Log 檔案名稱
                    var timestamp = DateTime.Now;
                    var logFileName = $"SM_{hostIp}_{timestamp:yyyyMMdd}.json";
                    var logFilePath = Path.Combine(dateFolderPath, logFileName);
                    var logEntry = new SystemMonitorJson()
                    {
                        HostIp = hostIp,
                        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        CpuUsage = $"{cpuUsage:0.00}%",
                        MemoryUsage = $"{ramUsage:0.00}%",
                        DiskUsage = diskUsage.Select(i => new DiskUsageEntry { Drive = i.Key, Usage = $"{i.Value:0.00}%" }).ToList(),
                        UrlStatus = urlStatus.Select(i => new UrlStatusEntry { Url = i.Key, Status = i.Value }).ToList(),
                        TaskLogs = taskLogs.Select(i => new TaskLogEntry { Name = i.Key, Status = i.Value }).ToList(),
                        NotifyInterval = _notifyInterval,
                        NotifyRecord = notifyRecord
                    };
                    var logJson = logEntry.ToJson();
                    await File.AppendAllTextAsync(logFilePath, logJson + Environment.NewLine, stoppingToken);
                    // 動態生成 Log 檔案名稱

                    // 更新共享資料夾Json檔
                    string result_transmit = TransmitJsonToTarget(logFilePath, logJson);
                    // 刪除過期檔案
                    string result_delete = DeleteOldFolders();

                    _logger.LogInformation($"CPU 使用率: {cpuUsage:0.00}%");
                    _logger.LogInformation($"記憶體使用率: {ramUsage:0.00}%");

                    foreach (var disk in diskUsage)
                    {
                        _logger.LogInformation($"磁碟 {disk.Key}, 使用率 {disk.Value:0.00}%");
                    }

                    foreach (var url in urlStatus)
                    {
                        _logger.LogInformation($"網址 {url.Key}, 狀態 {url.Value}");
                    }

                    foreach (var log in taskLogs)
                    {
                        _logger.LogInformation($"排程 {log.Key}, 狀態 {log.Value}");
                    }

                    _logger.LogInformation(result_transmit);
                    _logger.LogInformation(result_delete);

                    await Task.Delay(_runInterval, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// 取得記憶體資料
        /// </summary>
        /// <returns></returns>
        private static double GetTotalMemory()
        {
            try
            {
                var query = new ObjectQuery("SELECT * FROM Win32_ComputerSystem");
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return Convert.ToDouble(obj["TotalPhysicalMemory"]) / (1024 * 1024); // 轉換為 MB
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                return 0;
            }
        }

        /// <summary>
        /// 取得硬碟資料
        /// </summary>
        /// <returns></returns>
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
            catch (Exception ex)
            {
                return new Dictionary<string, double>();
            }
        }

        /// <summary>
        /// 取得網址狀態
        /// </summary>
        /// <returns></returns>
        public async Task<Dictionary<string, int>> GetUrlStatus()
        {
            string url = "";
            Dictionary<string, int> result = new Dictionary<string, int>();
            if (_urlHost == 0)
            {
                return await Task.FromResult(result);
            }

            // 網址訪問
            for (int i = 0; url_list.Length > i; i++)
            {
                url = url_list[i];
                try
                {
                    // 忽略憑證錯誤
                    HttpClientHandler handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                    };

                    using (var client = new HttpClient(handler))
                    {
                        // 設置超時時間（可根據需要調整）
                        client.Timeout = TimeSpan.FromSeconds(10);

                        // 發送 GET 請求
                        HttpResponseMessage response = await client.GetAsync(url);

                        // 如果返回成功狀態碼，將其存入結果
                        result[url] = (int)response.StatusCode;
                    }
                }
                catch (Exception ex)
                {
                    // 如果捕獲到異常，設置為無法連線的狀態碼（例如 500）
                    result[url] = 500;
                }
            }

            // 返回字典結果
            return result;
        }

        /// <summary>
        /// 取得工作排程
        /// </summary>
        /// <returns></returns>
        public async Task<Dictionary<string, string>> GetTaskSchedulerLogsAsync()
        {
            string result = "";
            Dictionary<string, string> logs = new Dictionary<string, string>();
            if (_taskHost == 0)
            {
                return await Task.FromResult(logs);
            }

            try
            {
                using (TaskScheduler.TaskService ts = new TaskScheduler.TaskService())
                {
                    // 取得指定資料夾
                    TaskScheduler.TaskFolder folder = ts.GetFolder(_taskPath);
                    if (folder != null)
                    {
                        foreach (var task in folder.Tasks)
                        {
                            logs[task.Name] = GetTaskStateDescription(task.State) + "[" + GetErrorMessage(task.LastTaskResult) + "]";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result = $"Error reading task scheduler logs: {ex.Message}";
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
            // 嘗試取得錯誤描述
            try
            {
                return new Win32Exception(errorCode).Message;
            }
            catch
            {
                return $"未知錯誤（錯誤碼: {errorCode}）";
            }
        }

        /// <summary>
        /// 更新共享資料夾Json檔
        /// </summary>
        /// <param name="localFilePath"></param>
        /// <returns></returns>
        private string TransmitJsonToTarget(string localFilePath,
                                            string logJson
                                            )
        {
            string result = "";
            try
            {
                // 解析 JSON 檔案為 JsonObject
                var jsonObject = JsonSerializer.Deserialize<SystemMonitorJson>(logJson);

                // 設定目標主機的共享資料夾
                string targetFolderPath = _sharedPath;

                // 確保共享資料夾存在
                if (Directory.Exists(targetFolderPath) == false)
                {
                    result = $"Target folder not found: {targetFolderPath}";
                }

                // 生成日期資料夾
                string dateFolder = DateTime.Now.ToString("yyyyMMdd");
                targetFolderPath = Path.Combine(targetFolderPath, dateFolder);
                if (Directory.Exists(targetFolderPath) == false)
                {
                    Directory.CreateDirectory(targetFolderPath);
                }

                // 只更新最新的一筆
                string targetFilePath = Path.Combine(targetFolderPath, Path.GetFileName(localFilePath));
                if (File.Exists(targetFilePath) == true)
                {
                    UpdateShareJson(targetFilePath, logJson);
                }
                else
                {
                    File.AppendAllTextAsync(targetFilePath, logJson + Environment.NewLine);
                }

                // 複製檔案到目標資料夾
                //string targetFilePath = Path.Combine(targetFolderPath, Path.GetFileName(localFilePath));
                //File.Copy(localFilePath, targetFilePath, overwrite: true);

                result = $"JSON file transmitted to: {targetFilePath}";
            }
            catch (Exception ex)
            {
                result = $"Error transmitting JSON to target: {ex.Message}";
            }
            return result;
        }

        /// <summary>
        /// 使用本機最新一筆Json更新共享資料夾的Json
        /// </summary>
        /// <param name="targetFilePath">共享資料夾路徑</param>
        /// <param name="logJson">本機Json</param>
        public void UpdateShareJson(string targetFilePath, string logJson)
        {
            /// 取得 JSON 檔案的路徑
            string filePath = targetFilePath;

            // 讀取 JSON 檔案內容
            string jsonContent = File.ReadAllText(filePath);

            // 解析 JSON 檔案為 JsonObject
            var loaclJson = JsonSerializer.Deserialize<SystemMonitorJson>(logJson);
            var shareJson = JsonSerializer.Deserialize<SystemMonitorJson>(jsonContent);

            if (shareJson != null)
            {
                List<string> notifyRecordList = shareJson.GetNotifyRecord();
                loaclJson.NotifyRecord = notifyRecordList;

                var logEntry = new SystemMonitorJson()
                {
                    HostIp = loaclJson.HostIp,
                    Timestamp = loaclJson.Timestamp,
                    CpuUsage = loaclJson.CpuUsage,
                    MemoryUsage = loaclJson.MemoryUsage,
                    DiskUsage = loaclJson.DiskUsage,
                    UrlStatus = loaclJson.UrlStatus,
                    TaskLogs = loaclJson.TaskLogs,
                    NotifyInterval = loaclJson.NotifyInterval,
                    NotifyRecord = loaclJson.NotifyRecord
                };
                string updatedJson = logEntry.ToJson();
                File.WriteAllText(filePath, updatedJson);
            }
        }

        /// <summary>
        /// 取得本機IP
        /// </summary>
        /// <returns></returns>
        private string GetLocalIp()
        {
            string result = "Unknown IP";
            try
            {
                // 取得本機 IP
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch
            {
                result = "Unable to retrieve local IP address.";
            }
            return result;
        }

        /// <summary>
        /// 刪除過期檔案
        /// </summary>
        /// <returns></returns>
        private string DeleteOldFolders()
        {
            string result = "";
            string[] pathList = { };

            if (_sharedHost == 0)
            {
                pathList = new string[] { _localPath };
            }
            else
            {
                pathList = new string[] { _localPath, _sharedPath };
            }

            try
            {
                for (int i = 0; pathList.Length > i; i++)
                {
                    string path = pathList[i];

                    if (Directory.Exists(path) == false)
                    {
                        result += "folder does not exist: {" + path + "}\n";
                        continue;
                    }

                    var targetDate = DateTime.Today.AddDays(-_storageDays);
                    var folders = Directory.GetDirectories(path);

                    foreach (var folder in folders)
                    {
                        var folderName = Path.GetFileName(folder);

                        // 驗證資料夾名稱是否符合日期格式
                        if (DateTime.TryParseExact(folderName, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var folderDate))
                        {
                            if (folderDate <= targetDate)
                            {
                                try
                                {
                                    Directory.Delete(folder, true); // 刪除資料夾
                                    result += "Deleted folder: {" + folder + "}\n";
                                }
                                catch (Exception ex)
                                {
                                    result += "Failed to delete folder: {" + folder + "}," + ex.Message + "\n";
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
