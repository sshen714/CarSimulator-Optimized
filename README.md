# CarSimulator-Optimized

這是從 [CarSimulatorWithCAN](https://github.com/ZAPEinthezone/CarSimulatorWithCAN) 分出的 Unity 專題優化版本。原專案保持不變。Unity 專案根目錄就是此儲存庫，無須再進入 `test3` 子資料夾。

## 開啟方式

1. 使用 Unity Hub 選擇 **Add > Add project from disk**，指定本資料夾。
2. 使用 Unity Editor **2022.3.62f3** 開啟。
3. 在 Project 視窗開啟 `Assets/Scenes/SampleScene.unity`。

## 目前注意事項

- 原儲存庫排除了第三方 EasyRoads3D 資產，因此單靠此 Git 儲存庫可能無法完整還原場景與腳本。發布或移交前，請確認第三方資產授權及安裝方式；不要直接把來源不明的資產上傳到公開 GitHub。
- `Library/`、`Temp/`、`Logs/` 等 Unity 產生檔不納入 Git，首次開啟會重新匯入，可能需要一些時間。
- 目前的效能調整包含降低 NPC 路徑更新及救護車附近車輛通知的呼叫頻率；尚未在此新副本完成 Unity 執行與 FPS 驗證。
