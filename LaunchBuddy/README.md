# LaunchBuddy — 本機 application／網站助手

這是 Windows 的本機測試版。它只會：

- 在系統列常駐；單擊或雙擊 icon 都會顯示完整聊天視窗。
- 透過本機 Ollama（`http://127.0.0.1:11434`）的 tool calling 理解中文指令。
- 搜尋 Windows 開始功能表、Windows App Paths、Microsoft Store 的開始功能表捷徑，以及所有固定磁碟中的 application、檔案與資料夾，然後準備開啟。
- 可開啟下載資料夾、我的檔案、桌面、文件、圖片、音樂、影片，以及使用者提供完整路徑的本機檔案或資料夾。
- 明確要求時，永久保存 `名稱 → http/https 網址` 到本機資料庫。
- 對 **開啟程式、以管理員身分開啟、開啟網站、儲存／更新網站、刪除網站** 全部顯示 `Approve`／`Reject`。

模型只能提出預先定義的動作；本程式沒有可供模型使用的 PowerShell、cmd 或任意命令功能。

## 執行

目前修正版的固定交付位置是 `E:\Ai-Assistance\dist\LaunchBuddy\LaunchBuddy.exe`。
標題列應顯示 **LaunchBuddy 0.3.0**。先結束系統列中的舊版，再開啟此檔案。

先確定 Ollama 正在執行，並已有至少一個模型（優先選用名稱以 `llama` 開頭的模型）。然後在此資料夾執行：

```powershell
dotnet run
```

或編譯後執行：

```powershell
dotnet build
.\bin\Debug\net10.0-windows\LaunchBuddy.exe
```

程式即使 Ollama 沒有啟動，也可用基本規則辨識常見的「記住／開啟／刪除」指令；模型啟動後會自動使用 Ollama tool calling。首次啟動時，所有固定磁碟會在背景索引，因此完整磁碟的結果可能需要一些時間才會出現，並暫時增加磁碟讀取與記憶體使用量；索引資料不會上傳。

## 可用測試指令

輸入後按 **Enter** 或點「傳送」。**Shift + Enter** 換行。
訊息會立即顯示，模型處理時會顯示等待秒數，並可點「取消等待」。
模型沒有連線或逾時會使用基本本機規則處理；等待 Approve／Reject 時需先完成選擇才能繼續傳送。

- `記住這個網站叫 GitHub https://github.com`
- `開 GitHub`
- `刪除 GitHub`
- `列出我記住的網站`
- `開 Notepad`
- `以管理員身分開 Notepad`
- `打開下載資料夾`
- `開我的檔案`
- `開 C:\Users\你的名稱\Downloads\example.pdf`

按系統列 icon 的右鍵選單「隨 Windows 開機啟動」，可由使用者自行切換開機常駐。資料庫位於：

`%LOCALAPPDATA%\LaunchBuddy\saved-websites.json`

## 安全行為

- `Reject` 不會啟動、寫入、更新或刪除任何資料。
- 所有網址都必須是 `http` 或 `https`。
- 管理員模式在按下 app 的 `Approve` 後，Windows 仍會自行顯示 UAC；取消 UAC 不會開啟程式。
- 儲存的網站不會自動消失；只有你在 app 中明確確認「刪除」後才會移除。

## 驗證

`dotnet run --project ..\LaunchBuddy.Checks\LaunchBuddy.Checks.csproj`

整合檢查包含實際 WinForms 傳送事件、Enter、訊息立即顯示、助手回覆、確認卡、
Reject、取消、離線／逾時、視窗縮放與長對話捲動。它使用模擬 Ollama 回應，
不會核准啟動 application，也不會寫入或刪除網站記錄。
