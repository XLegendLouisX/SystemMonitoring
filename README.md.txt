命令提示字元(系統管理員)
建立服務
sc create "System Monitoring" binPath="執行檔路徑"
執行服務
sc start "System Monitoring"
刪除服務(需停止服務)
sc delete "System Monitoring"

打包程式請使用發佈
組態:Release
目標 Framework:net8.0
目標執行階段:win-x86

執行檔路徑
D:\Project\SystemMonitoring\SystemMonitoring\SystemMonitoring\bin\Release\net8.0\publish\win-x86