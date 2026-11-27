# 網易雲 × Discord

Windows 10 2004 以上／Windows 11 的桌面小工具。讀取網易雲提供給 Windows 的媒體資訊，以 Discord 本機 RPC 顯示歌曲、歌手及播放時間；暫停時保留顯示並標示「已暫停」，停止同步或正常退出時清除狀態。可縮到系統匣，斷線會自動重試。

## 使用

1. 最新版開啟 `dist-v1.4.4/CloudMusicPresence.exe`（單一獨立執行檔，免另裝 .NET 8 Runtime；其他 `dist*` 資料夾為舊版）。
2. 點「設定教學」，在 https://discord.com/developers/applications 建立應用程式，名稱建議「網易雲音樂」。
3. 複製 General Information 的 Application ID 並貼進工具。這不是 Bot Token，不需要建立 Bot。
4. 開啟並登入 Discord **電腦版**，允許設定中的活動分享。開啟網易雲電腦版播放歌曲，按「開始同步」。
5. 「縮到系統匣」保持同步；關閉視窗或系統匣「結束」會停止同步。

v1.2 修正沒有時間時發送 `timestamps: null` 導致 Discord 4000 錯誤。對未提供 SMTC 時間的網易雲 v3，改以唯讀方式取得播放器的實際進度、總長度及播放狀態。主視窗會顯示 `mm:ss / mm:ss`。拖曳進度或單曲循環會在下一次輪詢更新 Discord 時間。

同時開多個 Discord 時，先按「掃描帳號」，在下拉選單選擇帳號，再開始同步。掃描只進行本機握手，不發佈歌曲。若 Discord 重啟使連線編號對應到不同帳號，工具會拒絕繼續同步，需重新掃描。連線狀態會顯示帳號和 IPC 編號。雙擊狀態文字可查看完整錯誤。

設定存於執行檔旁的 `settings.json`，只含公開 Application ID。程式不會自動開機啟動。

v1.4.1 加入原創紅底白音符圖示，用於 EXE、視窗／工作列及系統匣；包含 16–256 像素的七種尺寸。圖示已內嵌到程式，執行時不用另帶圖片檔。圖示原始產生腳本位於 `assets/generate-icon.ps1`。同步與節流功能沿用 v1.4。

v1.4.2 依使用者選擇，改用提案 B「Moonwave／月光唱片」圖示，包含月亮、雲朵、唱片與星光。原圖為 `assets/icon-concepts/B-moonwave.png`，透過 `assets/convert-image-icon.ps1` 轉成七種尺寸的 `assets/moonwave.ico`，內嵌用於 EXE、視窗／工作列和系統匣。其他提案和舊圖示保留，原圖不修改。

v1.4.3 修正數個穩定性問題：無總長度時不再誤判拖曳進度而頻繁觸發限流；切歌轉場瞬間先短暫重試並跳過半殘更新；保留網易雲時間讀取的實際錯誤診斷訊息；未連線時發送失敗不扣更新額度；改善停止／關閉視窗時的清除提示、掃描取消與 GDI 資源釋放。

v1.4.4 簡化介面並準備單一免安裝獨立執行檔分享：內建以 AES-256 加密封裝的預設 Application ID（不顯示於介面與設定檔，不需手動設定即可直接同步），多開帳號切換、自訂 Application ID 與播放來源診斷收納至「進階設定」折疊區；「掃描帳號」下方加上多開說明註記；音樂暫停時改為繼續顯示歌曲並標示「已暫停」（不送時間戳記），不再直接清除狀態。自動化檢查共 42 項通過。

v1.4 將本機輪詢從 5 秒縮短到 250 毫秒。一般更新至少相隔 1 秒，採用 20.1 秒滾動視窗，最多 5 次活動更新；一般歌曲更新最多使用 4 格，保留第 5 格給暫停／清除。額度不足時，保持讀取最新歌曲而不排隊舊歌曲，直到有額度再送出最新狀態。換歌、暫停和超過 2 秒的進度跳轉會優先更新；正常播放由 Discord 計時，只每 30 秒進行一次校正／保活。錯誤重試採 5、10、20、40、60 秒退避，重連不重置同一程序的發送額度。正常停止會等待必要額度以清除活動。

頻率以 Discord 官方已封存 Game SDK 文件的 5 次／20 秒上限作保守參考；這不是對目前所有 RPC 行為的保證。Discord 用戶端、伺服器傳播與圖片快取仍可能帶來額外延遲。此工具的本機輪詢不會向網易雲發送 API 請求，封面則在換圖時載入公開圖片網址。無法保證任何第三方平台的零封禁風險。

v1.3 新增專輯封面：以目前歌曲 ID 和歌名核對網易雲本機 `WebData/file/playingList` 的曲目，只取得當前歌曲的專輯名稱及封面網址，送到 Discord 的 `assets.large_image`；本機視窗也顯示封面預覽。只接受網易雲圖片 CDN 的公開網址並使用 HTTPS。換歌時重新取得封面，不會沿用上一首。封面顯示需要本機和 Discord 能連到該 CDN；圖片由 Discord 的媒體服務擷取，快取更新可能有延遲。

## 相容性

歌名和歌手來源使用 Windows System Media Transport Controls（SMTC），只接受識別碼含 cloudmusic、netease、网易云或網易雲的工作階段。其他播放器不會被同步。若偵測不到，按「偵測播放來源」，確認網易雲版本有提供 Windows 媒體控制；若版本提供相關設定，請開啟。未提供 SMTC 歌曲資料的版本仍無法使用。若 SMTC 沒有提供時間，會讀取 cloudmusic.dll 的程式碼特徵並以 VM_READ 權限讀取播放器的特定時間欄位；不注入、不修改播放器、不讀取登入資料。若新版網易雲改變欄位布局，或權限不同，會顯示診斷訊息，須更新相容性。

不提供歌詞、網頁版或手機版同步。Discord 實際活動樣式由用戶端決定。只有經 Discord 確認回覆，工具才會標示同步成功。v1.2 已在此電腦實測：SMTC 時間全為零時，成功從播放器讀取 176.44 秒總長度及持續增加的進度；掃描出兩個 Discord 連線，指定其中一個後成功收到活動更新確認，主視窗顯示 `00:17 / 02:56`。v1.3 已實測取得當前歌曲封面，本機預覽成功並收到 Discord 活動更新確認。v1.4 的自動化檢查共 32 項通過，涵蓋短間隔抑制、滾動視窗邊界、暫停保留額度及清除與更新共用總額。

## 編譯與測試

需要 .NET 8 SDK：

```powershell
dotnet build
dotnet run --project tests/Tests.csproj
dotnet publish -c Release --self-contained false -o dist
```

若需要不另裝 .NET 的獨立版本，需能連到 NuGet 下載 runtime：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

獨立發行版不需另裝 .NET。原始碼無第三方服務依賴，透過 Windows API 讀取媒體和播放器時間，透過本機 named pipe 連接 Discord，不會讀取網易雲帳號或 Discord Token。網易雲 v3 特徵碼與欄位布局參考 Kxnrl 的 MIT 專案，授權與來源見 `THIRD-PARTY-NOTICES.txt`。

協定參考：https://github.com/discord/discord-api-docs/blob/main/developers/topics/rpc.mdx

Windows API：https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager
