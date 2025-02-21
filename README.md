這是針對內部網路的主機監控程式，主機作業系統須為Windows，可以在背景執行。
監控結果以Json檔做為紀錄，可以另外建立專案取得Json檔做顯示。
也可以直接執行，即時監控結果會在命令提示字元視窗顯示。

功能包含:
1.CPU、記憶體、磁碟空間監測
2.網址監測(選擇性)
3.排程監測(選擇性)

注意:
本系統只提供監控數值，也就是Json檔，數值異常判斷和通知可在另一個專案執行。

開發環境:Visual Studio 2022
專案類型:Worker Service
程式語言:C#
Framework:.Net 8.0

使用說明:
Json檔目前會記錄在兩個路徑，一個是本機，另一個是共享資料夾。
本機會保留所有監控紀錄。
共享資料夾只會保留本機的最新一筆監控紀錄。

在發佈的檔案中找到appsettings.json檔，可進行設定路徑。
若只監控主機硬體情況，沒有要監控排程或網址，TaskHost和UrlHost設定為0即可。
若有需求可以設定為1，TaskPath可設定監控的排程路徑，HttpUrls可設定監控多個網址。

"WorkerSettings": {
  "SharedHost": 0, //是否為收集所有json檔的主機(0:否,1:是)(1會刪除SharedPath的資料)
  "TaskHost": 0, //是否監控排程(0:否,1:是)
  "UrlHost": 0, //是否監控URL(0:否,1:是)
  "RunInterval": 5000, //執行間隔(毫秒)
  "StorageDays": 60, //json保存天數
  "LocalPath": "D:\\SystemMonitoringLogs", //本機資料夾路徑
  "SharedPath": "\\\\{ip}\\Share", //共享資料夾路徑
  "TaskPath": "\\", //排程路徑
  "NotifyInterval": "1", //異常通知間隔(分鐘)(若不通知設定0)
  "HttpUrls": [
    "https://www.google.com.tw"
  ]
}

其他設定:
NotifyInterval在本系統無特定功用，不過可以提供其他專案做異常通知間隔判斷。

使用建議:
1.多台監控
將程式部署在需要監控的主機，然後其中一台的SharedHost設定為1。
那台主機的SharedPath可以取得多台主機的Json檔，可另外建立專案來顯示所有主機的監控情況。
2.一台監控
SharedHost設定為1。
SharedPath選擇本機路徑即可。

發佈設定:
組態:Release
目標 Framework:net8.0
目標執行階段:win-x86

發佈完成後，將win-x86的資料夾放在要監控的主機即可。
目標位置:
bin\release\net8.0\publish\win-x86
執行檔路徑:
bin\release\net8.0\publish\win-x86\SystemMonitoring.exe

建立背景執行:
命令提示字元(系統管理員)
1.建立服務
sc create "System Monitoring" binPath="執行檔路徑"
2.執行服務
sc start "System Monitoring"
備註:System Monitoring可在「開始」搜尋「服務」裡找到。

刪除背景執行:
命令提示字元(系統管理員)
1.停止服務
sc stop "System Monitoring"
2.刪除服務
sc delete "System Monitoring"
