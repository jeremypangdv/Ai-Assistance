# LaunchBuddy — 本機 application／網站助手

這是 Windows 的本機測試版。它只會：

- 啟動後只在螢幕右下角顯示一個圓形小 icon（可拖曳移動），不會直接打開視窗。
  - 左鍵點 icon：開／關小型聊天框。
  - 右鍵點 icon，選 **Maximize（完整聊天）**：開啟原本的完整聊天視窗；小型與完整視窗共用同一段對話。
  - 系統列 icon 亦可用：單擊開小型聊天，雙擊開完整聊天。
- 按住說話：按住 **Left Ctrl** 說指令，放開後辨識並送出（不用說 Hey Minibot）；可在完整聊天視窗右上角的 ⚙ 設定更改按鍵或關閉。
- 暫停語音：按 **Ctrl + Alt + M**（可在 ⚙ 設定更改）暫停按住說話和 Hey Minibot，再按一次恢復。
- 語音輸入（預設關閉，右鍵選單「語音輸入（Hey Minibot）」開啟）：說「Hey Minibot, open Google Chrome」或「Hey Minibot，幫我開記事本」；出現確認卡後說 **approve／批准** 或 **reject／取消**，也可照常手動按。詳見下方「語音輸入」。
- 透過本機 Ollama（`http://127.0.0.1:11434`）的 tool calling 理解中文指令。
- 搜尋 Windows 開始功能表、Windows App Paths、Microsoft Store 的開始功能表捷徑，以及所有固定磁碟中的 application、檔案與資料夾，然後準備開啟。
- 可開啟下載資料夾、我的檔案、桌面、文件、圖片、音樂、影片，以及使用者提供完整路徑的本機檔案或資料夾。
- 明確要求時，永久保存 `名稱 → http/https 網址` 到本機資料庫。
- 對 **開啟程式、以管理員身分開啟、開啟網站、儲存／更新網站、刪除網站** 全部顯示 `Approve`／`Reject`。

模型只能提出預先定義的動作；本程式沒有可供模型使用的 PowerShell、cmd 或任意命令功能。

## 執行

目前修正版的固定交付位置是 `E:\Ai-Assistance\dist\LaunchBuddy\LaunchBuddy.exe`。
標題列應顯示 **LaunchBuddy 0.8.0**。先結束系統列中的舊版，再開啟此檔案。

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

以上只是例子。Ollama 運作時，模型會理解不同說法（中文、粵語、英文皆可），例如「我想上 YouTube」、「用最高權限跑 cmd」、「bookmark https://reddit.com as reddit」。Ollama 沒有連線時，才需要接近上面的寫法。

按系統列 icon 的右鍵選單「隨 Windows 開機啟動」，可由使用者自行切換開機常駐。資料庫位於：

`%LOCALAPPDATA%\LaunchBuddy\saved-websites.json`

## 語音輸入

### 按住說話（預設開啟，Left Ctrl）

- 按住按鍵時才開啟麥克風，放開後約 2 秒辨識完成並送出；不需要說 Hey Minibot，也不需要開啟下方的 Hey Minibot 語音輸入。
- 聊天視窗沒開時，按住說話不會打開聊天，而是在圓形 icon 旁邊跳出小卡片，依序顯示聆聽中、辨識中、處理中、Approve／Reject 確認與結果；小卡片不會搶走鍵盤焦點。聊天視窗開著時，則照舊在聊天中顯示。
- 出現確認卡時，按住按鍵說 approve 或 reject，或直接點小卡片上的按鈕。
- 按住期間按了其他鍵（例如 Ctrl+C、Ctrl+V）或點了滑鼠，這次就不會送出；短按（少於 0.25 秒）也不會。Ctrl 本身照常運作。
- 完整聊天視窗右上角的 ⚙ 可以關閉按住說話，或按「更改按鍵」後按下任何按鍵（例如 Right Ctrl、F13）來更換。
- 第一次按住時若尚未下載語音模型，會跳出提示，點提示即可下載。
- 注意：若在遊戲中 Left Ctrl 是常用按鍵（例如蹲下），可按 **Ctrl + Alt + M** 暫時停用，或改成其他按鍵。

### 暫停語音（Ctrl + Alt + M）

- 按一次暫停按住說話和 Hey Minibot（不使用麥克風），再按一次恢復；也可在 icon 右鍵選單點「暫停語音輸入」。
- 暫停只在這次執行期間有效，重新開啟 LaunchBuddy 後會恢復原本設定。
- 在 ⚙ 設定可更換組合鍵（需包含 Ctrl、Alt 或 Shift）或停用；若組合鍵已被其他程式使用，會提示更換。

### Hey Minibot（預設關閉）

- 第一次開啟時會下載 Whisper small 模型（約 180 MB）到 `%LOCALAPPDATA%\LaunchBuddy\models`，之後完全在本機辨識，聲音不會上傳。
- 開啟後圓形 icon 右上角會出現綠點，Windows 也會顯示麥克風使用中。
- 可以一口氣說完：「Hey Minibot, open Google Chrome」；或先說「Hey Minibot」，聽到提示音、icon 變紅後，8 秒內說出指令。
- 等待 Approve／Reject 時 icon 會變紅，直接說 approve、批准、確認，或 reject、取消、不要。只接受單獨說出的確認詞，在確認卡出現之前說的話不算數。
- 每句大約 2 秒辨識時間（CPU）。中英文都可以；粵語會轉成書面中文。
- 若無法啟動，請確認已接上麥克風，並在 Windows 設定 → 隱私權 → 麥克風 允許桌面應用程式使用。

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
