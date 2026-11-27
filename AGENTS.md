# 專案 Agent 規則：GitHub 版本控制與管理規範

從此專案開始，Agent 負責本專案的 GitHub 版本控制與專案管理，優先使用 Git CLI（`git`）與 GitHub CLI（`gh`；若不在 PATH 中，Windows 預設路徑為 `C:\Program Files\GitHub CLI\gh.exe`）執行相關操作。

## 一、初始化與環境檢查
- 確認 `git` 與 `gh`（GitHub CLI）已安裝且可用。
- 確認 `gh auth status` 的登入及授權狀態。
- 確認當前專案的 Git Repository、分支與遠端（remote）設定。
- 若尚未建立遠端 Repository，**必須先詢問使用者 Repository 名稱以及公開（public）或私人（private）權限**後再建立。
- 主動維護 `.gitignore`，嚴格避免上傳敏感資訊、API Key、Token、`.env`、私鑰憑證、本地設定檔（如 `settings.json`）、編譯產物與壓縮封裝檔。

## 二、日常版本管理
- 開始新開發或修改前，先檢查本地與遠端版本是否同步（如 `git fetch` / `git status`）。
- 完成每項獨立功能或修復後，建立粒度適當的 Git Commit。
- Commit Message 必須依據實際變更內容撰寫，並嚴格遵循 **Conventional Commits** 規範（例如：`feat:`、`fix:`、`docs:`、`refactor:`、`test:`、`chore:`、`build:`、`ci:`）。
- 確認程式碼建置成功並通過必要自動化測試（`tests/Tests.csproj`）後，再 Push 至對應的 GitHub 分支。
- 避免將無關修改混在同一個 Commit 中。

## 三、分支管理
- `main` 為穩定版本分支，不直接進行未經確認的風險合併。
- 新功能開發使用 `feature/<name>` 分支。
- Bug 修復使用 `fix/<name>` 分支。
- 透過 `gh pr create` 建立 Pull Request，並附上清楚的變更摘要與測試結果。
- 合併前必須檢查程式碼差異（diff）、測試結果與潛在風險。

## 四、專案維護
- 隨功能與版本演進自動維護 `README.md`。
- 使用 GitHub Issues（`gh issue`）管理待辦事項與已知問題。
- 正式版本採用 **Semantic Versioning（SemVer，例如 `v1.4.4`）**。
- 透過 `gh release` 建立 GitHub Release、Git Tag 與版本更新紀錄（Changelog）。

## 五、安全與執行權限
- **可自主執行（不需每次詢問）**：一般本地 Git 操作、`git commit`、`git push`（至非受保護的開發／修復分支）、建立 `feature/*` 或 `fix/*` 分支、透過 `gh pr create` 建立 Pull Request、維護 `.gitignore` 與 `README.md`。
- **高風險操作（必須先取得使用者明確同意）**：
  - 刪除 Repository
  - 刪除遠端分支
  - Force Push（`git push --force` / `--force-with-lease`）
  - 覆寫歷史紀錄（如對已推送分支 `rebase`、`reset --hard`、`amend`）
  - 合併至 `main`（Merge PR into `main`）
  - 修改 Repository 可見性（public ↔ private）
- **資料保護原則**：發生合併衝突或任何可能造成資料遺失的情況時，嚴禁擅自覆寫，必須立即回報狀況並請使用者確認處理方式。
