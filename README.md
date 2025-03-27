# 主機監控系統

這是針對內部網路的主機監控程式，主機作業系統須為Windows，可以在背景執行。<br/>
監控結果以Json檔做為紀錄，可以另外建立專案取得Json檔做顯示。<br/>
也可以直接執行，即時監控結果會在命令提示字元視窗顯示。<br/>

### 功能包含:<br/>
1. CPU、記憶體、磁碟空間監測<br/>
2. 網址監測(選擇性)<br/>
3. 排程監測(選擇性)<br/>

### 注意:<br/>
本系統只提供監控數值，也就是Json檔，數值異常判斷和通知可在另一個專案執行。<br/>

開發環境:Visual Studio 2022<br/>
專案類型:Worker Service<br/>
程式語言:C#<br/>
Framework:.Net 8.0<br/>

### 使用說明:<br/>
Json檔目前會記錄在兩個路徑，一個是本機，另一個是共享資料夾。<br/>
本機會保留所有監控紀錄。<br/>
共享資料夾只會保留本機的最新一筆監控紀錄。<br/>

在發佈的檔案中找到```appsettings.json```檔，可進行設定路徑。<br/>
若只監控主機硬體情況，沒有要監控排程或網址，```TaskHost```和```UrlHost```設定為0即可。<br/>
若有需求可以設定為1，```TaskPath```可設定監控的排程路徑，```HttpUrls```可設定監控多個網址。<br/>

```
"WorkerSettings": {
  "SharedHost": 0, //是否為收集所有json檔的主機(0:否,1:是)(1會刪除SharedPath的資料)
  "TaskHost": 0, //是否監控排程(0:否,1:是)
  "UrlHost": 0, //是否監控URL(0:否,1:是)
  "RunInterval": 5000, //執行間隔(毫秒)
  "StorageDays": 60, //json保存天數
  "SharedJsonTotal": 0, //寫入共享資料夾Json數量(0:不限制)
  "LocalPath": "D:\\SystemMonitoringLogs", //本機資料夾路徑
  "SharedPath": "\\\\{ip}\\Share", //共享資料夾路徑
  "TaskPath": "\\", //排程路徑
  "NotifyInterval": "1", //異常通知間隔(分鐘)(若不通知設定0)
  "HttpUrls": [
    "https://www.google.com.tw"
  ]
}
```

### 其他設定:<br/>
```NotifyInterval```在本系統無特定功用，不過可以提供其他專案做異常通知間隔判斷。<br/>

### 使用建議:<br/>
1. 多台監控<br/>
將程式部署在需要監控的主機，然後其中一台的```SharedHost```設定為1。<br/>
那台主機的```SharedPath```可以取得多台主機的Json檔，可另外建立專案來顯示所有主機的監控情況。<br/>
2. 一台監控<br/>
```SharedHost```設定為1。<br/>
```SharedPath```選擇本機路徑即可。<br/>

### 發佈設定:<br/>
組態:Release<br/>
目標 Framework:net8.0<br/>
部署模式:獨立式 (在```顯示所有設定```裡面設定)<br/>
目標執行階段:win-x86<br/>

發佈完成後，將win-x86的資料夾放在要監控的主機即可。<br/>
目標位置:<br/>
```bin\release\net8.0\publish\win-x86```<br/>
執行檔路徑:<br/>
```bin\release\net8.0\publish\win-x86\SystemMonitoring.exe```<br/>

### 建立背景執行:<br/>
命令提示字元(系統管理員)<br/>
1. 建立服務<br/>
```sc create "System Monitoring" binPath="執行檔路徑" start=auto```<br/>
2. 執行服務<br/>
```sc start "System Monitoring"```<br/>
備註:System Monitoring可在「開始」搜尋「服務」裡找到。<br/>

### 刪除背景執行:<br/>
命令提示字元(系統管理員)<br/>
1. 停止服務<br/>
```sc stop "System Monitoring"```<br/>
2. 刪除服務<br/>
```sc delete "System Monitoring"```<br/>
