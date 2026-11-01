@echo off
setlocal
cd /d "%~dp0"

echo.
echo  TBox 首次运行准备
echo  ---------------------------------------------
echo  1) 安装 Windows App Runtime 1.7（已安装会自动跳过）
echo  2) 在桌面创建快捷方式
echo  3) 启动 TBox
echo.

if exist "WindowsAppRuntimeInstall-x64.exe" (
    start /wait "" "WindowsAppRuntimeInstall-x64.exe" --quiet
)

powershell -NoProfile -Command ^
  "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\TBox.lnk'); $lnk.TargetPath = '%~dp0TBox.exe'; $lnk.WorkingDirectory = '%~dp0'; $lnk.IconLocation = '%~dp0TBox.exe,0'; $lnk.Save()"

start "" "TBox.exe"
endlocal
