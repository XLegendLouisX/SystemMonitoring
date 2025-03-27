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
        private int _sharedJsonTotal = 0;
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
            _sharedJsonTotal = configuration.GetValue<int>("WorkerSettings:SharedJsonTotal");
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

            if (_sharedJsonTotal < 0)
            {
                _sharedJsonTotal = 0;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // 取得本機 IP
                string hostIp = GetLocalIp();

                // CPU第一次讀取需要花點時間，這邊先讀取一次，避免後面取值為0
                _cpuCounter.NextValue();
                // 等待1秒
                await Task.Delay(1000, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    string result_delete = "";

                    string loaclPath = CreateFile(_localPath);
                    string sharedPath = CreateFile(_sharedPath);

                    // 監測
                    float cpuUsage = _cpuCounter.NextValue();
                    float availableRam = _ramCounter.NextValue();
                    double totalMemory = GetTotalMemory(); // 取得記憶體空間
                    double ramUsage = ((totalMemory - availableRam) / totalMemory) * 100; // 計算記憶體使用率
                    Dictionary<string, double> diskUsage = GetDiskUsage();
                    Dictionary<string, int> urlStatus = GetUrlStatus().Result;
                    Dictionary<string, string> taskLogs = await GetTaskSchedulerLogsAsync();
                    List<string> notifyRecord = new List<string>();
                    // 監測

                    // 動態生成 Log 檔案名稱
                    var timestamp = DateTime.Now;
                    var logFileName = $"SM_{hostIp}_{timestamp:yyyyMMdd}.json";
                    var loaclFilePath = Path.Combine(loaclPath, logFileName);
                    var sharedFilePath = Path.Combine(sharedPath, Path.GetFileName(loaclFilePath));

                    SystemMonitorJson systemMonitorJson = new SystemMonitorJson()
                    {
                        HostIp = hostIp,
                        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        CpuUsage = $"{cpuUsage:0.00}%",
                        MemoryUsage = $"{ramUsage:0.00}%",
                        DiskUsage = diskUsage.Select(i => new DiskUsageEntry { Drive = i.Key, Usage = $"{i.Value:0.00}%" }).ToList(),
                        UrlStatus = urlStatus.Select(i => new UrlStatusEntry { Url = i.Key, Status = i.Value }).ToList(),
                        TaskLogs = taskLogs.Select(i => new TaskLogEntry { Name = i.Key, Status = i.Value }).ToList(),
                    };

                    // 產生檔案前再次檢查資料夾是否存在
                    // URL請求比較花時間，若這段時間手段刪除資料夾會有問題
                    if (Directory.Exists(loaclPath) == true)
                    {
                        //==========本機==========//
                        // 讀取現有資料
                        SystemMonitor systemMonitor = await ReadJsonFileAsync(loaclFilePath);
                        // 新增資料到現有資料中
                        systemMonitor.JsonList.Add(systemMonitorJson);
                        systemMonitor.NotifyInterval = _notifyInterval;
                        systemMonitor.NotifyRecord = notifyRecord;
                        // 寫回檔案
                        await WriteJsonFileAsync(loaclFilePath, systemMonitor);
                        //==========本機==========//

                        //==========遠端==========//
                        if (Directory.Exists(sharedPath) == true)
                        {
                            // 讀取現有資料
                            systemMonitor = await ReadJsonFileAsync(loaclFilePath);
                            // 更新共享資料夾Json檔
                            SystemMonitor newData = GetDataByQuantity(systemMonitor);
                            // 寫回檔案
                            await WriteJsonFileAsync(sharedFilePath, newData);
                            // 刪除過期檔案
                            result_delete = DeleteOldFolders();
                        }
                        //==========遠端==========//
                    }

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
        /// 建立Json路徑
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private string CreateFile(string path)
        {
            string newPath = path;

            // 確保 Log 路徑存在
            if (Directory.Exists(path) == false)
            {
                Directory.CreateDirectory(path);
            }

            // 生成日期資料夾
            string dateFolder = DateTime.Now.ToString("yyyyMMdd");
            string dateFolderPath = Path.Combine(path, dateFolder);
            if (Directory.Exists(dateFolderPath) == false)
            {
                Directory.CreateDirectory(dateFolderPath);
            }
            newPath = dateFolderPath;

            return newPath;
        }

        /// <summary>
        /// 讀取Json檔
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        private async Task<SystemMonitor> ReadJsonFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new SystemMonitor();
            }

            using (FileStream fs = File.OpenRead(filePath))
            {
                return await JsonSerializer.DeserializeAsync<SystemMonitor>(fs);
            }
        }

        /// <summary>
        /// 寫入Json檔
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task WriteJsonFileAsync(string filePath, SystemMonitor data)
        {
            using (FileStream fs = File.Create(filePath))
            {
                await JsonSerializer.SerializeAsync(fs, data, new JsonSerializerOptions { WriteIndented = true });
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
        private SystemMonitor GetDataByQuantity(SystemMonitor loaclSystemMonitor)
        {
            string result = "";
            SystemMonitor systemMonitor = new SystemMonitor();
            try
            {
                systemMonitor = loaclSystemMonitor;
                if (_sharedJsonTotal > 0)
                {
                    List<SystemMonitorJson> newJsonList = new List<SystemMonitorJson>();
                    for (int i = systemMonitor.JsonList.Count; systemMonitor.JsonList.Count < i; i--)
                    {
                        if (i == systemMonitor.JsonList.Count)
                        {
                            newJsonList.Add(systemMonitor.JsonList[i]);
                        }
                    }
                    systemMonitor.JsonList = newJsonList;
                }

                return systemMonitor;
            }
            catch (Exception ex)
            {
                result = $"Error: {ex.Message}";
            }
            return systemMonitor;
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
