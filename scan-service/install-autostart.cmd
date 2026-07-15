@echo off
rem Adds AtlasScan.exe to the current user's Startup folder so the scanner
rem service starts automatically on login.
powershell -NoProfile -Command ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup')+'\AtlasScan Service.lnk');" ^
  "$s.TargetPath='%~dp0AtlasScan.exe';$s.WorkingDirectory='%~dp0';$s.Description='AtlasScan local scanner service';$s.Save()"
if errorlevel 1 (
  echo Failed to create the startup shortcut.
  if not "%1"=="-q" pause
  exit /b 1
)
echo AtlasScan scanner service will now start automatically on login.
if not "%1"=="-q" pause
