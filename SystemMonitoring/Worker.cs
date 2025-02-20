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
            // ��l�Ʃʯ�p�ƾ�
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
                // �T�O Log ���|�s�b
                if (Directory.Exists(_localPath) == false)
                {
                    Directory.CreateDirectory(_localPath);
                }

                // �ͦ������Ƨ�
                string dateFolder = DateTime.Now.ToString("yyyyMMdd");
                string dateFolderPath = Path.Combine(_localPath, dateFolder);
                if (Directory.Exists(dateFolderPath) == false)
                {
                    Directory.CreateDirectory(dateFolderPath);
                }

                // ���o���� IP
                string hostIp = GetLocalIp();
                // ���o�O����Ŷ�
                var totalMemory = GetTotalMemory();

                while (!stoppingToken.IsCancellationRequested)
                {
                    // �ʴ�
                    float cpuUsage = _cpuCounter.NextValue();
                    float availableRam = _ramCounter.NextValue();
                    double ramUsage = ((totalMemory - availableRam) / totalMemory) * 100; // �p��O����ϥβv
                    Dictionary<string, double> diskUsage = GetDiskUsage();
                    Dictionary<string, int> urlStatus = GetUrlStatus().Result;
                    Dictionary<string, string> taskLogs = await GetTaskSchedulerLogsAsync();
                    List<string> notifyRecord = new List<string>();
                    // �ʴ�

                    // �����ɮ׫e�A���ˬd��Ƨ��O�_�s�b
                    // URL�ШD�����ɶ��A�Y�o�q�ɶ���q�R����Ƨ��|�����D
                    if (Directory.Exists(dateFolderPath) == false)
                    {
                        Directory.CreateDirectory(dateFolderPath);
                    }

                    // �ʺA�ͦ� Log �ɮצW��
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
                    // �ʺA�ͦ� Log �ɮצW��

                    // ��s�@�ɸ�Ƨ�Json��
                    string result_transmit = TransmitJsonToTarget(logFilePath, logJson);
                    // �R���L���ɮ�
                    string result_delete = DeleteOldFolders();

                    _logger.LogInformation($"CPU �ϥβv: {cpuUsage:0.00}%");
                    _logger.LogInformation($"�O����ϥβv: {ramUsage:0.00}%");

                    foreach (var disk in diskUsage)
                    {
                        _logger.LogInformation($"�Ϻ� {disk.Key}, �ϥβv {disk.Value:0.00}%");
                    }

                    foreach (var url in urlStatus)
                    {
                        _logger.LogInformation($"���} {url.Key}, ���A {url.Value}");
                    }

                    foreach (var log in taskLogs)
                    {
                        _logger.LogInformation($"�Ƶ{ {log.Key}, ���A {log.Value}");
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
        /// ���o�O������
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
                        return Convert.ToDouble(obj["TotalPhysicalMemory"]) / (1024 * 1024); // �ഫ�� MB
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
        /// ���o�w�и��
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
        /// ���o���}���A
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

            // ���}�X��
            for (int i = 0; url_list.Length > i; i++)
            {
                url = url_list[i];
                try
                {
                    // �������ҿ��~
                    HttpClientHandler handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                    };

                    using (var client = new HttpClient(handler))
                    {
                        // �]�m�W�ɮɶ��]�i�ھڻݭn�վ�^
                        client.Timeout = TimeSpan.FromSeconds(10);

                        // �o�e GET �ШD
                        HttpResponseMessage response = await client.GetAsync(url);

                        // �p�G��^���\���A�X�A�N��s�J���G
                        result[url] = (int)response.StatusCode;
                    }
                }
                catch (Exception ex)
                {
                    // �p�G����첧�`�A�]�m���L�k�s�u�����A�X�]�Ҧp 500�^
                    result[url] = 500;
                }
            }

            // ��^�r�嵲�G
            return result;
        }

        /// <summary>
        /// ���o�u�@�Ƶ{
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
                    // ���o���w��Ƨ�
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
                TaskScheduler.TaskState.Unknown => "����",
                TaskScheduler.TaskState.Disabled => "�w����",
                TaskScheduler.TaskState.Queued => "�ƶ���",
                TaskScheduler.TaskState.Ready => "�N��",
                TaskScheduler.TaskState.Running => "���b�B��",
                _ => "���w�q"
            };
        }
        private string GetErrorMessage(int errorCode)
        {
            // ���ը��o���~�y�z
            try
            {
                return new Win32Exception(errorCode).Message;
            }
            catch
            {
                return $"�������~�]���~�X: {errorCode}�^";
            }
        }

        /// <summary>
        /// ��s�@�ɸ�Ƨ�Json��
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
                // �ѪR JSON �ɮ׬� JsonObject
                var jsonObject = JsonSerializer.Deserialize<SystemMonitorJson>(logJson);

                // �]�w�ؼХD�����@�ɸ�Ƨ�
                string targetFolderPath = _sharedPath;

                // �T�O�@�ɸ�Ƨ��s�b
                if (Directory.Exists(targetFolderPath) == false)
                {
                    result = $"Target folder not found: {targetFolderPath}";
                }

                // �ͦ������Ƨ�
                string dateFolder = DateTime.Now.ToString("yyyyMMdd");
                targetFolderPath = Path.Combine(targetFolderPath, dateFolder);
                if (Directory.Exists(targetFolderPath) == false)
                {
                    Directory.CreateDirectory(targetFolderPath);
                }

                // �u��s�̷s���@��
                string targetFilePath = Path.Combine(targetFolderPath, Path.GetFileName(localFilePath));
                if (File.Exists(targetFilePath) == true)
                {
                    UpdateShareJson(targetFilePath, logJson);
                }
                else
                {
                    File.AppendAllTextAsync(targetFilePath, logJson + Environment.NewLine);
                }

                // �ƻs�ɮר�ؼи�Ƨ�
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
        /// �ϥΥ����̷s�@��Json��s�@�ɸ�Ƨ���Json
        /// </summary>
        /// <param name="targetFilePath">�@�ɸ�Ƨ����|</param>
        /// <param name="logJson">����Json</param>
        public void UpdateShareJson(string targetFilePath, string logJson)
        {
            /// ���o JSON �ɮת����|
            string filePath = targetFilePath;

            // Ū�� JSON �ɮפ��e
            string jsonContent = File.ReadAllText(filePath);

            // �ѪR JSON �ɮ׬� JsonObject
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
        /// ���o����IP
        /// </summary>
        /// <returns></returns>
        private string GetLocalIp()
        {
            string result = "Unknown IP";
            try
            {
                // ���o���� IP
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
        /// �R���L���ɮ�
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

                        // ���Ҹ�Ƨ��W�٬O�_�ŦX����榡
                        if (DateTime.TryParseExact(folderName, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var folderDate))
                        {
                            if (folderDate <= targetDate)
                            {
                                try
                                {
                                    Directory.Delete(folder, true); // �R����Ƨ�
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
