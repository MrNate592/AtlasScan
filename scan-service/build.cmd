@echo off
rem Builds AtlasScan.exe using the C# compiler that ships with the .NET Framework.
rem No SDK or Visual Studio required — this works on any stock Windows 10/11 machine.
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler on this machine.
  if not "%1"=="-q" pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /out:"%~dp0AtlasScan.exe" ^
  /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll ^
  "%~dp0AtlasScan.cs"

if errorlevel 1 (
  echo Build failed.
  if not "%1"=="-q" pause
  exit /b 1
)
echo Built AtlasScan.exe
if not "%1"=="-q" pause
